using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lemora.Server;

/// <summary>
/// Encryption of shared passwords. The key (32 random bytes) is in the server.key file in the data folder, OUTSIDE the database folder:
/// whoever copies only the database cannot read the passwords. Each password is bound to its own entry (id): swapping two passwords in the database does not work.
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
                if (k.Length != 32) throw new InvalidOperationException("invalid server.key");
                return _key = k;
            }
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(KeyFile, key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); // on Windows the data folder's ACL applies
            return _key = key;
        }
    }

    /// <summary>nonce(12) + tag(16) + ciphertext, in base64</summary>
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

    /// <summary>fingerprint keyed with the server key: whoever copies only the database cannot guess the passwords by trying them</summary>
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

/// <summary>RDP, SSH and VNC connections shared by the administrator with the linked PCs</summary>
internal static class Conns
{
    public record Item(string Id, string Folder, string Name, string Protocol, string Host, int Port, string User, bool HasPassword, string Tags, int Version, bool Disabled, string UpdatedAt);
    /// <summary>BaseVersion: only from linked PCs, it is the version the PC saw; if it has changed in the meantime the edit is rejected</summary>
    /// <summary>Ucfg: the entry's binding to folder credentials, in JSON (active users, default, "always ask"); the server stores it and passes it to the PCs without reading it.
    /// null = unchanged (old client), "" = no binding</summary>
    /// <summary>LOCAL user of an entry (only for that server): all are passed to the PCs, with the password encrypted on the server. Lusers: null = unchanged (old client), empty list = none</summary>
    public record LUser(string? User, string? Password);
    public record Input(string? Folder, string? Name, string? Protocol, string? Host, int Port, string? User, string? Password, string? Tags, int? BaseVersion = null, string? Ucfg = null, List<LUser>? Lusers = null);

    internal static string? EncU(List<LUser>? l, string id) => l == null ? null : Vault.Encrypt(System.Text.Json.JsonSerializer.Serialize(l), id);
    internal static List<LUser>? DecU(string? enc, string id)
    {
        if (string.IsNullOrEmpty(enc)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<LUser>>(Vault.Decrypt(enc, id)); } catch (System.Text.Json.JsonException) { return null; }
    }
    /// <summary>local user names for the console (never the passwords)</summary>
    internal static string LocalNames(List<LUser>? l) => l == null || l.Count == 0 ? "" : $"{l.Count} local users: " + string.Join(", ", l.Select(x => x.User));
    /// <summary>Origin: the entry comes from a PC's personal connection (Personal.cs): the originating PC recognizes it and hides its local copy</summary>
    public record SyncItem(string Id, int Version, string Folder, string Name, string Protocol, string Host, int Port, string User, string? Password, string Tags, bool Disabled, bool Deleted, Origin? Origin = null, string? Ucfg = null, List<LUser>? Lusers = null);
    public record Origin(string Pc, string LocalId);

    internal static bool Clean(string? s, int max, bool required = false) => s != null && s.Length <= max && !s.Any(char.IsControl) && (!required || s.Trim().Length > 0);

    /// <summary>null if the data is fine, otherwise the name of the wrong field</summary>
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
        if (!Clean(i.Ucfg ?? "", 4000)) return "ucfg";
        if (i.Lusers != null)
        {
            if (i.Lusers.Count > 50) return "lusers";
            foreach (var u in i.Lusers)
                if (!Clean(u.User, 128, true) || (u.Password != null && (u.Password.Length > 256 || u.Password.Any(c => char.IsControl(c) && c != '\t')))) return "lusers";
        }
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
        Pcs.Cmd(c, "INSERT INTO connections (id, folder, name, protocol, host, port, user, pw_enc, tags, version, disabled, rev, created_at, updated_at, ucfg, users_enc) VALUES ($i, $f, $n, $p, $h, $o, $u, $w, $t, 1, 0, $r, $c, $c, $ug, $ue)",
            ("$i", id), ("$f", NormFolder(i.Folder)), ("$n", i.Name!.Trim()), ("$p", i.Protocol), ("$h", i.Host!.Trim()), ("$o", i.Port), ("$u", i.User ?? ""),
            ("$w", string.IsNullOrEmpty(i.Password) ? null : Vault.Encrypt(i.Password, id)), ("$t", NormTags(i.Tags)), ("$r", rev), ("$c", now), ("$ug", i.Ucfg ?? ""), ("$ue", EncU(i.Lusers, id))).ExecuteNonQuery();
        Pcs.Log(c, pcId, "connection-created", i.Name!.Trim());
        tx.Commit();
        return id;
    }

    /// <summary>password null = leave it as is, "" = remove it</summary>
    public static bool Update(string id, Input i, string? pcId = null) => TryUpdate(id, i, pcId) == "ok";

    /// <summary>"ok" | "notfound" | "conflict" (the entry changed after the version the PC saw)</summary>
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
            // the 409 to the client does not change: the version proposed by the PC is only kept for the Conflicts page
            if (pcId != null) { Conflicts.Record(c, id, pcId, i); tx.Commit(); }
            return "conflict";
        }
        var pw = i.Password == null ? oldPw : i.Password.Length == 0 ? null : Vault.Encrypt(i.Password, id);
        Pcs.Cmd(c, "UPDATE connections SET folder=$f, name=$n, protocol=$p, host=$h, port=$o, user=$u, pw_enc=$w, tags=$t, ucfg=COALESCE($ug, ucfg), users_enc=COALESCE($ue, users_enc), version=version+1, rev=$r, updated_at=$d WHERE id=$i",
            ("$f", NormFolder(i.Folder)), ("$n", i.Name!.Trim()), ("$p", i.Protocol), ("$h", i.Host!.Trim()), ("$o", i.Port), ("$u", i.User ?? ""), ("$w", pw),
            ("$t", NormTags(i.Tags)), ("$ug", i.Ucfg), ("$ue", EncU(i.Lusers, id)), ("$r", NextRev(c)), ("$d", Pcs.Now()), ("$i", id)).ExecuteNonQuery();
        Pcs.Log(c, pcId, "connection-updated", i.Name!.Trim() + (i.Password != null ? " (password changed)" : ""));
        tx.Commit();
        return "ok";
    }

    /// <summary>an entry as the PCs see it (plaintext password), null if missing or in the trash</summary>
    public static SyncItem? Get(string id)
    {
        using var c = Pcs.Open();
        using var r = Pcs.Cmd(c, "SELECT id, version, folder, name, protocol, host, port, user, pw_enc, tags, disabled, ucfg, users_enc FROM connections WHERE id = $i AND deleted_at IS NULL", ("$i", id)).ExecuteReader();
        if (!r.Read()) return null;
        return new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7),
            r.IsDBNull(8) ? null : Vault.Decrypt(r.GetString(8), id), r.GetString(9), r.GetInt32(10) == 1, false, null, r.IsDBNull(11) ? "" : r.GetString(11),
            DecU(r.IsDBNull(12) ? null : r.GetString(12), id));
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

    /// <summary>the entry goes to the trash: it disappears from the PCs at their next contact, the password stays encrypted for a possible restore</summary>
    /// <param name="admin">name of the administrator who deleted it (or approved the PC's request); with pcId and admin = approved request</param>
    public static bool Delete(string id, string? pcId = null, string? admin = null)
    {
        using var c = Pcs.Open();
        using var tx = c.BeginTransaction();
        string? by = null;
        if (pcId != null)
        {
            var who = Pcs.Cmd(c, "SELECT pc || ' - ' || user FROM pcs WHERE id = $p", ("$p", pcId)).ExecuteScalar() as string ?? "?";
            by = admin != null ? $"R:{who}|{admin}" : $"P:{who}";
        }
        else if (admin != null) by = "A:" + admin;
        int n = Pcs.Cmd(c, "UPDATE connections SET deleted_at=$t, deleted_by=$b, purged_at=NULL, purged_by=NULL, version=version+1, rev=$r, updated_at=$t WHERE id=$i AND deleted_at IS NULL", ("$t", Pcs.Now()), ("$b", by), ("$r", NextRev(c)), ("$i", id)).ExecuteNonQuery();
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

    /// <summary>"Show password": whoever asks for it is recorded in the event log</summary>
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

    /// <summary>changes after the given revision (including deleted and disabled ones), for the linked PCs</summary>
    /// <param name="vnc">false = client that does not declare the "vnc" capability (X-Lemora-Caps, see Caps in Requests.cs): no VNC entries, which it would show as RDP.
    /// If it had already received some (since > 0) it gets a "deleted" entry (protocol vnc) for each one, removing it from its copy</param>
    /// <param name="full">true = the PC's capabilities changed (see FullSync): all visible entries, not only those changed after since</param>
    public static (long rev, List<SyncItem> items) Changes(long since, bool vnc = true, bool full = false)
    {
        using var c = Pcs.Open();
        var list = new List<SyncItem>();
        if (!vnc && since > 0)
        {
            using var t = Pcs.Cmd(c, "SELECT id, version FROM connections WHERE protocol = 'vnc'").ExecuteReader();
            while (t.Read()) list.Add(new(t.GetString(0), t.GetInt32(1), "", "", "vnc", "", 0, "", null, "", false, true));
        }
        // from zero (new PC) already deleted entries are not needed: there is nothing to remove
        using (var r = Pcs.Cmd(c, "SELECT x.id, x.version, x.folder, x.name, x.protocol, x.host, x.port, x.user, x.pw_enc, x.tags, x.disabled, x.deleted_at IS NOT NULL, p.pc_id, p.local_id, x.ucfg, x.users_enc " +
                   "FROM connections x LEFT JOIN personal p ON p.conn_id = x.id AND p.state = 'published' " +
                   "WHERE (x.rev > $s OR ($f = 1 AND x.deleted_at IS NULL)) AND ($s > 0 OR x.deleted_at IS NULL) AND ($v = 1 OR x.protocol <> 'vnc') ORDER BY x.rev", ("$s", since), ("$v", vnc ? 1 : 0), ("$f", full ? 1 : 0)).ExecuteReader())
        {
            while (r.Read())
            {
                var id = r.GetString(0);
                bool deleted = r.GetInt32(11) == 1;
                list.Add(new(id, r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7),
                    deleted || r.IsDBNull(8) ? null : Vault.Decrypt(r.GetString(8), id), r.GetString(9), r.GetInt32(10) == 1, deleted,
                    r.IsDBNull(12) ? null : new Origin(r.GetString(12), r.GetString(13)), r.IsDBNull(14) ? "" : r.GetString(14),
                    deleted ? null : DecU(r.IsDBNull(15) ? null : r.GetString(15), id)));
            }
        }
        var rev = Pcs.Cmd(c, "SELECT v FROM meta WHERE k = 'rev'").ExecuteScalar() is long v ? v : 0;
        return (rev, list);
    }

    /// <summary>
    /// The entries sent depend on the client's capabilities (no VNC to old clients): if the PC changes capabilities (updated or rolled back to an old
    /// version) its since cursor is not enough, because the previously hidden entries did not change after since. The capabilities of the last sync and the
    /// revision at which they changed are remembered: full sync if the capabilities differ, and also until the PC has reached that revision (response lost on the way).
    /// PC never seen (including those paired before this table existed): one full sync, then normal.
    /// </summary>
    public static bool FullSync(string pcId, string caps, long since)
    {
        using var c = Pcs.Open();
        using (var r = Pcs.Cmd(c, "SELECT caps, rev FROM pc_caps WHERE pc_id = $i", ("$i", pcId)).ExecuteReader())
            if (r.Read() && r.GetString(0) == caps) return since < r.GetInt64(1);
        // ponytail: if the revision does not change and the full response is lost, the next sync is not full; it recovers at the first change of the VNC entries
        var rev = Pcs.Cmd(c, "SELECT v FROM meta WHERE k = 'rev'").ExecuteScalar() is long v ? v : 0;
        Pcs.Cmd(c, "INSERT OR REPLACE INTO pc_caps (pc_id, caps, rev) VALUES ($i, $c, $r)", ("$i", pcId), ("$c", caps), ("$r", rev)).ExecuteNonQuery();
        return true;
    }

    public static void LogSync(string pcId, int count, int shared = 0)
    {
        using var c = Pcs.Open();
        Pcs.Log(c, pcId, "sync", shared == 0 ? $"{count} connections downloaded" : $"{count} connections and {shared} shared items (devices, folders, credentials) downloaded");
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
            !Api.SignedIn(c) ? Results.StatusCode(401) : Conns.Delete(id, null, Admins.Current(c) ?? "?") ? Results.Json(new { ok = true }) : Results.NotFound());

        api.MapPost("/connections/{id}/reveal", (HttpContext c, string id) =>
        {
            if (!Api.SignedIn(c)) return Results.StatusCode(401);
            try
            {
                var (found, pw) = Conns.Reveal(id);
                return found ? Results.Json(new { password = pw }) : Results.NotFound();
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); } // password cannot be decrypted: key changed or database tampered with
        });

        // ---- linked PC with "write" permission: adds, edits, deletes ----
        // "do not copy" forbids ADDING entries (so a PC does not upload its locally saved connections to the server); editing and deleting shared entries is still allowed.
        // Response: the entry as it now is on the server (the PC merges it into its copy without waiting for the sync).
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

        // ---- linked PC: downloads the entries changed after the last revision it knows ----
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
                bool vnc = Caps.Has(c, "vnc"); // old clients (no capabilities): only RDP and SSH
                if (since < 0) since = 0;
                bool full = Conns.FullSync(pc.Id, Caps.Key(c), since); // capabilities changed: all entries, the PC's cursor is not enough (the client merges by id)
                var (rev, items) = Conns.Changes(since, vnc, full);
                int count = vnc ? items.Count : items.Count(i => i.Protocol != "vnc"); // VNC entries removed for old clients do not count as downloaded
                // devices/folders/fcreds (Shared.cs): only to clients with the "shared" capability; the others do not see the fields
                var sh = Caps.Has(c, "shared") ? Shared.Changes(since, full) : default;
                int shCount = sh.devices?.Count + sh.folders?.Count + sh.fcreds?.Count ?? 0;
                if (count + shCount > 0) Conns.LogSync(pc.Id, count, shCount);
                // requests: outcome of this PC's requests (Requests.cs); personal: state of its submitted personal connections (Personal.cs), only to clients that know them
                var body = new Dictionary<string, object?> { ["rev"] = rev, ["items"] = items, ["permission"] = Caps.Perm(c, pc.Permission), ["claude"] = Pcs.McpRule(pc.Mcp), ["requests"] = Reqs.ListMine(pc.Id), ["personal"] = Caps.Has(c, "personal") ? Pers.ListMine(pc.Id) : null };
                if (sh.devices != null) { body["devices"] = sh.devices; body["folders"] = sh.folders; body["fcreds"] = sh.fcreds; }
                return Results.Json(body);
            }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "key" }, statusCode: 500); }
        });
    }
}
