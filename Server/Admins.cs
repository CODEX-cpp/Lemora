using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// amministratori della console: tutti con gli stessi permessi, ognuno puo' crearne altri ed eliminare chiunque tranne se stesso
/// e tranne il principale (il primo, creato nella creazione guidata).
/// Mai si elimina l'ultimo. Prima esisteva un solo amministratore (tabella <c>admin</c>, riga 1): alla prima apertura diventa il primo della lista.
/// </summary>
internal static partial class Admins
{
    public record Info(string User, string? CreatedAt, string? CreatedBy, bool Main);

    // sessione -> utente (le sessioni stanno in State.Sessions); serve per "(tu)" e per chiudere le sessioni di chi viene eliminato
    // ponytail: le voci di sessioni scadute restano finche' il server non riparte (poche righe in memoria)
    public static readonly ConcurrentDictionary<string, string> SessionUser = new();
    private static readonly Lock Gate = new(); // crea/elimina uno alla volta: "mai l'ultimo" resta vero anche con due richieste insieme

    private const int MinPassword = 10; // la stessa regola della creazione guidata

    [GeneratedRegex(@"^[\p{L}\p{N}._@-]{1,64}$")]
    private static partial Regex UserRule();

    private static SqliteConnection Open(string dbDir)
    {
        Directory.CreateDirectory(dbDir);
        var c = new SqliteConnection($"Data Source={Path.Combine(dbDir, "server.db")}");
        c.Open();
        Migrate(c, dbDir);
        return c;
    }

    private static SqliteConnection Open() => Open(State.Config.DbDir);

    /// <summary>crea la tabella e porta dentro l'amministratore unico del vecchio formato, senza toccare la password</summary>
    private static void Migrate(SqliteConnection c, string dbDir)
    {
        using var tx = c.BeginTransaction();
        Pcs.Cmd(c, """
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, pc_id TEXT, kind TEXT NOT NULL, detail TEXT);
            CREATE TABLE IF NOT EXISTS admins (user TEXT NOT NULL UNIQUE COLLATE NOCASE, hash TEXT NOT NULL, created_at TEXT NOT NULL, created_by TEXT, main INTEGER NOT NULL DEFAULT 0);
            """).ExecuteNonQuery();
        using (var old = Pcs.Cmd(c, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'admin'"))
        {
            if (old.ExecuteScalar() != null)
            {
                // la data vera non c'e': si usa quella del file del database, la piu' vicina alla creazione guidata
                var when = File.GetCreationTimeUtc(Path.Combine(dbDir, "server.db")).ToString("o");
                Pcs.Cmd(c, "INSERT OR IGNORE INTO admins (user, hash, created_at, created_by, main) SELECT user, hash, $w, NULL, 1 FROM admin; DROP TABLE admin;", ("$w", when)).ExecuteNonQuery();
                Pcs.Log(c, null, "admins-migrated");
            }
        }
        tx.Commit();
    }

    /// <summary>creazione guidata: primo amministratore (se si rifa' su un database esistente, quello stesso nome prende la nuova password)</summary>
    public static void CreateFirst(string dbDir, string user, string password)
    {
        lock (Gate)
        {
            using var c = Open(dbDir);
            Pcs.Cmd(c, "INSERT INTO admins (user, hash, created_at, created_by, main) VALUES ($u, $h, $a, NULL, 1) ON CONFLICT(user) DO UPDATE SET hash = excluded.hash, main = 1",
                ("$u", user), ("$h", Admin.Hash(password)), ("$a", Pcs.Now())).ExecuteNonQuery();
            Pcs.Log(c, null, "admin-created", $"{user} (creazione guidata)");
        }
    }

    /// <summary>controlla utente e password; restituisce il nome come e' salvato. Il tempo non rivela se l'utente esiste</summary>
    public static string? Check(string user, string password)
    {
        string? dbUser = null, hash = null;
        if (File.Exists(Path.Combine(State.Config.DbDir, "server.db")))
        {
            using var c = Open();
            using var r = Pcs.Cmd(c, "SELECT user, hash FROM admins").ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(0), user, StringComparison.OrdinalIgnoreCase)) { dbUser = r.GetString(0); hash = r.GetString(1); }
        }
        bool ok = Admin.VerifyOrDummy(password, hash);
        return ok && hash != null ? dbUser : null;
    }

    public static List<Info> List()
    {
        using var c = Open();
        using var r = Pcs.Cmd(c, "SELECT user, created_at, created_by, main FROM admins ORDER BY main DESC, created_at, user").ExecuteReader();
        var list = new List<Info>();
        while (r.Read()) list.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) != 0));
        return list;
    }

    /// <summary>null = creato; altrimenti il codice d'errore per la console</summary>
    public static string? Create(string by, string? user, string? password)
    {
        user = user?.Trim() ?? "";
        if (!UserRule().IsMatch(user)) return "user";
        if ((password ?? "").Length < MinPassword) return "password";
        lock (Gate)
        {
            using var c = Open();
            if (List().Any(a => string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase))) return "exists";
            Pcs.Cmd(c, "INSERT INTO admins (user, hash, created_at, created_by) VALUES ($u, $h, $a, $b)",
                ("$u", user), ("$h", Admin.Hash(password!)), ("$a", Pcs.Now()), ("$b", by)).ExecuteNonQuery();
            Pcs.Log(c, null, "admin-created", $"{user} da {by}");
        }
        return null;
    }

    /// <summary>null = eliminato; "self" = se stessi, "main" = il principale (creato nella creazione guidata), "last" = l'ultimo, "notfound"</summary>
    public static string? Delete(string by, string user)
    {
        if (string.Equals(by, user, StringComparison.OrdinalIgnoreCase)) return "self";
        lock (Gate)
        {
            var all = List();
            var target = all.FirstOrDefault(a => string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase));
            if (target == null) return "notfound";
            if (target.Main) return "main";
            if (all.Count <= 1) return "last";
            using var c = Open();
            Pcs.Cmd(c, "DELETE FROM admins WHERE user = $u", ("$u", target.User)).ExecuteNonQuery();
            Pcs.Log(c, null, "admin-deleted", $"{target.User} da {by}");
            // chi viene eliminato esce subito dalla console
            foreach (var (token, u) in SessionUser)
                if (string.Equals(u, target.User, StringComparison.OrdinalIgnoreCase)) { State.Sessions.TryRemove(token, out _); SessionUser.TryRemove(token, out _); }
        }
        return null;
    }

    /// <summary>utente della sessione, o null se non c'e' accesso</summary>
    public static string? Current(HttpContext c) =>
        Api.SignedIn(c) && SessionUser.TryGetValue(c.Request.Cookies["sid"]!, out var u) ? u : null;

    private record NewReq(string? User, string? Password);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/admins", (HttpContext c) =>
        {
            var me = Current(c);
            if (me == null) return Results.StatusCode(401);
            return Results.Json(List().Select(a => new { user = a.User, createdAt = a.CreatedAt, createdBy = a.CreatedBy, main = a.Main, me = string.Equals(a.User, me, StringComparison.OrdinalIgnoreCase) }));
        });

        api.MapPost("/admins", (HttpContext c, NewReq r) =>
        {
            var me = Current(c);
            if (me == null) return Results.StatusCode(401);
            var err = Create(me, r.User, r.Password);
            return err == null ? Results.Json(new { ok = true }) : Results.Json(new { error = err }, statusCode: err == "exists" ? 409 : 400);
        });

        api.MapPost("/admins/{user}/delete", (HttpContext c, string user) =>
        {
            var me = Current(c);
            if (me == null) return Results.StatusCode(401);
            return Delete(me, user) switch
            {
                null => Results.Json(new { ok = true }),
                "self" => Results.Json(new { error = "self", message = "Non puoi eliminare te stesso: chiedi a un altro amministratore." }, statusCode: 403),
                "main" => Results.Json(new { error = "main", message = "L'amministratore principale non si puo' eliminare." }, statusCode: 403),
                "last" => Results.Json(new { error = "last", message = "E' l'ultimo amministratore: non si puo' eliminare." }, statusCode: 409),
                _ => Results.Json(new { error = "notfound" }, statusCode: 404),
            };
        });
    }
}
