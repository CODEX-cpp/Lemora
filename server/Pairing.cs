using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>PCs paired with the server: one-time code, PC request, administrator approval, per-PC key (revocable)</summary>
internal static class Pcs
{
    private static string DbFile => Path.Combine(State.Config.DbDir, "server.db");

    internal static SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={DbFile};Pooling=False"); // see Admins.Open
        c.Open();
        using var cmd = c.CreateCommand();
        // secure_delete is per connection: set it on each one, or removed passwords (emptied trash, decided conflicts, changes) stay in the file's free space
        cmd.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA secure_delete = ON;
            CREATE TABLE IF NOT EXISTS pairing (id INTEGER PRIMARY KEY CHECK (id = 1), code TEXT NOT NULL, expires TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS pcs (
                id TEXT PRIMARY KEY, pc TEXT NOT NULL, user TEXT NOT NULL, app_version TEXT, address TEXT,
                state TEXT NOT NULL, permission TEXT NOT NULL DEFAULT 'read', no_copy INTEGER NOT NULL DEFAULT 0,
                token_hash TEXT NOT NULL, key_hash TEXT, key_plain TEXT,
                requested_at TEXT NOT NULL, decided_at TEXT, last_seen TEXT);
            CREATE TABLE IF NOT EXISTS pc_caps (pc_id TEXT PRIMARY KEY, caps TEXT NOT NULL, rev INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, pc_id TEXT, kind TEXT NOT NULL, detail TEXT);
            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS connections (
                id TEXT PRIMARY KEY, folder TEXT NOT NULL DEFAULT '', name TEXT NOT NULL, protocol TEXT NOT NULL, host TEXT NOT NULL, port INTEGER NOT NULL,
                user TEXT NOT NULL DEFAULT '', pw_enc TEXT, tags TEXT NOT NULL DEFAULT '', version INTEGER NOT NULL DEFAULT 1, disabled INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT, rev INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS connections_rev ON connections (rev);
            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY, folder TEXT NOT NULL DEFAULT '', name TEXT NOT NULL, ip TEXT NOT NULL DEFAULT '', users_enc TEXT,
                version INTEGER NOT NULL DEFAULT 1, rev INTEGER NOT NULL, deleted_at TEXT, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS devices_rev ON devices (rev);
            CREATE TABLE IF NOT EXISTS folders (
                path TEXT PRIMARY KEY COLLATE NOCASE, domain TEXT NOT NULL DEFAULT '', ip_prefix TEXT NOT NULL DEFAULT '',
                version INTEGER NOT NULL DEFAULT 1, rev INTEGER NOT NULL, deleted_at TEXT, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS folders_rev ON folders (rev);
            CREATE TABLE IF NOT EXISTS fcreds (
                id TEXT PRIMARY KEY, folder TEXT NOT NULL DEFAULT '', user TEXT NOT NULL, pw_enc TEXT,
                version INTEGER NOT NULL DEFAULT 1, rev INTEGER NOT NULL, deleted_at TEXT, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS fcreds_rev ON fcreds (rev);
            CREATE TABLE IF NOT EXISTS requests (
                id TEXT PRIMARY KEY, pc_id TEXT NOT NULL, kind TEXT NOT NULL, conn_id TEXT, base_version INTEGER,
                folder TEXT, name TEXT, protocol TEXT, host TEXT, port INTEGER, user TEXT, pw_enc TEXT, tags TEXT,
                state TEXT NOT NULL DEFAULT 'pending', note TEXT, created_at TEXT NOT NULL, decided_at TEXT);
            CREATE TABLE IF NOT EXISTS personal (
                id TEXT PRIMARY KEY, pc_id TEXT NOT NULL, local_id TEXT NOT NULL, folder TEXT NOT NULL DEFAULT '', name TEXT NOT NULL, protocol TEXT NOT NULL, host TEXT NOT NULL,
                port INTEGER NOT NULL, user TEXT NOT NULL DEFAULT '', pw_enc TEXT, tags TEXT NOT NULL DEFAULT '', version INTEGER NOT NULL DEFAULT 1,
                state TEXT NOT NULL DEFAULT 'pending', note TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, decided_at TEXT, conn_id TEXT, conn_version INTEGER,
                UNIQUE (pc_id, local_id));
            CREATE INDEX IF NOT EXISTS personal_conn ON personal (conn_id);
            CREATE TABLE IF NOT EXISTS personal_rejected (local_id TEXT NOT NULL, hash TEXT NOT NULL, note TEXT, decided_at TEXT NOT NULL, PRIMARY KEY (local_id, hash));
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT NOT NULL, pc_id TEXT NOT NULL, conn_id TEXT NOT NULL, protocol TEXT NOT NULL,
                started_at TEXT NOT NULL, ended_at TEXT, PRIMARY KEY (pc_id, id));
            CREATE INDEX IF NOT EXISTS sessions_conn ON sessions (conn_id);
            """;
        cmd.ExecuteNonQuery();
        AddShareColumns(c);
        return c;
    }

    private static string? _colsDone;

    /// <summary>columns added later: sync state chosen by the PC (on | off | unknown) and when it changed. Only once per database file</summary>
    private static void AddShareColumns(SqliteConnection c)
    {
        if (_colsDone == DbFile) return;
        bool has = false;
        using (var r = Cmd(c, "SELECT 1 FROM pragma_table_info('pcs') WHERE name = 'share_state'").ExecuteReader()) has = r.Read();
        if (!has)
        {
            try
            {
                Cmd(c, "ALTER TABLE pcs ADD COLUMN share_state TEXT NOT NULL DEFAULT 'unknown'").ExecuteNonQuery();
                Cmd(c, "ALTER TABLE pcs ADD COLUMN share_changed_at TEXT").ExecuteNonQuery();
            }
            catch (SqliteException) { } // another process/request has just added it
        }
        // the entry's binding to folder credentials (JSON), in shared entries and in requests
        // (requests.old_json: what the entry looked like at decision time, to review the request in the history)
        foreach (var (t, col) in new[] { ("connections", "ucfg"), ("requests", "ucfg"), ("requests", "old_json"), ("connections", "users_enc"), ("requests", "users_enc"), ("sessions", "label"), ("sessions", "host"), ("sessions", "folder"), ("sessions", "adhoc"),
            // trash: who deleted the entry ("A:admin" | "P:PC - user" | "R:PC - user|admin") and, if it was later permanently deleted, when and by whom (the row stays as history)
            ("connections", "deleted_by"), ("connections", "purged_at"), ("connections", "purged_by"),
            // event log: which administrator performed the action from the console (empty for PCs)
            ("events", "admin"),
            // PC key: fingerprint of the key handed out before the last one (valid until one of the two is used) and "0" = delivery not yet confirmed
            ("pcs", "key_hash2"), ("pcs", "key_acked"),
            // Lemora's MCP server allowed on the PC: NULL = yes (PCs paired before this column keep it), "0" = turned off by the administrator
            ("pcs", "mcp"),
            // other addresses of a password manager device (JSON list), besides ip
            ("devices", "more_ips") })
        {
            bool hasU = false;
            using (var r = Cmd(c, $"SELECT 1 FROM pragma_table_info('{t}') WHERE name = '{col}'").ExecuteReader()) hasU = r.Read();
            if (!hasU)
            {
                try { Cmd(c, $"ALTER TABLE {t} ADD COLUMN {col} TEXT").ExecuteNonQuery(); }
                catch (SqliteException) { } // added at that moment by another request
            }
        }
        // older databases: plaintext keys awaiting confirmation -> removed.
        // A key that has been in plaintext in the file is compromised (copies and backups of server.db): its fingerprint is removed too and no longer authenticates.
        // "0" makes a new key be handed out: the PC collects it with the pairing secret (even if it had already saved the old one)
        try
        {
            Cmd(c, "PRAGMA secure_delete = ON").ExecuteNonQuery();   // zeroes removed bytes: the key does not stay in the file's free space
            Cmd(c, "UPDATE pcs SET key_acked = '0', key_hash = NULL, key_hash2 = NULL, key_plain = NULL WHERE key_plain IS NOT NULL AND state = 'approved'").ExecuteNonQuery();
            Cmd(c, "UPDATE pcs SET key_plain = NULL WHERE key_plain IS NOT NULL").ExecuteNonQuery();
        }
        catch (SqliteException) { /* database busy: retried at the next open */ }
        // only once: VACUUM rewrites the file and removes what versions without secure_delete left in the free space
        // (deleted passwords, older plaintext PC keys), in the free pages of server.db
        try
        {
            if (Cmd(c, "SELECT v FROM meta WHERE k = 'secure_vacuum'").ExecuteScalar() == null)
            {
                Cmd(c, "VACUUM").ExecuteNonQuery();
                Cmd(c, "INSERT OR REPLACE INTO meta (k, v) VALUES ('secure_vacuum', 1)").ExecuteNonQuery();
            }
        }
        catch (SqliteException) { /* busy: retried at the next start */ }
        _colsDone = DbFile;
    }

    /// <summary>records what the PC chose (on | off). When it switches from on to off or vice versa it writes the 'sync-setting' event; the first time (unknown) only the state</summary>
    public static void SetShareState(SqliteConnection c, PcRow pc, string state)
    {
        string old = "unknown";
        using (var r = Cmd(c, "SELECT share_state FROM pcs WHERE id = $i", ("$i", pc.Id)).ExecuteReader()) if (r.Read()) old = r.GetString(0);
        if (old == state) return;
        Cmd(c, "UPDATE pcs SET share_state = $s, share_changed_at = $d WHERE id = $i", ("$s", state), ("$d", Now()), ("$i", pc.Id)).ExecuteNonQuery();
        if (old != "unknown") Log(c, pc.Id, "sync-setting", state == "off" ? "Sync turned off by the PC" : "Sync turned back on by the PC");
    }

    internal static string Now() => DateTime.UtcNow.ToString("o");
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    /// <summary>administrator of the current console request (set by Api.Map for every request with a session); null for PCs and background work</summary>
    public static readonly AsyncLocal<string?> CurrentAdmin = new();

    /// <param name="admin">who performed the action from the console; if missing, the one of the current request (CurrentAdmin)</param>
    public static void Log(SqliteConnection c, string? pcId, string kind, string? detail = null, string? admin = null) =>
        Cmd(c, "INSERT INTO events (at, pc_id, kind, detail, admin) VALUES ($a, $p, $k, $d, $u)", ("$a", Now()), ("$p", pcId), ("$k", kind), ("$d", detail), ("$u", admin ?? CurrentAdmin.Value)).ExecuteNonQuery();

    /// <summary>events.admin column (added later): called on every database open, including the administrators' one</summary>
    internal static void AddEventsAdmin(SqliteConnection c)
    {
        bool has;
        using (var r = Cmd(c, "SELECT 1 FROM pragma_table_info('events') WHERE name = 'admin'").ExecuteReader()) has = r.Read();
        if (has) return;
        try { Cmd(c, "ALTER TABLE events ADD COLUMN admin TEXT").ExecuteNonQuery(); }
        catch (SqliteException) { }   // added at that moment by another request
    }

    // ---- pairing code: only one active at a time, 30 minutes, single use ----

    public record CodeInfo(string Code, DateTime Expires);

    public static CodeInfo NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // without 0/O, 1/I
        var code = new string(RandomNumberGenerator.GetItems<char>(alphabet, 6));
        var exp = DateTime.UtcNow.AddMinutes(30);
        using var c = Open();
        Cmd(c, "INSERT OR REPLACE INTO pairing (id, code, expires, used) VALUES (1, $c, $e, 0)", ("$c", code), ("$e", exp.ToString("o"))).ExecuteNonQuery();
        Log(c, null, "code-created");
        return new(code, exp);
    }

    public static CodeInfo? CurrentCode()
    {
        using var c = Open();
        using var r = Cmd(c, "SELECT code, expires FROM pairing WHERE id = 1 AND used = 0").ExecuteReader();
        if (!r.Read()) return null;
        var exp = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
        return exp > DateTime.UtcNow ? new(r.GetString(0), exp) : null;
    }

    /// <summary>true if the code is correct, valid and unused; in that case it consumes it (one PC per code)</summary>
    private static bool ConsumeCode(SqliteConnection c, string? given)
    {
        var norm = (given ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        using var r = Cmd(c, "SELECT code, expires FROM pairing WHERE id = 1 AND used = 0").ExecuteReader();
        string want = ""; bool live = false;
        if (r.Read())
        {
            want = r.GetString(0);
            live = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind) > DateTime.UtcNow;
        }
        r.Close();
        bool same = want.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(norm), Encoding.UTF8.GetBytes(want));
        if (!same || !live) return false;
        // the conditional UPDATE makes consumption atomic even with two concurrent requests
        return Cmd(c, "UPDATE pairing SET used = 1 WHERE id = 1 AND used = 0").ExecuteNonQuery() == 1;
    }

    // ---- PC request ----

    public record Request(string? Code, string? Pc, string? User, string? AppVersion);

    /// <summary>null if the code is not valid</summary>
    public static (string id, string token)? CreateRequest(Request r, string address)
    {
        using var c = Open();
        if (!ConsumeCode(c, r.Code)) return null;
        var id = Guid.NewGuid().ToString("N");
        var token = Secret();
        Cmd(c, "INSERT INTO pcs (id, pc, user, app_version, address, state, token_hash, requested_at) VALUES ($i, $pc, $u, $v, $a, 'pending', $t, $n)",
            ("$i", id), ("$pc", r.Pc!.Trim()), ("$u", r.User!.Trim()), ("$v", r.AppVersion), ("$a", address), ("$t", Sha(token)), ("$n", Now())).ExecuteNonQuery();
        Log(c, id, "pair-request", $"{r.Pc!.Trim()} - {r.User!.Trim()} from {address}");
        return (id, token);
    }

    public record Status(string State, string? Key, string? Permission, bool NoCopy, bool Mcp = true);

    /// <summary>the MCP rule as the app reads it: off = MCP server and its settings page unavailable on the PC</summary>
    public static object McpRule(bool mcp) => new { enabled = mcp, allServers = true, servers = Array.Empty<string>() };

    /// <summary>
    /// Request state. Approved and delivery not yet confirmed: a NEW key is created and handed out (only its fingerprint goes into the database).
    /// The key never stays in plaintext in the database. Two collections in a row: the last two keys are valid
    /// until one is used (Authenticate). Confirmation (ack) or first use: no more keys.
    /// </summary>
    public static Status? CheckStatus(string? id, string? token, bool ack)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) return null;
        using var c = Open();
        string state, permission, tokenHash; bool noCopy, mcp; string? acked;
        using (var r = Cmd(c, "SELECT state, permission, no_copy, token_hash, key_acked, mcp FROM pcs WHERE id = $i", ("$i", id)).ExecuteReader())
        {
            if (!r.Read()) return null;
            state = r.GetString(0); permission = r.GetString(1); noCopy = r.GetInt32(2) == 1; tokenHash = r.GetString(3); acked = r.IsDBNull(4) ? null : r.GetString(4);
            mcp = r.IsDBNull(5) || r.GetString(5) != "0";
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sha(token)), Encoding.UTF8.GetBytes(tokenHash))) return null;
        if (state != "approved") return new(state, null, null, false);
        if (acked != "0") return new(state, null, permission, noCopy, mcp);   // already confirmed (or PC from before this version): no new key
        if (ack)
        {
            Cmd(c, "UPDATE pcs SET key_acked = '1' WHERE id = $i", ("$i", id)).ExecuteNonQuery();
            Log(c, id, "key-acked");
            return new(state, null, permission, noCopy, mcp);
        }
        var key = Secret();
        if (Cmd(c, "UPDATE pcs SET key_hash2 = key_hash, key_hash = $h WHERE id = $i AND state = 'approved' AND key_acked = '0'", ("$h", Sha(key)), ("$i", id)).ExecuteNonQuery() != 1)
            return new(state, null, permission, noCopy, mcp);
        return new(state, $"{id}.{key}", permission, noCopy, mcp);
    }

    /// <summary>the PC withdraws its still pending request ("Annulla" button): it disappears from the console. false if missing, the secret does not match or it is no longer pending</summary>
    public static bool Cancel(string? id, string? token)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) return false;
        using var c = Open();
        string? state, tokenHash, pc;
        using (var r = Cmd(c, "SELECT state, token_hash, pc FROM pcs WHERE id = $i", ("$i", id)).ExecuteReader())
        {
            if (!r.Read()) return false;
            state = r.GetString(0); tokenHash = r.GetString(1); pc = r.GetString(2);
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sha(token)), Encoding.UTF8.GetBytes(tokenHash)) || state != "pending") return false;
        if (Cmd(c, "DELETE FROM pcs WHERE id = $i AND state = 'pending'", ("$i", id)).ExecuteNonQuery() != 1) return false;
        Log(c, null, "pair-cancelled", pc);
        return true;
    }

    // ---- administrator ----

    public record PcRow(string Id, string Pc, string User, string State, string Permission, bool NoCopy, string? AppVersion, string? Address, string RequestedAt, string? DecidedAt, string? LastSeen, string ShareState, string? ShareChangedAt, bool Mcp = true);

    public static List<PcRow> List()
    {
        using var c = Open();
        using var r = Cmd(c, "SELECT id, pc, user, state, permission, no_copy, app_version, address, requested_at, decided_at, last_seen, share_state, share_changed_at, mcp FROM pcs ORDER BY requested_at DESC").ExecuteReader();
        var list = new List<PcRow>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) == 1,
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) || r.GetString(13) != "0"));
        return list;
    }

    /// <summary>still pending requests, proposals and conflicts of a revoked PC: rejected, without passwords</summary>
    internal static void CloseLeftovers(SqliteConnection c, string pcId)
    {
        var p = new (string, object?)[] { ("$p", pcId), ("$d", Now()) };
        Cmd(c, "UPDATE requests SET state='rejected', note='PC revoked', pw_enc=NULL, decided_at=$d WHERE pc_id=$p AND state='pending'", p).ExecuteNonQuery();
        Cmd(c, "UPDATE personal SET state='rejected', note='PC revoked', pw_enc=NULL, decided_at=$d WHERE pc_id=$p AND state='pending'", p).ExecuteNonQuery();
        try { Cmd(c, "UPDATE conflicts SET state='kept-server', pw_enc=NULL, decided_at=$d WHERE pc_id=$p AND state='pending'", p).ExecuteNonQuery(); }
        catch (SqliteException) { /* conflicts table not created yet: nothing pending */ }
    }

    /// <summary>approve | reject | revoke | update. false if the PC does not exist or its state does not allow it</summary>
    public static bool Decide(string id, string action, string? permission, bool noCopy, bool mcp = true)
    {
        var mcpVal = mcp ? null : "0";
        using var c = Open();
        string? state;
        using (var r = Cmd(c, "SELECT state FROM pcs WHERE id = $i", ("$i", id)).ExecuteReader()) state = r.Read() ? r.GetString(0) : null;
        if (state == null) return false;
        var perm = permission is "write" or "requests" ? permission : "read"; // requests = edit and delete only through a request to be approved (Requests.cs)
        int n;
        switch (action)
        {
            case "approve" when state == "pending":
                // no key now: CheckStatus creates and hands it out when the PC collects it (so it never stays in plaintext in the database)
                n = Cmd(c, "UPDATE pcs SET state='approved', permission=$p, no_copy=$n, mcp=$m, key_hash=NULL, key_hash2=NULL, key_plain=NULL, key_acked='0', decided_at=$d WHERE id=$i",
                    ("$p", perm), ("$n", noCopy ? 1 : 0), ("$m", mcpVal), ("$d", Now()), ("$i", id)).ExecuteNonQuery();
                break;
            case "reject" when state == "pending":
                n = Cmd(c, "UPDATE pcs SET state='rejected', decided_at=$d WHERE id=$i", ("$d", Now()), ("$i", id)).ExecuteNonQuery();
                break;
            case "revoke" when state == "approved":
                // the key stops working immediately: the PC notices at its next contact
                n = Cmd(c, "UPDATE pcs SET state='revoked', key_hash=NULL, key_hash2=NULL, key_plain=NULL, decided_at=$d WHERE id=$i", ("$d", Now()), ("$i", id)).ExecuteNonQuery();
                // whatever the PC left pending can no longer be approved: a PC revoked as suspicious does not write through the
                // administrator's hand
                if (n == 1) CloseLeftovers(c, id);
                break;
            case "update" when state == "approved":
                n = Cmd(c, "UPDATE pcs SET permission=$p, no_copy=$n, mcp=$m WHERE id=$i", ("$p", perm), ("$n", noCopy ? 1 : 0), ("$m", mcpVal), ("$i", id)).ExecuteNonQuery();
                break;
            default: return false;
        }
        if (n == 1) Log(c, id, action, action is "approve" or "update" ? $"permission {perm}{(noCopy ? ", do not copy saved connections" : "")}{(mcp ? "" : ", MCP server off")}" : null);
        return n == 1;
    }

    // ---- the PC presents its key ----

    /// <summary>key in the "id.key" format; returns the PC if the key is still valid and updates its last contact</summary>
    public static PcRow? Authenticate(string? bearer)
    {
        var p = (bearer ?? "").Split('.');
        if (p.Length != 2 || p[0].Length != 32) return null;
        using var c = Open();
        string? hash, hash2, acked;
        using (var r = Cmd(c, "SELECT key_hash, key_hash2, key_acked FROM pcs WHERE id = $i AND state = 'approved'", ("$i", p[0])).ExecuteReader())
        {
            var found = r.Read();
            hash = found && !r.IsDBNull(0) ? r.GetString(0) : null;
            hash2 = found && !r.IsDBNull(1) ? r.GetString(1) : null;
            acked = found && !r.IsDBNull(2) ? r.GetString(2) : null;
        }
        // the hash is computed even for a nonexistent key: timing does not tell whether the id exists
        var given = Encoding.UTF8.GetBytes(Sha(p[1]));
        bool ok1 = CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(hash ?? new string('0', 64))) && hash != null;
        bool ok2 = CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(hash2 ?? new string('0', 64))) && hash2 != null;
        if (!ok1 && !ok2) return null;
        // first use of a handed-out key: it is the one the PC saved. Only that one stays, no more new keys (even without the PC's confirmation)
        if (ok2 || hash2 != null || acked == "0")
            Cmd(c, "UPDATE pcs SET key_hash = $h, key_hash2 = NULL, key_acked = '1' WHERE id = $i", ("$h", ok1 ? hash : hash2), ("$i", p[0])).ExecuteNonQuery();
        Cmd(c, "UPDATE pcs SET last_seen = $n WHERE id = $i", ("$n", Now()), ("$i", p[0])).ExecuteNonQuery();
        return List().FirstOrDefault(x => x.Id == p[0]);
    }

    /// <summary>a revoked key must be recognized as such to tell the PC to stop</summary>
    public static bool IsRevoked(string? bearer)
    {
        var p = (bearer ?? "").Split('.');
        if (p.Length != 2 || p[0].Length != 32) return false;
        using var c = Open();
        using var r = Cmd(c, "SELECT 1 FROM pcs WHERE id = $i AND state = 'revoked'", ("$i", p[0])).ExecuteReader();
        return r.Read();
    }
}

internal static class PairingApi
{
    private static readonly AttemptLimiter CodeLimiter = new(5, TimeSpan.FromMinutes(5));      // per address
    private static readonly AttemptLimiter AllCodesLimiter = new(30, TimeSpan.FromMinutes(5)); // in total: changing address does not bypass the limit
    private record ApprovalReq(string? Permission, bool NoCopy, bool? Mcp);
    private record StatusReq(string? Id, string? Token, bool Ack);

    /// <summary>only PCs on the local network: the server does not answer others even if someone managed to reach it</summary>
    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    private static bool Ok(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);

    public static void Map(RouteGroupBuilder api)
    {
        // ---- administrator (signed in to the console) ----
        api.MapPost("/pairing/new", (HttpContext c) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            var code = Pcs.NewCode();
            return Results.Json(new { code = code.Code, expires = code.Expires });
        });

        api.MapGet("/pairing/current", (HttpContext c) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            var code = Pcs.CurrentCode();
            return Results.Json(code == null ? new { code = (string?)null, expires = (DateTime?)null } : new { code = (string?)code.Code, expires = (DateTime?)code.Expires });
        });

        api.MapGet("/pcs", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Pcs.List()));

        api.MapPost("/pcs/{id}/{action}", (HttpContext c, string id, string action, ApprovalReq? r) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (action is not ("approve" or "reject" or "revoke" or "update")) return Results.NotFound();
            return Pcs.Decide(id, action, r?.Permission, r?.NoCopy ?? false, r?.Mcp ?? true) ? Results.Json(new { ok = true }) : Results.Json(new { error = "state" }, statusCode: 409);
        });

        // ---- PC asking to link ----
        api.MapPost("/pair/request", (HttpContext c, Pcs.Request r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var ip = Api.Ip(c);
            if (!Ok(r.Pc, 64) || !Ok(r.User, 64) || (r.AppVersion?.Length ?? 0) > 32) return Results.Json(new { error = "input" }, statusCode: 400);
            // attempts counted before the code check
            int left = CodeLimiter.Take(ip, out var until);
            if (left < 0) return Results.Json(new { error = "blocked", until }, statusCode: 429);
            if (AllCodesLimiter.Take("*", out var untilAll) < 0) return Results.Json(new { error = "blocked", until = untilAll }, statusCode: 429);
            var res = Pcs.CreateRequest(r, ip);
            if (res == null)
                return left == 0 ? Results.Json(new { error = "blocked", until = DateTime.UtcNow.AddMinutes(5) }, statusCode: 429) : Results.Json(new { error = "code", remaining = left }, statusCode: 401);
            CodeLimiter.Succeeded(ip);
            AllCodesLimiter.Undo("*");
            return Results.Json(new { id = res.Value.id, token = res.Value.token, fingerprint = CertStore.Fingerprint(State.Cert) });
        });

        api.MapPost("/pair/status", (HttpContext c, StatusReq r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var s = Pcs.CheckStatus(r.Id, r.Token, r.Ack);
            return s == null ? Results.StatusCode(404) : Results.Json(new { state = s.State, key = s.Key, permission = Caps.Perm(c, s.Permission), noCopy = s.NoCopy, claude = s.State == "approved" ? Pcs.McpRule(s.Mcp) : null });
        });

        api.MapPost("/pair/cancel", (HttpContext c, StatusReq r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            return Pcs.Cancel(r.Id, r.Token) ? Results.Json(new { ok = true }) : Results.StatusCode(404);
        });

        // ---- already paired PC: key check (the actual syncs come later) ----
        api.MapGet("/client/me", (HttpContext c) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc != null) return Results.Json(new { state = pc.State, permission = Caps.Perm(c, pc.Permission), noCopy = pc.NoCopy, claude = Pcs.McpRule(pc.Mcp) });
            return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
        });
    }
}
