using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Cifratura delle password condivise. La chiave (32 byte casuali) sta nel file server.key nella cartella dei dati, FUORI dalla cartella del database:
/// chi copia solo il database non legge le password. Ogni password e' legata alla propria voce (id): scambiare due password nel database non funziona.
/// </summary>
internal static class Vault
{
    private static readonly object Gate = new();
    private static byte[]? _key;
    private static string KeyFile => Path.Combine(State.ConfigDir, "server.key");

    private static byte[] Key()
    {
        lock (Gate)
        {
            if (_key != null) return _key;
            if (File.Exists(KeyFile))
            {
                var k = File.ReadAllBytes(KeyFile);
                if (k.Length != 32) throw new InvalidOperationException("server.key non valida");
                return _key = k;
            }
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(KeyFile, key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); // su Windows vale l'ACL della cartella dei dati
            return _key = key;
        }
    }

    /// <summary>nonce(12) + tag(16) + testo cifrato, in base64</summary>
    public static string Encrypt(string plain, string id)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ct = new byte[data.Length];
        using var gcm = new AesGcm(Key(), 16);
        gcm.Encrypt(nonce, data, ct, tag, Encoding.UTF8.GetBytes(id));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }

    /// <summary>impronta con la chiave del server: chi copia solo il database non puo' indovinare le password provandole</summary>
    public static string Mac(string s) => Convert.ToHexString(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(s)));

    public static string Decrypt(string stored, string id)
    {
        var all = Convert.FromBase64String(stored);
        var ct = new byte[all.Length - 28];
        var plain = new byte[ct.Length];
        Array.Copy(all, 28, ct, 0, ct.Length);
        using var gcm = new AesGcm(Key(), 16);
        gcm.Decrypt(all.AsSpan(0, 12), ct, all.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(id));
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>connessioni RDP, SSH e VNC condivise dall'amministratore con i PC collegati</summary>
internal static class Conns
{
    public record Item(string Id, string Folder, string Name, string Protocol, string Host, int Port, string User, bool HasPassword, string Tags, int Version, bool Disabled, string UpdatedAt);
    /// <summary>BaseVersion: solo dai PC collegati, e' la versione che il PC ha visto; se nel frattempo e' cambiata la modifica viene rifiutata</summary>
    public record Input(string? Folder, string? Name, string? Protocol, string? Host, int Port, string? User, string? Password, string? Tags, int? BaseVersion = null);
    /// <summary>Origin: la voce nasce da una connessione personale di un PC (Personal.cs): il PC d'origine la riconosce e nasconde la sua copia locale</summary>
    public record SyncItem(string Id, int Version, string Folder, string Name, string Protocol, string Host, int Port, string User, string? Password, string Tags, bool Disabled, bool Deleted, Origin? Origin = null);
    public record Origin(string Pc, string LocalId);

    internal static bool Clean(string? s, int max, bool required = false) => s != null && s.Length <= max && !s.Any(char.IsControl) && (!required || s.Trim().Length > 0);

    /// <summary>null se i dati vanno bene, altrimenti il nome del campo sbagliato</summary>
    public static string? Validate(Input i)
    {
        if (!Clean(i.Name, 100, true)) return "name";
        if (!Clean(i.Folder ?? "", 200)) return "folder";
        if (i.Protocol is not ("rdp" or "ssh" or "vnc")) return "protocol";
        if (!Clean(i.Host, 255, true) || i.Host!.Any(char.IsWhiteSpace)) return "host";
        if (i.Port < 1 || i.Port > 65535) return "port";
        if (!Clean(i.User ?? "", 128)) return "user";
        if (i.Password != null && (i.Password.Length > 256 || i.Password.Any(c => char.IsControl(c) && c != '\t'))) return "password";
        if (!Clean(i.Tags ?? "", 400)) return "tags";
        return null;
    }

    private static string NormTags(string? tags) => string.Join(",", (tags ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0 && t.Length <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));
    internal static string NormFolder(string? folder) => string.Join("/", (folder ?? "").Split('/', '\\').Select(p => p.Trim()).Where(p => p.Length > 0));

    internal static long NextRev(SqliteConnection c)
    {
        Pcs.Cmd(c, "INSERT OR IGNORE INTO meta (k, v) VALUES ('rev', 0)").ExecuteNonQuery();
        Pcs.Cmd(c, "UPDATE meta SET v = v + 1 WHERE k = 'rev'").ExecuteNonQuery();
        return (long)Pcs.Cmd(c, "SELECT v FROM meta WHERE k = 'rev'").ExecuteScalar()!;
    }

    public static string Create(Input i, string? pcId = null)
    {
        var id = Guid.NewGuid().ToString("N");
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var rev = NextRev(c);
        var now = Pcs.Now();
        Pcs.Cmd(c, "INSERT INTO connections (id, folder, name, protocol, host, port, user, pw_enc, tags, version, disabled, rev, created_at, updated_at) VALUES ($i, $f, $n, $p, $h, $o, $u, $w, $t, 1, 0, $r, $c, $c)",
            ("$i", id), ("$f", NormFolder(i.Folder)), ("$n", i.Name!.Trim()), ("$p", i.Protocol), ("$h", i.Host!.Trim()), ("$o", i.Port), ("$u", i.User ?? ""),
            ("$w", string.IsNullOrEmpty(i.Password) ? null : Vault.Encrypt(i.Password, id)), ("$t", NormTags(i.Tags)), ("$r", rev), ("$c", now)).ExecuteNonQuery();
        Pcs.Log(c, pcId, "connection-created", i.Name!.Trim());
        tx.Commit();
        return id;
    }

    /// <summary>password null = lasciala com'e', "" = la toglie</summary>
    public static bool Update(string id, Input i, string? pcId = null) => TryUpdate(id, i, pcId) == "ok";

    /// <summary>"ok" | "notfound" | "conflict" (la voce e' cambiata dopo la versione vista dal PC)</summary>
    public static string TryUpdate(string id, Input i, string? pcId = null)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        string? oldPw; int version;
        using (var r = Pcs.Cmd(c, "SELECT pw_enc, version FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteReader())
        {
            if (!r.Read()) return "notfound";
            oldPw = r.IsDBNull(0) ? null : r.GetString(0);
            version = r.GetInt32(1);
        }
        if (i.BaseVersion != null && i.BaseVersion != version)
        {
            // il 409 verso il client non cambia: si conserva solo la versione proposta dal PC per la pagina Conflitti
            if (pcId != null) { Conflicts.Record(c, id, pcId, i); tx.Commit(); }
            return "conflict";
        }
        var pw = i.Password == null ? oldPw : i.Password.Length == 0 ? null : Vault.Encrypt(i.Password, id);
        Pcs.Cmd(c, "UPDATE connections SET folder=$f, name=$n, protocol=$p, host=$h, port=$o, user=$u, pw_enc=$w, tags=$t, version=version+1, rev=$r, updated_at=$d WHERE id=$i",
            ("$f", NormFolder(i.Folder)), ("$n", i.Name!.Trim()), ("$p", i.Protocol), ("$h", i.Host!.Trim()), ("$o", i.Port), ("$u", i.User ?? ""), ("$w", pw),
            ("$t", NormTags(i.Tags)), ("$r", NextRev(c)), ("$d", Pcs.Now()), ("$i", id)).ExecuteNonQuery();
        Pcs.Log(c, pcId, "connection-updated", i.Name!.Trim() + (i.Password != null ? " (password cambiata)" : ""));
        tx.Commit();
        return "ok";
    }

    /// <summary>una voce com'e' per i PC (password in chiaro), null se non c'e' o e' nel cestino</summary>
    public static SyncItem? Get(string id)
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, "SELECT id, version, folder, name, protocol, host, port, user, pw_enc, tags, disabled FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteReader();
        if (!r.Read()) return null;
        return new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7),
            r.IsDBNull(8) ? null : Vault.Decrypt(r.GetString(8), id), r.GetString(9), r.GetInt32(10) == 1, false);
    }

    public static bool SetDisabled(string id, bool disabled)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = Pcs.Cmd(c, "UPDATE connections SET disabled=$d, version=version+1, rev=$r, updated_at=$t WHERE id=$i AND deleted_at IS NULL", ("$d", disabled ? 1 : 0), ("$r", NextRev(c)), ("$t", Pcs.Now()), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, null, disabled ? "connection-disabled" : "connection-enabled", id);
        tx.Commit();
        return n == 1;
    }

    /// <summary>la voce va nel cestino: sparisce dai PC al prossimo contatto, la password resta cifrata per un eventuale ripristino</summary>
    public static bool Delete(string id, string? pcId = null)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        int n = Pcs.Cmd(c, "UPDATE connections SET deleted_at=$t, version=version+1, rev=$r, updated_at=$t WHERE id=$i AND deleted_at IS NULL", ("$t", Pcs.Now()), ("$r", NextRev(c)), ("$i", id)).ExecuteNonQuery();
        if (n == 1) Pcs.Log(c, pcId, "connection-deleted", id);
        tx.Commit();
        return n == 1;
    }

    public static List<Item> List()
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, "SELECT id, folder, name, protocol, host, port, user, pw_enc IS NOT NULL, tags, version, disabled, updated_at FROM connections WHERE deleted_at IS NULL ORDER BY folder, name COLLATE NOCASE").ExecuteReader();
        var list = new List<Item>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetInt32(7) == 1, r.GetString(8), r.GetInt32(9), r.GetInt32(10) == 1, r.GetString(11)));
        return list;
    }

    /// <summary>"Mostra password": chi la chiede finisce nel registro</summary>
    public static (bool found, string? password) Reveal(string id)
    {
        using var c = Pcs.Open();
        string? enc, name;
        using (var r = Pcs.Cmd(c, "SELECT pw_enc, name FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteReader())
        {
            if (!r.Read()) return (false, null);
            enc = r.IsDBNull(0) ? null : r.GetString(0); name = r.GetString(1);
        }
        Pcs.Log(c, null, "password-shown", name);
        return (true, enc == null ? "" : Vault.Decrypt(enc, id));
    }

    /// <summary>cambiamenti dopo la revisione indicata (anche cancellate e disabilitate), per i PC collegati</summary>
    /// <param name="vnc">false = client che non dichiara la capacita' "vnc" (X-Lemora-Caps, vedi Caps in Requests.cs): niente voci VNC, che mostrerebbe come RDP.
    /// Se ne aveva gia' ricevute (since > 0) riceve per ognuna una voce "deleted" (protocol vnc) che la toglie dalla sua copia</param>
    /// <param name="full">true = capacita' del PC cambiate (vedi FullSync): tutte le voci visibili, non solo quelle cambiate dopo since</param>
    public static (long rev, List<SyncItem> items) Changes(long since, bool vnc = true, bool full = false)
    {
        using var c = Pcs.Open();
        var list = new List<SyncItem>();
        if (!vnc && since > 0)
        {
            using var t = Pcs.Cmd(c, "SELECT id, version FROM connections WHERE protocol = 'vnc'").ExecuteReader();
            while (t.Read()) list.Add(new(t.GetString(0), t.GetInt32(1), "", "", "vnc", "", 0, "", null, "", false, true));
        }
        // da zero (PC nuovo) le voci gia' cancellate non servono: non c'e' niente da togliere
        using (var r = Pcs.Cmd(c, "SELECT x.id, x.version, x.folder, x.name, x.protocol, x.host, x.port, x.user, x.pw_enc, x.tags, x.disabled, x.deleted_at IS NOT NULL, p.pc_id, p.local_id " +
                   "FROM connections x LEFT JOIN personal p ON p.conn_id = x.id AND p.state = 'published' " +
                   "WHERE (x.rev > $s OR ($f = 1 AND x.deleted_at IS NULL)) AND ($s > 0 OR x.deleted_at IS NULL) AND ($v = 1 OR x.protocol <> 'vnc') ORDER BY x.rev", ("$s", since), ("$v", vnc ? 1 : 0), ("$f", full ? 1 : 0)).ExecuteReader())
        {
            while (r.Read())
            {
                var id = r.GetString(0);
                bool deleted = r.GetInt32(11) == 1;
                list.Add(new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7),
                    deleted || r.IsDBNull(8) ? null : Vault.Decrypt(r.GetString(8), id), r.GetString(9), r.GetInt32(10) == 1, deleted,
                    r.IsDBNull(12) ? null : new Origin(r.GetString(12), r.GetString(13))));
            }
        }
        var rev = Pcs.Cmd(c, "SELECT v FROM meta WHERE k = 'rev'").ExecuteScalar() is long v ? v : 0;
        return (rev, list);
    }

    /// <summary>
    /// Le voci mandate dipendono dalle capacita' del client (niente VNC ai client vecchi): se il PC cambia capacita' (aggiornato o riportato a una versione
    /// vecchia) il suo cursore since non basta, perche' le voci prima nascoste non sono cambiate dopo since. Si ricordano le capacita' dell'ultima sync e la
    /// revisione a cui sono cambiate: sync completa se le capacita' sono diverse, e anche finche' il PC non ha raggiunto quella revisione (risposta persa per strada).
    /// PC mai visto (anche quelli abbinati prima di questo registro): una sync completa, poi normale.
    /// </summary>
    public static bool FullSync(string pcId, string caps, long since)
    {
        using var c = Pcs.Open();
        using (var r = Pcs.Cmd(c, "SELECT caps, rev FROM pc_caps WHERE pc_id = $i", ("$i", pcId)).ExecuteReader())
            if (r.Read() && r.GetString(0) == caps) return since < r.GetInt64(1);
        // ponytail: se la revisione non cambia e la risposta completa si perde, la sync dopo non e' completa; si recupera al primo cambiamento delle voci VNC
        var rev = Pcs.Cmd(c, "SELECT v FROM meta WHERE k = 'rev'").ExecuteScalar() is long v ? v : 0;
        Pcs.Cmd(c, "INSERT OR REPLACE INTO pc_caps (pc_id, caps, rev) VALUES ($i, $c, $r)", ("$i", pcId), ("$c", caps), ("$r", rev)).ExecuteNonQuery();
        return true;
    }

    public static void LogSync(string pcId, int count, int shared = 0)
    {
        using var c = Pcs.Open();
        Pcs.Log(c, pcId, "sync", shared == 0 ? $"{count} connessioni scaricate" : $"{count} connessioni e {shared} voci condivise (dispositivi, cartelle, credenziali) scaricate");
    }
}

internal static class ConnectionsApi
{
    private record IdOnly(bool Disabled);

    private static bool LocalClient(HttpContext c) =>
        c.Connection.RemoteIpAddress is { } ip && (System.Net.IPAddress.IsLoopback(ip) || NetUtil.IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/connections", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Conns.List()));

        api.MapPost("/connections", (HttpContext c, Conns.Input i) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (Conns.Validate(i) is { } bad) return Results.Json(new { error = "input", field = bad }, statusCode: 400);
            return Results.Json(new { id = Conns.Create(i) });
        });

        api.MapPost("/connections/{id}/update", (HttpContext c, string id, Conns.Input i) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            if (Conns.Validate(i) is { } bad) return Results.Json(new { error = "input", field = bad }, statusCode: 400);
            return Conns.Update(id, i) ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        api.MapPost("/connections/{id}/disable", (HttpContext c, string id, IdOnly r) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Conns.SetDisabled(id, r.Disabled) ? Results.Json(new { ok = true }) : Results.NotFound());

        api.MapPost("/connections/{id}/delete", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Conns.Delete(id) ? Results.Json(new { ok = true }) : Results.NotFound());

        api.MapPost("/connections/{id}/reveal", (HttpContext c, string id) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            try
            {
                var (found, pw) = Conns.Reveal(id);
                return found ? Results.Json(new { password = pw }) : Results.NotFound();
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); } // password non decifrabile: chiave cambiata o database manomesso
        });

        // ---- PC collegato con permesso "scrittura": aggiunge, modifica, cancella ----
        // "non copiare" vieta di AGGIUNGERE voci (cosi' un PC non carica sul server le connessioni salvate in locale); modificare e cancellare le voci condivise resta permesso.
        // Risposta: la voce com'e' ora sul server (il PC la unisce alla sua copia senza aspettare la sincronizzazione).
        static (Pcs.PcRow? pc, IResult? err) Writer(HttpContext c, bool adding)
        {
            if (!State.Config.Configured) return (null, Results.NotFound());
            if (!LocalClient(c)) return (null, Results.StatusCode(403));
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return (null, Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401));
            if (pc.Permission != "write") return (null, Results.Json(new { error = "permission" }, statusCode: 403));
            if (adding && pc.NoCopy) return (null, Results.Json(new { error = "nocopy" }, statusCode: 403));
            return (pc, null);
        }

        api.MapPost("/client/connections", (HttpContext c, Conns.Input i) =>
        {
            var (pc, err) = Writer(c, adding: true);
            if (err != null) return err;
            if (Conns.Validate(i) is { } bad) return Results.Json(new { error = "input", field = bad }, statusCode: 400);
            return Results.Json(new { item = Conns.Get(Conns.Create(i, pc!.Id)) });
        });

        api.MapPost("/client/connections/{id}/update", (HttpContext c, string id, Conns.Input i) =>
        {
            var (pc, err) = Writer(c, adding: false);
            if (err != null) return err;
            if (Conns.Validate(i) is { } bad) return Results.Json(new { error = "input", field = bad }, statusCode: 400);
            return Conns.TryUpdate(id, i, pc!.Id) switch
            {
                "ok" => Results.Json(new { item = Conns.Get(id) }),
                "conflict" => Results.Json(new { error = "conflict" }, statusCode: 409),
                _ => Results.NotFound(),
            };
        });

        api.MapPost("/client/connections/{id}/delete", (HttpContext c, string id) =>
        {
            var (pc, err) = Writer(c, adding: false);
            if (err != null) return err;
            return Conns.Delete(id, pc!.Id) ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        // ---- PC collegato: scarica le voci cambiate dopo l'ultima revisione che conosce ----
        api.MapGet("/client/sync", (HttpContext c, long since = 0) =>
        {
            if (!State.Config.Configured) return Results.NotFound();
            if (!LocalClient(c)) return Results.StatusCode(403);
            var bearer = c.Request.Headers.Authorization.ToString();
            bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
            var pc = Pcs.Authenticate(bearer);
            if (pc == null) return Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401);
            try
            {
                bool vnc = Caps.Has(c, "vnc"); // client vecchi (senza capacita'): solo RDP e SSH
                if (since < 0) since = 0;
                bool full = Conns.FullSync(pc.Id, Caps.Key(c), since); // capacita' cambiate: tutte le voci, il cursore del PC non basta (il client fonde per id)
                var (rev, items) = Conns.Changes(since, vnc, full);
                int count = vnc ? items.Count : items.Count(i => i.Protocol != "vnc"); // le voci VNC tolte ai client vecchi non contano come scaricate
                // devices/folders/fcreds (Shared.cs): solo ai client con la capacita' "shared"; gli altri non vedono i campi
                var sh = Caps.Has(c, "shared") ? Shared.Changes(since, full) : default;
                int shCount = sh.devices?.Count + sh.folders?.Count + sh.fcreds?.Count ?? 0;
                if (count + shCount > 0) Conns.LogSync(pc.Id, count, shCount);
                // requests: esito delle richieste di questo PC (Requests.cs); personal: stato delle sue connessioni personali inviate (Personal.cs), solo ai client che le conoscono
                var body = new Dictionary<string, object?> { ["rev"] = rev, ["items"] = items, ["permission"] = Caps.Perm(c, pc.Permission), ["requests"] = Reqs.ListMine(pc.Id), ["personal"] = Caps.Has(c, "personal") ? Pers.ListMine(pc.Id) : null };
                if (sh.devices != null) { body["devices"] = sh.devices; body["folders"] = sh.folders; body["fcreds"] = sh.fcreds; }
                return Results.Json(body);
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });
    }
}
