using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Conflitti di scrittura. Un PC con permesso "scrittura" modifica una voce condivisa partendo da una versione ormai cambiata sul server:
/// Conns.TryUpdate restituisce "conflict" (409), il PC risincronizza (vince la versione sul server). Qui si conserva la versione PROPOSTA dal PC
/// per un confronto in console, dove l'amministratore sceglie "Tieni versione server" (scarta la proposta) o "Usa versione del PC" (la applica).
/// Il 409 verso il client non cambia: la conservazione e' solo un effetto collaterale. La password non esce mai: si mostra solo se e' cambiata.
/// Tabella creata al volo (CREATE TABLE IF NOT EXISTS): compatibile con i database gia' esistenti.
/// </summary>
internal static class Conflicts
{
    public record Values(string Folder, string Name, string Protocol, string Host, int Port, string User, string Tags);
    public record Row(string Id, string? ConnId, string Pc, string State, string CreatedAt, string? DecidedAt, Values? Server, Values Client, bool PwChanged, bool Gone);

    private static void Ensure(SqliteConnection c) =>
        Pcs.Cmd(c, "CREATE TABLE IF NOT EXISTS conflicts (id TEXT PRIMARY KEY, conn_id TEXT NOT NULL, pc_id TEXT, base_version INTEGER, " +
            "folder TEXT, name TEXT, protocol TEXT, host TEXT, port INTEGER, user TEXT, pw_enc TEXT, pw_change INTEGER NOT NULL DEFAULT 0, tags TEXT, " +
            "state TEXT NOT NULL DEFAULT 'pending', created_at TEXT NOT NULL, decided_at TEXT)").ExecuteNonQuery();

    /// <summary>chiamata dentro la transazione di Conns.TryUpdate quando sta per restituire "conflict" (solo da un PC: pcId valorizzato)</summary>
    public static void Record(SqliteConnection c, string connId, string pcId, Conns.Input i)
    {
        Ensure(c);
        // un solo conflitto in sospeso per PC+voce: un PC che ritenta non ne accumula
        Pcs.Cmd(c, "DELETE FROM conflicts WHERE conn_id = $c AND pc_id = $p AND state = 'pending'", ("$c", connId), ("$p", pcId)).ExecuteNonQuery();
        var id = Guid.NewGuid().ToString("N");
        bool pwChange = i.Password != null; // null = "lascia invariata": nessun cambio di password proposto
        Pcs.Cmd(c, "INSERT INTO conflicts (id, conn_id, pc_id, base_version, folder, name, protocol, host, port, user, pw_enc, pw_change, tags, state, created_at) " +
            "VALUES ($i, $c, $p, $b, $f, $n, $pr, $h, $o, $u, $w, $pc, $t, 'pending', $d)",
            ("$i", id), ("$c", connId), ("$p", pcId), ("$b", i.BaseVersion),
            ("$f", i.Folder ?? ""), ("$n", i.Name ?? ""), ("$pr", i.Protocol ?? ""), ("$h", i.Host ?? ""), ("$o", i.Port), ("$u", i.User ?? ""),
            ("$w", pwChange && i.Password!.Length > 0 ? Vault.Encrypt(i.Password!, id) : null), ("$pc", pwChange ? 1 : 0), ("$t", i.Tags ?? ""), ("$d", Pcs.Now())).ExecuteNonQuery();
        Pcs.Log(c, pcId, "conflict-recorded", i.Name ?? connId);
    }

    public static int Count()
    {
        using var c = Pcs.Open();
        Ensure(c);
        return Convert.ToInt32(Pcs.Cmd(c, "SELECT COUNT(*) FROM conflicts WHERE state = 'pending'").ExecuteScalar());
    }

    /// <summary>in sospeso e storico, con la versione del PC e quella attuale sul server. La password non esce: solo se e' cambiata</summary>
    public static List<Row> List()
    {
        using var c = Pcs.Open();
        Ensure(c);
        using var r = Pcs.Cmd(c,
            "SELECT q.id, q.conn_id, COALESCE(p.pc || ' - ' || p.user, '?'), q.state, q.created_at, q.decided_at, " +
            "q.folder, q.name, q.protocol, q.host, q.port, q.user, q.tags, q.pw_change, " +
            "x.folder, x.name, x.protocol, x.host, x.port, x.user, x.tags " +
            "FROM conflicts q LEFT JOIN pcs p ON p.id = q.pc_id LEFT JOIN connections x ON x.id = q.conn_id AND x.deleted_at IS NULL " +
            "ORDER BY q.created_at DESC LIMIT 500").ExecuteReader();
        var list = new List<Row>();
        while (r.Read())
        {
            var client = new Values(r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetString(11), r.GetString(12));
            bool pwChange = r.GetInt32(13) == 1;
            bool gone = r.IsDBNull(14);
            Values? server = gone ? null : new Values(r.GetString(14), r.GetString(15), r.GetString(16), r.GetString(17), r.GetInt32(18), r.GetString(19), r.GetString(20));
            list.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), server, client, pwChange, gone));
        }
        return list;
    }

    /// <summary>"ok" | "notfound" | "state". keepServer = false: applica la versione del PC come scrittura dell'amministratore</summary>
    public static string Resolve(string id, bool keepServer)
    {
        using var c = Pcs.Open();
        Ensure(c);
        string? connId, state;
        Conns.Input? input = null;
        using (var r = Pcs.Cmd(c, "SELECT conn_id, state, folder, name, protocol, host, port, user, pw_enc, pw_change, tags FROM conflicts WHERE id = $i", ("$i", id)).ExecuteReader())
        {
            if (!r.Read()) return "notfound";
            connId = r.GetString(0); state = r.GetString(1);
            if (state == "pending" && !keepServer)
            {
                bool pwChange = r.GetInt32(9) == 1;
                string? pwEnc = r.IsDBNull(8) ? null : r.GetString(8);
                var pw = !pwChange ? null : pwEnc == null ? "" : Vault.Decrypt(pwEnc, id);
                input = new Conns.Input(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7), pw, r.GetString(10));
            }
        }
        if (state != "pending") return "state";
        if (!keepServer && !Conns.Update(connId!, input!)) return "notfound"; // la voce non c'e' piu' (nel cestino): niente da aggiornare
        Pcs.Cmd(c, "UPDATE conflicts SET state = $s, decided_at = $d, pw_enc = NULL WHERE id = $i AND state = 'pending'",
            ("$s", keepServer ? "kept-server" : "used-pc"), ("$d", Pcs.Now()), ("$i", id)).ExecuteNonQuery();
        Pcs.Log(c, null, keepServer ? "conflict-kept-server" : "conflict-used-pc", connId);
        return "ok";
    }
}

internal static class ConflictsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/conflicts", (HttpContext c) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            try { return Results.Json(Conflicts.List()); }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });

        api.MapPost("/conflicts/{id}/{action}", (HttpContext c, string id, string action) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (action is not ("keep-server" or "use-pc")) return Results.NotFound();
            try
            {
                return Conflicts.Resolve(id, action == "keep-server") switch
                {
                    "ok" => Results.Json(new { ok = true }),
                    "state" => Results.Json(new { error = "state" }, statusCode: 409),
                    _ => Results.Json(new { error = "notfound" }, statusCode: 409),
                };
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });
    }
}
