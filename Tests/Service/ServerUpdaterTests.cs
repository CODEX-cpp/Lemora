using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// automatic update of the Teams server ("Lemora.Server --update"), run for real in test mode: a fake local GitHub,
    /// a test signing key and a copy of the server in a temporary folder. No service is touched (--test-update refuses to run as administrator)
    /// </summary>
    public class ServerUpdaterTests : IDisposable
    {
        private readonly HttpListener _http = new();
        private readonly int _port = Random.Shared.Next(40000, 50000);
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _work = Path.Combine(Path.GetTempPath(), "server-update-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _install, _data, _exe, _version;
        private readonly byte[] _program;
        private string _releases = "[]";
        private byte[] _file = [];

        private static string ExePath => Path.GetFullPath(typeof(ServerUpdaterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ServerExe").Value!);

        public ServerUpdaterTests()
        {
            Assert.True(File.Exists(ExePath), "server not built: " + ExePath);
            // a copy of the server build (the program needs its dll next to it); the program file becomes "old", recognisable
            _install = Path.Combine(_work, "install");
            _data = Path.Combine(_work, "data");
            Directory.CreateDirectory(_data);
            Copy(Path.GetDirectoryName(ExePath)!, _install);
            _exe = Path.Combine(_install, "Lemora.Server.exe");
            _program = File.ReadAllBytes(_exe);
            File.WriteAllText(_exe, "old-program");
            _version = RunExe(ExePath, "--version").output.Trim();
            Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", _version);

            _http.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _http.Start();
            _ = Task.Run(Serve);
        }

        public void Dispose()
        {
            try { _http.Close(); } catch { }
            _key.Dispose();
            try { Directory.Delete(_work, true); } catch { /* temp folder: cleaned later by the system */ }
        }

        private static void Copy(string from, string to)
        {
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
        }

        private static string PublicBlob(ECDsa k)
        {
            var q = k.ExportParameters(false).Q;
            var b = new byte[72];
            BitConverter.GetBytes(0x31534345u).CopyTo(b, 0); BitConverter.GetBytes(32u).CopyTo(b, 4);
            q.X!.CopyTo(b, 8); q.Y!.CopyTo(b, 40);
            return Convert.ToBase64String(b);
        }

        private async Task Serve()
        {
            while (_http.IsListening)
            {
                HttpListenerContext c;
                try { c = await _http.GetContextAsync(); } catch { return; }
                var p = c.Request.Url!.AbsolutePath;
                byte[] body = p == "/list" ? Encoding.UTF8.GetBytes(_releases) : p == "/file" ? _file : [];
                if (body.Length == 0 && p != "/list") c.Response.StatusCode = 404;
                else c.Response.OutputStream.Write(body);
                c.Response.Close();
            }
        }

        private string Sign(byte[] data, ECDsa? key = null) => Convert.ToBase64String((key ?? _key).SignData(data, HashAlgorithmName.SHA256));

        /// <summary>a release as GitHub returns it: the programs as files, the signatures hidden in the text (as the Release workflow writes them)</summary>
        private object Rel(string tag, string? sig = null, bool pre = false, string sigName = "Lemora.Server.exe")
        {
            var body = "Lemora Server\n\n<!-- lemora-signatures\nLemora.Server-linux-x64 " + Convert.ToBase64String(new byte[64]) + "\n"
                     + (sig != null ? $"{sigName} {sig}\n" : "") + "-->\n";
            return new
            {
                tag_name = tag, draft = false, prerelease = pre, body,
                assets = new object[] { new { name = "Lemora.Server.exe", browser_download_url = $"http://127.0.0.1:{_port}/file" },
                                        new { name = "Lemora.Server-linux-x64", browser_download_url = $"http://127.0.0.1:{_port}/nothing" } },
            };
        }

        private static (int code, string output) RunExe(string exe, string args, (string, string)[]? env = null)
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(120_000), "the update did not finish within 2 minutes");
            return (p.ExitCode, o);
        }

        /// <summary>the updater as the scheduled task would run it, but in test mode, from the real build</summary>
        private (int code, string output) Update(string extra = "--now") => RunExe(ExePath,
            $"--update --test-update {extra} --data \"{_data}\" --install-dir \"{_install}\" --current 1.0.0",
            [("LEMORA_UPDATE_API", $"http://127.0.0.1:{_port}/list"), ("LEMORA_UPDATE_KEY", PublicBlob(_key))]);

        private JsonElement State() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_data, "update-state.json"))).RootElement;

        [Fact]
        public void NewServerRelease_IsCheckedAndInstalled_OtherReleasesIgnored()
        {
            // the app (v...), the extension and a newer pre-release are in the same repo: only the published server release counts
            _file = _program;
            var s = Sign(_file);
            _releases = JsonSerializer.Serialize(new[] { Rel("v99.0.0.1", s), Rel("estensione-99.0.4", s), Rel("server-99.0.0", s, pre: true), Rel("server-" + _version, s) });
            var (code, output) = Update();
            Assert.True(code == 0, output);
            Assert.Equal("ok", State().GetProperty("Result").GetString());
            Assert.Equal(_version, State().GetProperty("Current").GetString());
            Assert.Equal(_program, File.ReadAllBytes(_exe));                          // new program in place
            Assert.Equal("old-program", File.ReadAllText(_exe + ".old"));       // old one kept for going back by hand
            Assert.False(File.Exists(_exe + ".new"));
        }

        [Fact]
        public void TagWithThreePartVersion_IsFound()
        {
            // the workflow writes the tag "server-1.2.3" while the program says "1.2.3.0"
            var three = _version.EndsWith(".0") ? _version[..^2] : _version;
            _file = _program;
            _releases = JsonSerializer.Serialize(new[] { Rel("server-" + three, Sign(_file)) });
            var (code, output) = Update();
            Assert.True(code == 0, output);
            Assert.Equal("ok", State().GetProperty("Result").GetString());
            Assert.Equal(_program, File.ReadAllBytes(_exe));
        }

        [Fact]
        public void SignedWithAnotherKey_NothingIsTouched()
        {
            _file = _program;
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _releases = JsonSerializer.Serialize(new[] { Rel("server-" + _version, Sign(_file, other)) });
            var (code, output) = Update();
            Assert.True(code == 3, output);
            Assert.Equal("error", State().GetProperty("Result").GetString());
            Assert.Equal("old-program", File.ReadAllText(_exe));
            Assert.False(File.Exists(_exe + ".new"));
        }

        [Fact]
        public void SignatureMissingOrOfAnotherFile_NothingIsTouched()
        {
            // no signature line for this system's program; a valid signature listed under another file name does not count
            _file = _program;
            foreach (var rel in new[] { Rel("server-" + _version), Rel("server-" + _version, Sign(_file), sigName: "Lemora.Server-linux-arm64") })
            {
                _releases = JsonSerializer.Serialize(new[] { rel });
                var (code, output) = Update();
                Assert.True(code == 3, output);
                Assert.Equal("error", State().GetProperty("Result").GetString());
                Assert.Equal("old-program", File.ReadAllText(_exe));
                Assert.False(File.Exists(_exe + ".new"));
            }
        }

        [Fact]
        public void ProgramChangedAfterSigning_IsRejected()
        {
            // the file on GitHub is replaced (one byte changed) while the text keeps the old signature
            var s = Sign(_program);
            _file = (byte[])_program.Clone(); _file[^1] ^= 0xFF;
            _releases = JsonSerializer.Serialize(new[] { Rel("server-" + _version, s) });
            var (code, output) = Update();
            Assert.True(code == 3, output);
            Assert.Equal("old-program", File.ReadAllText(_exe));
        }

        [Fact]
        public void OnlyOlderOrOtherReleases_NoUpdate()
        {
            _releases = JsonSerializer.Serialize(new[] { Rel("v99.0.0.1"), Rel("estensione-9.0.0"), Rel("server-0.9.0") });
            var (code, output) = Update();
            Assert.True(code == 0, output);
            Assert.Equal("uptodate", State().GetProperty("Result").GetString());
            Assert.Equal("old-program", File.ReadAllText(_exe));
        }

        [Fact]
        public void AutoUpdateOffInTheConsole_NightRunDoesNothing()
        {
            File.WriteAllText(Path.Combine(_data, "server.json"), "{\"AutoUpdate\": false}");
            _releases = JsonSerializer.Serialize(new[] { Rel("server-" + _version) });
            var (code, output) = Update("");   // without --now, as the scheduled task does
            Assert.True(code == 0, output);
            Assert.False(File.Exists(Path.Combine(_data, "update-state.json")));
            Assert.Equal("old-program", File.ReadAllText(_exe));
        }
    }
}
