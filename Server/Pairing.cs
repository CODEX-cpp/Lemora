using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>PC abbinati al server: codice monouso, richiesta del PC, approvazione dell'amministratore, chiave per PC (revocabile)</summary>
internal static class Pcs
{
    private static string DbFile => Path.Combine(State.Config.DbDir, "server.db");

    internal static SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={DbFile}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout = 5000;
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

    /// <summary>colonne aggiunte dopo: stato della sincronizzazione scelto dal PC (on | off | unknown) e quando e' cambiato. Una sola volta per file di database</summary>
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
            catch (SqliteException) { } // un altro processo/richiesta l'ha appena aggiunta
        }
        _colsDone = DbFile;
    }

    /// <summary>registra cosa ha scelto il PC (on | off). Quando passa da on a off o viceversa scrive l'evento 'sync-setting'; la prima volta (unknown) solo lo stato</summary>
    public static void SetShareState(SqliteConnection c, PcRow pc, string state)
    {
        string old = "unknown";
        using (var r = Cmd(c, "SELECT share_state FROM pcs WHERE id = $i", ("$i", pc.Id)).ExecuteReader()) if (r.Read()) old = r.GetString(0);
        if (old == state) return;
        Cmd(c, "UPDATE pcs SET share_state = $s, share_changed_at = $d WHERE id = $i", ("$s", state), ("$d", Now()), ("$i", pc.Id)).ExecuteNonQuery();
        if (old != "unknown") Log(c, pc.Id, "sync-setting", state == "off" ? "Sincronizzazione disattivata dal PC" : "Sincronizzazione riattivata dal PC");
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

    public static void Log(SqliteConnection c, string? pcId, string kind, string? detail = null) =>
        Cmd(c, "INSERT INTO events (at, pc_id, kind, detail) VALUES ($a, $p, $k, $d)", ("$a", Now()), ("$p", pcId), ("$k", kind), ("$d", detail)).ExecuteNonQuery();

    // ---- codice di abbinamento: uno solo attivo alla volta, 30 minuti, un solo uso ----

    public record CodeInfo(string Code, DateTime Expires);

    public static CodeInfo NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // senza 0/O, 1/I
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

    /// <summary>true se il codice e' giusto, valido e non usato; in quel caso lo consuma (un solo PC per codice)</summary>
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
        // l'UPDATE condizionato rende il consumo atomico anche con due richieste insieme
        return Cmd(c, "UPDATE pairing SET used = 1 WHERE id = 1 AND used = 0").ExecuteNonQuery() == 1;
    }

    // ---- richiesta del PC ----

    public record Request(string? Code, string? Pc, string? User, string? AppVersion);

    /// <summary>null se il codice non e' valido</summary>
    public static (string id, string token)? CreateRequest(Request r, string address)
    {
        using var c = Open();
        if (!ConsumeCode(c, r.Code)) return null;
        var id = Guid.NewGuid().ToString("N");
        var token = Secret();
        Cmd(c, "INSERT INTO pcs (id, pc, user, app_version, address, state, token_hash, requested_at) VALUES ($i, $pc, $u, $v, $a, 'pending', $t, $n)",
            ("$i", id), ("$pc", r.Pc!.Trim()), ("$u", r.User!.Trim()), ("$v", r.AppVersion), ("$a", address), ("$t", Sha(token)), ("$n", Now())).ExecuteNonQuery();
        Log(c, id, "pair-request", $"{r.Pc!.Trim()} - {r.User!.Trim()} da {address}");
        return (id, token);
    }

    public record Status(string State, string? Key, string? Permission, bool NoCopy);

    /// <summary>stato della richiesta; se approvata consegna la chiave finche' il PC non conferma di averla salvata (ack)</summary>
    public static Status? CheckStatus(string? id, string? token, bool ack)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) return null;
        using var c = Open();
        string state, permission, tokenHash; bool noCopy; string? keyPlain;
        using (var r = Cmd(c, "SELECT state, permission, no_copy, token_hash, key_plain FROM pcs WHERE id = $i", ("$i", id)).ExecuteReader())
        {
            if (!r.Read()) return null;
            state = r.GetString(0); permission = r.GetString(1); noCopy = r.GetInt32(2) == 1; tokenHash = r.GetString(3); keyPlain = r.IsDBNull(4) ? null : r.GetString(4);
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sha(token)), Encoding.UTF8.GetBytes(tokenHash))) return null;
        if (state != "approved") return new(state, null, null, false);
        if (ack && keyPlain != null)
        {
            Cmd(c, "UPDATE pcs SET key_plain = NULL WHERE id = $i", ("$i", id)).ExecuteNonQuery();
            Log(c, id, "key-acked");
            keyPlain = null;
        }
        return new(state, keyPlain == null ? null : $"{id}.{keyPlain}", permission, noCopy);
    }

    /// <summary>il PC ritira la richiesta ancora in attesa (pulsante "Annulla"): sparisce dalla console. false se non c'e', il segreto non torna o non e' piu' in attesa</summary>
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

    // ---- amministratore ----

    public record PcRow(string Id, string Pc, string User, string State, string Permission, bool NoCopy, string? AppVersion, string? Address, string RequestedAt, string? DecidedAt, string? LastSeen, string ShareState, string? ShareChangedAt);

    public static List<PcRow> List()
    {
        using var c = Open();
        using var r = Cmd(c, "SELECT id, pc, user, state, permission, no_copy, app_version, address, requested_at, decided_at, last_seen, share_state, share_changed_at FROM pcs ORDER BY requested_at DESC").ExecuteReader();
        var list = new List<PcRow>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) == 1,
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)));
        return list;
    }

    /// <summary>approve | reject | revoke | update. false se il PC non esiste o lo stato non lo permette</summary>
    public static bool Decide(string id, string action, string? permission, bool noCopy)
    {
        using var c = Open();
        string? state;
        using (var r = Cmd(c, "SELECT state FROM pcs WHERE id = $i", ("$i", id)).ExecuteReader()) state = r.Read() ? r.GetString(0) : null;
        if (state == null) return false;
        var perm = permission is "write" or "requests" ? permission : "read"; // requests = modifica ed elimina solo con richiesta da approvare (Requests.cs)
        int n;
        switch (action)
        {
            case "approve" when state == "pending":
                var key = Secret();
                n = Cmd(c, "UPDATE pcs SET state='approved', permission=$p, no_copy=$n, key_hash=$h, key_plain=$k, decided_at=$d WHERE id=$i",
                    ("$p", perm), ("$n", noCopy ? 1 : 0), ("$h", Sha(key)), ("$k", key), ("$d", Now()), ("$i", id)).ExecuteNonQuery();
                break;
            case "reject" when state == "pending":
                n = Cmd(c, "UPDATE pcs SET state='rejected', decided_at=$d WHERE id=$i", ("$d", Now()), ("$i", id)).ExecuteNonQuery();
                break;
            case "revoke" when state == "approved":
                // la chiave smette di valere subito: il PC se ne accorge al prossimo contatto
                n = Cmd(c, "UPDATE pcs SET state='revoked', key_hash=NULL, key_plain=NULL, decided_at=$d WHERE id=$i", ("$d", Now()), ("$i", id)).ExecuteNonQuery();
                break;
            case "update" when state == "approved":
                n = Cmd(c, "UPDATE pcs SET permission=$p, no_copy=$n WHERE id=$i", ("$p", perm), ("$n", noCopy ? 1 : 0), ("$i", id)).ExecuteNonQuery();
                break;
            default: return false;
        }
        if (n == 1) Log(c, id, action, action is "approve" or "update" ? $"permesso {perm}{(noCopy ? ", non copiare connessioni salvate" : "")}" : null);
        return n == 1;
    }

    // ---- il PC si presenta con la chiave ----

    /// <summary>chiave nel formato "id.chiave"; restituisce il PC se la chiave vale ancora e ne aggiorna l'ultimo contatto</summary>
    public static PcRow? Authenticate(string? bearer)
    {
        var p = (bearer ?? "").Split('.');
        if (p.Length != 2 || p[0].Length != 32) return null;
        using var c = Open();
        string? hash;
        using (var r = Cmd(c, "SELECT key_hash FROM pcs WHERE id = $i AND state = 'approved'", ("$i", p[0])).ExecuteReader()) hash = r.Read() && !r.IsDBNull(0) ? r.GetString(0) : null;
        // anche con una chiave inesistente si calcola l'hash: i tempi non dicono se l'id esiste
        bool ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sha(p[1])), Encoding.UTF8.GetBytes(hash ?? new string('0', 64)));
        if (!ok || hash == null) return null;
        Cmd(c, "UPDATE pcs SET last_seen = $n WHERE id = $i", ("$n", Now()), ("$i", p[0])).ExecuteNonQuery();
        return List().FirstOrDefault(x => x.Id == p[0]);
    }

    /// <summary>una chiave revocata va riconosciuta come tale per dire al PC di smettere</summary>
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
    private static readonly AttemptLimiter CodeLimiter = new(5, TimeSpan.FromMinutes(5));      // per indirizzo
    private static readonly AttemptLimiter AllCodesLimiter = new(30, TimeSpan.FromMinutes(5)); // in totale: chi cambia indirizzo non aggira il limite
    private record ApprovalReq(string? Permission, bool NoCopy);
    private record StatusReq(string? Id, string? Token, bool Ack);

    /// <summary>solo PC della rete locale: il server non risponde ad altri anche se qualcuno riuscisse a raggiungerlo</summary>
    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    private static bool Ok(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);

    public static void Map(RouteGroupBuilder api)
    {
        // ---- amministratore (con accesso alla console) ----
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
            return Pcs.Decide(id, action, r?.Permission, r?.NoCopy ?? false) ? Results.Json(new { ok = true }) : Results.Json(new { error = "state" }, statusCode: 409);
        });

        // ---- PC che chiede di collegarsi ----
        api.MapPost("/pair/request", (HttpContext c, Pcs.Request r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var ip = Api.Ip(c);
            if (CodeLimiter.IsBlocked(ip, out var until) || AllCodesLimiter.IsBlocked("*", out until)) return Results.Json(new { error = "blocked", until }, statusCode: 429);
            if (!Ok(r.Pc, 64) || !Ok(r.User, 64) || (r.AppVersion?.Length ?? 0) > 32) return Results.Json(new { error = "input" }, statusCode: 400);
            var res = Pcs.CreateRequest(r, ip);
            if (res == null)
            {
                AllCodesLimiter.Fail("*");
                int left = CodeLimiter.Fail(ip);
                return left == 0 ? Results.Json(new { error = "blocked", until = DateTime.UtcNow.AddMinutes(5) }, statusCode: 429) : Results.Json(new { error = "code", remaining = left }, statusCode: 401);
            }
            CodeLimiter.Reset(ip);
            return Results.Json(new { id = res.Value.id, token = res.Value.token, fingerprint = CertStore.Fingerprint(State.Cert) });
        });

        api.MapPost("/pair/status", (HttpContext c, StatusReq r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var s = Pcs.CheckStatus(r.Id, r.Token, r.Ack);
            return s == null ? Results.StatusCode(404) : Results.Json(new { state = s.State, key = s.Key, permission = Caps.Perm(c, s.Permission), noCopy = s.NoCopy });
        });

        api.MapPost("/pair/cancel", (HttpContext c, StatusReq r) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            return Pcs.Cancel(r.Id, r.Token) ? Results.Json(new { ok = true }) : Results.StatusCode(404);
        });

        // ---- PC gia' abbinato: controllo della chiave (le sincronizzazioni vere arrivano dopo) ----
        api.MapGet("/client/me", (HttpContext c) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc != null) return Results.Json(new { state = pc.State, permission = Caps.Perm(c, pc.Permission), noCopy = pc.NoCopy });
            return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
        });
    }
}
