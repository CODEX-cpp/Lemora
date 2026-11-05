namespace Lemora.Server;

/// <summary>Registro attivita': gli eventi gia' scritti da Pcs.Log nella tabella events. Solo lettura, dal piu' recente. Nessuna password nei dettagli.</summary>
internal static class Events
{
    public record Row(string At, string? Pc, string Kind, string? Detail);

    public static (int total, List<Row> items) List(string? kind, string? pcId, int limit, int offset)
    {
        using var c = Pcs.Open();
        var where = "1 = 1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(kind)) { where += " AND e.kind = $k"; ps.Add(("$k", kind)); }
        if (!string.IsNullOrEmpty(pcId)) { where += " AND e.pc_id = $p"; ps.Add(("$p", pcId)); }
        int total = Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM events e WHERE " + where, ps.ToArray()).ExecuteScalar());
        ps.Add(("$l", limit)); ps.Add(("$o", offset));
        using var r = Pcs.Cmd(c, "SELECT e.at, p.pc || ' - ' || p.user, e.kind, e.detail FROM events e LEFT JOIN pcs p ON p.id = e.pc_id WHERE " + where +
            " ORDER BY e.id DESC LIMIT $l OFFSET $o", ps.ToArray()).ExecuteReader();
        var list = new List<Row>();
        while (r.Read()) list.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return (total, list);
    }
}

internal static class EventsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/events", (HttpContext c, string? kind, string? pc, int limit = 100, int offset = 0) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (limit is < 1 or > 200) limit = 100;
            if (offset < 0) offset = 0;
            var (total, items) = Events.List(kind, pc, limit, offset);
            return Results.Json(new { total, items });
        });
    }
}
