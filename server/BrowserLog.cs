using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Usage log of device passwords from the browser extension (only PCs linked to Teams). The PC records every event in its encrypted database
/// and uploads it here: filled into a page, copied, created, edited, saved from a login, deleted. For each event: who (PC and user, from the PC's
/// key and not from the message), when, which entry and folder, which site (page origin) and which browser. NEVER the password, not even partly.
/// Separate console section ("Extension log"), administrators only.
/// </summary>
internal static class BrowserLog
{
    public static readonly string[] Kinds = { "fill", "copy", "create", "update", "save", "delete" };

    /// <summary>Id: local event id (the PC's guid: resending the same event does not duplicate it). Account: the entry's user (e.g. admin), not the password</summary>
    public record In(string? Id, string? At, string? Kind, string? DeviceId, string? Device, string? Folder, string? Account, string? Origin, string? Browser);
    public record Submit(List<In>? Items);

    private static SqliteConnection Open()
    {
        var c = Pcs.Open();
        // table created on the fly (like Conflicts): compatible with existing databases. pc and user copied on arrival: they stay readable even if the PC is removed
        Pcs.Cmd(c, """
            CREATE TABLE IF NOT EXISTS browser_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT, pc_id TEXT NOT NULL, local_id TEXT NOT NULL, pc TEXT, pc_user TEXT,
                at TEXT NOT NULL, received_at TEXT NOT NULL, kind TEXT NOT NULL, device_id TEXT, device TEXT, folder TEXT, account TEXT, origin TEXT, browser TEXT,
                UNIQUE (pc_id, local_id));
            CREATE INDEX IF NOT EXISTS browser_log_at ON browser_log (at);
            """).ExecuteNonQuery();
        return c;
    }

    private static bool Free(string? s, int max) => s == null || (s.Length <= max && !s.Any(char.IsControl));
    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>page origin: only "http(s)://host[:port]", no path or query (they could contain data); empty for events without a page</summary>
    internal static bool OriginOk(string? s) =>
        string.IsNullOrEmpty(s) || (s.Length <= 300 && Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
            && u.GetLeftPart(UriPartial.Authority).Equals(s, StringComparison.OrdinalIgnoreCase) && u.UserInfo.Length == 0);

    /// <summary>saves the valid events (the others are dropped one by one). Returns how many were accepted, duplicates included (the PC removes them from its queue)</summary>
    public static int Apply(string pcId, Submit s)
    {
        if (s.Items == null || s.Items.Count == 0) return 0;
        using var c = Open();
        string? pc = null, user = null;
        using (var r = Pcs.Cmd(c, "SELECT pc, user FROM pcs WHERE id = $i", ("$i", pcId)).ExecuteReader()) if (r.Read()) { pc = r.GetString(0); user = r.GetString(1); }
        using var tx = c.BeginTransaction();
        int n = 0;
        var now = DateTime.UtcNow;
        foreach (var i in s.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Id) || !Free(i.Id, 64) || i.Kind == null || !Kinds.Contains(i.Kind)) continue;
            if (!DateTime.TryParse(i.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) continue;
            at = at.ToUniversalTime();
            if (at > now.AddDays(1)) at = now;   // PC clock ahead: the event does not stay at the top of the log forever
            if (!Free(i.DeviceId, 64) || !Free(i.Device, 200) || !Free(i.Folder, 300) || !Free(i.Account, 200) || !Free(i.Browser, 100) || !OriginOk(i.Origin)) continue;
            Pcs.Cmd(c, "INSERT OR IGNORE INTO browser_log (pc_id, local_id, pc, pc_user, at, received_at, kind, device_id, device, folder, account, origin, browser) " +
                       "VALUES ($p, $l, $pc, $pu, $a, $r, $k, $di, $d, $f, $ac, $o, $b)",
                ("$p", pcId), ("$l", i.Id.Trim()), ("$pc", pc), ("$pu", user), ("$a", at.ToString("o")), ("$r", now.ToString("o")), ("$k", i.Kind),
                ("$di", Nz(i.DeviceId)), ("$d", Nz(i.Device)), ("$f", Nz(i.Folder)), ("$ac", Nz(i.Account)), ("$o", Nz(i.Origin)?.ToLowerInvariant()), ("$b", Nz(i.Browser))).ExecuteNonQuery();
            n++;
        }
        tx.Commit();
        return n;
    }

    public record Row(string At, string PcId, string? Pc, string Kind, string? Device, string? Folder, string? Account, string? Origin, string? Browser);
    public record Page(int total, List<Row> items);
    public record Filter(string? Pc, string? Folder, string? Device, string? Kind, string? Site, DateTime? FromUtc, DateTime? ToUtc);

    /// <summary>filtered log, newest first, paged. Text (folder, entry, site): case-insensitive contains (instr: no LIKE wildcards)</summary>
    public static Page List(Filter f, int limit, int offset)
    {
        using var c = Open();
        var where = "1 = 1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(f.Pc)) { where += " AND pc_id = $p"; ps.Add(("$p", f.Pc)); }
        if (!string.IsNullOrEmpty(f.Kind)) { where += " AND kind = $k"; ps.Add(("$k", f.Kind)); }
        if (!string.IsNullOrWhiteSpace(f.Folder)) { where += " AND instr(lower(ifnull(folder, '')), lower($f)) > 0"; ps.Add(("$f", f.Folder.Trim())); }
        if (!string.IsNullOrWhiteSpace(f.Device)) { where += " AND instr(lower(ifnull(device, '')), lower($d)) > 0"; ps.Add(("$d", f.Device.Trim())); }
        if (!string.IsNullOrWhiteSpace(f.Site)) { where += " AND instr(ifnull(origin, ''), lower($s)) > 0"; ps.Add(("$s", f.Site.Trim())); }
        if (f.FromUtc is { } a) { where += " AND at >= $from"; ps.Add(("$from", a.ToString("o"))); }
        if (f.ToUtc is { } b) { where += " AND at < $to"; ps.Add(("$to", b.ToString("o"))); }
        int total = Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM browser_log WHERE " + where, ps.ToArray()).ExecuteScalar());
        ps.Add(("$l", limit)); ps.Add(("$o", offset));
        using var r = Pcs.Cmd(c, "SELECT at, pc_id, pc, pc_user, kind, device, folder, account, origin, browser FROM browser_log WHERE " + where +
                                 " ORDER BY at DESC, id DESC LIMIT $l OFFSET $o", ps.ToArray()).ExecuteReader();
        string? S(int n) => r.IsDBNull(n) ? null : r.GetString(n);
        var list = new List<Row>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), S(2) == null ? null : $"{S(2)} - {S(3)}", r.GetString(4), S(5), S(6), S(7), S(8), S(9)));
        return new Page(total, list);
    }
}

internal static class BrowserLogApi
{
    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    public static void Map(RouteGroupBuilder api)
    {
        // ---- administrator: "Extension log" ----
        api.MapGet("/browserlog", (HttpContext c, string? pc, string? folder, string? device, string? kind, string? site, string? from, string? to, int limit = 100, int offset = 0) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (limit is < 1 or > 200) limit = 100;
            if (offset < 0) offset = 0;
            if (!string.IsNullOrEmpty(kind) && !BrowserLog.Kinds.Contains(kind)) return Results.Json(new { error = "input", field = "kind" }, statusCode: 400);
            if ((folder?.Length ?? 0) > 200 || (device?.Length ?? 0) > 200 || (site?.Length ?? 0) > 200) return Results.Json(new { error = "input" }, statusCode: 400);
            if (!EventsApi.Instant(from, out var f) || !EventsApi.Instant(to, out var u)) return Results.Json(new { error = "date" }, statusCode: 400);
            return Results.Json(BrowserLog.List(new(pc, folder, device, kind, site, f, u), limit, offset));
        });

        // ---- linked PC: uploads the extension's events ("browserlog" capability) ----
        api.MapPost("/client/browserlog", (HttpContext c, BrowserLog.Submit s) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if ((s.Items?.Count ?? 0) > 500) return Results.Json(new { error = "input", field = "items" }, statusCode: 400);
            return Results.Json(new { accepted = BrowserLog.Apply(pc.Id, s) });
        });
    }
}
