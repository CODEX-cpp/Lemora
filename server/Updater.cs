using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lemora.Server;

/// <summary>
/// Automatic update of the server, every night at 02:00. It does NOT run inside the service (the service is unprivileged and
/// cannot replace its own program): the installer creates a scheduled task (Windows, as SYSTEM) or a systemd timer (Linux, as root)
/// that launches "Lemora.Server --update". Steps:
///   1. reads the release list of CODEX-cpp/Lemora and takes the newest published "server-x.y.z" (app and extension releases are skipped);
///   2. downloads the program for this system (the same file used for a new installation: Lemora.Server.exe, Lemora.Server-linux-x64,
///      Lemora.Server-linux-arm64) INTO MEMORY and checks its ECDSA signature with the key compiled in here (the same key as the app's
///      updates). The signatures are in the release text, inside a hidden HTML comment; nothing is written to disk before the check;
///   3. the new program must start ("--version") and say the expected version;
///   4. stops the service, copies the database aside, puts the new program in place (the old one stays as .old), starts the service;
///   5. waits for the server to answer with the new version: otherwise it puts back program and database and restarts the old one.
/// The result goes to update-state.json in the data folder (shown in the console) and to update.log.
/// </summary>
internal static class Updater
{
    public const string Owner = "CODEX-cpp";
    public const string Repo = "Lemora";
    /// <summary>public key of the updates (ECCPUBLIC blob, base64), the same as the app: the private one lives only on the publisher's PC</summary>
    public const string PublicKeyB64 = "RUNTMSAAAAAa3ie5qw/WRTBnxZJgqBLcf/83vqod1zRZ5KqVtmtMWrfkBqWMi3vPpjJxNHAI1O/hidYC5LAjgf8/hAAYunPa";
    public const string WinTask = @"Lemora Server\Automatic update";
    public const string LinuxUnit = "lemora-server-update";
    public const string Hour = "02:00";

    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    /// <summary>win-x64, linux-x64 or linux-arm64: the package to download for this machine</summary>
    public static string Rid => (OperatingSystem.IsWindows() ? "win" : "linux") + "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
    public static string ExeName => OperatingSystem.IsWindows() ? "Lemora.Server.exe" : "Lemora.Server";
    /// <summary>name of this system's program in the release</summary>
    public static string AssetName => OperatingSystem.IsWindows() ? "Lemora.Server.exe" : $"Lemora.Server-{Rid}";

    /// <summary>"server-1.2.3" or "server-v1.2.3" -> 1.2.3; anything else (app "v1.3.1", "estensione-1.0.4") -> null</summary>
    public static Version? ParseTag(string? tag)
    {
        tag = (tag ?? "").Trim();
        if (!tag.StartsWith("server-", StringComparison.OrdinalIgnoreCase)) return null;
        tag = tag[7..];
        if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag[1..];
        return Version.TryParse(tag, out var v) ? Norm(v) : null;
    }

    /// <summary>always 4 parts: "1.1.0" and the assembly's "1.1.0.0" must be the same version (Version treats them as different)</summary>
    public static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>newest published server release in the GitHub answer (list or single release): drafts and pre-releases are skipped</summary>
    public static (Version Version, JsonElement Release)? Newest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var all = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        (Version, JsonElement)? best = null;
        foreach (var r in all)
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;
            var v = ParseTag(r.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
            if (v != null && (best == null || v > best.Value.Item1)) best = (v, r.Clone());
        }
        return best;
    }

    public static string? AssetUrl(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var a) || a.ValueKind != JsonValueKind.Array) return null;
        foreach (var x in a.EnumerateArray())
            if (x.TryGetProperty("name", out var n) && n.GetString() == name && x.TryGetProperty("browser_download_url", out var u)) return u.GetString();
        return null;
    }

    /// <summary>the signature (64 bytes, base64) is valid for these bytes with the key (ECCPUBLIC blob: "ECS1" + 32 + X + Y)</summary>
    public static bool Verify(byte[] data, string sigB64, string keyB64)
    {
        try
        {
            var blob = Convert.FromBase64String(keyB64);
            if (blob.Length != 72 || BitConverter.ToUInt32(blob, 0) != 0x31534345 || BitConverter.ToUInt32(blob, 4) != 32) return false;
            using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = blob[8..40], Y = blob[40..72] } });
            var sig = Convert.FromBase64String(sigB64.Trim());
            return sig.Length == 64 && ec.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>signature of a release file, from the hidden block in the release text:
    /// &lt;!-- lemora-signatures / one line "file-name base64-signature" per file / --&gt; (not shown on the release page)</summary>
    public static string? SignatureFor(JsonElement release, string name)
    {
        if (!release.TryGetProperty("body", out var b) || b.GetString() is not { } body) return null;
        var block = Regex.Match(body, @"<!--\s*lemora-signatures(.*?)-->", RegexOptions.Singleline);
        if (!block.Success) return null;
        var line = Regex.Match(block.Groups[1].Value, $@"(?m)^\s*{Regex.Escape(name)}\s+([A-Za-z0-9+/=]+)\s*$");
        return line.Success ? line.Groups[1].Value : null;
    }

    // ---------------------------------------------------------------- run ("--update")

    public record UpdateState(string Result, string Current, string? Available, DateTime When, string Detail);

    public static string StateFile(string data) => Path.Combine(data, "update-state.json");

    public static UpdateState? ReadState(string data)
    {
        try { return File.Exists(StateFile(data)) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFile(data))) : null; }
        catch { return null; }
    }

    /// <summary>
    /// --update [--now] [--data dir]. Without --now it does nothing if automatic updates are off in the console.
    /// For tests only (not elevated): --test-update with --install-dir, LEMORA_UPDATE_API and LEMORA_UPDATE_KEY; no service is touched.
    /// The elevated run (the real scheduled one) ignores those variables: nobody can make it install a package signed by someone else.
    /// </summary>
    public static int Run(string[] args, string? dataArg)
    {
        var data = dataArg ?? Paths.DefaultDataDir;
        bool test = args.Contains("--test-update");
        // the test mode accepts another address and another key: never as administrator, where it would install for real
        if (test && Svc.IsElevated()) { Console.Error.WriteLine("--test-update must not be run as administrator."); return 4; }
        if (!test && !Svc.IsElevated()) { Console.Error.WriteLine("The update must be run as administrator (the scheduled task does it at " + Hour + ")."); return 4; }
        var cfg = ServerConfig.Load(Path.Combine(data, "server.json"));
        if (!cfg.AutoUpdate && !args.Contains("--now")) { Log(data, "automatic updates turned off in the console: nothing to do"); return 0; }

        var installDir = test ? Arg(args, "--install-dir") ?? throw new ArgumentException("--install-dir") : Path.GetDirectoryName(Environment.ProcessPath!)!;
        var api = test && Environment.GetEnvironmentVariable("LEMORA_UPDATE_API") is { Length: > 0 } a ? a : $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=50";
        var key = test && Environment.GetEnvironmentVariable("LEMORA_UPDATE_KEY") is { Length: > 0 } k ? k : PublicKeyB64;
        var current = test && Version.TryParse(Arg(args, "--current"), out var cv) ? Norm(cv) : Current;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Lemora-Server-Updater");   // GitHub rejects requests without one
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json;
            try { json = http.GetStringAsync(api).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return Save(data, "offline", current, null, "GitHub unreachable: will retry next night (" + e.Message + ")", 0);
            }
            var found = Newest(json);
            if (found == null || found.Value.Version <= current) return Save(data, "uptodate", current, found?.Version, "No new version", 0);
            var (ver, rel) = found.Value;

            var url = AssetUrl(rel, AssetName); var sig = SignatureFor(rel, AssetName);
            if (url == null || sig == null) return Save(data, "error", current, ver, $"Release server-{ver} does not contain {AssetName} and its signature", 3);
            var program = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (!Verify(program, sig, key)) return Save(data, "error", current, ver, "Invalid program signature: download discarded", 3);

            var exe = Path.Combine(installDir, ExeName);
            var next = exe + ".new";
            File.WriteAllBytes(next, program);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(next, (UnixFileMode)0b111_101_101);   // 755
            // the new program must run on this machine and be the announced version, before touching anything
            var said = RunVersion(next);
            if (!Version.TryParse(said, out var sv) || Norm(sv) != ver) { File.Delete(next); return Save(data, "error", current, ver, $"The new program does not start or reports version '{said}' instead of {ver}", 3); }

            // once the program file is swapped, this running copy can no longer load the parts of .NET it has not used yet
            // (a single-file program reads them from its own exe, which is now the new one): JSON writing, needed by Save, is loaded now
            _ = JsonSerializer.Serialize(new UpdateState("", "", null, DateTime.UtcNow, ""));
            if (!test) ServiceStop();
            var dbBackup = test ? null : BackupDb(cfg, ver);
            var old = exe + ".old";
            if (File.Exists(exe)) File.Move(exe, old, true);   // a running program can be renamed (Windows) or replaced (Linux)
            File.Move(next, exe, true);
            if (test) return Save(data, "ok", ver, ver, $"Updated from {current} to {ver} (test, no service)", 0);

            var since = DateTime.UtcNow;
            ServiceStart();
            if (WaitRunning(data, ver.ToString(), since, TimeSpan.FromSeconds(120))) return Save(data, "ok", ver, ver, $"Updated from {current} to {ver}", 0);

            // the new version does not answer: everything back as before
            ServiceStop();
            if (File.Exists(old)) File.Move(old, exe, true);
            if (dbBackup != null) RestoreDb(cfg, dbBackup);
            ServiceStart();
            return Save(data, "restored", current, ver, $"Version {ver} did not start: restored {current} with its database", 3);
        }
        catch (Exception ex)
        {
            return Save(data, "error", current, null, ex.Message, 3);
        }
    }

    static int Save(string data, string result, Version current, Version? available, string detail, int code)
    {
        var st = new UpdateState(result, current.ToString(), available?.ToString(), DateTime.UtcNow, detail);
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(StateFile(data), JsonSerializer.Serialize(st));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(StateFile(data), (UnixFileMode)0b110_100_100);   // the unprivileged service must read it
        }
        catch (Exception e) { Console.Error.WriteLine("update state not written: " + e.Message); }
        Log(data, $"{result}: {detail}");
        return code;
    }

    static void Log(string data, string line)
    {
        Console.WriteLine(line);
        try
        {
            var f = Path.Combine(data, "update.log");
            if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Move(f, f + ".1", true);   // a few nights of history are enough
            File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* the log is only informational */ }
    }

    /// <summary>"Lemora.Server --version" of a program file: its version, or "" if it does not start</summary>
    static string RunVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(30000) && p.ExitCode == 0 ? o : "";
        }
        catch { return ""; }
    }

    static void ServiceStop()
    {
        if (OperatingSystem.IsWindows())
        {
            Exec("sc.exe", "stop", Svc.WinName);
            for (int i = 0; i < 60 && !Exec("sc.exe", "query", Svc.WinName).Contains("STOPPED"); i++) Thread.Sleep(500);
        }
        else Exec("systemctl", "stop", "lemora-server");
    }

    static void ServiceStart()
    {
        if (OperatingSystem.IsWindows()) Exec("sc.exe", "start", Svc.WinName);
        else Exec("systemctl", "start", "lemora-server");
    }

    static string Exec(string file, params string[] a)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var x in a) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return o;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>the server writes runtime.json with its pid and version at every start: wait for the one of this start</summary>
    static bool WaitRunning(string data, string version, DateTime since, TimeSpan max)
    {
        var f = Path.Combine(data, "runtime.json");
        var end = DateTime.UtcNow + max;
        while (DateTime.UtcNow < end)
        {
            try
            {
                if (File.Exists(f) && File.GetLastWriteTimeUtc(f) >= since.AddSeconds(-1))
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(f));
                    if (d.RootElement.TryGetProperty("version", out var v) && v.GetString() == version) return true;
                }
            }
            catch { /* being written */ }
            Thread.Sleep(1000);
        }
        return false;
    }

    /// <summary>copy of the database with the service stopped (consistent), kept for a rollback; the last 3 copies are kept</summary>
    static string? BackupDb(ServerConfig cfg, Version to)
    {
        if (cfg.DbDir.Length == 0) return null;
        var db = Path.Combine(cfg.DbDir, "server.db");
        if (!File.Exists(db)) return null;
        var dir = Path.Combine(cfg.DbDir, "update-backups");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"server-before-{to}-{DateTime.Now:yyyyMMdd-HHmm}.db");
        File.Copy(db, dest, true);
        foreach (var ext in new[] { "-wal", "-shm" }) if (File.Exists(db + ext)) File.Copy(db + ext, dest + ext, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, (UnixFileMode)0b110_000_000);   // 600: it holds the encrypted passwords
        foreach (var f in Directory.GetFiles(dir, "server-before-*.db").OrderByDescending(File.GetCreationTimeUtc).Skip(3))
            foreach (var g in new[] { f, f + "-wal", f + "-shm" }) try { File.Delete(g); } catch { }
        return dest;
    }

    static void RestoreDb(ServerConfig cfg, string backup)
    {
        var db = Path.Combine(cfg.DbDir, "server.db");
        File.Copy(backup, db, true);
        foreach (var ext in new[] { "-wal", "-shm" })
        {
            try { File.Delete(db + ext); } catch { }
            if (File.Exists(backup + ext)) File.Copy(backup + ext, db + ext, true);
        }
    }

    // ---------------------------------------------------------------- scheduling (called by the installer, already elevated)

    /// <summary>Windows: scheduled task as SYSTEM every day at 02:00. Linux: systemd timer + oneshot service as root</summary>
    public static string? Schedule(string exe, string dataArg)
    {
        if (OperatingSystem.IsWindows())
        {
            var o = Exec("schtasks.exe", "/Create", "/F", "/TN", WinTask, "/TR", $"\"{exe}\" --update{dataArg}", "/SC", "DAILY", "/ST", Hour, "/RU", "SYSTEM", "/RL", "HIGHEST");
            return TaskExists() ? null : "Update scheduled task not created: " + o.Trim();   // schtasks answers in the system language: check by querying
        }
        File.WriteAllText($"/etc/systemd/system/{LinuxUnit}.service", $"""
            [Unit]
            Description=Lemora Server: automatic update

            [Service]
            Type=oneshot
            ExecStart=/usr/bin/env DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/lemora-server-root HOME=/root {exe} --update
            """.Replace("\r", "") + "\n");
        File.WriteAllText($"/etc/systemd/system/{LinuxUnit}.timer", $"""
            [Unit]
            Description=Lemora Server: automatic update every night at {Hour}

            [Timer]
            OnCalendar=*-*-* {Hour}:00

            [Install]
            WantedBy=timers.target
            """.Replace("\r", "") + "\n");
        Exec("systemctl", "daemon-reload");
        var e = Exec("systemctl", "enable", "--now", LinuxUnit + ".timer");
        return Exec("systemctl", "is-enabled", LinuxUnit + ".timer").Trim() == "enabled" ? null : "Update timer not enabled: " + e.Trim();
    }

    public static bool TaskExists() => OperatingSystem.IsWindows() && Exec("schtasks.exe", "/Query", "/TN", WinTask).Contains("Lemora");

    public static void Unschedule()
    {
        if (OperatingSystem.IsWindows()) { Exec("schtasks.exe", "/Delete", "/F", "/TN", WinTask); return; }
        Exec("systemctl", "disable", "--now", LinuxUnit + ".timer");
        foreach (var f in new[] { $"/etc/systemd/system/{LinuxUnit}.timer", $"/etc/systemd/system/{LinuxUnit}.service" }) try { File.Delete(f); } catch { }
        Exec("systemctl", "daemon-reload");
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
