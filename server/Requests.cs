using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Requests from PCs with the "requests" permission: edit, deletion or new shared entry, to be approved in the console.
/// Approving applies the change as a write by the administrator (deletion = trash). Every step goes to the event log (events).
/// </summary>
internal static class Reqs
{
    public record Input(string? Kind, string? ConnId, int? BaseVersion, string? Folder, string? Name, string? Protocol, string? Host, int Port, string? User, string? Password, string? Tags, string? Ucfg = null, List<Conns.LUser>? Lusers = null, List<Shared.UserPw>? Devusers = null);
    private static string JoinS(string a, string b) => string.Join(" · ", new[] { a, b }.Where(x => x.Length > 0));
    public record Values(string Folder, string Name, string Protocol, string Host, int Port, string User, string Tags, string Creds = "");

    /// <summary>readable line for the console from the JSON binding ({"c":[{"f":folder,"u":user}],"d":default,"a":ask}): "folder\user, ..."; empty if none</summary>
    internal static string Summ(string? ucfg)
    {
        if (string.IsNullOrWhiteSpace(ucfg)) return "";
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(ucfg);
            var parts = new List<string>();
            if (d.RootElement.TryGetProperty("c", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in c.EnumerateArray())
                    parts.Add((e.TryGetProperty("f", out var f) ? f.GetString() : "") + " \\ " + (e.TryGetProperty("u", out var u) ? u.GetString() : ""));
            if (d.RootElement.TryGetProperty("a", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True) parts.Add("chiedi sempre");
            return string.Join(", ", parts);
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }
    /// <summary>State: pending | stale (pending, but the entry changed after the request) | approved | rejected</summary>
    public record Row(string Id, string Pc, string Kind, string? ConnId, string State, string? Note, string CreatedAt, string? DecidedAt, Values? Old, Values? New, bool PwChanged);
    public record Mine(string Id, string Kind, string? ConnId, string State, string? Note, string? DecidedAt);

    private static Conns.Input ToConn(Input i) => new(i.Folder, i.Name, i.Protocol, i.Host, i.Port, i.User, i.Password, i.Tags, null, i.Ucfg, i.Lusers);

    /// <summary>(id, null) or (null, error): kind | input:field | notfound</summary>
    public static (string? id, string? error) Create(string pcId, Input i)
    {
        if (i.Kind == "fcred-user") return CreateFcredUser(pcId, i);
        if (i.Kind == "folder-rename") return CreateFolderRename(pcId, i);
        if (i.Kind is "fcred-create" or "fcred-update" or "fcred-delete") return CreateFc(pcId, i);
        if (i.Kind is "device-create" or "device-update" or "device-delete") return CreateDev(pcId, i);
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
        Pcs.Cmd(c, "INSERT INTO requests (id, pc_id, kind, conn_id, base_version, folder, name, protocol, host, port, user, pw_enc, tags, ucfg, users_enc, state, created_at) VALUES ($i, $pc, $k, $c, $b, $f, $n, $p, $h, $o, $u, $w, $t, $ug, $ue, 'pending', $d)",
            ("$i", id), ("$pc", pcId), ("$k", i.Kind), ("$c", i.Kind == "create" ? null : i.ConnId), ("$b", i.BaseVersion),
            ("$f", vals ? i.Folder ?? "" : null), ("$n", vals ? i.Name!.Trim() : null), ("$p", vals ? i.Protocol : null), ("$h", vals ? i.Host!.Trim() : null),
            ("$o", vals ? i.Port : null), ("$u", vals ? i.User ?? "" : null), ("$w", vals ? Vault.Encrypt(i.Password ?? "", id) : null), ("$t", vals ? i.Tags ?? "" : null), ("$ug", vals ? i.Ucfg : null), ("$ue", vals ? Conns.EncU(i.Lusers, id) : null), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "request-created", $"{i.Kind} {(vals ? i.Name!.Trim() : connName)}");
        return (id, null);
    }

    /// <summary>
    /// Change of a folder credential's user requested by a "requests" PC (for the other permissions it is a disguised deletion).
    /// Columns: folder = folder, user = current user, name = new user. The password is not involved: the credential's one stays.
    /// </summary>
    private static (string? id, string? error) CreateFcredUser(string pcId, Input i)
    {
        if (Shared.Validate(new Shared.FcIn(null, i.Folder, i.Name, "")) is { } bad) return (null, "input:" + (bad == "user" ? "name" : bad));
        if (Shared.Validate(new Shared.FcIn(null, i.Folder, i.User, "")) != null) return (null, "input:user");
        var folder = Conns.NormFolder(i.Folder); var oldUser = i.User!.Trim(); var newUser = i.Name!.Trim();
        using var c = Pcs.Open();
        if (FindFcred(c, folder, oldUser) == null) return (null, "notfound");
        var id = Guid.NewGuid().ToString("N");
        Pcs.Cmd(c, "INSERT INTO requests (id, pc_id, kind, conn_id, base_version, folder, name, protocol, host, port, user, pw_enc, tags, state, created_at) VALUES ($i, $pc, 'fcred-user', NULL, NULL, $f, $n, '', '', 0, $u, NULL, '', 'pending', $d)",
            ("$i", id), ("$pc", pcId), ("$f", folder), ("$n", newUser), ("$u", oldUser), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "request-created", $"fcred-user {folder} / {oldUser} -> {newUser}");
        return (id, null);
    }

    /// <summary>
    /// Rename or move of a folder with shared entries, asked by a "requests" PC (without approval it would move entries of everyone).
    /// Columns: folder = current path, name = new path. Approving does the same rename as the administrator (Shared.Rename).
    /// </summary>
    private static (string? id, string? error) CreateFolderRename(string pcId, Input i)
    {
        if (Shared.CleanPath(i.Folder) is not { } from) return (null, "input:folder");
        if (Shared.CleanPath(i.Name) is not { } to) return (null, "input:name");
        if (string.Equals(from, to, StringComparison.Ordinal)) return (null, "input:name");
        if (to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) return (null, "input:name");   // into itself
        var id = Guid.NewGuid().ToString("N");
        using var c = Pcs.Open();
        Pcs.Cmd(c, "INSERT INTO requests (id, pc_id, kind, conn_id, base_version, folder, name, protocol, host, port, user, pw_enc, tags, state, created_at) VALUES ($i, $pc, 'folder-rename', NULL, NULL, $f, $n, '', '', 0, '', NULL, '', 'pending', $d)",
            ("$i", id), ("$pc", pcId), ("$f", from), ("$n", to), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "request-created", $"folder-rename {from} -> {to}");
        return (id, null);
    }

    private static string? FindFcred(SqliteConnection c, string folder, string user) =>
        Pcs.Cmd(c, "SELECT id FROM fcreds WHERE deleted_at IS NULL AND folder = $f COLLATE NOCASE AND user = $u COLLATE NOCASE", ("$f", folder), ("$u", user)).ExecuteScalar() as string;

    private static readonly System.Text.RegularExpressions.Regex Hex32 = new("^[0-9a-fA-F]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static bool FcLive(SqliteConnection c, string? id) => id != null && Pcs.Cmd(c, "SELECT 1 FROM fcreds WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteScalar() != null;
    private static bool DevLive(SqliteConnection c, string? id) => id != null && Pcs.Cmd(c, "SELECT 1 FROM devices WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteScalar() != null;

    /// <summary>
    /// Request from a "requests" PC on a folder credential: new (fcred-create), change of folder/user/password
    /// (fcred-update, with the id in conn_id) or deletion (fcred-delete, id in conn_id). Password encrypted with the request id;
    /// null = do not change it. Approval calls Shared.SaveFcred/DeleteFcred like the administrator (see Decide).
    /// </summary>
    private static (string? id, string? error) CreateFc(string pcId, Input i)
    {
        bool del = i.Kind == "fcred-delete";
        string? targetId = i.Kind == "fcred-create" ? null : i.ConnId;
        if (!del && Shared.Validate(new Shared.FcIn(null, i.Folder, i.User, i.Password)) is { } bad) return (null, "input:" + bad);
        using var c = Pcs.Open();
        if (targetId != null && !FcLive(c, targetId)) return (null, "notfound");
        if (del && targetId == null) return (null, "notfound");
        var id = Guid.NewGuid().ToString("N");
        var folder = del ? "" : Conns.NormFolder(i.Folder);
        var user = del ? "" : i.User!.Trim();
        Pcs.Cmd(c, "INSERT INTO requests (id, pc_id, kind, conn_id, base_version, folder, name, protocol, host, port, user, pw_enc, tags, state, created_at) VALUES ($i, $pc, $k, $c, NULL, $f, '', '', '', 0, $u, $w, '', 'pending', $d)",
            ("$i", id), ("$pc", pcId), ("$k", i.Kind), ("$c", targetId), ("$f", folder), ("$u", user),
            ("$w", del || i.Password == null ? null : Vault.Encrypt(i.Password, id)), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "request-created", $"{i.Kind} {(del ? targetId : folder + " / " + user)}");
        return (id, null);
    }

    /// <summary>
    /// Request from a "requests" PC on a shared device: new (device-create), edit (device-update, id in conn_id) or
    /// deletion (device-delete). The device users (user+password) are encrypted with the request id in the users_enc column.
    /// </summary>
    private static (string? id, string? error) CreateDev(string pcId, Input i)
    {
        bool del = i.Kind == "device-delete", create = i.Kind == "device-create";
        string? targetId = i.ConnId;
        if (!del && Shared.Validate(new Shared.DevIn(null, i.Folder, i.Name, i.Host, i.Devusers)) is { } bad) return (null, "input:" + bad);
        using var c = Pcs.Open();
        // new: the PC may propose the id of its local entry (so it is not duplicated after approval), never that of a live device (it would be a disguised edit)
        if (create) { if (targetId == null || !Hex32.IsMatch(targetId) || DevLive(c, targetId)) targetId = null; else targetId = targetId.ToLowerInvariant(); }
        else if (!DevLive(c, targetId)) return (null, "notfound");
        var id = Guid.NewGuid().ToString("N");
        var folder = del ? "" : Conns.NormFolder(i.Folder);
        var name = del ? "" : i.Name!.Trim();
        var ip = del ? "" : (i.Host ?? "").Trim();
        string? usersEnc = del ? null : Vault.Encrypt(System.Text.Json.JsonSerializer.Serialize(i.Devusers ?? new()), id);
        Pcs.Cmd(c, "INSERT INTO requests (id, pc_id, kind, conn_id, base_version, folder, name, protocol, host, port, user, pw_enc, tags, users_enc, state, created_at) VALUES ($i, $pc, $k, $c, NULL, $f, $n, '', $h, 0, '', NULL, '', $ue, 'pending', $d)",
            ("$i", id), ("$pc", pcId), ("$k", i.Kind), ("$c", targetId), ("$f", folder), ("$n", name), ("$h", ip), ("$ue", usersEnc), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "request-created", $"{i.Kind} {(del ? targetId : folder + " / " + name)}");
        return (id, null);
    }

    private record Raw(string Id, string PcId, string Pc, string Kind, string? ConnId, int? Base, Values? New, string? PwEnc, string State, string? Note, string CreatedAt, string? DecidedAt, string? Ucfg = null, string? OldJson = null, List<Conns.LUser>? Lusers = null);
    /// <summary>what the entry looked like when the administrator decided (no password: only whether the request changed it)</summary>
    private record Snap(Values? V, bool Pw);

    private static List<Raw> Read(SqliteConnection c, string where, params (string, object?)[] p)
    {
        using var r = Pcs.Cmd(c, "SELECT q.id, q.pc_id, COALESCE(p.pc || ' - ' || p.user, '?'), q.kind, q.conn_id, q.base_version, q.folder, q.name, q.protocol, q.host, q.port, q.user, q.tags, q.pw_enc, q.state, q.note, q.created_at, q.decided_at, q.ucfg, q.old_json, q.users_enc " +
            "FROM requests q LEFT JOIN pcs p ON p.id = q.pc_id WHERE " + where + " ORDER BY q.created_at DESC LIMIT 500", p).ExecuteReader();
        var list = new List<Raw>();
        string? S(int n) => r.IsDBNull(n) ? null : r.GetString(n);
        while (r.Read())
        {
            var rid = r.GetString(0);
            // devices encrypt their users in users_enc with a different schema (device-*): they do not go through Conns.DecU
            var lus = r.GetString(3).StartsWith("device-") ? null : Conns.DecU(S(20), rid);   // requested local users (encrypted with the request id)
            list.Add(new(rid, r.GetString(1), r.GetString(2), r.GetString(3), S(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(7) ? null : new Values(S(6) ?? "", S(7)!, S(8) ?? "", S(9) ?? "", r.IsDBNull(10) ? 0 : r.GetInt32(10), S(11) ?? "", S(12) ?? "", JoinS(Summ(S(18)), Conns.LocalNames(lus))),
                S(13), r.GetString(14), S(15), r.GetString(16), S(17), S(18), S(19), lus));
        }
        return list;
    }

    /// <summary>the entry as it is now (null if missing or in the trash), with version and encrypted password</summary>
    private static (Values v, int version, string? pwEnc)? Current(SqliteConnection c, string? connId)
    {
        if (connId == null) return null;
        using var r = Pcs.Cmd(c, "SELECT folder, name, protocol, host, port, user, tags, version, pw_enc, ucfg, users_enc FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", connId)).ExecuteReader();
        if (!r.Read()) return null;
        var cl = Conns.DecU(r.IsDBNull(10) ? null : r.GetString(10), connId);
        return (new Values(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), JoinS(Summ(r.IsDBNull(9) ? null : r.GetString(9)), Conns.LocalNames(cl))), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8));
    }

    private static bool FcDev(string k) => k is "fcred-create" or "fcred-update" or "fcred-delete" or "device-create" or "device-update" or "device-delete";

    /// <summary>requested device users (encrypted with the request id), null if none</summary>
    private static List<Shared.UserPw>? DevUsers(SqliteConnection c, string reqId) =>
        Pcs.Cmd(c, "SELECT users_enc FROM requests WHERE id = $i", ("$i", reqId)).ExecuteScalar() is string ue
            ? System.Text.Json.JsonSerializer.Deserialize<List<Shared.UserPw>>(Vault.Decrypt(ue, reqId)) : null;

    /// <summary>
    /// for requests on folder credentials and devices: what the entry is now (null if new or no longer live) and whether the request changes
    /// any password. Passwords never leave: only the fact that they change. Device users in the Creds field (names only).
    /// </summary>
    private static (Values? old, bool pw) FcDevOld(SqliteConnection c, Raw q)
    {
        if (q.Kind.StartsWith("fcred-"))
        {
            var cur = q.ConnId == null ? null : Shared.GetFc(q.ConnId);
            if (cur is { Deleted: true }) cur = null;
            bool pw = q.PwEnc != null && Vault.Decrypt(q.PwEnc, q.Id) != (cur?.Password ?? "");
            return (cur == null ? null : new Values(cur.Folder, "", "", "", 0, cur.User, ""), pw);
        }
        var d = q.ConnId == null ? null : Shared.GetDev(q.ConnId);
        if (d is { Deleted: true }) d = null;
        var want = DevUsers(c, q.Id) ?? new();
        bool pwd = want.Any(u => (d?.Users.FirstOrDefault(x => string.Equals(x.User, u.User, StringComparison.OrdinalIgnoreCase))?.Password ?? "") != (u.Password ?? ""));
        return (d == null ? null : new Values(d.Folder, d.Name, "", d.Ip, 0, "", "", string.Join(", ", d.Users.Select(u => u.User))), pwd);
    }

    private static string LiveState(Raw q, (Values v, int version, string? pwEnc)? cur) =>
        q.State == "pending" && q.Kind is "update" or "delete" && (cur == null || cur.Value.version != q.Base) ? "stale" : q.State;   // "stale" only for connections (conn_id points to connections); fcred/device have a conn_id from another table

    /// <summary>for the console: pending (including "stale") and history, with the old/new comparison. The password never leaves: only whether it changes</summary>
    public static List<Row> List()
    {
        using var c = Pcs.Open();
        var list = new List<Row>();
        foreach (var q in Read(c, "1 = 1"))
        {
            if (FcDev(q.Kind))
            {
                // folder credential or device: "before" from the right table (not from connections); in the history as it was at decision time
                var newV = q.Kind.StartsWith("device-") && q.New != null ? q.New with { Creds = string.Join(", ", (DevUsers(c, q.Id) ?? new()).Select(u => u.User)) } : q.New;
                Values? ov; bool p;
                if (q.State is "approved" or "rejected")
                {
                    Snap? sn = null;
                    try { sn = q.OldJson == null ? null : System.Text.Json.JsonSerializer.Deserialize<Snap>(q.OldJson); } catch (System.Text.Json.JsonException) { }
                    ov = sn?.V; p = sn?.Pw ?? false;
                }
                else (ov, p) = FcDevOld(c, q);
                list.Add(new(q.Id, q.Pc, q.Kind, q.ConnId, q.State, q.Note, q.CreatedAt, q.DecidedAt, ov, newV, p));
                continue;
            }
            var cur = Current(c, q.ConnId);
            bool pw = false;
            if (q.State == "pending" && q.PwEnc != null)
            {
                var want = Vault.Decrypt(q.PwEnc, q.Id);
                var have = cur?.pwEnc is { } e ? Vault.Decrypt(e, q.ConnId!) : "";
                pw = want != have;
            }
            var oldV = cur?.v;
            if (q.Kind == "folder-rename" && q.New != null)
            {
                // current path -> new path (in the request: folder = current, name = new)
                list.Add(new(q.Id, q.Pc, q.Kind, null, q.State, q.Note, q.CreatedAt, q.DecidedAt, new Values(q.New.Folder, "", "", "", 0, "", ""), new Values(q.New.Name, "", "", "", 0, "", ""), false));
                continue;
            }
            if (q.Kind == "fcred-user" && q.New != null)
            {
                // comparison current user -> new user (in the request: user = current, name = new); the "current" entry also stays in the history
                var f = q.New.Folder;
                list.Add(new(q.Id, q.Pc, q.Kind, null, q.State, q.Note, q.CreatedAt, q.DecidedAt, new Values(f, q.New.User, "", "", 0, "", ""), new Values(f, q.New.Name, "", "", 0, "", ""), false));
                continue;
            }
            if (q.State is "approved" or "rejected")
            {
                // history: what the entry was at decision time (not what it is now, after a possible approval)
                Snap? sn = null;
                try { sn = q.OldJson == null ? null : System.Text.Json.JsonSerializer.Deserialize<Snap>(q.OldJson); } catch (System.Text.Json.JsonException) { }
                oldV = sn?.V; pw = sn?.Pw ?? false;
            }
            list.Add(new(q.Id, q.Pc, q.Kind, q.ConnId, LiveState(q, cur), q.Note, q.CreatedAt, q.DecidedAt, oldV, q.New, pw));
        }
        return list;
    }

    /// <summary>
    /// The PC changes ITS OWN request still pending (new entry or edit of an entry): the administrator sees the latest values.
    /// "ok" | "notfound" (not its own or missing) | "state" (already decided) | "kind" (not an entry request) | "input:field"
    /// </summary>
    public static string EditMine(string pcId, string reqId, Input i)
    {
        if (Conns.Validate(ToConn(i)) is { } bad) return "input:" + bad;
        using var c = Pcs.Open();
        var q = Read(c, "q.id = $i AND q.pc_id = $p", ("$i", reqId), ("$p", pcId)).FirstOrDefault();
        if (q == null) return "notfound";
        if (q.State != "pending") return "state";
        if (q.Kind is not ("create" or "update")) return "kind";
        int n = Pcs.Cmd(c, "UPDATE requests SET folder = $f, name = $n, protocol = $p, host = $h, port = $o, user = $u, pw_enc = $w, tags = $t, ucfg = $ug, users_enc = $ue WHERE id = $i AND pc_id = $pc AND state = 'pending'",
            ("$f", i.Folder ?? ""), ("$n", i.Name!.Trim()), ("$p", i.Protocol), ("$h", i.Host!.Trim()), ("$o", i.Port), ("$u", i.User ?? ""), ("$w", Vault.Encrypt(i.Password ?? "", reqId)),
            ("$t", i.Tags ?? ""), ("$ug", i.Ucfg), ("$ue", Conns.EncU(i.Lusers, reqId)), ("$i", reqId), ("$pc", pcId)).ExecuteNonQuery();
        if (n != 1) return "state";
        Pcs.Log(c, pcId, "request-edited", $"{q.Kind} {i.Name!.Trim()}");
        return "ok";
    }

    /// <summary>the PC withdraws ITS OWN pending request (e.g. it deletes the new entry it had proposed). "ok" | "notfound" | "state"</summary>
    public static string WithdrawMine(string pcId, string reqId)
    {
        using var c = Pcs.Open();
        var q = Read(c, "q.id = $i AND q.pc_id = $p", ("$i", reqId), ("$p", pcId)).FirstOrDefault();
        if (q == null) return "notfound";
        if (q.State != "pending") return "state";
        int n = Pcs.Cmd(c, "UPDATE requests SET state = 'withdrawn', decided_at = $d, pw_enc = NULL, users_enc = NULL WHERE id = $i AND pc_id = $p AND state = 'pending'",
            ("$d", Pcs.Now()), ("$i", reqId), ("$p", pcId)).ExecuteNonQuery();
        if (n != 1) return "state";
        Pcs.Log(c, pcId, "request-withdrawn", $"{q.Kind} {q.New?.Name ?? q.ConnId}");
        return "ok";
    }

    /// <summary>for the PC: its pending requests and those decided in the last 30 days</summary>
    public static List<Mine> ListMine(string pcId)
    {
        using var c = Pcs.Open();
        var since = DateTime.UtcNow.AddDays(-30).ToString("o");
        return Read(c, "q.pc_id = $p AND (q.state = 'pending' OR q.decided_at > $s)", ("$p", pcId), ("$s", since))
            .Select(q => new Mine(q.Id, q.Kind, q.ConnId, LiveState(q, Current(c, q.ConnId)), q.Note, q.DecidedAt)).ToList();
    }

    /// <summary>"ok" | "notfound" (request or entry no longer there) | "state" (already decided)</summary>
    public static string Decide(string id, bool approve, string? note, string? admin = null)
    {
        using var c = Pcs.Open();
        var q = Read(c, "q.id = $i", ("$i", id)).FirstOrDefault();
        if (q == null) return "notfound";
        if (q.State != "pending") return "state";
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        string? connId = q.ConnId;
        // what the entry was before the decision (and whether the request changed the password), kept for the history
        var before = Current(c, q.ConnId);
        bool pwChanged = q.PwEnc != null && Vault.Decrypt(q.PwEnc, q.Id) != (before?.pwEnc is { } pe ? Vault.Decrypt(pe, q.ConnId!) : "");
        var snapV = before?.v;
        if (FcDev(q.Kind)) (snapV, pwChanged) = FcDevOld(c, q);   // credentials/devices: the previous state comes from their own table
        var oldJson = System.Text.Json.JsonSerializer.Serialize(new Snap(snapV, pwChanged));
        if (approve && q.Kind == "folder-rename")
        {
            // same rename as the administrator: folders, connections, devices and credentials inside follow the new path
            if (q.New == null || Shared.Rename(q.New.Folder, q.New.Name, q.PcId, mayMoveShared: true).status != "ok") return "notfound";
        }
        else if (approve && q.Kind == "fcred-user")
        {
            // same credential (same id, so the servers using it stay linked): only the user changes
            var fid = q.New == null ? null : FindFcred(c, q.New.Folder, q.New.User);
            if (fid == null) return "notfound";
            if (Shared.SaveFcred(new Shared.FcIn(fid, q.New!.Folder, q.New.Name, null), q.PcId).status != "ok") return "notfound";
        }
        else if (approve && q.Kind is "fcred-create" or "fcred-update" or "fcred-delete")
        {
            // applied like the administrator (without readOnly): creates/edits/deletes the folder credential
            var folder = q.New?.Folder ?? ""; var user = q.New?.User ?? "";
            string? pw = q.PwEnc == null ? null : Vault.Decrypt(q.PwEnc, q.Id);   // null = do not change the password
            var st = q.Kind switch
            {
                "fcred-create" => Shared.SaveFcred(new Shared.FcIn(null, folder, user, pw ?? ""), q.PcId).status,
                "fcred-update" => q.ConnId == null ? "notfound" : Shared.SaveFcred(new Shared.FcIn(q.ConnId, folder, user, pw), q.PcId).status,
                _ => q.ConnId != null && Shared.DeleteFcred(q.ConnId, q.PcId) ? "ok" : "notfound",   // fcred-delete
            };
            if (st != "ok") return "notfound";
        }
        else if (approve && q.Kind is "device-create" or "device-update" or "device-delete")
        {
            var users = new List<Shared.UserPw>();
            if (Pcs.Cmd(c, "SELECT users_enc FROM requests WHERE id = $i", ("$i", q.Id)).ExecuteScalar() is string ue)
                users = System.Text.Json.JsonSerializer.Deserialize<List<Shared.UserPw>>(Vault.Decrypt(ue, q.Id)) ?? new();
            var folder = q.New?.Folder ?? ""; var name = q.New?.Name ?? ""; var ip = q.New?.Host ?? "";
            var st = q.Kind switch
            {
                "device-create" => Shared.SaveDevice(new Shared.DevIn(q.ConnId != null && !DevLive(c, q.ConnId) ? q.ConnId : null, folder, name, ip, users), q.PcId).status,   // proposed id, unless it has become live in the meantime
                "device-update" => q.ConnId == null ? "notfound" : Shared.SaveDevice(new Shared.DevIn(q.ConnId, folder, name, ip, users), q.PcId).status,
                _ => q.ConnId != null && Shared.DeleteDevice(q.ConnId, q.PcId) ? "ok" : "notfound",   // device-delete
            };
            if (st != "ok") return "notfound";
        }
        else if (approve)
        {
            // ponytail: no lock between two concurrent approvals of the same request (single admin); if needed, first mark it 'approving' with a conditional UPDATE
            var input = q.New == null ? null : new Conns.Input(q.New.Folder, q.New.Name, q.New.Protocol, q.New.Host, q.New.Port, q.New.User, q.PwEnc == null ? null : Vault.Decrypt(q.PwEnc, q.Id), q.New.Tags, null, q.Ucfg, q.Lusers);
            switch (q.Kind)
            {
                case "update" when Conns.TryUpdate(q.ConnId!, input!) != "ok": return "notfound"; // overwrites even if "stale": the admin saw the warning
                case "delete" when !Conns.Delete(q.ConnId!, q.PcId, admin ?? "?"): return "notfound";   // in the trash: "request from <PC>, approved by <admin>"
                case "create": connId = Conns.Create(input!); break;
            }
        }
        int n = Pcs.Cmd(c, "UPDATE requests SET state = $s, note = $n, decided_at = $d, conn_id = $c, pw_enc = NULL, old_json = $oj WHERE id = $i AND state = 'pending'",
            ("$s", approve ? "approved" : "rejected"), ("$n", note), ("$d", Pcs.Now()), ("$c", connId), ("$oj", oldJson), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, q.PcId, approve ? "request-approved" : "request-rejected", $"{q.Kind} {q.New?.Name ?? connId}{(note != null ? " - " + note : "")}");
        return n == 1 ? "ok" : "state";
    }
}

/// <summary>
/// Capabilities declared by the client in every call with the "X-Lemora-Caps: vnc,requests" header.
/// A client that does not declare them (v9 and earlier) is treated as "RDP/SSH only": no VNC entries and the "requests" permission becomes "read".
/// </summary>
internal static class Caps
{
    public static bool Has(HttpContext c, string cap) =>
        c.Request.Headers["X-Lemora-Caps"].ToString().Split(',').Any(x => x.Trim().Equals(cap, StringComparison.OrdinalIgnoreCase));

    /// <summary>capabilities in comparable form (lowercase, sorted, no duplicates): "" for an old client</summary>
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
        // ---- administrator ----
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
            return Reqs.Decide(id, action == "approve", r?.Note, Admins.Current(c)) switch
            {
                "ok" => Results.Json(new { ok = true }),
                "state" => Results.Json(new { error = "state" }, statusCode: 409),
                _ => Results.Json(new { error = "notfound" }, statusCode: 409),
            };
        });

        // ---- PC with "requests" permission: sends a request (with "write" it writes directly, with "read" nothing) ----
        api.MapPost("/client/requests", (HttpContext c, Reqs.Input i) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if (Caps.Perm(c, pc.Permission) != "requests") return Results.Json(new { error = "permission" }, statusCode: 403);
            if (i.Kind == "create" && pc.NoCopy) return Results.Json(new { error = "nocopy" }, statusCode: 403);   // like /client/connections: "do not copy" also forbids proposing new entries
            var (id, err) = Reqs.Create(pc.Id, i);
            if (err == "notfound") return Results.NotFound();
            if (err != null) return Results.Json(new { error = "input", field = err.StartsWith("input:") ? err[6..] : err }, statusCode: 400);
            return Results.Json(new { id, state = "pending" });
        });

        // ---- the same PC changes or withdraws one of ITS requests still pending (entry proposed and then edited or deleted) ----
        api.MapPost("/client/requests/{id}/{action}", (HttpContext c, string id, string action, Reqs.Input? i) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            if (action is not ("update" or "withdraw")) return Results.NotFound();
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if (Caps.Perm(c, pc.Permission) != "requests") return Results.Json(new { error = "permission" }, statusCode: 403);
            if (action == "update" && i == null) return Results.Json(new { error = "input", field = "body" }, statusCode: 400);
            var res = action == "withdraw" ? Reqs.WithdrawMine(pc.Id, id) : Reqs.EditMine(pc.Id, id, i!);
            return res switch
            {
                "ok" => Results.Json(new { ok = true }),
                "notfound" => Results.Json(new { error = "notfound" }, statusCode: 404),
                "state" => Results.Json(new { error = "state" }, statusCode: 409),
                _ => Results.Json(new { error = "input", field = res.StartsWith("input:") ? res[6..] : res }, statusCode: 400),
            };
        });
    }
}
