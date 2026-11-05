// Converte un database di Lemora dalla vecchia cifratura (1Remote.Security, chiave ricavabile dal codice) alla cassaforte nuova
// (AES-256-GCM, chiave dei dati chiusa con la password principale). Uso:
//   migra-cifratura <percorso\Lemora.db> [--prova]
// --prova: conta cosa cambierebbe senza scrivere nulla. Prima di scrivere fa una copia del database accanto all'originale.
// La password principale si scrive due volte (oppure LEMORA_VAULT_PASSWORD per le prove automatiche).
// Windows Hello si aggiunge poi dall'app, al primo sblocco con la password.
using System.Data.SQLite;
using Lemora.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("Uso: migra-cifratura <percorso\\Lemora.db> [--prova]"); return 2; }
var dbPath = Path.GetFullPath(args[0]);
var dryRun = args.Contains("--prova");

_1Remote.Security.Config.SetSalt("===REPLACE_ME_WITH_SALT===");   // il sale delle build di Lemora fino al 04/11/2026 (mai sostituito)

using var cn = new SQLiteConnection($"Data Source={dbPath}");
cn.Open();
string? GetConfig(string key)
{
    using var c = new SQLiteCommand("SELECT `Value` FROM `Configs` WHERE `Key` = @k", cn);
    c.Parameters.AddWithValue("@k", key);
    return c.ExecuteScalar() as string;
}
if (GetConfig(DataVault.HeaderConfigKey) != null) { Console.WriteLine("Il database ha gia' la cassaforte nuova: niente da fare."); return 0; }
var et = GetConfig("EncryptionTest");
if (et == null || OldDecrypt(et) != "EncryptionTest") { Console.WriteLine("La prova di cifratura del database non si apre con la vecchia chiave: mi fermo senza toccare nulla."); return 3; }

string password;
var envPwd = Environment.GetEnvironmentVariable("LEMORA_VAULT_PASSWORD");
if (envPwd != null) password = envPwd;
else if (dryRun) password = "solo-prova-non-salvata";
else
{
    while (true)
    {
        var p1 = ReadSecret("Nuova password principale (almeno 8 caratteri): ");
        if (p1.Length < 8) { Console.WriteLine("  troppo corta."); continue; }
        if (ReadSecret("Ripetila: ") != p1) { Console.WriteLine("  le due password non coincidono."); continue; }
        password = p1; break;
    }
}

var key = DataVault.NewKey();
DataVault.SetKey(key);
var header = DataVault.NewHeader(key, password);

if (!dryRun)
{
    var backup = dbPath + ".prima-della-cassaforte-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    cn.Close();
    File.Copy(dbPath, backup);
    cn.Open();
    Console.WriteLine($"Copia di sicurezza: {backup}");
}

int changedCells = 0, oldValues = 0, wholeRows = 0, renamedKeys = 0;
using var tx = cn.BeginTransaction();
var tables = new List<string>();
using (var c = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", cn, tx))
using (var r = c.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));

foreach (var table in tables)
{
    var cols = new List<string>();
    using (var c = new SQLiteCommand($"PRAGMA table_info(`{table}`)", cn, tx))
    using (var r = c.ExecuteReader()) while (r.Read()) cols.Add(r.GetString(1));
    var rows = new List<(long id, Dictionary<string, string?> values)>();
    using (var c = new SQLiteCommand($"SELECT rowid, * FROM `{table}`", cn, tx))
    using (var r = c.ExecuteReader())
        while (r.Read())
        {
            var d = new Dictionary<string, string?>();
            foreach (var col in cols) d[col] = r[col] as string;
            rows.Add((r.GetInt64(0), d));
        }
    int tableChanged = 0;
    foreach (var (id, values) in rows)
    {
        foreach (var col in cols)
        {
            var v = values[col];
            if (string.IsNullOrEmpty(v)) continue;
            var nv = ConvertValue(v);
            // connessioni e credenziali: si cifra la riga intera (nomi, indirizzi, utenti), come fa ora l'app
            if ((table == "Servers" || table == "Credentials") && col == "Json" && !DataVault.IsVaultText(nv)) { nv = DataVault.SimpleEncrypt(nv); wholeRows++; }
            // archivio chiave/valore: prefisso nuovo delle chiavi (prima "1rm.")
            if (table == "Configs" && col == "Key" && nv.StartsWith("1rm.")) { nv = "lm." + nv.Substring(4); renamedKeys++; }
            if (nv == v) continue;
            using var u = new SQLiteCommand($"UPDATE `{table}` SET `{col}` = @v WHERE rowid = @id", cn, tx);
            u.Parameters.AddWithValue("@v", nv); u.Parameters.AddWithValue("@id", id);
            u.ExecuteNonQuery();
            changedCells++; tableChanged++;
        }
    }
    Console.WriteLine($"  {table}: {rows.Count} righe, {tableChanged} valori cambiati");
}

using (var c = new SQLiteCommand("INSERT OR REPLACE INTO `Configs` (`Key`, `Value`) VALUES (@k, @v)", cn, tx))
{
    c.Parameters.AddWithValue("@k", DataVault.HeaderConfigKey); c.Parameters.AddWithValue("@v", DataVault.HeaderToJson(header));
    c.ExecuteNonQuery();
}

// controllo: nessun valore deve piu' aprirsi con la vecchia chiave
int leftovers = 0;
foreach (var table in tables)
{
    using var c = new SQLiteCommand($"SELECT * FROM `{table}`", cn, tx);
    using var r = c.ExecuteReader();
    while (r.Read()) for (int i = 0; i < r.FieldCount; i++) if (r[i] is string s && CountOld(s) > 0) leftovers++;
}
Console.WriteLine($"Valori vecchi convertiti: {oldValues}; righe cifrate per intero: {wholeRows}; chiavi rinominate: {renamedKeys}; celle cambiate: {changedCells}; resti col vecchio formato: {leftovers}");
if (dryRun || leftovers > 0) { tx.Rollback(); Console.WriteLine(dryRun ? "Prova: nulla e' stato scritto." : "Restano valori vecchi: annullo tutto, il database e' com'era."); return leftovers > 0 ? 4 : 0; }
tx.Commit();
Console.WriteLine("Fatto: il database usa la cassaforte nuova. Al primo avvio Lemora chiede la password principale e propone Windows Hello.");
return 0;

// ---------------------------------------------------------------- conversione

string ConvertValue(string s)
{
    var old = OldDecrypt(s);
    if (old != null) { oldValues++; return DataVault.SimpleEncrypt(ConvertValue(old)); }   // il testo vecchio puo' contenere a sua volta JSON con valori cifrati
    var t = s.TrimStart();
    if (t.StartsWith("{") || t.StartsWith("["))
    {
        JToken tok;
        try { tok = JToken.ReadFrom(new JsonTextReader(new StringReader(s)) { DateParseHandling = DateParseHandling.None }); }
        catch { return s; }
        bool changed = false;
        foreach (var v in tok.SelectTokens("..*").OfType<JValue>().Where(x => x.Type == JTokenType.String).ToList())
        {
            var str = (string)v.Value!;
            var n = ConvertValue(str);
            if (n != str) { v.Value = n; changed = true; }
        }
        return changed ? tok.ToString(Formatting.None) : s;
    }
    return s;
}

int CountOld(string s)
{
    if (OldDecrypt(s) != null) return 1;
    if (DataVault.IsVaultText(s)) { var p = DataVault.SimpleDecrypt(s); return p == null ? 0 : CountOld(p); }
    var t = s.TrimStart();
    if (!(t.StartsWith("{") || t.StartsWith("["))) return 0;
    try
    {
        var tok = JToken.ReadFrom(new JsonTextReader(new StringReader(s)) { DateParseHandling = DateParseHandling.None });
        return tok.SelectTokens("..*").OfType<JValue>().Where(x => x.Type == JTokenType.String).Sum(x => CountOld((string)x.Value!));
    }
    catch { return 0; }
}

static string? OldDecrypt(string s)
{
    if (s.Length < 16 || DataVault.IsVaultText(s)) return null;
    try { var r = _1Remote.Security.SimpleStringEncipher.Decrypt(s); return r.IsSuccess ? r.PlainText : null; }
    catch { return null; }
}

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(true);
        if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(k.KeyChar);
    }
}
