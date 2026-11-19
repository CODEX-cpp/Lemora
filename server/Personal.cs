using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// PCs' personal connections (saved only on the PC) sent to the server. Rule (sharing the PC's personal connections):
/// read-only = always collected (unless "do not copy"), they stay pending until the administrator approves; requests and write = only with the client's
/// switch on; write publishes immediately, requests waits for approval. Approving = entry shared with everyone (version 1) with origin {pc, localId}.
/// The password is encrypted like the others (Vault, bound to the row id). Event log entries: personal-received, -approved, -rejected, -published.
/// </summary>
internal static class Pers
{
    public record In(string? LocalId, string? Folder, string? Name, string? Protocol, string? Host, int Port, string? User, string? Password, string? Tags);
    public record Submit(bool Share, List<In>? Items, List<string>? Removed);
    /// <summary>State: pending | published | rejected | conflict (shared entry changed by others) | invalid</summary>
    public record Res(string LocalId, string State, string? ConnId);
    public record Mine(string LocalId, string State, string? Note, string? ConnId, string? DecidedAt);
    public record Dup(string Kind, string Name, string? Pc); // Kind: shared (entry already shared) | proposal (another PC's proposal)
    public record Row(string Id, string PcId, string Pc, string LocalId, string Folder, string Name, string Protocol, string Host, int Port, string User, bool HasPassword,
        string Tags, string State, string? Note, string CreatedAt, string UpdatedAt, string? DecidedAt, string? ConnId, List<Dup> Dups);

    private const string Cols = "id, pc_id, local_id, folder, name, protocol, host, port, user, pw_enc, tags, state, note, created_at, updated_at, decided_at, conn_id, conn_version";
    private record Raw(string Id, string PcId, string LocalId, string Folder, string Name, string Protocol, string Host, int Port, string User, string? PwEnc, string Tags,
        string State, string? Note, string CreatedAt, string UpdatedAt, string? DecidedAt, string? ConnId, int ConnVersion);

    private static List<Raw> Read(SqliteConnection c, string where, params (string, object?)[] p)
    {
        using var r = Pcs.Cmd(c, $"SELECT {Cols} FROM personal WHERE {where} ORDER BY updated_at DESC LIMIT 2000", p).ExecuteReader();
        var list = new List<Raw>();
        string? S(int n) => r.IsDBNull(n) ? null : r.GetString(n);
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8), S(9),
                r.GetString(10), r.GetString(11), S(12), r.GetString(13), r.GetString(14), S(15), S(16), r.IsDBNull(17) ? 0 : r.GetInt32(17)));
        return list;
    }

    /// <summary>content fingerprint (keyed with the server key, since it includes the password)</summary>
    private static string Hash(string folder, string name, string? protocol, string host, int port, string user, string tags, string pw) =>
        Vault.Mac(string.Join('\0', folder, name, protocol, host, port, user, tags, pw));
    private static string Hash(Raw x) => Hash(x.Folder, x.Name, x.Protocol, x.Host, x.Port, x.User, x.Tags, x.PwEnc == null ? "" : Vault.Decrypt(x.PwEnc, x.Id));

    /// <summary>
    /// Rejection remembered per {localId, content}, not per PC: it survives the switch being turned off/on (which deletes unpublished rows),
    /// a permission change and a new pairing (new PC id, same localId). Only different content becomes a proposal again.
    /// </summary>
    private static void Remember(SqliteConnection c, string lid, string hash, string? note, string at) =>
        Pcs.Cmd(c, "INSERT OR REPLACE INTO personal_rejected (local_id, hash, note, decided_at) VALUES ($l, $h, $n, $d)",
            ("$l", lid), ("$h", hash), ("$n", note), ("$d", at)).ExecuteNonQuery();

    /// <summary>content already rejected: the row goes back to rejected with the note from back then. true if so</summary>
    private static bool Blocked(SqliteConnection c, string id, string lid, string hash) =>
        Pcs.Cmd(c, "UPDATE personal SET state='rejected', (note, decided_at) = (SELECT note, decided_at FROM personal_rejected WHERE local_id=$l AND hash=$h) " +
                   "WHERE id=$i AND EXISTS (SELECT 1 FROM personal_rejected WHERE local_id=$l AND hash=$h)", ("$l", lid), ("$h", hash), ("$i", id)).ExecuteNonQuery() == 1;

    private static Conns.Input ToConn(Raw x, string? pw, int? baseVersion = null) => new(x.Folder, x.Name, x.Protocol, x.Host, x.Port, x.User, pw, x.Tags, baseVersion);

    /// <summary>publishes the row as a shared entry (same creation path as the administrator); the row remembers the entry id, from which the sync derives the origin</summary>
    private static string Publish(SqliteConnection c, Raw x, string? note = null)
    {
        var connId = Conns.Create(ToConn(x, x.PwEnc == null ? null : Vault.Decrypt(x.PwEnc, x.Id)), x.PcId);
        Pcs.Cmd(c, "UPDATE personal SET state='published', conn_id=$c, conn_version=1, note=$n, decided_at=$d WHERE id=$i",
            ("$c", connId), ("$n", note), ("$d", Pcs.Now()), ("$i", x.Id)).ExecuteNonQuery();
        Pcs.Log(c, x.PcId, "personal-published", x.Name);
        return connId;
    }

    /// <summary>
    /// PC submission. perm = effective permission (Caps.Perm). "do not copy" applies to every permission (the server collects nothing);
    /// switch off with requests/write = nothing. In both cases that PC's unpublished copies are deleted.
    /// Returns the overall state (ok | nocopy | off) and the state of each entry sent.
    /// </summary>
    public static (string state, List<Res> items) Apply(Pcs.PcRow pc, string perm, Submit s)
    {
        using var c = Pcs.Open();
        Pcs.SetShareState(c, pc, perm == "read" || s.Share ? "on" : "off"); // with read-only, sharing is always on, whatever the switch says
        if (pc.NoCopy || (perm != "read" && !s.Share))
        {
            foreach (var x in Read(c, "pc_id = $p AND state = 'rejected'", ("$p", pc.Id))) Remember(c, x.LocalId, Hash(x), x.Note, x.DecidedAt ?? Pcs.Now()); // also rejections from before personal_rejected existed
            int n = Pcs.Cmd(c, "DELETE FROM personal WHERE pc_id = $p AND state <> 'published'", ("$p", pc.Id)).ExecuteNonQuery();
            if (n > 0) Pcs.Log(c, pc.Id, "personal-removed", $"{n} unpublished copies deleted ({(pc.NoCopy ? "do not copy" : "sharing turned off")})");
            return (pc.NoCopy ? "nocopy" : "off", new List<Res>());
        }
        var res = (s.Items ?? new List<In>()).Select(i => One(c, pc.Id, perm, i)).ToList();
        foreach (var lid in s.Removed ?? new List<string>())
            Pcs.Cmd(c, "DELETE FROM personal WHERE pc_id = $p AND local_id = $l AND state <> 'published'", ("$p", pc.Id), ("$l", lid)).ExecuteNonQuery(); // published = now belongs to the team: it stays
        return ("ok", res);
    }

    private static Res One(SqliteConnection c, string pcId, string perm, In i)
    {
        var lid = i.LocalId ?? "";
        if (lid.Length is 0 or > 64 || lid.Any(char.IsControl)) return new(lid, "invalid", null);
        var input = new Conns.Input(i.Folder, i.Name, i.Protocol, i.Host, i.Port, i.User, i.Password, i.Tags);
        if (Conns.Validate(input) != null) return new(lid, "invalid", null);
        var pw = i.Password ?? "";
        var now = Pcs.Now();
        var hash = Hash(i.Folder ?? "", i.Name!.Trim(), i.Protocol, i.Host!.Trim(), i.Port, i.User ?? "", i.Tags ?? "", pw);
        var old = Read(c, "pc_id = $p AND local_id = $l", ("$p", pcId), ("$l", lid)).FirstOrDefault();
        var vals = new (string, object?)[] { ("$f", i.Folder ?? ""), ("$n", i.Name!.Trim()), ("$pr", i.Protocol), ("$h", i.Host!.Trim()), ("$o", i.Port), ("$u", i.User ?? ""), ("$t", i.Tags ?? ""), ("$d", now) };
        if (old == null)
        {
            var id = Guid.NewGuid().ToString("N");
            Pcs.Cmd(c, "INSERT INTO personal (id, pc_id, local_id, folder, name, protocol, host, port, user, pw_enc, tags, version, state, created_at, updated_at) " +
                       "VALUES ($i, $p, $l, $f, $n, $pr, $h, $o, $u, $w, $t, 1, 'pending', $d, $d)",
                vals.Concat(new (string, object?)[] { ("$i", id), ("$p", pcId), ("$l", lid), ("$w", pw.Length == 0 ? null : Vault.Encrypt(pw, id)) }).ToArray()).ExecuteNonQuery();
            Pcs.Log(c, pcId, "personal-received", i.Name!.Trim());
            if (Blocked(c, id, lid, hash)) return new(lid, "rejected", null);
            var row = Read(c, "id = $i", ("$i", id)).First();
            return perm == "write" ? new(lid, "published", Publish(c, row)) : new(lid, "pending", null);
        }
        bool same = old.Folder == (i.Folder ?? "") && old.Name == i.Name!.Trim() && old.Protocol == i.Protocol && old.Host == i.Host!.Trim() && old.Port == i.Port
                    && old.User == (i.User ?? "") && old.Tags == (i.Tags ?? "") && (old.PwEnc == null ? "" : Vault.Decrypt(old.PwEnc, old.Id)) == pw;
        if (old.State == "published")
        {
            if (same || perm != "write") return new(lid, "published", old.ConnId); // without write the published entry belongs to the team: the update is ignored
            var r = Conns.TryUpdate(old.ConnId!, input with { BaseVersion = old.ConnVersion }, pcId);
            if (r == "conflict") return new(lid, "conflict", old.ConnId);
            if (r == "notfound") // the administrator deleted the shared entry: it stays personal to the PC
            {
                Pcs.Cmd(c, "UPDATE personal SET state='rejected', decided_at=$d WHERE id=$i", ("$d", now), ("$i", old.Id)).ExecuteNonQuery();
                Remember(c, lid, hash, null, now);
                return new(lid, "rejected", null);
            }
            Pcs.Cmd(c, "UPDATE personal SET folder=$f, name=$n, protocol=$pr, host=$h, port=$o, user=$u, pw_enc=$w, tags=$t, version=version+1, conn_version=$cv, updated_at=$d WHERE id=$i",
                vals.Concat(new (string, object?)[] { ("$w", pw.Length == 0 ? null : Vault.Encrypt(pw, old.Id)), ("$cv", Conns.Get(old.ConnId!)?.Version ?? old.ConnVersion + 1), ("$i", old.Id) }).ToArray()).ExecuteNonQuery();
            Pcs.Log(c, pcId, "personal-published", i.Name!.Trim() + " (updated)");
            return new(lid, "published", old.ConnId);
        }
        if (!same)
        {
            // pending or rejected and then changed: becomes a new proposal for the administrator
            Pcs.Cmd(c, "UPDATE personal SET folder=$f, name=$n, protocol=$pr, host=$h, port=$o, user=$u, pw_enc=$w, tags=$t, version=version+1, state='pending', note=NULL, decided_at=NULL, updated_at=$d WHERE id=$i",
                vals.Concat(new (string, object?)[] { ("$w", pw.Length == 0 ? null : Vault.Encrypt(pw, old.Id)), ("$i", old.Id) }).ToArray()).ExecuteNonQuery();
            Pcs.Log(c, pcId, "personal-received", i.Name!.Trim() + " (updated)");
        }
        else if (old.State == "rejected") return new(lid, "rejected", null);
        if (Blocked(c, old.Id, lid, hash)) return new(lid, "rejected", null);
        if (perm == "write") return new(lid, "published", Publish(c, Read(c, "id = $i", ("$i", old.Id)).First()));
        return new(lid, "pending", null);
    }

    /// <summary>"ok" | "notfound" | "state" (already decided)</summary>
    public static string Decide(string id, bool approve, string? note)
    {
        using var c = Pcs.Open();
        var x = Read(c, "id = $i", ("$i", id)).FirstOrDefault();
        if (x == null) return "notfound";
        if (x.State != "pending") return "state";
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        // ponytail: no lock between two concurrent approvals of the same proposal (single admin), as in Reqs.Decide
        if (approve)
        {
            Pcs.Log(c, x.PcId, "personal-approved", x.Name + (note != null ? " - " + note : ""));
            Publish(c, x, note);
        }
        else
        {
            var at = Pcs.Now();
            Pcs.Cmd(c, "UPDATE personal SET state='rejected', note=$n, decided_at=$d WHERE id=$i AND state='pending'", ("$n", note), ("$d", at), ("$i", id)).ExecuteNonQuery();
            Remember(c, x.LocalId, Hash(x), note, at);
            Pcs.Log(c, x.PcId, "personal-rejected", x.Name + (note != null ? " - " + note : ""));
        }
        return "ok";
    }

    /// <summary>approves all pending proposals of a PC; returns how many</summary>
    public static int ApproveAll(string pcId)
    {
        List<string> ids;
        using (var c = Pcs.Open()) ids = Read(c, "pc_id = $p AND state = 'pending'", ("$p", pcId)).Select(x => x.Id).ToList();
        return ids.Count(id => Decide(id, true, null) == "ok");
    }

    /// <summary>for the console: all rows, with possible duplicates (same address, port and user already shared or proposed by another PC). Never the password: only whether there is one</summary>
    public static List<Row> List()
    {
        using var c = Pcs.Open();
        var names = Pcs.List().ToDictionary(p => p.Id, p => $"{p.Pc} - {p.User}");
        var all = Read(c, "1 = 1");
        var shared = new List<(string id, string name, string host, int port, string user)>();
        using (var r = Pcs.Cmd(c, "SELECT id, name, host, port, user FROM connections WHERE deleted_at IS NULL").ExecuteReader())
            while (r.Read()) shared.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
        bool Same(string h1, int p1, string u1, string h2, int p2, string u2) => string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase) && p1 == p2 && string.Equals(u1, u2, StringComparison.OrdinalIgnoreCase);
        return all.Select(x =>
        {
            var dups = shared.Where(s => s.id != x.ConnId && Same(s.host, s.port, s.user, x.Host, x.Port, x.User)).Select(s => new Dup("shared", s.name, null))
                .Concat(all.Where(o => o.PcId != x.PcId && o.State == "pending" && Same(o.Host, o.Port, o.User, x.Host, x.Port, x.User)).Select(o => new Dup("proposal", o.Name, names.GetValueOrDefault(o.PcId, "?"))))
                .ToList();
            return new Row(x.Id, x.PcId, names.GetValueOrDefault(x.PcId, "?"), x.LocalId, x.Folder, x.Name, x.Protocol, x.Host, x.Port, x.User, x.PwEnc != null, x.Tags,
                x.State, x.Note, x.CreatedAt, x.UpdatedAt, x.DecidedAt, x.ConnId, dups);
        }).ToList();
    }

    /// <summary>for the PC (in the sync): the state of its submitted connections, with the administrator's note</summary>
    public static List<Mine> ListMine(string pcId)
    {
        using var c = Pcs.Open();
        return Read(c, "pc_id = $p", ("$p", pcId)).Select(x => new Mine(x.LocalId, x.State, x.Note, x.ConnId, x.DecidedAt)).ToList();
    }
}

internal static class PersonalApi
{
    private record DecideReq(string? Note);

    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    public static void Map(RouteGroupBuilder api)
    {
        // ---- administrator: "Proposte" (proposals) page ----
        api.MapGet("/personal", (HttpContext c) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            return Results.Json(Pers.List());
        });

        api.MapPost("/personal/{id}/{action}", (HttpContext c, string id, string action, DecideReq? r) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (action is not ("approve" or "reject")) return Results.NotFound();
            if (r?.Note is { } n && (n.Length > 500 || n.Any(ch => char.IsControl(ch) && ch != '\n'))) return Results.Json(new { error = "input", field = "note" }, statusCode: 400);
            try
            {
                return Pers.Decide(id, action == "approve", r?.Note) switch
                {
                    "ok" => Results.Json(new { ok = true }),
                    "state" => Results.Json(new { error = "state" }, statusCode: 409),
                    _ => Results.Json(new { error = "notfound" }, statusCode: 409),
                };
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });

        api.MapPost("/personal/pc/{pcId}/approve-all", (HttpContext c, string pcId) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            try { return Results.Json(new { approved = Pers.ApproveAll(pcId) }); }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });

        // ---- linked PC: sends its saved connections (client with the "personal" capability) ----
        api.MapPost("/client/personal", (HttpContext c, Pers.Submit s) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if (!Caps.Has(c, "personal")) return Results.Json(new { error = "caps" }, statusCode: 400); // client that does not know this call: nothing is saved
            if ((s.Items?.Count ?? 0) > 5000 || (s.Removed?.Count ?? 0) > 5000) return Results.Json(new { error = "input", field = "items" }, statusCode: 400);
            try
            {
                var (state, items) = Pers.Apply(pc, Caps.Perm(c, pc.Permission) ?? "read", s);
                return Results.Json(new { state, items });
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });
    }
}
