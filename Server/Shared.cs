using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Cose condivise con tutti i PC collegati oltre alle connessioni: dispositivi con le loro credenziali (gestore password), cartelle (con dominio e prefisso IP)
/// e credenziali di cartella. Stesso contatore di revisione (meta 'rev') e stesso cursore since delle connessioni.
/// Le cancellazioni sono "lapidi" (deleted_at): i PC le scaricano e tolgono la voce. I percorsi di cartella usano '/' e si confrontano senza badare alle maiuscole.
/// </summary>
internal static class Shared
{
    public record UserPw(string? User, string? Password);
    public record DevItem(string Id, int Version, string Folder, string Name, string Ip, List<UserPw> Users, bool Deleted);
    public record FolderItem(string Path, string Domain, string IpPrefix, int Version, bool Deleted);
    public record FcredItem(string Id, int Version, string Folder, string User, string Password, bool Deleted);

    public record DevIn(string? Id, string? Folder, string? Name, string? Ip, List<UserPw>? Users, int? BaseVersion = null);
    public record FcIn(string? Id, string? Folder, string? User, string? Password, int? BaseVersion = null);
    public record FolderIn(string? Path, string? Domain, string? IpPrefix);

    private static bool Clean(string? s, int max, bool required = false) => Conns.Clean(s, max, required);
    private static bool PwOk(string? p) => p == null || (p.Length <= 500 && !p.Any(c => char.IsControl(c) && c != '\t'));
    private static readonly Regex Hex32 = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private static string? GoodId(string? id) => id != null && Hex32.IsMatch(id) ? id.ToLowerInvariant() : null;

    /// <summary>true se la colonna e' la cartella $f o sta sotto $f/ (confronto senza maiuscole; niente LIKE, quindi % e _ non sono jolly)</summary>
    private static string Under(string col) => $"({col} = $f COLLATE NOCASE OR substr({col}, 1, length($f) + 1) = ($f || '/') COLLATE NOCASE)";

    // ---- validazione: null se va bene, altrimenti il nome del campo sbagliato ----

    public static string? Validate(DevIn i)
    {
        if (!Clean(i.Name, 200, true)) return "name";
        if (!Clean(i.Folder ?? "", 200)) return "folder";
        if (!Clean(i.Ip ?? "", 100)) return "ip";
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
        if (!Clean(i.Folder ?? "", 200)) return "folder";
        if (!PwOk(i.Password)) return "password";
        return null;
    }

    /// <summary>percorso normalizzato oppure null se non valido (vuoto, troppo lungo, caratteri di controllo)</summary>
    public static string? CleanPath(string? p) => Clean(p, 200, true) && Conns.NormFolder(p) is { Length: > 0 and <= 200 } n ? n : null;

    // ---- dispositivi ----

    private static string UsersJson(List<UserPw>? users) =>
        JsonSerializer.Serialize((users ?? new()).Select(u => new UserPw((u.User ?? "").Trim(), u.Password ?? "")).ToList());

    private static DevItem ReadDev(SqliteDataReader r)
    {
        var id = r.GetString(0);
        bool deleted = r.GetInt32(6) == 1;
        var users = deleted || r.IsDBNull(5) ? new List<UserPw>() : JsonSerializer.Deserialize<List<UserPw>>(Vault.Decrypt(r.GetString(5), "dev:" + id)) ?? new();
        return new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), users, deleted);
    }
    private const string DevCols = "id, version, folder, name, ip, users_enc, deleted_at IS NOT NULL";

    public static DevItem? GetDev(string id)
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, $"SELECT {DevCols} FROM devices WHERE id = $i", ("$i", id)).ExecuteReader();
        return r.Read() ? ReadDev(r) : null;
    }

    /// <summary>"ok" | "conflict" | "notfound". Id sconosciuto senza baseVersion = nuovo dispositivo (con l'id proposto se e' di 32 cifre esadecimali)</summary>
    public static (string status, string id) SaveDevice(DevIn i, string? pcId)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var id = GoodId(i.Id);
        int? version = null; bool tomb = false;
        if (id != null)
            using (var r = Pcs.Cmd(c, "SELECT version, deleted_at IS NOT NULL FROM devices WHERE id = $i", ("$i", id)).ExecuteReader())
                if (r.Read()) { version = r.GetInt32(0); tomb = r.GetInt32(1) == 1; }
        var vals = new (string, object?)[] { ("$f", Conns.NormFolder(i.Folder)), ("$n", i.Name!.Trim()), ("$p", (i.Ip ?? "").Trim()) };
        if (version == null)
        {
            if (i.BaseVersion != null && id != null) return ("notfound", id); // voleva modificare un dispositivo che non c'e'
            id ??= Guid.NewGuid().ToString("N");
            Pcs.Cmd(c, "INSERT INTO devices (id, folder, name, ip, users_enc, version, rev, updated_at) VALUES ($i, $f, $n, $p, $u, 1, $r, $d)",
                vals.Concat(new (string, object?)[] { ("$i", id), ("$u", Vault.Encrypt(UsersJson(i.Users), "dev:" + id)), ("$r", Conns.NextRev(c)), ("$d", Pcs.Now()) }).ToArray()).ExecuteNonQuery();
            Pcs.Log(c, pcId, "device-created", i.Name.Trim());
        }
        else
        {
            if (tomb && i.BaseVersion != null) return ("notfound", id!);
            if (!tomb && i.BaseVersion != null && i.BaseVersion != version) return ("conflict", id!);
            Pcs.Cmd(c, "UPDATE devices SET folder=$f, name=$n, ip=$p, users_enc=$u, deleted_at=NULL, version=version+1, rev=$r, updated_at=$d WHERE id=$i",
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

    // ---- credenziali di cartella ----

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

    /// <summary>"ok" | "conflict" | "notfound" | "exists" (stessa cartella e utente gia' presenti). Password null in modifica = lasciata com'e', "" = tolta</summary>
    public static (string status, string id) SaveFcred(FcIn i, string? pcId)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        var id = GoodId(i.Id);
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
            Pcs.Log(c, pcId, tomb ? "fcred-created" : "fcred-updated", $"{folder} / {user}" + (i.Password != null ? " (password cambiata)" : ""));
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

    // ---- cartelle ----

    private static FolderItem ReadFolder(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4) == 1);
    private const string FolderCols = "path, domain, ip_prefix, version, deleted_at IS NOT NULL";

    /// <summary>crea la cartella o ne cambia le impostazioni; se era cancellata la ripristina. Se non cambia nulla non tocca la revisione</summary>
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

    /// <summary>"ok" | "notempty" (c'e' una connessione condivisa viva dentro) | "notfound". Cancella anche le sottocartelle e, a cascata, dispositivi e credenziali</summary>
    public static string DeleteFolder(string path, string? pcId)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        using (var r = Pcs.Cmd(c, $"SELECT 1 FROM connections WHERE deleted_at IS NULL AND {Under("folder")} LIMIT 1", ("$f", path)).ExecuteReader())
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
    /// "ok" | "notfound" | "exists" | "into_itself". Sposta il prefisso in cartelle, connessioni, dispositivi, credenziali e connessioni personali (version+1 e nuova revisione).
    /// Per le cartelle il vecchio percorso resta come lapide e il nuovo diventa una riga viva: cosi' i PC che non l'hanno mai vista scaricano entrambe.
    /// Cambio di sole maiuscole: la riga resta la stessa.
    /// </summary>
    public static (string status, RenameCounts? counts) Rename(string from, string to, string? pcId)
    {
        if (to.Length > from.Length && to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) return ("into_itself", null);
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        var folders = new List<FolderItem>();
        using (var r = Pcs.Cmd(c, $"SELECT {FolderCols} FROM folders WHERE deleted_at IS NULL AND {Under("path")}", ("$f", from)).ExecuteReader())
            while (r.Read()) folders.Add(ReadFolder(r));
        bool Any(string table, string col, string extra) { using var r = Pcs.Cmd(c, $"SELECT 1 FROM {table} WHERE {extra}{Under(col)} LIMIT 1", ("$f", from)).ExecuteReader(); return r.Read(); }
        if (folders.Count == 0 && !Any("connections", "folder", "deleted_at IS NULL AND ") && !Any("devices", "folder", "deleted_at IS NULL AND ")
            && !Any("fcreds", "folder", "deleted_at IS NULL AND ") && !Any("personal", "folder", "")) return ("notfound", null);
        if (string.Equals(from, to, StringComparison.Ordinal)) return ("ok", new(0, 0, 0, 0, 0));
        string Tail(string p) => to + p[from.Length..];
        if (!caseOnly)
        {
            // un percorso di arrivo gia' vivo (fuori da quelli che si spostano) blocca lo spostamento
            // (Prepend(from): la cartella di arrivo stessa, anche se `from` non ha una riga propria)
            foreach (var path in folders.Select(f => f.Path).Prepend(from).Select(Tail).Distinct(StringComparer.OrdinalIgnoreCase))
                using (var r = Pcs.Cmd(c, "SELECT 1 FROM folders WHERE deleted_at IS NULL AND path = $p", ("$p", path)).ExecuteReader())
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
                    ("$n", np), ("$m", f.Domain), ("$i", f.IpPrefix), ("$r", rev), ("$d", now)).ExecuteNonQuery(); // lapide preesistente sul nuovo percorso: si riusa
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
        int np2 = Pcs.Cmd(c, $"UPDATE personal SET {set} WHERE {Under("folder")}", p).ExecuteNonQuery();
        // le connessioni personali pubblicate ricordano la versione della connessione condivisa: si riallinea, o la prossima modifica del PC darebbe conflitto
        Pcs.Cmd(c, "UPDATE personal SET conn_version = (SELECT version FROM connections WHERE id = personal.conn_id) WHERE state = 'published' AND conn_id IN (SELECT id FROM connections WHERE rev = $r)", ("$r", rev)).ExecuteNonQuery();
        Pcs.Log(c, pcId, "folder-renamed", $"{from} -> {to}");
        tx.Commit();
        return ("ok", new(folders.Count, nc, nd, nf, np2));
    }

    // ---- sincronizzazione ----

    /// <summary>righe cambiate dopo since (anche le lapidi, ma non da zero); full = tutte quelle vive</summary>
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

    // ---- console (sola lettura, senza password) ----

    public static object ListDevices()
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, $"SELECT {DevCols}, updated_at FROM devices WHERE deleted_at IS NULL ORDER BY folder, name COLLATE NOCASE").ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            var d = ReadDev(r);
            list.Add(new { d.Id, d.Folder, d.Name, d.Ip, UserCount = d.Users.Count, Users = d.Users.Select(u => u.User).ToList(), d.Version, UpdatedAt = r.GetString(7) });
        }
        return list;
    }

    /// <summary>cartelle vive con quante voci vive ci sono dentro (sottocartelle comprese): connessioni condivise, dispositivi, credenziali. Entries = somma</summary>
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

    /// <summary>PC approvato con la chiave giusta. Creare e modificare: tutti i permessi (anche "sola lettura"; "richieste" qui non passa dalle approvazioni).
    /// deleting = eliminare un dispositivo o una credenziale: vietato solo a "sola lettura" (403 permission). Le cartelle non chiedono nulla di piu'.</summary>
    private static (Pcs.PcRow? pc, IResult? err) Writer(HttpContext c, bool deleting = false)
    {
        if (!State.Config.Configured) return (null, Results.NotFound());
        if (!LocalClient(c)) return (null, Results.StatusCode(403));
        var bearer = c.Request.Headers.Authorization.ToString();
        bearer = bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
        var pc = Pcs.Authenticate(bearer);
        if (pc == null) return (null, Pcs.IsRevoked(bearer) ? Results.Json(new { state = "revoked" }, statusCode: 401) : Results.StatusCode(401));
        if (deleting && pc.Permission == "read") return (null, Results.Json(new { error = "permission" }, statusCode: 403));
        return (pc, null);
    }

    private static IResult Input(string field) => Results.Json(new { error = "input", field }, statusCode: 400);

    /// <summary>esegue la scrittura; password non decifrabili (chiave cambiata) = 500 error key, come nel sync</summary>
    private static IResult Guard(Func<IResult> f)
    {
        try { return f(); }
        catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
    }

    public static void Map(RouteGroupBuilder api)
    {
        // ---- console (sola lettura) ----
        api.MapGet("/devices", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Guard(() => Results.Json(Shared.ListDevices())));
        api.MapGet("/folders", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Shared.ListFolders()));
        api.MapGet("/fcreds", (HttpContext c) => !Api.SignedIn(c) ? Results.StatusCode(401) : Results.Json(Shared.ListFcreds()));

        // ---- PC collegato ----
        api.MapPost("/client/devices", (HttpContext c, Shared.DevIn i) =>
        {
            var (pc, err) = Writer(c);
            if (err != null) return err;
            if (Shared.Validate(i) is { } bad) return Input(bad);
            return Guard(() =>
            {
                var (st, id) = Shared.SaveDevice(i, pc!.Id);
                return st switch
                {
                    "ok" => Results.Json(new { item = Shared.GetDev(id) }),
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
                var (st, id) = Shared.SaveFcred(i, pc!.Id);
                return st switch
                {
                    "ok" => Results.Json(new { item = Shared.GetFc(id) }),
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

        // cartelle: nessun permesso particolare, basta essere un PC approvato (cancellare solo se vuote). Stessa logica per il PC e per l'amministratore (pcId null = amministratore)
        IResult SaveFolderReq(Shared.FolderIn i, string? pcId)
        {
            if (Shared.CleanPath(i.Path) is not { } path) return Input("path");
            if (!(i.Domain ?? "").All(ch => !char.IsControl(ch)) || (i.Domain ?? "").Length > 200) return Input("domain");
            if (!(i.IpPrefix ?? "").All(ch => !char.IsControl(ch)) || (i.IpPrefix ?? "").Length > 100) return Input("ipPrefix");
            return Results.Json(new { item = Shared.SaveFolder(path, (i.Domain ?? "").Trim(), (i.IpPrefix ?? "").Trim(), pcId) });
        }
        IResult DeleteFolderReq(PathOnly i, string? pcId)
        {
            if (Shared.CleanPath(i.Path) is not { } path) return Input("path");
            return Shared.DeleteFolder(path, pcId) switch
            {
                "ok" => Results.Json(new { ok = true }),
                "notempty" => Results.Json(new { error = "notempty" }, statusCode: 409),
                _ => Results.NotFound(),
            };
        }
        IResult RenameReq(Rename i, string? pcId)
        {
            if (Shared.CleanPath(i.From) is not { } from) return Input("from");
            if (Shared.CleanPath(i.To) is not { } to) return Input("to");
            var (st, counts) = Shared.Rename(from, to, pcId);
            return st switch
            {
                "ok" => Results.Json(new { ok = true, from, to, moved = counts }),
                "exists" or "into_itself" => Results.Json(new { error = st }, statusCode: 409),
                _ => Results.NotFound(),
            };
        }

        api.MapPost("/client/folders", (HttpContext c, Shared.FolderIn i) => { var (pc, err) = Writer(c); return err ?? SaveFolderReq(i, pc!.Id); });
        api.MapPost("/client/folders/delete", (HttpContext c, PathOnly i) => { var (pc, err) = Writer(c); return err ?? DeleteFolderReq(i, pc!.Id); });
        api.MapPost("/client/folders/rename", (HttpContext c, Rename i) => { var (pc, err) = Writer(c); return err ?? RenameReq(i, pc!.Id); });

        // ---- console (azioni dell'amministratore, registrate nel registro senza PC) ----
        api.MapPost("/folders", (HttpContext c, Shared.FolderIn i) => !Api.SignedIn(c) ? Results.StatusCode(401) : SaveFolderReq(i, null));
        api.MapPost("/folders/delete", (HttpContext c, PathOnly i) => !Api.SignedIn(c) ? Results.StatusCode(401) : DeleteFolderReq(i, null));
        api.MapPost("/folders/rename", (HttpContext c, Rename i) => !Api.SignedIn(c) ? Results.StatusCode(401) : RenameReq(i, null));
        api.MapPost("/devices/{id}/delete", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Shared.DeleteDevice(id, null) ? Results.Json(new { ok = true }) : Results.NotFound());
        api.MapPost("/fcreds/{id}/delete", (HttpContext c, string id) =>
            !Api.SignedIn(c) ? Results.StatusCode(401) : Shared.DeleteFcred(id, null) ? Results.Json(new { ok = true }) : Results.NotFound());

        // "credenziale copiata": finisce nel registro, mai la password
        api.MapPost("/client/audit", (HttpContext c, Audit a) =>
        {
            var (pc, err) = Writer(c);
            if (err != null) return err;
            if (a.Kind != "copy") return Input("kind");
            if (a.What is not ("password" or "user" or "ip")) return Input("what");
            if (!Conns.Clean(a.Device ?? "", 200)) return Input("device");
            if (!Conns.Clean(a.User ?? "", 200)) return Input("user");
            using var db = Pcs.Open();
            Pcs.Log(db, pc!.Id, "copy", $"{a.What} copiata: {a.Device} / {a.User}");
            return Results.Json(new { ok = true });
        });
    }
}
