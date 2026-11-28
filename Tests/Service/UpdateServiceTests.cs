using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lemora.Setup;
using Newtonsoft.Json;
using Xunit;

namespace Tests.Service
{
    /// <summary>update check, download and signature: against a fake local "GitHub" and a test signing key</summary>
    [Collection("UpdateEnv")]   // environment variables are process-wide: these tests must not run in parallel
    public class UpdateServiceTests : IDisposable
    {
        private readonly HttpListener _http = new();
        private readonly int _port = Random.Shared.Next(20000, 40000);
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string? _oldApi = Environment.GetEnvironmentVariable("LEMORA_UPDATE_API"), _oldKey = Environment.GetEnvironmentVariable("LEMORA_UPDATE_KEY");
        private byte[] _zip = Encoding.UTF8.GetBytes("fake-package");
        private string _sig = "";
        private string _tag = "";
        private bool _withAssets = true;

        public UpdateServiceTests()
        {
            _http.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _http.Start();
            Environment.SetEnvironmentVariable("LEMORA_UPDATE_API", $"http://127.0.0.1:{_port}/latest");
            Environment.SetEnvironmentVariable("LEMORA_UPDATE_KEY", PublicBlob(_key));
            _ = Task.Run(Serve);
        }

        public void Dispose()
        {
            try { _http.Close(); } catch { }
            Environment.SetEnvironmentVariable("LEMORA_UPDATE_API", _oldApi);
            Environment.SetEnvironmentVariable("LEMORA_UPDATE_KEY", _oldKey);
            _key.Dispose();
            UpdateService.CleanTemp();
        }

        /// <summary>ECCPUBLIC blob as written by new-keys.ps1: "ECS1" + 32 + X + Y</summary>
        private static string PublicBlob(ECDsa k)
        {
            var q = k.ExportParameters(false).Q;
            var b = new byte[72];
            BitConverter.GetBytes(0x31534345u).CopyTo(b, 0); BitConverter.GetBytes(32u).CopyTo(b, 4);
            q.X!.CopyTo(b, 8); q.Y!.CopyTo(b, 40);
            return Convert.ToBase64String(b);
        }

        private string Sign(byte[] data, ECDsa? k = null) => Convert.ToBase64String((k ?? _key).SignData(data, HashAlgorithmName.SHA256));

        private async Task Serve()
        {
            while (_http.IsListening)
            {
                HttpListenerContext c;
                try { c = await _http.GetContextAsync(); } catch { return; }
                var path = c.Request.Url!.AbsolutePath;
                byte[] body;
                if (path == "/latest")
                {
                    var ver = _tag.TrimStart('v');
                    var name = $"lemora-{ver}-update-package.zip";
                    var assets = _withAssets
                        ? new[] { new { name, browser_download_url = $"http://127.0.0.1:{_port}/zip" }, new { name = name + ".sig", browser_download_url = $"http://127.0.0.1:{_port}/sig" } }
                        : Array.Empty<object>().Length == 0 ? new[] { new { name = "other.txt", browser_download_url = "x" } } : null;
                    body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { tag_name = _tag, assets }));
                }
                else if (path == "/list")
                {
                    // the same repo with releases of other parts: only the published app release must be chosen
                    var ver = _tag.TrimStart('v');
                    object Rel(string tag, bool draft = false, bool pre = false) => new
                    {
                        tag_name = tag, draft, prerelease = pre,
                        assets = new[] { new { name = $"lemora-{ParseTagText(tag)}-update-package.zip", browser_download_url = $"http://127.0.0.1:{_port}/zip" },
                                         new { name = $"lemora-{ParseTagText(tag)}-update-package.zip.sig", browser_download_url = $"http://127.0.0.1:{_port}/sig" } }
                    };
                    body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new[]
                    {
                        Rel("server-99.0.0"), Rel("estensione-99.0.4"), Rel("v99.0.0.1", pre: true), Rel("v98.0.0.1", draft: true),
                        Rel("app-v" + ver), Rel("v0.0.1")
                    }));
                }
                else if (path == "/zip") body = _zip;
                else if (path == "/sig") body = Encoding.UTF8.GetBytes(_sig);
                else { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                c.Response.OutputStream.Write(body);
                c.Response.Close();
            }
        }

        private static string Newer() { var v = UpdateService.CurrentVersion; return $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision + 1}"; }

        [Fact]
        public async Task NewerRelease_IsFound_DownloadedAndVerified()
        {
            var ct = TestContext.Current.CancellationToken;
            _tag = Newer(); _sig = Sign(_zip);
            var info = await UpdateService.CheckAsync(ct);
            Assert.NotNull(info);
            Assert.Equal(UpdateService.ParseTag(_tag), info!.Version);
            var file = await UpdateService.DownloadAsync(info, null, ct);
            Assert.Equal(_zip, await File.ReadAllBytesAsync(file, ct));
        }

        [Fact]
        public async Task SameOrOlderRelease_NoUpdate()
        {
            var ct = TestContext.Current.CancellationToken;
            _tag = "v" + UpdateService.CurrentVersion; Assert.Null(await UpdateService.CheckAsync(ct));
            _tag = "v0.0.1"; Assert.Null(await UpdateService.CheckAsync(ct));
        }

        [Fact]
        public async Task ChangedPackage_IsRejectedAndDeleted()
        {
            var ct = TestContext.Current.CancellationToken;
            _tag = Newer(); _sig = Sign(_zip);
            var info = (await UpdateService.CheckAsync(ct))!;
            _zip = Encoding.UTF8.GetBytes("tampered-package");   // the file changes after signing
            await Assert.ThrowsAsync<CryptographicException>(() => UpdateService.DownloadAsync(info, null, ct));
            Assert.False(File.Exists(Path.Combine(UpdateService.TempRoot, info.Version.ToString(), "package.zip")));
        }

        [Fact]
        public async Task SignedWithAnotherKey_IsRejected()
        {
            var ct = TestContext.Current.CancellationToken;
            _tag = Newer();
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _sig = Sign(_zip, other);
            var info = (await UpdateService.CheckAsync(ct))!;
            await Assert.ThrowsAsync<CryptographicException>(() => UpdateService.DownloadAsync(info, null, ct));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-signature")]
        [InlineData("AAAA")]
        public void BadSignatureText_IsFalseNotACrash(string sig) => Assert.False(UpdateService.VerifySignature(_zip, sig));

        [Fact]
        public async Task ReleaseWithoutFiles_IsAnError_ServerDown_IsQuietNull()
        {
            var ct = TestContext.Current.CancellationToken;
            _tag = Newer(); _withAssets = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.CheckAsync(ct));
            _http.Close();
            Assert.Null(await UpdateService.CheckAsync(ct));
        }

        [Fact]
        public void ShippedPublicKey_HasTheRightShape()
        {
            var b = Convert.FromBase64String(UpdateService.PublicKeyB64);
            Assert.Equal(72, b.Length);
            Assert.Equal(0x31534345u, BitConverter.ToUInt32(b, 0));
            Assert.Equal(32u, BitConverter.ToUInt32(b, 4));
        }

        private static string ParseTagText(string tag) => UpdateService.ParseTag(tag)?.ToString() ?? tag;

        [Fact]
        public async Task ReleasesOfOtherParts_DraftsAndPreReleases_AreIgnored()
        {
            var ct = TestContext.Current.CancellationToken;
            Environment.SetEnvironmentVariable("LEMORA_UPDATE_API", $"http://127.0.0.1:{_port}/list");
            _tag = Newer(); _sig = Sign(_zip);
            var info = await UpdateService.CheckAsync(ct);
            Assert.NotNull(info);
            Assert.Equal(UpdateService.ParseTag(_tag), info!.Version);   // not 99.x (server, extension, pre-release) nor 98.x (draft)
            Assert.Equal(_zip, await File.ReadAllBytesAsync(await UpdateService.DownloadAsync(info, null, ct), ct));
        }

        [Theory]
        [InlineData("v1.3.1.11105", true)]
        [InlineData("1.2.3", true)]
        [InlineData("app-1.3.2", true)]
        [InlineData("app-v1.3.2.5", true)]
        [InlineData("server-1.0.0", false)]
        [InlineData("estensione-1.0.4", false)]
        [InlineData("", false)]
        [InlineData("latest", false)]
        public void Tags(string tag, bool ok) => Assert.Equal(ok, UpdateService.ParseTag(tag) != null);
    }
}
