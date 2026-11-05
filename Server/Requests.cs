using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Richieste dei PC con permesso "requests": modifica, eliminazione o nuova voce condivisa, da approvare nella console.
/// Approvare applica la modifica come una scrittura dell'amministratore (eliminazione = cestino). Ogni passo va nel registro (events).
/// </summary>
internal static class Reqs
{
    public record Input(string? Kind, string? ConnId, int? BaseVersion, string? Folder, string? Name, string? Protocol, string? Host, int Port, string? User, string? Password, string? Tags);
    public record Values(string Folder, string Name, string Protocol, string Host, int Port, string User, string Tags);
    /// <summary>State: pending | stale (in attesa, ma la voce e' cambiata dopo la richiesta) | approved | rejected</summary>
    public record Row(string Id, string Pc, string Kind, string? ConnId, string State, string? Note, string CreatedAt, string? DecidedAt, Values? Old, Values? New, bool PwChanged);
    public record Mine(string Id, string Kind, string? ConnId, string State, string? Note, string? DecidedAt);

    private static Conns.Input ToConn(Input i) => new(i.Folder, i.Name, i.Protocol, i.Host, i.Port, i.User, i.Password, i.Tags);

    /// <summary>(id, null) oppure (null, errore): kind | input:campo | notfound</summary>
    public static (string? id, string? error) Create(string pcId, Input i)
    {
        if (i.Kind is not ("update" or "delete" or "create")) return (null, "kind");
        if (i.Kind != "delete" && Conns.Validate(ToConn(i)) is { } bad) return (null, "input:" + bad);
        var id = Guid.NewGuid().ToString("N");
        using var c = Pcs.Open();
        string? connName = null;
        if (i.Kind != "create")
        {
            using var r = Pcs.Cmd(c, "SELECT name FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", i.ConnId)).ExecuteReader();
            if (!r.Read()) return (null, "notfound");
            connName = r.GetString(0);
        }
        bool vals = i.Kind != "delete";
        Pcs.Cmd(c, "INSERT INTO requests (id, pc_id, kind, conn_id, base_version, folder, name, protocol, host, port, user, pw_enc, tags, state, created_at) VALUES ($i, $pc, $k, $c, $b, $f, $n, $p, $h, $o, $u, $w, $t, 'pending', $d)",
            ("$i", id), ("$pc", pcId), ("$k", i.Kind), ("$c", i.Kind == "create" ? null : i.ConnId), ("$b", i.BaseVersion),
            ("$f", vals ? i.Folder ?? "" : null), ("$n", vals ? i.Name!.Trim() : null), ("$p", vals ? i.Protocol : null), ("$h", vals ? i.Host!.Trim() : null),
            ("$o", vals ? i.Port : null), ("$u", vals ? i.User ?? "" : null), ("$w", vals ? Vault.Encrypt(i.Password ?? "", id) : null), ("$t", vals ? i.Tags ?? "" : null), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "request-created", $"{i.Kind} {(vals ? i.Name!.Trim() : connName)}");
        return (id, null);
    }

    private record Raw(string Id, string PcId, string Pc, string Kind, string? ConnId, int? Base, Values? New, string? PwEnc, string State, string? Note, string CreatedAt, string? DecidedAt);

    private static List<Raw> Read(SqliteConnection c, string where, params (string, object?)[] p)
    {
        using var r = Pcs.Cmd(c, "SELECT q.id, q.pc_id, COALESCE(p.pc || ' - ' || p.user, '?'), q.kind, q.conn_id, q.base_version, q.folder, q.name, q.protocol, q.host, q.port, q.user, q.tags, q.pw_enc, q.state, q.note, q.created_at, q.decided_at " +
            "FROM requests q LEFT JOIN pcs p ON p.id = q.pc_id WHERE " + where + " ORDER BY q.created_at DESC LIMIT 500", p).ExecuteReader();
        var list = new List<Raw>();
        string? S(int n) => r.IsDBNull(n) ? null : r.GetString(n);
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), S(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(7) ? null : new Values(S(6) ?? "", S(7)!, S(8) ?? "", S(9) ?? "", r.IsDBNull(10) ? 0 : r.GetInt32(10), S(11) ?? "", S(12) ?? ""),
                S(13), r.GetString(14), S(15), r.GetString(16), S(17)));
        return list;
    }

    /// <summary>la voce com'e' ora (null se non c'e' o e' nel cestino), con versione e password cifrata</summary>
    private static (Values v, int version, string? pwEnc)? Current(SqliteConnection c, string? connId)
    {
        if (connId == null) return null;
        using var r = Pcs.Cmd(c, "SELECT folder, name, protocol, host, port, user, tags, version, pw_enc FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", connId)).ExecuteReader();
        if (!r.Read()) return null;
        return (new Values(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6)), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8));
    }

    private static string LiveState(Raw q, (Values v, int version, string? pwEnc)? cur) =>
        q.State == "pending" && q.Kind != "create" && (cur == null || cur.Value.version != q.Base) ? "stale" : q.State;

    /// <summary>per la console: in attesa (anche "stale") e storico, con il confronto vecchio/nuovo. La password non esce mai: solo se cambia</summary>
    public static List<Row> List()
    {
        using var c = Pcs.Open();
        var list = new List<Row>();
        foreach (var q in Read(c, "1 = 1"))
        {
            var cur = Current(c, q.ConnId);
            bool pw = false;
            if (q.State == "pending" && q.PwEnc != null)
            {
                var want = Vault.Decrypt(q.PwEnc, q.Id);
                var have = cur?.pwEnc is { } e ? Vault.Decrypt(e, q.ConnId!) : "";
                pw = want != have;
            }
            list.Add(new(q.Id, q.Pc, q.Kind, q.ConnId, LiveState(q, cur), q.Note, q.CreatedAt, q.DecidedAt, cur?.v, q.New, pw));
        }
        return list;
    }

    /// <summary>per il PC: le sue richieste in attesa e quelle decise negli ultimi 30 giorni</summary>
    public static List<Mine> ListMine(string pcId)
    {
        using var c = Pcs.Open();
        var since = DateTime.UtcNow.AddDays(-30).ToString("o");
        return Read(c, "q.pc_id = $p AND (q.state = 'pending' OR q.decided_at > $s)", ("$p", pcId), ("$s", since))
            .Select(q => new Mine(q.Id, q.Kind, q.ConnId, LiveState(q, Current(c, q.ConnId)), q.Note, q.DecidedAt)).ToList();
    }

    /// <summary>"ok" | "notfound" (richiesta o voce che non c'e' piu') | "state" (gia' decisa)</summary>
    public static string Decide(string id, bool approve, string? note)
    {
        using var c = Pcs.Open();
        var q = Read(c, "q.id = $i", ("$i", id)).FirstOrDefault();
        if (q == null) return "notfound";
        if (q.State != "pending") return "state";
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        string? connId = q.ConnId;
        if (approve)
        {
            // ponytail: nessun blocco tra due approvazioni contemporanee della stessa richiesta (un solo admin); se serve, prima marcare 'approving' con UPDATE condizionato
            var input = q.New == null ? null : new Conns.Input(q.New.Folder, q.New.Name, q.New.Protocol, q.New.Host, q.New.Port, q.New.User, q.PwEnc == null ? null : Vault.Decrypt(q.PwEnc, q.Id), q.New.Tags);
            switch (q.Kind)
            {
                case "update" when Conns.TryUpdate(q.ConnId!, input!) != "ok": return "notfound"; // sovrascrive anche se "stale": l'admin ha visto l'avviso
                case "delete" when !Conns.Delete(q.ConnId!): return "notfound";
                case "create": connId = Conns.Create(input!); break;
            }
        }
        int n = Pcs.Cmd(c, "UPDATE requests SET state = $s, note = $n, decided_at = $d, conn_id = $c, pw_enc = NULL WHERE id = $i AND state = 'pending'",
            ("$s", approve ? "approved" : "rejected"), ("$n", note), ("$d", Pcs.Now()), ("$c", connId), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, q.PcId, approve ? "request-approved" : "request-rejected", $"{q.Kind} {q.New?.Name ?? connId}{(note != null ? " - " + note : "")}");
        return n == 1 ? "ok" : "state";
    }
}

/// <summary>
/// Capacita' dichiarate dal client in ogni chiamata con l'intestazione "X-Lemora-Caps: vnc,requests".
/// Un client che non le dichiara (v9 e precedenti) e' trattato come "RDP/SSH soltanto": niente voci VNC e il permesso "requests" diventa "read".
/// </summary>
internal static class Caps
{
    public static bool Has(HttpContext c, string cap) =>
        c.Request.Headers["X-Lemora-Caps"].ToString().Split(',').Any(x => x.Trim().Equals(cap, StringComparison.OrdinalIgnoreCase));

    /// <summary>capacita' in forma confrontabile (minuscole, ordinate, senza doppioni): "" per un client vecchio</summary>
    public static string Key(HttpContext c) =>
        string.Join(",", c.Request.Headers["X-Lemora-Caps"].ToString().Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().Order());

    public static string? Perm(HttpContext c, string? permission) => permission == "requests" && !Has(c, "requests") ? "read" : permission;
}

internal static class RequestsApi
{
    private record DecideReq(string? Note);

    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    public static void Map(RouteGroupBuilder api)
    {
        // ---- amministratore ----
        api.MapGet("/requests", (HttpContext c) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            try { return Results.Json(Reqs.List()); }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });

        api.MapPost("/requests/{id}/{action}", (HttpContext c, string id, string action, DecideReq? r) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (action is not ("approve" or "reject")) return Results.NotFound();
            if (r?.Note is { } n && (n.Length > 500 || n.Any(ch => char.IsControl(ch) && ch != '\n'))) return Results.Json(new { error = "input", field = "note" }, statusCode: 400);
            return Reqs.Decide(id, action == "approve", r?.Note) switch
            {
                "ok" => Results.Json(new { ok = true }),
                "state" => Results.Json(new { error = "state" }, statusCode: 409),
                _ => Results.Json(new { error = "notfound" }, statusCode: 409),
            };
        });

        // ---- PC con permesso "requests": manda una richiesta (con "write" scrive direttamente, con "read" niente) ----
        api.MapPost("/client/requests", (HttpContext c, Reqs.Input i) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if (Caps.Perm(c, pc.Permission) != "requests") return Results.Json(new { error = "permission" }, statusCode: 403);
            var (id, err) = Reqs.Create(pc.Id, i);
            if (err == "notfound") return Results.NotFound();
            if (err != null) return Results.Json(new { error = "input", field = err.StartsWith("input:") ? err[6..] : err }, statusCode: 400);
            return Results.Json(new { id, state = "pending" });
        });
    }
}
