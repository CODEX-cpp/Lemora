using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Console administrators: all with the same permissions, each can create others and delete anyone except themselves
/// and except the main one (the first, created by the setup wizard).
/// The last one is never deleted. There used to be a single administrator (table <c>admin</c>, row 1): on first open it becomes the first in the list.
/// </summary>
internal static partial class Admins
{
    public record Info(string User, string? CreatedAt, string? CreatedBy, bool Main);

    // session -> user (sessions live in State.Sessions); used for "(you)" and to close the sessions of whoever gets deleted
    // ponytail: entries of expired sessions stay until the server restarts (a few rows in memory)
    public static readonly ConcurrentDictionary<string, string> SessionUser = new();
    private static readonly Lock Gate = new(); // create/delete one at a time: "never the last one" holds even with two concurrent requests

    private const int MinPassword = 6; // same rule as the setup wizard

    [GeneratedRegex(@"^[\p{L}\p{N}._@-]{1,64}$")]
    private static partial Regex UserRule();

    private static SqliteConnection Open(string dbDir)
    {
        Directory.CreateDirectory(dbDir);
        var c = new SqliteConnection($"Data Source={Path.Combine(dbDir, "server.db")};Pooling=False"); // no pooled connections: if the disk or network goes away and comes back, the next request recovers on its own
        c.Open();
        Migrate(c, dbDir);
        return c;
    }

    private static SqliteConnection Open() => Open(State.Config.DbDir);

    /// <summary>creates the table and imports the single administrator of the old format, without touching the password</summary>
    private static void Migrate(SqliteConnection c, string dbDir)
    {
        using var tx = c.BeginTransaction();
        Pcs.Cmd(c, """
            PRAGMA busy_timeout = 5000;
            PRAGMA secure_delete = ON;
            CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, pc_id TEXT, kind TEXT NOT NULL, detail TEXT);
            CREATE TABLE IF NOT EXISTS admins (user TEXT NOT NULL UNIQUE COLLATE NOCASE, hash TEXT NOT NULL, created_at TEXT NOT NULL, created_by TEXT, main INTEGER NOT NULL DEFAULT 0);
            """).ExecuteNonQuery();
        Pcs.AddEventsAdmin(c);   // Pcs.Log also writes the admin column
        using (var old = Pcs.Cmd(c, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'admin'"))
        {
            if (old.ExecuteScalar() != null)
            {
                // the real date is missing: use the database file's date, the closest to the setup wizard
                var when = File.GetCreationTimeUtc(Path.Combine(dbDir, "server.db")).ToString("o");
                Pcs.Cmd(c, "INSERT OR IGNORE INTO admins (user, hash, created_at, created_by, main) SELECT user, hash, $w, NULL, 1 FROM admin; DROP TABLE admin;", ("$w", when)).ExecuteNonQuery();
                Pcs.Log(c, null, "admins-migrated");
            }
        }
        tx.Commit();
    }

    /// <summary>setup wizard: first administrator (if rerun on an existing database, that same name gets the new password)</summary>
    public static void CreateFirst(string dbDir, string user, string password)
    {
        lock (Gate)
        {
            using var c = Open(dbDir);
            Pcs.Cmd(c, "INSERT INTO admins (user, hash, created_at, created_by, main) VALUES ($u, $h, $a, NULL, 1) ON CONFLICT(user) DO UPDATE SET hash = excluded.hash, main = 1",
                ("$u", user), ("$h", Admin.Hash(password)), ("$a", Pcs.Now())).ExecuteNonQuery();
            Pcs.Log(c, null, "admin-created", $"{user} (setup wizard)");
        }
    }

    /// <summary>checks user and password; returns the name as stored. Timing does not reveal whether the user exists</summary>
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

    /// <summary>null = created; otherwise the error code for the console</summary>
    public static string? Create(string by, string? user, string? password)
    {
        user = user?.Trim() ?? "";
        if (!UserRule().IsMatch(user)) return "user";
        if ((password ?? "").Length < MinPassword) return "password";
        lock (Gate)
        {
            using var c = Open();
            // the creator must still be an administrator (a session left over from an in-flight login is not enough)
            if (!List().Any(a => string.Equals(a.User, by, StringComparison.OrdinalIgnoreCase))) return "forbidden";
            if (List().Any(a => string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase))) return "exists";
            Pcs.Cmd(c, "INSERT INTO admins (user, hash, created_at, created_by) VALUES ($u, $h, $a, $b)",
                ("$u", user), ("$h", Admin.Hash(password!)), ("$a", Pcs.Now()), ("$b", by)).ExecuteNonQuery();
            Pcs.Log(c, null, "admin-created", $"{user} by {by}");
        }
        return null;
    }

    /// <summary>null = deleted; "self" = oneself, "main" = the main one (created by the setup wizard), "last" = the last one, "notfound"</summary>
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
            Pcs.Log(c, null, "admin-deleted", $"{target.User} by {by}");
            // a deleted administrator is logged out of the console right away
            foreach (var (token, u) in SessionUser)
                if (string.Equals(u, target.User, StringComparison.OrdinalIgnoreCase)) { State.Sessions.TryRemove(token, out _); SessionUser.TryRemove(token, out _); }
        }
        return null;
    }

    /// <summary>
    /// opens the console session only if the administrator still exists, under the same lock as Delete: an administrator deleted
    /// while their login was in flight (password already checked) gets no session
    /// </summary>
    public static string? StartSession(string user, TimeSpan life)
    {
        lock (Gate)
        {
            if (!List().Any(a => string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase))) return null;
            var token = State.NewToken();
            SessionUser[token] = user;
            State.Sessions[token] = DateTime.UtcNow.Add(life);
            return token;
        }
    }

    /// <summary>the session's user, or null if not signed in</summary>
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
            return err == null ? Results.Json(new { ok = true }) : Results.Json(new { error = err }, statusCode: err == "exists" ? 409 : err == "forbidden" ? 403 : 400);
        });

        api.MapPost("/admins/{user}/delete", (HttpContext c, string user) =>
        {
            var me = Current(c);
            if (me == null) return Results.StatusCode(401);
            return Delete(me, user) switch
            {
                null => Results.Json(new { ok = true }),
                "self" => Results.Json(new { error = "self", message = "You cannot delete yourself: ask another administrator." }, statusCode: 403),
                "main" => Results.Json(new { error = "main", message = "The main administrator cannot be deleted." }, statusCode: 403),
                "last" => Results.Json(new { error = "last", message = "This is the last administrator: it cannot be deleted." }, statusCode: 409),
                _ => Results.Json(new { error = "notfound" }, statusCode: 404),
            };
        });
    }
}
