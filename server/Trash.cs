using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Trash and history: deleted shared entries do not disappear, they stay in the connections table with deleted_at set (see Conns.Delete).
/// From here they are restored (they go back to the PCs). "Delete permanently" removes only the secrets (password and local users): the row stays as history
/// ("permanently deleted on ... by ...") and can still be restored, but the password must be re-entered.
/// </summary>
internal static class Trash
{
    /// <param name="ByKind">admin | pc | request | "" (entry deleted before the deleter was recorded)</param>
    /// <param name="By">admin: name; pc: "PC - user"; request: the PC that requested it</param>
    /// <param name="ApprovedBy">only for request: the administrator who approved it</param>
    public record Item(string Id, string Folder, string Name, string Protocol, string Host, int Port, string User, bool HasPassword, string Tags, string DeletedAt,
        string ByKind, string? By, string? ApprovedBy, string Users, string? PurgedAt, string? PurgedBy);

    private static string? S(SqliteDataReader r, int n) => r.IsDBNull(n) ? null : r.GetString(n);

    public static List<Item> List()
    {
        using var c = Pcs.Open();
        // "who": deleted_by (A:admin | P:pc | R:pc|admin); for entries deleted before this column existed it falls back to the last connection-deleted event (empty pc_id = administrator)
        using var r = Pcs.Cmd(c,
            "SELECT x.id, x.folder, x.name, x.protocol, x.host, x.port, x.user, x.pw_enc IS NOT NULL, x.tags, x.deleted_at, x.deleted_by, p.pc || ' - ' || p.user, x.ucfg, x.users_enc, x.purged_at, x.purged_by " +
            "FROM connections x " +
            "LEFT JOIN (SELECT detail, pc_id, MAX(id) AS eid FROM events WHERE kind = 'connection-deleted' GROUP BY detail) e ON e.detail = x.id " +
            "LEFT JOIN pcs p ON p.id = e.pc_id " +
            "WHERE x.deleted_at IS NOT NULL ORDER BY x.deleted_at DESC").ExecuteReader();
        var list = new List<Item>();
        while (r.Read())
        {
            string kind = "", by = ""; string? approved = null;
            var raw = S(r, 10);
            if (raw != null && raw.Length > 2 && raw[1] == ':')
            {
                var rest = raw.Substring(2);
                switch (raw[0])
                {
                    case 'A': kind = "admin"; by = rest; break;
                    case 'P': kind = "pc"; by = rest; break;
                    case 'R': kind = "request"; var i = rest.LastIndexOf('|'); by = i < 0 ? rest : rest.Substring(0, i); approved = i < 0 ? null : rest.Substring(i + 1); break;
                }
            }
            else if (S(r, 11) is { } legacy) { kind = "pc"; by = legacy; }
            var id = r.GetString(0);
            string users = "";
            try
            {
                var lus = Conns.DecU(S(r, 13), id);
                users = string.Join(" · ", new[] { Reqs.Summ(S(r, 12)), Conns.LocalNames(lus) }.Where(x => x.Length > 0));
            }
            catch (System.Security.Cryptography.CryptographicException) { /* key not readable: the users are not shown */ }
            list.Add(new(id, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetInt32(7) == 1, r.GetString(8), r.GetString(9),
                kind, by.Length == 0 ? null : by, approved, users, S(r, 14), S(r, 15)));
        }
        return list;
    }

    /// <summary>entries in the trash still to be decided (permanently deleted ones are history only)</summary>
    public static int Count()
    {
        using var c = Pcs.Open();
        return Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM connections WHERE deleted_at IS NOT NULL AND purged_at IS NULL").ExecuteScalar());
    }

    /// <summary>goes back among the shared connections: new rev and version+1 so the PCs download it again</summary>
    public static bool Restore(string id, string? admin = null)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = Pcs.Cmd(c, "UPDATE connections SET deleted_at = NULL, deleted_by = NULL, purged_at = NULL, purged_by = NULL, version = version + 1, rev = $r, updated_at = $t WHERE id = $i AND deleted_at IS NOT NULL",
            ("$r", Conns.NextRev(c)), ("$t", Pcs.Now()), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, null, "connection-restored", id);
        tx.Commit();
        return n == 1;
    }

    private const string PurgeSet = "purged_at = $t, purged_by = $b, pw_enc = NULL, users_enc = NULL";

    /// <summary>permanent deletion: password and local users are really gone, the row stays as history</summary>
    public static bool Purge(string id, string? admin = null)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        string? name;
        using (var rr = Pcs.Cmd(c, "SELECT name FROM connections WHERE id = $i AND deleted_at IS NOT NULL AND purged_at IS NULL", ("$i", id)).ExecuteReader())
            name = rr.Read() ? rr.GetString(0) : null;
        if (name == null) { tx.Rollback(); return false; }
        Pcs.Cmd(c, $"UPDATE connections SET {PurgeSet} WHERE id = $i AND deleted_at IS NOT NULL", ("$t", Pcs.Now()), ("$b", admin), ("$i", id)).ExecuteNonQuery();
        Pcs.Log(c, null, "connection-purged", name);
        tx.Commit();
        return true;
    }

    /// <summary>empties the trash: permanently deletes (passwords included) all entries still in the trash; returns how many</summary>
    public static int Empty(string? admin = null)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = Pcs.Cmd(c, $"UPDATE connections SET {PurgeSet} WHERE deleted_at IS NOT NULL AND purged_at IS NULL", ("$t", Pcs.Now()), ("$b", admin)).ExecuteNonQuery();
        if (n > 0) Pcs.Log(c, null, "trash-emptied", $"{n} items permanently deleted");
        tx.Commit();
        return n;
    }
}

internal static class TrashApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/trash", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Trash.List()));

        api.MapPost("/trash/empty", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(new { purged = Trash.Empty(Admins.Current(c)) }));

        api.MapPost("/trash/{id}/restore", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Trash.Restore(id, Admins.Current(c)) ? Results.Json(new { ok = true }) : Results.NotFound());

        api.MapPost("/trash/{id}/purge", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Trash.Purge(id, Admins.Current(c)) ? Results.Json(new { ok = true }) : Results.NotFound());
    }
}
