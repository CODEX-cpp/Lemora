using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Cestino: le voci condivise eliminate non spariscono subito, restano nella tabella connections con deleted_at valorizzato (vedi Conns.Delete).
/// Da qui si ripristinano (tornano ai PC) o si eliminano davvero. Nessuna modifica allo schema: deleted_at c'e' gia'.
/// </summary>
internal static class Trash
{
    public record Item(string Id, string Folder, string Name, string Protocol, string Host, int Port, string User, bool HasPassword, string Tags, string DeletedAt, string? By);

    public static List<Item> List()
    {
        using var c = Pcs.Open();
        // "chi": l'ultimo evento connection-deleted di quella voce (detail = id). pc_id vuoto = eliminata dall'amministratore in console.
        using var r = Pcs.Cmd(c,
            "SELECT x.id, x.folder, x.name, x.protocol, x.host, x.port, x.user, x.pw_enc IS NOT NULL, x.tags, x.deleted_at, p.pc || ' - ' || p.user " +
            "FROM connections x " +
            "LEFT JOIN (SELECT detail, pc_id, MAX(id) AS eid FROM events WHERE kind = 'connection-deleted' GROUP BY detail) e ON e.detail = x.id " +
            "LEFT JOIN pcs p ON p.id = e.pc_id " +
            "WHERE x.deleted_at IS NOT NULL ORDER BY x.deleted_at DESC").ExecuteReader();
        var list = new List<Item>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetInt32(7) == 1, r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }

    public static int Count()
    {
        using var c = Pcs.Open();
        return Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM connections WHERE deleted_at IS NOT NULL").ExecuteScalar());
    }

    /// <summary>torna tra le connessioni condivise: nuova rev e versione+1 cosi' i PC la riscaricano</summary>
    public static bool Restore(string id)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = Pcs.Cmd(c, "UPDATE connections SET deleted_at = NULL, version = version + 1, rev = $r, updated_at = $t WHERE id = $i AND deleted_at IS NOT NULL",
            ("$r", Conns.NextRev(c)), ("$t", Pcs.Now()), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, null, "connection-restored", id);
        tx.Commit();
        return n == 1;
    }

    /// <summary>eliminazione definitiva: la riga sparisce davvero (con lei la password cifrata)</summary>
    public static bool Purge(string id)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        string? name;
        using (var rr = Pcs.Cmd(c, "SELECT name FROM connections WHERE id = $i AND deleted_at IS NOT NULL", ("$i", id)).ExecuteReader())
            name = rr.Read() ? rr.GetString(0) : null;
        if (name == null) { tx.Rollback(); return false; }
        // ponytail: le eventuali righe personal con conn_id = id restano come storico (riferimento penzolante, innocuo); niente cascata
        Pcs.Cmd(c, "DELETE FROM connections WHERE id = $i AND deleted_at IS NOT NULL", ("$i", id)).ExecuteNonQuery();
        Pcs.Log(c, null, "connection-purged", name);
        tx.Commit();
        return true;
    }

    /// <summary>svuota il cestino: elimina definitivamente tutte le voci nel cestino; torna quante</summary>
    public static int Empty()
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = Pcs.Cmd(c, "DELETE FROM connections WHERE deleted_at IS NOT NULL").ExecuteNonQuery();
        if (n > 0) Pcs.Log(c, null, "trash-emptied", $"{n} voci eliminate definitivamente");
        tx.Commit();
        return n;
    }
}

internal static class TrashApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/trash", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Trash.List()));

        api.MapPost("/trash/empty", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(new { purged = Trash.Empty() }));

        api.MapPost("/trash/{id}/restore", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Trash.Restore(id) ? Results.Json(new { ok = true }) : Results.NotFound());

        api.MapPost("/trash/{id}/purge", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Trash.Purge(id) ? Results.Json(new { ok = true }) : Results.NotFound());
    }
}
