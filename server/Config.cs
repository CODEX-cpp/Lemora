using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>settings chosen in the wizard, in <c>server.json</c> next to the certificate</summary>
internal class ServerConfig
{
    public string DbDir { get; set; } = "";
    public const int DefaultPort = 25353; // outside the well-known ports and below the outgoing connection range (Linux from 32768, Windows from 49152)
    public int Port { get; set; } = DefaultPort;
    public string Listen { get; set; } = "all"; // "all" = every local network address, or a single IP
    public bool Configured { get; set; }
    /// <summary>automatic update every night at 02:00 (Updater): on by default, can be turned off from the console</summary>
    public bool AutoUpdate { get; set; } = true;

    public static ServerConfig Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(file)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string file) => File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>process-wide shared state: paths, certificate, setup code, sessions</summary>
internal static class State
{
    public static string ConfigDir = "";
    public static string ConfigFile => Path.Combine(ConfigDir, "server.json");
    public static ServerConfig Config = new();
    public static X509Certificate2 Cert = null!;
    public static int CurrentPort;
    public static List<IPAddress> Addresses = [];
    public static string SetupCode = "";
    public static TaskCompletionSource Restart = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // in-memory sessions: after a server restart you sign in again
    public static readonly ConcurrentDictionary<string, DateTime> SetupTokens = new();
    public static readonly ConcurrentDictionary<string, DateTime> Sessions = new();

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string SetupCodeFile => Path.Combine(ConfigDir, "setup-code.txt");

    /// <summary>the code lives in a file readable only by administrators: the service runs without a terminal, the installer reads it from there</summary>
    public static void LoadSetupCode()
    {
        if (Config.Configured) { ClearSetupCode(); return; }
        try { SetupCode = File.Exists(SetupCodeFile) ? File.ReadAllText(SetupCodeFile).Trim() : ""; } catch { SetupCode = ""; }
        if (SetupCode.Length == 0) { SetupCode = NewSetupCode(); File.WriteAllText(SetupCodeFile, SetupCode); }
    }

    public static void ClearSetupCode()
    {
        SetupCode = "";
        try { File.Delete(SetupCodeFile); } catch { }
    }

    /// <summary>one-time code printed in the terminal: whoever has no terminal access cannot become administrator</summary>
    public static string NewSetupCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // without 0/O, 1/I
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 8);
        return new string(chars.Take(4).ToArray()) + "-" + new string(chars.Skip(4).ToArray());
    }
}

/// <summary>lockout after too many failed attempts, per source address</summary>
internal class AttemptLimiter(int max, TimeSpan block)
{
    private readonly Dictionary<string, (int fails, DateTime until)> _s = new();

    public bool IsBlocked(string key, out DateTime until)
    {
        lock (_s)
        {
            until = default;
            if (_s.TryGetValue(key, out var v) && v.until > DateTime.UtcNow) { until = v.until; return true; }
            return false;
        }
    }

    /// <summary>records a failure; returns the attempts left (0 = just locked out)</summary>
    public int Fail(string key)
    {
        lock (_s)
        {
            _s.TryGetValue(key, out var v);
            int fails = v.fails + 1;
            if (fails >= max) { _s[key] = (0, DateTime.UtcNow + block); return 0; }
            _s[key] = (fails, DateTime.MinValue);
            return max - fails;
        }
    }

    public void Reset(string key) { lock (_s) _s.Remove(key); }

    /// <summary>
    /// counts the attempt BEFORE checking the password, in a single step: with IsBlocked + Fail, parallel requests all passed
    /// the check before the first failure was counted. -1 = locked out; otherwise the attempts left after this one.
    /// Failures older than the lockout time no longer count (sliding window: needed for the global cap)
    /// </summary>
    public int Take(string key, out DateTime until)
    {
        lock (_s)
        {
            until = default;
            var now = DateTime.UtcNow;
            _s.TryGetValue(key, out var v);
            if (v.until > now) { until = v.until; return -1; }
            var (fails, first) = _w.TryGetValue(key, out var w) && now - w.first < block ? (w.fails, w.first) : (0, now);
            fails++;
            if (fails > max) { until = now + block; _s[key] = (0, until); _w.Remove(key); return -1; }
            _w[key] = (fails, first);
            _s[key] = (fails, DateTime.MinValue);
            return max - fails;
        }
    }

    /// <summary>successful login: the attempt counted by Take no longer weighs</summary>
    public void Succeeded(string key) { lock (_s) { _s.Remove(key); _w.Remove(key); } }

    /// <summary>removes only the successful attempt (global cap: a correct login must not reset the others' failures)</summary>
    public void Undo(string key)
    {
        lock (_s)
        {
            if (_w.TryGetValue(key, out var w) && w.fails > 0) _w[key] = (w.fails - 1, w.first);
            if (_s.TryGetValue(key, out var v) && v.fails > 0) _s[key] = (v.fails - 1, v.until);
        }
    }

    private readonly Dictionary<string, (int fails, DateTime first)> _w = new();
}

internal static class Admin
{
    private const int Iterations = 600_000;
    private static readonly string DummyHash = Hash("not-a-password");

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out var it)) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(p[2]), it, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[3]));
    }

    /// <summary>without an administrator (null hash) a hash is still computed: timing does not reveal whether the user exists</summary>
    public static bool VerifyOrDummy(string password, string? stored) => Verify(password, stored ?? DummyHash);

    /// <summary>SQLite version actually loaded (native): used to check it is the updated one</summary>
    public static string SqliteVersion()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        return (string)cmd.ExecuteScalar()!;
    }

    // the administrator list, login and setup wizard are in Admins.cs
}
