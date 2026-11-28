namespace Lemora.Server;

/// <summary>Activity log: the events already written by Pcs.Log in the events table. Read-only, newest first. No passwords in the details.</summary>
internal static class Events
{
    /// <param name="Admin">administrator who performed the action from the console (null for PCs and for old events)</param>
    public record Row(string At, string? Pc, string Kind, string? Detail, string? Admin);

    /// <param name="fromUtc">inclusive; <paramref name="toUtc"/> exclusive (instants are compared as ISO "o" text in UTC, as Pcs.Now writes them)</param>
    public static (int total, List<Row> items) List(string? kind, string? pcId, int limit, int offset, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        using var c = Pcs.Open();
        var where = "1 = 1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(kind)) { where += " AND e.kind = $k"; ps.Add(("$k", kind)); }
        if (!string.IsNullOrEmpty(pcId)) { where += " AND e.pc_id = $p"; ps.Add(("$p", pcId)); }
        if (fromUtc is { } f) { where += " AND e.at >= $f"; ps.Add(("$f", f.ToString("o"))); }
        if (toUtc is { } u) { where += " AND e.at < $u"; ps.Add(("$u", u.ToString("o"))); }
        int total = Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM events e WHERE " + where, ps.ToArray()).ExecuteScalar());
        ps.Add(("$l", limit)); ps.Add(("$o", offset));
        using var r = Pcs.Cmd(c, "SELECT e.at, p.pc || ' - ' || p.user, e.kind, e.detail, e.admin FROM events e LEFT JOIN pcs p ON p.id = e.pc_id WHERE " + where +
            " ORDER BY e.id DESC LIMIT $l OFFSET $o", ps.ToArray()).ExecuteReader();
        var list = new List<Row>();
        while (r.Read()) list.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return (total, list);
    }
}

internal static class EventsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/events", (HttpContext c, string? kind, string? pc, string? from, string? to, int limit = 100, int offset = 0) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (limit is < 1 or > 200) limit = 100;
            if (offset < 0) offset = 0;
            // date filter: ISO instants with time zone (the console sends start and end of the chosen days in the browser's time); other text = 400
            if (!Instant(from, out var f) || !Instant(to, out var u)) return Results.Json(new { error = "date" }, statusCode: 400);
            var (total, items) = Events.List(kind, pc, limit, offset, f, u);
            return Results.Json(new { total, items });
        });
    }

    /// <summary>empty = no limit; otherwise an instant with an explicit time zone (e.g. 2026-11-12T00:00:00.000Z), converted to UTC</summary>
    internal static bool Instant(string? s, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrEmpty(s)) return true;
        if (s.Length > 40 || !DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            || !(s.EndsWith("Z") || s.Contains('+') || s.LastIndexOf('-') > 9)) return false;   // without a time zone the time would be the server's, not the viewer's
        utc = d.UtcDateTime;
        return true;
    }
}
