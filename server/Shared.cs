using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Things shared with all linked PCs besides connections: devices with their credentials (password manager), folders (with domain and IP prefix)
/// and folder credentials. Same revision counter (meta 'rev') and same since cursor as connections.
/// Deletions are "tombstones" (deleted_at): the PCs download them and remove the entry. Folder paths use '/' and are compared case-insensitively.
/// </summary>
internal static class Shared
{
    public record UserPw(string? User, string? Password);
    /// <param name="MoreIps">addresses besides Ip (e.g. a NAS with two network cards)</param>
    public record DevItem(string Id, int Version, string Folder, string Name, string Ip, List<UserPw> Users, bool Deleted, List<string> MoreIps);
    public record FolderItem(string Path, string Domain, string IpPrefix, int Version, bool Deleted);
    public record FcredItem(string Id, int Version, string Folder, string User, string Password, bool Deleted);

    /// <param name="MoreIps">null = keep the saved ones (PCs with an older Lemora do not send them and must not erase them)</param>
    public record DevIn(string? Id, string? Folder, string? Name, string? Ip, List<UserPw>? Users, int? BaseVersion = null, List<string>? MoreIps = null);
    public record FcIn(string? Id, string? Folder, string? User, string? Password, int? BaseVersion = null);
    public record FolderIn(string? Path, string? Domain, string? IpPrefix);

    private static bool Clean(string? s, int max, bool required = false) => Conns.Clean(s, max, required);
    private static bool PwOk(string? p) => p == null || (p.Length <= 500 && !p.Any(c => char.IsControl(c) && c != '\t'));
    private static readonly Regex Hex32 = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private static string? GoodId(string? id) => id != null && Hex32.IsMatch(id) ? id.ToLowerInvariant() : null;

    /// <summary>true if the column is folder $f or lies under $f/ (case-insensitive; no LIKE, so % and _ are not wildcards)</summary>
    private static string Under(string col) => $"({col} = $f COLLATE NOCASE OR substr({col}, 1, length($f) + 1) = ($f || '/') COLLATE NOCASE)";

    // ---- validation: null if fine, otherwise the name of the wrong field ----

    public static string? Validate(DevIn i)
    {
        if (!Clean(i.Name, 200, true)) return "name";
        if (!Clean(i.Folder ?? "", 200)) return "folder";
        if (!Clean(i.Ip ?? "", 100)) return "ip";
        if (i.MoreIps != null && (i.MoreIps.Count > 20 || i.MoreIps.Any(x => x == null || !Clean(x, 100, true)))) return "ip";
        var users = i.Users ?? new();
        if (users.Count > 20) return "users";
        foreach (var u in users)
        {
            if (u == null || !Clean(u.User ?? "", 200)) return "users";
            if (!PwOk(u.Password)) return "users";
        }
        return null;
    }

    public static string? Validate(FcIn i)
    {
        if (!Clean(i.User, 200, true)) return "user";
        // the folder is mandatory: without it, on the PCs it became a normal Vault credential that no revocation removes
        if (CleanPath(i.Folder) == null) return "folder";
        if (!PwOk(i.Password)) return "password";
        return null;
    }

    /// <summary>normalized path, or null if invalid (empty, too long, control characters)</summary>
    public static string? CleanPath(string? p) => Clean(p, 200, true) && Conns.NormFolder(p) is { Length: > 0 and <= 200 } n ? n : null;

    // ---- devices ----

    private static string UsersJson(List<UserPw>? users) =>
        JsonSerializer.Serialize((users ?? new()).Select(u => new UserPw((u.User ?? "").Trim(), u.Password ?? "")).ToList());

    private static DevItem ReadDev(SqliteDataReader r)
    {
        var id = r.GetString(0);
        bool deleted = r.GetInt32(6) == 1;
        var users = deleted || r.IsDBNull(5) ? new List<UserPw>() : JsonSerializer.Deserialize<List<UserPw>>(Vault.Decrypt(r.GetString(5), "dev:" + id)) ?? new();
        return new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), users, deleted, IpsFromDb(r.IsDBNull(7) ? null : r.GetString(7)));
    }
    private const string DevCols = "id, version, folder, name, ip, users_enc, deleted_at IS NOT NULL, more_ips";

    // the other addresses of a device: JSON list in the more_ips column (NULL = none)
    internal static List<string> IpsFromDb(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }
    internal static string? IpsToDb(List<string>? ips)
    {
        var l = (ips ?? new()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return l.Count == 0 ? null : JsonSerializer.Serialize(l);
    }

    public static DevItem? GetDev(string id)
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, $"SELECT {DevCols} FROM devices WHERE id = $i", ("$i", id)).ExecuteReader();
        return r.Read() ? ReadDev(r) : null;
    }

    /// <summary>"ok" | "conflict" | "notfound". Unknown id without baseVersion = new device (with the proposed id if it is 32 hex digits)</summary>
    /// <param name="readOnly">read-only PC: can add users and change passwords, but cannot remove a user or blank their password
    /// (it would be a deletion disguised as an edit) -> "permission"</param>
    public static (string status, string id) SaveDevice(DevIn i, string? pcId, bool readOnly = false)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var id = GoodId(i.Id);
        // read-only or 'requests' PC (readOnly): no direct writes - create, add/remove users, change passwords, nothing.
        // Read-only can only read; the changes of 'requests' PCs go through approvals (without readOnly).
        // Read-only used to be able to add users and change passwords: no longer.
        if (readOnly) return ("permission", id ?? "");
        int? version = null; bool tomb = false;
        if (id != null)
            using (var r = Pcs.Cmd(c, "SELECT version, deleted_at IS NOT NULL FROM devices WHERE id = $i", ("$i", id)).ExecuteReader())
                if (r.Read()) { version = r.GetInt32(0); tomb = r.GetInt32(1) == 1; }
        var vals = new (string, object?)[] { ("$f", Conns.NormFolder(i.Folder)), ("$n", i.Name!.Trim()), ("$p", (i.Ip ?? "").Trim()), ("$m", IpsToDb(i.MoreIps)), ("$keep", i.MoreIps == null ? 1 : 0) };
        if (version == null)
        {
            if (i.BaseVersion != null && id != null) return ("notfound", id); // it wanted to edit a device that does not exist
            id ??= Guid.NewGuid().ToString("N");
            Pcs.Cmd(c, "INSERT INTO devices (id, folder, name, ip, more_ips, users_enc, version, rev, updated_at) VALUES ($i, $f, $n, $p, $m, $u, 1, $r, $d)",
                vals.Concat(new (string, object?)[] { ("$i", id), ("$u", Vault.Encrypt(UsersJson(i.Users), "dev:" + id)), ("$r", Conns.NextRev(c)), ("$d", Pcs.Now()) }).ToArray()).ExecuteNonQuery();
            Pcs.Log(c, pcId, "device-created", i.Name.Trim());
        }
        else
        {
            if (tomb && i.BaseVersion != null) return ("notfound", id!);
            if (!tomb && i.BaseVersion != null && i.BaseVersion != version) return ("conflict", id!);
            Pcs.Cmd(c, "UPDATE devices SET folder=$f, name=$n, ip=$p, more_ips=CASE WHEN $keep = 1 THEN more_ips ELSE $m END, users_enc=$u, deleted_at=NULL, version=version+1, rev=$r, updated_at=$d WHERE id=$i",
                vals.Concat(new (string, object?)[] { ("$i", id), ("$u", Vault.Encrypt(UsersJson(i.Users), "dev:" + id)), ("$r", Conns.NextRev(c)), ("$d", Pcs.Now()) }).ToArray()).ExecuteNonQuery();
            Pcs.Log(c, pcId, tomb ? "device-created" : "device-updated", i.Name!.Trim());
        }
        tx.Commit();
        return ("ok", id!);
    }

    public static bool DeleteDevice(string id, string? pcId)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var what = Pcs.Cmd(c, "SELECT folder || ' / ' || name FROM devices WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteScalar() as string ?? id;
        int n =Pcs.Cmd(c, "UPDATE devices SET deleted_at=$d, users_enc=NULL, version=version+1, rev=$r, updated_at=$d WHERE id=$i AND deleted_at IS NULL", ("$d", Pcs.Now()), ("$r", Conns.NextRev(c)), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, pcId, "device-deleted", what);
        tx.Commit();
        return n == 1;
    }

    // ---- folder credentials ----

    private static FcredItem ReadFc(SqliteDataReader r)
    {
        var id = r.GetString(0);
        bool deleted = r.GetInt32(6) == 1;
        return new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), deleted || r.IsDBNull(4) ? "" : Vault.Decrypt(r.GetString(4), "fc:" + id), deleted);
    }
    private const string FcCols = "id, version, folder, user, pw_enc, 0, deleted_at IS NOT NULL";

    public static FcredItem? GetFc(string id)
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, $"SELECT {FcCols} FROM fcreds WHERE id = $i", ("$i", id)).ExecuteReader();
        return r.Read() ? ReadFc(r) : null;
    }

    /// <summary>"ok" | "conflict" | "notfound" | "exists" (same folder and user already present). Null password on edit = left as is, "" = removed</summary>
    /// <param name="readOnly">read-only PC: cannot remove the password from a credential that has one -> "permission"</param>
    public static (string status, string id) SaveFcred(FcIn i, string? pcId, bool readOnly = false)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var id = GoodId(i.Id);
        // read-only or 'requests' PC (readOnly): no direct writes - create, change user/password/folder, nothing.
        // Read-only can only read; the changes of 'requests' PCs go through the administrator's approvals,
        // which call SaveFcred without readOnly (Requests.cs). Password changes and folder moves used to be allowed.
        if (readOnly) return ("permission", id ?? "");
        int? version = null; bool tomb = false; string? oldPw = null;
        if (id != null)
            using (var r = Pcs.Cmd(c, "SELECT version, deleted_at IS NOT NULL, pw_enc FROM fcreds WHERE id = $i", ("$i", id)).ExecuteReader())
                if (r.Read()) { version = r.GetInt32(0); tomb = r.GetInt32(1) == 1; oldPw = r.IsDBNull(2) ? null : r.GetString(2); }
        if (version == null && i.BaseVersion != null && id != null) return ("notfound", id);
        if (version != null && tomb && i.BaseVersion != null) return ("notfound", id!);
        if (version != null && !tomb && i.BaseVersion != null && i.BaseVersion != version) return ("conflict", id!);
        id ??= Guid.NewGuid().ToString("N");
        var folder = Conns.NormFolder(i.Folder); var user = i.User!.Trim();
        using (var r = Pcs.Cmd(c, "SELECT 1 FROM fcreds WHERE deleted_at IS NULL AND id <> $i AND folder = $f COLLATE NOCASE AND user = $u COLLATE NOCASE", ("$i", id), ("$f", folder), ("$u", user)).ExecuteReader())
            if (r.Read()) return ("exists", id);
        string? pw = i.Password == null ? (tomb ? null : oldPw) : i.Password.Length == 0 ? null : Vault.Encrypt(i.Password, "fc:" + id);
        if (version == null)
        {
            Pcs.Cmd(c, "INSERT INTO fcreds (id, folder, user, pw_enc, version, rev, updated_at) VALUES ($i, $f, $u, $w, 1, $r, $d)",
                ("$i", id), ("$f", folder), ("$u", user), ("$w", pw), ("$r", Conns.NextRev(c)), ("$d", Pcs.Now())).ExecuteNonQuery();
            Pcs.Log(c, pcId, "fcred-created", $"{folder} / {user}");
        }
        else
        {
            Pcs.Cmd(c, "UPDATE fcreds SET folder=$f, user=$u, pw_enc=$w, deleted_at=NULL, version=version+1, rev=$r, updated_at=$d WHERE id=$i",
                ("$i", id), ("$f", folder), ("$u", user), ("$w", pw), ("$r", Conns.NextRev(c)), ("$d", Pcs.Now())).ExecuteNonQuery();
            Pcs.Log(c, pcId, tomb ? "fcred-created" : "fcred-updated", $"{folder} / {user}" + (i.Password != null ? " (password changed)" : ""));
        }
        tx.Commit();
        return ("ok", id);
    }

    public static bool DeleteFcred(string id, string? pcId)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var what = Pcs.Cmd(c, "SELECT folder || ' / ' || user FROM fcreds WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteScalar() as string ?? id;
        int n =Pcs.Cmd(c, "UPDATE fcreds SET deleted_at=$d, pw_enc=NULL, version=version+1, rev=$r, updated_at=$d WHERE id=$i AND deleted_at IS NULL", ("$d", Pcs.Now()), ("$r", Conns.NextRev(c)), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, pcId, "fcred-deleted", what);
        tx.Commit();
        return n == 1;
    }

    // ---- folders ----

    private static FolderItem ReadFolder(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4) == 1);
    private const string FolderCols = "path, domain, ip_prefix, version, deleted_at IS NOT NULL";

    /// <summary>creates the folder or changes its settings; if it was deleted, restores it. If nothing changes the revision is not touched</summary>
    public static FolderItem SaveFolder(string path, string domain, string ipPrefix, string? pcId)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        FolderItem? old;
        using (var r = Pcs.Cmd(c, $"SELECT {FolderCols} FROM folders WHERE path = $p", ("$p", path)).ExecuteReader()) old = r.Read() ? ReadFolder(r) : null;
        if (old != null && !old.Deleted && old.Domain == domain && old.IpPrefix == ipPrefix) return old;
        if (old == null)
            Pcs.Cmd(c, "INSERT INTO folders (path, domain, ip_prefix, version, rev, updated_at) VALUES ($p, $d, $i, 1, $r, $u)",
                ("$p", path), ("$d", domain), ("$i", ipPrefix), ("$r", Conns.NextRev(c)), ("$u", Pcs.Now())).ExecuteNonQuery();
        else
            Pcs.Cmd(c, "UPDATE folders SET path=$p, domain=$d, ip_prefix=$i, deleted_at=NULL, version=version+1, rev=$r, updated_at=$u WHERE path=$o",
                ("$p", old.Deleted ? path : old.Path), ("$o", old.Path), ("$d", domain), ("$i", ipPrefix), ("$r", Conns.NextRev(c)), ("$u", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, old == null || old.Deleted ? "folder-created" : "folder-updated", path);
        FolderItem res;
        using (var r = Pcs.Cmd(c, $"SELECT {FolderCols} FROM folders WHERE path = $p", ("$p", path)).ExecuteReader()) { r.Read(); res = ReadFolder(r); }
        tx.Commit();
        return res;
    }

    /// <summary>"ok" | "notempty" (a live shared connection is inside) | "notfound". Also deletes subfolders and, in cascade, devices and credentials</summary>
    /// <param name="readOnly">read-only PC: cannot delete devices and credentials, so not even a folder that contains some (the cascade would delete them)</param>
    public static string DeleteFolder(string path, string? pcId, bool readOnly = false)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        using (var r = Pcs.Cmd(c, $"SELECT 1 FROM connections WHERE deleted_at IS NULL AND {Under("folder")} LIMIT 1", ("$f", path)).ExecuteReader())
            if (r.Read()) return "notempty";
        if (readOnly)
            foreach (var t in new[] { "devices", "fcreds" })
                using (var r = Pcs.Cmd(c, $"SELECT 1 FROM {t} WHERE deleted_at IS NULL AND {Under("folder")} LIMIT 1", ("$f", path)).ExecuteReader())
                    if (r.Read()) return "notempty";
        var p = new (string, object?)[] { ("$f", path), ("$d", Pcs.Now()), ("$r", Conns.NextRev(c)) };
        int n = Pcs.Cmd(c, $"UPDATE folders SET deleted_at=$d, version=version+1, rev=$r, updated_at=$d WHERE deleted_at IS NULL AND {Under("path")}", p).ExecuteNonQuery();
        n += Pcs.Cmd(c, $"UPDATE devices SET deleted_at=$d, users_enc=NULL, version=version+1, rev=$r, updated_at=$d WHERE deleted_at IS NULL AND {Under("folder")}", p).ExecuteNonQuery();
        n += Pcs.Cmd(c, $"UPDATE fcreds SET deleted_at=$d, pw_enc=NULL, version=version+1, rev=$r, updated_at=$d WHERE deleted_at IS NULL AND {Under("folder")}", p).ExecuteNonQuery();
        if (n == 0) return "notfound";
        Pcs.Log(c, pcId, "folder-deleted", path);
        tx.Commit();
        return "ok";
    }

    public record RenameCounts(int Folders, int Connections, int Devices, int Fcreds, int Personal);

    /// <summary>
    /// "ok" | "notfound" | "exists" | "into_itself". Moves the prefix in folders, connections, devices, credentials and personal connections (version+1 and new revision).
    /// For folders the old path stays as a tombstone and the new one becomes a live row: so PCs that never saw it download both.
    /// Case-only change: the row stays the same.
    /// </summary>
    /// <param name="mayMoveShared">false = the PC has no "write": if live shared connections are inside it answers "permission" (moving them is editing them)</param>
    public static (string status, RenameCounts? counts) Rename(string from, string to, string? pcId, bool mayMoveShared = true)
    {
        if (to.Length > from.Length && to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) return ("into_itself", null);
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        var folders = new List<FolderItem>();
        using (var r = Pcs.Cmd(c, $"SELECT {FolderCols} FROM folders WHERE deleted_at IS NULL AND {Under("path")}", ("$f", from)).ExecuteReader())
            while (r.Read()) folders.Add(ReadFolder(r));
        bool Any(string table, string col, string extra) { using var r = Pcs.Cmd(c, $"SELECT 1 FROM {table} WHERE {extra}{Under(col)} LIMIT 1", ("$f", from)).ExecuteReader(); return r.Read(); }
        bool hasShared = Any("connections", "folder", "deleted_at IS NULL AND ");
        // a PC without "write" cannot move folder credentials and devices either: SaveFcred/SaveDevice already forbid it
        if (!mayMoveShared && (hasShared || Any("devices", "folder", "deleted_at IS NULL AND ") || Any("fcreds", "folder", "deleted_at IS NULL AND "))) return ("permission", null);
        if (folders.Count == 0 && !hasShared && !Any("devices", "folder", "deleted_at IS NULL AND ")
            && !Any("fcreds", "folder", "deleted_at IS NULL AND ") && !Any("personal", "folder", "")) return ("notfound", null);
        if (string.Equals(from, to, StringComparison.Ordinal)) return ("ok", new(0, 0, 0, 0, 0));
        string Tail(string p) => to + p[from.Length..];
        if (!caseOnly)
        {
            // an already live destination path (outside those being moved) blocks the move
            // (Prepend(from): the destination folder itself, even if `from` has no row of its own)
            foreach (var path in folders.Select(f => f.Path).Prepend(from).Select(Tail).Distinct(StringComparer.OrdinalIgnoreCase))
                using (var r = Pcs.Cmd(c, "SELECT 1 FROM folders WHERE deleted_at IS NULL AND path = $p", ("$p", path)).ExecuteReader())
                    if (r.Read()) return ("exists", null);
            // a moved credential must not end up next to a live one with the same user (same check as SaveFcred): the PCs would use one password for the other
            using (var r = Pcs.Cmd(c, "SELECT 1 FROM fcreds a JOIN fcreds b ON b.deleted_at IS NULL AND b.id <> a.id AND b.user = a.user COLLATE NOCASE"
                + $" AND b.folder = ($t || substr(a.folder, length($f) + 1)) COLLATE NOCASE WHERE a.deleted_at IS NULL AND {Under("a.folder")} AND NOT {Under("b.folder")} LIMIT 1",
                ("$f", from), ("$t", to)).ExecuteReader())
                if (r.Read()) return ("exists", null);
        }
        var rev = Conns.NextRev(c); var now = Pcs.Now();
        foreach (var f in folders)
        {
            var np = Tail(f.Path);
            if (caseOnly)
                Pcs.Cmd(c, "UPDATE folders SET path=$n, version=version+1, rev=$r, updated_at=$d WHERE path=$o", ("$n", np), ("$o", f.Path), ("$r", rev), ("$d", now)).ExecuteNonQuery();
            else
            {
                Pcs.Cmd(c, "UPDATE folders SET deleted_at=$d, version=version+1, rev=$r, updated_at=$d WHERE path=$o", ("$o", f.Path), ("$r", rev), ("$d", now)).ExecuteNonQuery();
                int u = Pcs.Cmd(c, "UPDATE folders SET path=$n, domain=$m, ip_prefix=$i, deleted_at=NULL, version=version+1, rev=$r, updated_at=$d WHERE path=$n",
                    ("$n", np), ("$m", f.Domain), ("$i", f.IpPrefix), ("$r", rev), ("$d", now)).ExecuteNonQuery(); // existing tombstone on the new path: reused
                if (u == 0)
                    Pcs.Cmd(c, "INSERT INTO folders (path, domain, ip_prefix, version, rev, updated_at) VALUES ($n, $m, $i, 1, $r, $d)",
                        ("$n", np), ("$m", f.Domain), ("$i", f.IpPrefix), ("$r", rev), ("$d", now)).ExecuteNonQuery();
            }
        }
        var p = new (string, object?)[] { ("$f", from), ("$t", to), ("$r", rev), ("$d", now) };
        const string set = "folder = $t || substr(folder, length($f) + 1), version = version + 1, updated_at = $d";
        int nc = Pcs.Cmd(c, $"UPDATE connections SET {set}, rev = $r WHERE deleted_at IS NULL AND {Under("folder")}", p).ExecuteNonQuery();
        int nd = Pcs.Cmd(c, $"UPDATE devices SET {set}, rev = $r WHERE deleted_at IS NULL AND {Under("folder")}", p).ExecuteNonQuery();
        int nf = Pcs.Cmd(c, $"UPDATE fcreds SET {set}, rev = $r WHERE deleted_at IS NULL AND {Under("folder")}", p).ExecuteNonQuery();
        int np2 = Pcs.Cmd(c, $"UPDATE personal SET {set} WHERE {Under("folder")}", p).ExecuteNonQuery();   // other PCs' proposals follow the folder too (intended: the team tree stays one)
        // published personal connections remember the shared connection's version: realign it, or the PC's next edit would conflict
        Pcs.Cmd(c, "UPDATE personal SET conn_version = (SELECT version FROM connections WHERE id = personal.conn_id) WHERE state = 'published' AND conn_id IN (SELECT id FROM connections WHERE rev = $r)", ("$r", rev)).ExecuteNonQuery();
        Pcs.Log(c, pcId, "folder-renamed", $"{from} -> {to}");
        tx.Commit();
        return ("ok", new(folders.Count, nc, nd, nf, np2));
    }

    // ---- sync ----

    /// <summary>rows changed after since (tombstones included, but not from zero); full = all live ones</summary>
    public static (List<DevItem> devices, List<FolderItem> folders, List<FcredItem> fcreds) Changes(long since, bool full)
    {
        using var c = Pcs.Open();
        const string where = "(rev > $s OR ($f = 1 AND deleted_at IS NULL)) AND ($s > 0 OR deleted_at IS NULL) ORDER BY rev";
        var p = new (string, object?)[] { ("$s", since), ("$f", full ? 1 : 0) };
        var dev = new List<DevItem>(); var fol = new List<FolderItem>(); var fc = new List<FcredItem>();
        using (var r = Pcs.Cmd(c, $"SELECT {DevCols} FROM devices WHERE {where}", p).ExecuteReader()) while (r.Read()) dev.Add(ReadDev(r));
        using (var r = Pcs.Cmd(c, $"SELECT {FolderCols} FROM folders WHERE {where}", p).ExecuteReader()) while (r.Read()) fol.Add(ReadFolder(r));
        using (var r = Pcs.Cmd(c, $"SELECT {FcCols} FROM fcreds WHERE {where}", p).ExecuteReader()) while (r.Read()) fc.Add(ReadFc(r));
        return (dev, fol, fc);
    }

    // ---- console (read-only, no passwords) ----

    public static object ListDevices()
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, $"SELECT {DevCols}, updated_at FROM devices WHERE deleted_at IS NULL ORDER BY folder, name COLLATE NOCASE").ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            var d = ReadDev(r);
            list.Add(new { d.Id, d.Folder, d.Name, d.Ip, d.MoreIps, UserCount = d.Users.Count, Users = d.Users.Select(u => u.User).ToList(), d.Version, UpdatedAt = r.GetString(8) });
        }
        return list;
    }

    /// <summary>live folders with how many live entries they contain (subfolders included): shared connections, devices, credentials. Entries = sum</summary>
    public static List<object> ListFolders()
    {
        using var c = Pcs.Open();
        var folders = new List<FolderItem>();
        using (var r = Pcs.Cmd(c, $"SELECT {FolderCols} FROM folders WHERE deleted_at IS NULL ORDER BY path").ExecuteReader())
            while (r.Read()) folders.Add(ReadFolder(r));
        int Count(string table, string path) => Convert.ToInt32(Pcs.Cmd(c, $"SELECT COUNT(*) FROM {table} WHERE deleted_at IS NULL AND {Under("folder")}", ("$f", path)).ExecuteScalar());
        return folders.Select(f =>
        {
            int cn = Count("connections", f.Path), dv = Count("devices", f.Path), fc = Count("fcreds", f.Path);
            return (object)new { f.Path, f.Domain, f.IpPrefix, f.Version, f.Deleted, Connections = cn, Devices = dv, Fcreds = fc, Entries = cn + dv + fc };
        }).ToList();
    }

    public static List<object> ListFcreds()
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, "SELECT id, folder, user, pw_enc IS NOT NULL, version FROM fcreds WHERE deleted_at IS NULL ORDER BY folder, user COLLATE NOCASE").ExecuteReader();
        var list = new List<object>();
        while (r.Read()) list.Add(new { Id = r.GetString(0), Folder = r.GetString(1), User = r.GetString(2), HasPassword = r.GetInt32(3) == 1, Version = r.GetInt32(4) });
        return list;
    }
}

internal static class SharedApi
{
    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    private record Rename(string? From, string? To);
    private record PathOnly(string? Path);
    private record Audit(string? Kind, string? What, string? Device, string? User);

    /// <summary>approved PC with the right key. Create and edit: every permission (including "read-only"; "requests" here does not go through approvals to create and change).
    /// deleting = deleting a device or a credential: forbidden to "read-only" and "requests" (403 permission): a deletion without the administrator's approval is a bypass. Folders require nothing more.</summary>
    private static (Pcs.PcRow? pc, IResult? err) Writer(HttpContext c, bool deleting = false)
    {
        if (!State.Config.Configured) return (null, Results.NotFound());
        if (!LocalClient(c)) return (null, Results.StatusCode(403));
        var bearer = c.Request.Headers.Authorization.ToString();
        bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
        var pc = Pcs.Authenticate(bearer);
        if (pc == null) return (null, Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401));
        if (deleting && pc.Permission is "read" or "requests") return (null, Results.Json(new { error = "permission" }, statusCode: 403));
        return (pc, null);
    }

    private static IResult Input(string field) => Results.Json(new { error = "input", field }, statusCode: 400);

    /// <summary>runs the write; passwords that cannot be decrypted (key changed) = 500 error key, as in the sync</summary>
    private static IResult Guard(Func<IResult> f)
    {
        try { return f(); }
        catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
    }

    public static void Map(RouteGroupBuilder api)
    {
        // ---- console (read-only) ----
        api.MapGet("/devices", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Guard(() => Results.Json(Shared.ListDevices())));
        api.MapGet("/folders", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Shared.ListFolders()));
        api.MapGet("/fcreds", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Shared.ListFcreds()));

        // ---- linked PC ----
        api.MapPost("/client/devices", (HttpContext c, Shared.DevIn i) =>
        {
            var (pc, err) = Writer(c);
            if (err != null) return err;
            if (Shared.Validate(i) is { } bad) return Input(bad);
            return Guard(() =>
            {
                var (st, id) = Shared.SaveDevice(i, pc!.Id, readOnly: pc.Permission is "read" or "requests");
                return st switch
                {
                    "ok" => Results.Json(new { item = Shared.GetDev(id) }),
                    "permission" => Results.Json(new { error = "permission" }, statusCode: 403),
                    "conflict" => Results.Json(new { error = "conflict" }, statusCode: 409),
                    _ => Results.NotFound(),
                };
            });
        });

        api.MapPost("/client/devices/{id}/delete", (HttpContext c, string id) =>
        {
            var (pc, err) = Writer(c, deleting: true);
            if (err != null) return err;
            return Shared.DeleteDevice(id, pc!.Id) ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        api.MapPost("/client/fcreds", (HttpContext c, Shared.FcIn i) =>
        {
            var (pc, err) = Writer(c);
            if (err != null) return err;
            if (Shared.Validate(i) is { } bad) return Input(bad);
            return Guard(() =>
            {
                var (st, id) = Shared.SaveFcred(i, pc!.Id, readOnly: pc.Permission is "read" or "requests");
                return st switch
                {
                    "ok" => Results.Json(new { item = Shared.GetFc(id) }),
                    "permission" => Results.Json(new { error = "permission" }, statusCode: 403),
                    "conflict" or "exists" => Results.Json(new { error = st }, statusCode: 409),
                    _ => Results.NotFound(),
                };
            });
        });

        api.MapPost("/client/fcreds/{id}/delete", (HttpContext c, string id) =>
        {
            var (pc, err) = Writer(c, deleting: true);
            if (err != null) return err;
            return Shared.DeleteFcred(id, pc!.Id) ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        // folders: read-only does not touch them (view only); "requests" and "write" create and edit them directly, without approval.
        // Shared data stays protected: a folder with devices/credentials is not deleted in cascade without "write" (readOnly in DeleteFolder)
        // and moving shared connections by renaming the folder requires "write" (mayMoveShared). Same logic for the PC and the administrator (pcId null = administrator)
        static IResult? NoRead(Pcs.PcRow pc) => pc.Permission == "read" ? Results.Json(new { error = "permission" }, statusCode: 403) : null;
        IResult SaveFolderReq(Shared.FolderIn i, string? pcId)
        {
            if (Shared.CleanPath(i.Path) is not { } path) return Input("path");
            if (!(i.Domain ?? "").All(ch => !char.IsControl(ch)) || (i.Domain ?? "").Length > 200) return Input("domain");
            if (!(i.IpPrefix ?? "").All(ch => !char.IsControl(ch)) || (i.IpPrefix ?? "").Length > 100) return Input("ipPrefix");
            return Results.Json(new { item = Shared.SaveFolder(path, (i.Domain ?? "").Trim(), (i.IpPrefix ?? "").Trim(), pcId) });
        }
        IResult DeleteFolderReq(PathOnly i, string? pcId, bool readOnly = false)
        {
            if (Shared.CleanPath(i.Path) is not { } path) return Input("path");
            return Shared.DeleteFolder(path, pcId, readOnly) switch
            {
                "ok" => Results.Json(new { ok = true }),
                "notempty" => Results.Json(new { error = "notempty" }, statusCode: 409),
                _ => Results.NotFound(),
            };
        }
        IResult RenameReq(Rename i, string? pcId, bool mayMoveShared = true)
        {
            if (Shared.CleanPath(i.From) is not { } from) return Input("from");
            if (Shared.CleanPath(i.To) is not { } to) return Input("to");
            var (st, counts) = Shared.Rename(from, to, pcId, mayMoveShared);
            return st switch
            {
                "ok" => Results.Json(new { ok = true, from, to, moved = counts }),
                "permission" => Results.Json(new { error = "permission" }, statusCode: 403),
                "exists" or "into_itself" => Results.Json(new { error = st }, statusCode: 409),
                _ => Results.NotFound(),
            };
        }

        api.MapPost("/client/folders", (HttpContext c, Shared.FolderIn i) => { var (pc, err) = Writer(c); return err ?? NoRead(pc!) ?? SaveFolderReq(i, pc!.Id); });
        // the permission is the one stored here for the key, never the one sent by the PC
        api.MapPost("/client/folders/delete", (HttpContext c, PathOnly i) => { var (pc, err) = Writer(c); return err ?? NoRead(pc!) ?? DeleteFolderReq(i, pc!.Id, readOnly: pc!.Permission != "write"); });
        api.MapPost("/client/folders/rename", (HttpContext c, Rename i) => { var (pc, err) = Writer(c); return err ?? NoRead(pc!) ?? RenameReq(i, pc!.Id, mayMoveShared: pc!.Permission == "write"); });

        // ---- console (administrator actions, recorded in the event log without a PC) ----
        api.MapPost("/folders", (HttpContext c, Shared.FolderIn i) => !Api.SignedIn(c) ? Results.StatusCode(401) : SaveFolderReq(i, null));
        api.MapPost("/folders/delete", (HttpContext c, PathOnly i) => !Api.SignedIn(c) ? Results.StatusCode(401) : DeleteFolderReq(i, null));
        api.MapPost("/folders/rename", (HttpContext c, Rename i) => !Api.SignedIn(c) ? Results.StatusCode(401) : RenameReq(i, null));
        api.MapPost("/devices/{id}/delete", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Shared.DeleteDevice(id, null) ? Results.Json(new { ok = true }) : Results.NotFound());
        api.MapPost("/fcreds/{id}/delete", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Shared.DeleteFcred(id, null) ? Results.Json(new { ok = true }) : Results.NotFound());

        // "credential copied": goes to the event log, never the password
        api.MapPost("/client/audit", (HttpContext c, Audit a) =>
        {
            var (pc, err) = Writer(c);
            if (err != null) return err;
            if (a.Kind != "copy") return Input("kind");
            if (a.What is not ("password" or "user" or "ip")) return Input("what");
            if (!Conns.Clean(a.Device ?? "", 200)) return Input("device");
            if (!Conns.Clean(a.User ?? "", 200)) return Input("user");
            using var db = Pcs.Open();
            Pcs.Log(db, pc!.Id, "copy", $"{a.What} copied: {a.Device} / {a.User}");
            return Results.Json(new { ok = true });
        });
    }
}
