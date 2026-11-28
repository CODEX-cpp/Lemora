using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Connection durations. For every connection a PC opens to a SHARED entry (RDP/SSH/VNC from the Teams server)
/// the client records start and end (entry, protocol, PC; NEVER the password) and uploads them at sync. The administrator sees, per PC and per entry, how many times,
/// how long and the last connection. A session without an end is "in progress"; if the PC has not been heard from for a while it shows as "interrupted" (end estimated from the last contact).
/// </summary>
internal static class Sessions
{
    /// <summary>a PC not seen for longer than this with an open session: the session is "interrupted", the end is estimated from the last contact (sync every 20 min)</summary>
    public const int StaleMinutes = 15;   // 3 sync rounds (every 5 minutes)

    /// <summary>Name/Host/Folder/Adhoc: only for connections to NON-shared entries (saved on the PC or ad hoc with an IP): shown in the console. Never the password</summary>
    public record In(string? Id, string? ConnId, string? Protocol, string? Start, string? End, string? Name = null, string? Host = null, string? Folder = null, bool? Adhoc = null);
    public record Submit(List<In>? Items);

    private record Raw(string PcId, string ConnId, string Protocol, DateTime Start, DateTime? End, string? Label = null, string? Host = null, string? Folder = null, bool Adhoc = false);

    /// <summary>upsert per (pc_id, id); the end, once written, never goes back. Returns how many valid rows were accepted</summary>
    public static int Apply(string pcId, Submit s)
    {
        if (s.Items == null || s.Items.Count == 0) return 0;
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = 0;
        foreach (var i in s.Items)
        {
            if (!Ok(i.Id, 64) || !Ok(i.ConnId, 64) || i.Protocol is not ("rdp" or "ssh" or "vnc")) continue;
            if (!TryUtc(i.Start, out var start)) continue;
            string? end = null;
            if (i.End != null) { if (!TryUtc(i.End, out var e) || e < start) continue; end = e.ToString("o"); }
            if (!Free(i.Name, 200) || !Free(i.Host, 200) || !Free(i.Folder, 200)) continue;
            Pcs.Cmd(c, "INSERT INTO sessions (id, pc_id, conn_id, protocol, started_at, ended_at, label, host, folder, adhoc) VALUES ($i, $p, $c, $pr, $s, $e, $l, $h, $f, $a) " +
                       "ON CONFLICT(pc_id, id) DO UPDATE SET ended_at = $e WHERE sessions.ended_at IS NULL",
                ("$i", i.Id!.Trim()), ("$p", pcId), ("$c", i.ConnId!.Trim()), ("$pr", i.Protocol), ("$s", start.ToString("o")), ("$e", end),
                ("$l", Nz(i.Name)), ("$h", Nz(i.Host)), ("$f", Nz(i.Folder)), ("$a", i.Adhoc == true ? "1" : null)).ExecuteNonQuery();
            n++;
        }
        tx.Commit();
        return n;
    }

    /// <summary>optional field: missing, or without control characters within the maximum</summary>
    private static bool Free(string? s, int max) => s == null || (s.Length <= max && !s.Any(char.IsControl));
    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static bool Ok(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);
    private static bool TryUtc(string? s, out DateTime t)
    {
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) { t = t.ToUniversalTime(); return true; }
        return false;
    }
    private static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public record PcRow(string PcId, string Pc, int Count, int Open, long TotalSeconds, string? LastAt);
    public record ConnRow(string ConnId, string? Name, int Count, int Open, int PcCount, long TotalSeconds, string? LastAt);
    /// <summary>Name: shared entry (from the server) or name/address sent by the PC. Folder: folder; null for the ad hoc connection (the console writes "unsaved entry").
    /// Host: address (only for non-shared entries). Adhoc: ad hoc connection to an unsaved IP</summary>
    public record Detail(string Pc, string ConnId, string? Name, string Protocol, string Start, string? End, long Seconds, string Status, string? Folder = null, string? Host = null, bool Adhoc = false);
    /// <summary>open: the connections in progress right now (who, to what, since when); recent: history (who, where, when, how long)</summary>
    public record Report(int staleMinutes, List<PcRow> byPc, List<ConnRow> byConn, List<Detail> recent, List<Detail> open);

    /// <summary>
    /// Aggregations for the console. The durations of open sessions and the "interrupted" state depend on the PC's last contact (last_seen) and the current time,
    /// so they are computed here, not in SQL. ponytail: O(n) in memory over all rows; for large volumes switch to sums in SQL. Detail limited to the most recent ones.
    /// </summary>
    /// <summary>names of PCs and shared entries, rows read and the common calculations (duration, state, row for the console)</summary>
    private sealed class Ctx
    {
        public readonly Dictionary<string, (string pc, DateTime? seen)> Names = new();
        public readonly Dictionary<string, string> ConnNames = new(), ConnFolders = new();
        public readonly List<Raw> Rows = new();
        private readonly DateTime _now = DateTime.UtcNow;

        /// <param name="where">SQL filter on the sessions table (parameters in <paramref name="ps"/>); rows newest first</param>
        public Ctx(SqliteConnection c, string where = "1 = 1", string tail = "LIMIT 20000", params (string, object?)[] ps)
        {
            using (var r = Pcs.Cmd(c, "SELECT id, pc || ' - ' || user, last_seen FROM pcs").ExecuteReader())
                while (r.Read()) Names[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? null : Utc(r.GetString(2)));
            using (var r = Pcs.Cmd(c, "SELECT id, name, folder FROM connections").ExecuteReader())
                while (r.Read()) { ConnNames[r.GetString(0)] = r.GetString(1); ConnFolders[r.GetString(0)] = r.GetString(2); }
            string? S(SqliteDataReader r, int n) => r.IsDBNull(n) ? null : r.GetString(n);
            using (var r = Pcs.Cmd(c, "SELECT pc_id, conn_id, protocol, started_at, ended_at, label, host, folder, adhoc FROM sessions WHERE " + where +
                                      " ORDER BY started_at DESC, rowid DESC " + tail, ps).ExecuteReader())
                while (r.Read())
                    Rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Utc(r.GetString(3)), r.IsDBNull(4) ? null : Utc(r.GetString(4)), S(r, 5), S(r, 6), S(r, 7), S(r, 8) == "1"));
        }

        /// <summary>for each row: effective end, seconds and state</summary>
        public (long seconds, string status) Calc(Raw x)
        {
            if (x.End != null) return ((long)(x.End.Value - x.Start).TotalSeconds, "closed");
            var seen = Names.TryGetValue(x.PcId, out var p) ? p.seen : null;
            bool stale = seen == null || _now - seen.Value > TimeSpan.FromMinutes(StaleMinutes);
            var end = stale ? (seen ?? x.Start) : _now;
            if (end < x.Start) end = x.Start;
            return ((long)(end - x.Start).TotalSeconds, stale ? "interrupted" : "open");
        }

        public Detail ToDetail(Raw x)
        {
            var (sec, st) = Calc(x);
            // shared entry: name and folder from the server; otherwise those sent by the PC (ad hoc: only the address, no folder)
            bool shared = ConnNames.ContainsKey(x.ConnId);
            var name = shared ? ConnNames[x.ConnId] : !string.IsNullOrEmpty(x.Label) ? x.Label : x.Host;
            var folder = shared ? ConnFolders.GetValueOrDefault(x.ConnId) : x.Adhoc ? null : x.Folder;
            return new Detail(Names.TryGetValue(x.PcId, out var p) ? p.pc : x.PcId, x.ConnId, name,
                x.Protocol, x.Start.ToString("o"), x.End?.ToString("o"), sec, st, folder, x.Host, x.Adhoc);
        }
    }

    public static Report Build()
    {
        using var c = Pcs.Open();
        var k = new Ctx(c);
        var rows = k.Rows; var names = k.Names; var connNames = k.ConnNames;
        var Calc = k.Calc; var ToDetail = k.ToDetail;

        var byPc = rows.GroupBy(x => x.PcId).Select(g =>
        {
            var calc = g.Select(Calc).ToList();
            return new PcRow(g.Key, names.TryGetValue(g.Key, out var p) ? p.pc : g.Key, g.Count(), g.Count(x => x.End == null),
                calc.Sum(x => x.seconds), g.Max(x => x.Start).ToString("o"));
        }).OrderByDescending(x => x.LastAt).ToList();

        var byConn = rows.GroupBy(x => x.ConnId).Select(g =>
        {
            var calc = g.Select(Calc).ToList();
            return new ConnRow(g.Key, connNames.GetValueOrDefault(g.Key), g.Count(), g.Count(x => x.End == null),
                g.Select(x => x.PcId).Distinct().Count(), calc.Sum(x => x.seconds), g.Max(x => x.Start).ToString("o"));
        }).OrderByDescending(x => x.LastAt).ToList();

        var recent = rows.Take(500).Select(ToDetail).ToList();
        // in progress: by PC (simultaneous connections of the same PC and user stay together, one row each), then newest first
        var open = rows.Where(x => Calc(x).status == "open").Select(ToDetail).OrderBy(x => x.Pc, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(x => x.Start).ToList();

        return new Report(StaleMinutes, byPc, byConn, recent, open);
    }

    public record Page(int total, List<Detail> items);

    /// <summary>
    /// Filtered history, paged ("Show more" button). PC and dates are filtered in SQL, so without a search ALL the history can be scrolled.
    /// The search (entry, address, folder, PC) looks at names that live in different tables or come from the PC: it is done in memory.
    /// shortcut: with a search only the 20000 most recent connections of the PC/date filter are examined; if more were needed, a search column in SQL.
    /// </summary>
    /// <param name="fromUtc">connection start, inclusive; <paramref name="toUtc"/> exclusive</param>
    public static Page History(string? pcId, string? q, DateTime? fromUtc, DateTime? toUtc, int limit, int offset)
    {
        using var c = Pcs.Open();
        var where = "1 = 1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(pcId)) { where += " AND pc_id = $p"; ps.Add(("$p", pcId)); }
        if (fromUtc is { } f) { where += " AND started_at >= $f"; ps.Add(("$f", f.ToString("o"))); }
        if (toUtc is { } u) { where += " AND started_at < $u"; ps.Add(("$u", u.ToString("o"))); }
        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            int total = Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM sessions WHERE " + where, ps.ToArray()).ExecuteScalar());
            ps.Add(("$l", limit)); ps.Add(("$o", offset));
            var k = new Ctx(c, where, "LIMIT $l OFFSET $o", ps.ToArray());
            return new Page(total, k.Rows.Select(k.ToDetail).ToList());
        }
        var all = new Ctx(c, where, "LIMIT 20000", ps.ToArray());
        bool Has(string? s) => s != null && s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var found = all.Rows.Select(all.ToDetail).Where(d => Has(d.Name) || Has(d.Host) || Has(d.Folder) || Has(d.Pc) || Has(d.Protocol)).ToList();
        return new Page(found.Count, found.Skip(offset).Take(limit).ToList());
    }
}

internal static class SessionsApi
{
    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    public static void Map(RouteGroupBuilder api)
    {
        // ---- administrator: "Collegamenti" (connections) page ----
        api.MapGet("/sessions", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Sessions.Build()));
        // "Storico" (history) tab: filters by PC, text and days (instants with time zone, as in the activity log), paged
        api.MapGet("/sessions/history", (HttpContext c, string? pc, string? q, string? from, string? to, int limit = 100, int offset = 0) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (limit is < 1 or > 200) limit = 100;
            if (offset < 0) offset = 0;
            if ((q?.Length ?? 0) > 200) return Results.Json(new { error = "input", field = "q" }, statusCode: 400);
            if (!EventsApi.Instant(from, out var f) || !EventsApi.Instant(to, out var u)) return Results.Json(new { error = "date" }, statusCode: 400);
            return Results.Json(Sessions.History(pc, q, f, u, limit, offset));
        });

        // ---- linked PC: uploads start/end of connections to shared entries ("sessions" capability) ----
        api.MapPost("/client/sessions", (HttpContext c, Sessions.Submit s) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if (!Caps.Has(c, "sessions")) return Results.Json(new { error = "caps" }, statusCode: 400); // client that does not know this call: nothing is saved
            if ((s.Items?.Count ?? 0) > 1000) return Results.Json(new { error = "input", field = "items" }, statusCode: 400);
            return Results.Json(new { accepted = Sessions.Apply(pc.Id, s) });
        });
    }
}
