using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Durata dei collegamenti (Installer/TEAMS-DESIGN.md sezione 6). Per ogni collegamento che un PC apre verso una voce CONDIVISA (RDP/SSH/VNC del server Teams)
/// il client registra inizio e fine (voce, protocollo, PC; MAI la password) e li carica al sync. L'amministratore vede, per PC e per voce, quante volte, quanto
/// tempo e l'ultimo collegamento. Una sessione senza fine e' "in corso"; se il PC non si fa sentire da un po' risulta "interrotta" (fine stimata dall'ultimo contatto).
/// </summary>
internal static class Sessions
{
    /// <summary>un PC non visto da piu' di questo tempo con una sessione aperta: la sessione e' "interrotta", la fine si stima dall'ultimo contatto (sync ogni 20 min)</summary>
    public const int StaleMinutes = 45;

    public record In(string? Id, string? ConnId, string? Protocol, string? Start, string? End);
    public record Submit(List<In>? Items);

    private record Raw(string PcId, string ConnId, string Protocol, DateTime Start, DateTime? End);

    /// <summary>upsert per (pc_id, id); la fine, una volta scritta, non torna indietro. Restituisce quante righe valide ha accettato</summary>
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
            Pcs.Cmd(c, "INSERT INTO sessions (id, pc_id, conn_id, protocol, started_at, ended_at) VALUES ($i, $p, $c, $pr, $s, $e) " +
                       "ON CONFLICT(pc_id, id) DO UPDATE SET ended_at = $e WHERE sessions.ended_at IS NULL",
                ("$i", i.Id!.Trim()), ("$p", pcId), ("$c", i.ConnId!.Trim()), ("$pr", i.Protocol), ("$s", start.ToString("o")), ("$e", end)).ExecuteNonQuery();
            n++;
        }
        tx.Commit();
        return n;
    }

    private static bool Ok(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);
    private static bool TryUtc(string? s, out DateTime t)
    {
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) { t = t.ToUniversalTime(); return true; }
        return false;
    }
    private static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public record PcRow(string PcId, string Pc, int Count, int Open, long TotalSeconds, string? LastAt);
    public record ConnRow(string ConnId, string? Name, int Count, int Open, int PcCount, long TotalSeconds, string? LastAt);
    public record Detail(string Pc, string ConnId, string? Name, string Protocol, string Start, string? End, long Seconds, string Status);
    public record Report(int staleMinutes, List<PcRow> byPc, List<ConnRow> byConn, List<Detail> recent);

    /// <summary>
    /// Aggregazioni per la console. Le durate delle sessioni aperte e lo stato "interrotta" dipendono dall'ultimo contatto del PC (last_seen) e dall'ora attuale,
    /// quindi si calcolano qui, non in SQL. ponytail: O(n) in memoria su tutte le righe; per volumi grandi passare alle somme in SQL. Dettaglio limitato alle piu' recenti.
    /// </summary>
    public static Report Build()
    {
        using var c = Pcs.Open();
        var names = new Dictionary<string, (string pc, DateTime? seen)>();
        using (var r = Pcs.Cmd(c, "SELECT id, pc || ' - ' || user, last_seen FROM pcs").ExecuteReader())
            while (r.Read()) names[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? null : Utc(r.GetString(2)));
        var connNames = new Dictionary<string, string>();
        using (var r = Pcs.Cmd(c, "SELECT id, name FROM connections").ExecuteReader())
            while (r.Read()) connNames[r.GetString(0)] = r.GetString(1);

        var rows = new List<Raw>();
        using (var r = Pcs.Cmd(c, "SELECT pc_id, conn_id, protocol, started_at, ended_at FROM sessions ORDER BY started_at DESC LIMIT 20000").ExecuteReader())
            while (r.Read())
                rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Utc(r.GetString(3)), r.IsDBNull(4) ? null : Utc(r.GetString(4))));

        var now = DateTime.UtcNow;
        // per ogni riga: fine effettiva, secondi e stato
        (long seconds, string status) Calc(Raw x)
        {
            if (x.End != null) return ((long)(x.End.Value - x.Start).TotalSeconds, "closed");
            var seen = names.TryGetValue(x.PcId, out var p) ? p.seen : null;
            bool stale = seen == null || now - seen.Value > TimeSpan.FromMinutes(StaleMinutes);
            var end = stale ? (seen ?? x.Start) : now;
            if (end < x.Start) end = x.Start;
            return ((long)(end - x.Start).TotalSeconds, stale ? "interrupted" : "open");
        }

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

        var recent = rows.Take(500).Select(x =>
        {
            var (sec, st) = Calc(x);
            return new Detail(names.TryGetValue(x.PcId, out var p) ? p.pc : x.PcId, x.ConnId, connNames.GetValueOrDefault(x.ConnId),
                x.Protocol, x.Start.ToString("o"), x.End?.ToString("o"), sec, st);
        }).ToList();

        return new Report(StaleMinutes, byPc, byConn, recent);
    }
}

internal static class SessionsApi
{
    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    public static void Map(RouteGroupBuilder api)
    {
        // ---- amministratore: pagina "Collegamenti" ----
        api.MapGet("/sessions", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Sessions.Build()));

        // ---- PC collegato: carica inizio/fine dei collegamenti alle voci condivise (capacita' "sessions") ----
        api.MapPost("/client/sessions", (HttpContext c, Sessions.Submit s) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            if (!Caps.Has(c, "sessions")) return Results.Json(new { error = "caps" }, statusCode: 400); // client che non conosce questa chiamata: nulla si salva
            if ((s.Items?.Count ?? 0) > 1000) return Results.Json(new { error = "input", field = "items" }, statusCode: 400);
            return Results.Json(new { accepted = Sessions.Apply(pc.Id, s) });
        });
    }
}
