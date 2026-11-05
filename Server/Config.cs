using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>impostazioni scelte nel wizard, in <c>server.json</c> accanto al certificato</summary>
internal class ServerConfig
{
    public string DbDir { get; set; } = "";
    public const int DefaultPort = 25353; // fuori dalle porte note e sotto la zona delle connessioni in uscita (Linux da 32768, Windows da 49152)
    public int Port { get; set; } = DefaultPort;
    public string Listen { get; set; } = "all"; // "all" = tutti gli indirizzi di rete locale, oppure un singolo IP
    public bool Configured { get; set; }

    public static ServerConfig Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(file)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string file) => File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>stato condiviso dal processo: percorsi, certificato, codice di avvio, sessioni</summary>
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

    // sessioni in memoria: al riavvio del server si rifa' l'accesso
    public static readonly ConcurrentDictionary<string, DateTime> SetupTokens = new();
    public static readonly ConcurrentDictionary<string, DateTime> Sessions = new();

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string SetupCodeFile => Path.Combine(ConfigDir, "setup-code.txt");

    /// <summary>il codice sta in un file leggibile solo dagli amministratori: il servizio gira senza terminale, chi installa lo legge da li'</summary>
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

    /// <summary>codice monouso stampato nel terminale: chi non ha accesso al terminale non puo' diventare amministratore</summary>
    public static string NewSetupCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // senza 0/O, 1/I
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 8);
        return new string(chars.Take(4).ToArray()) + "-" + new string(chars.Skip(4).ToArray());
    }
}

/// <summary>blocco dopo troppi tentativi sbagliati, per indirizzo di provenienza</summary>
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

    /// <summary>registra un errore; restituisce i tentativi rimasti (0 = appena bloccato)</summary>
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
}

internal static class Admin
{
    private const int Iterations = 600_000;
    private static readonly string DummyHash = Hash("non-e-una-password");

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

    /// <summary>senza amministratore (hash null) si calcola comunque un hash: i tempi non rivelano se l'utente esiste</summary>
    public static bool VerifyOrDummy(string password, string? stored) => Verify(password, stored ?? DummyHash);

    /// <summary>versione di SQLite realmente caricata (nativa): serve a controllare che sia quella aggiornata</summary>
    public static string SqliteVersion()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        return (string)cmd.ExecuteScalar()!;
    }

    // l'elenco degli amministratori, l'accesso e la creazione guidata sono in Admins.cs
}
