using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Model;
using Lemora.Service;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.Model;
using Lemora.Service.Locality;

namespace Lemora.Setup
{
    /// <summary>state of this PC's link to a Teams server, saved in the encrypted database (AppDbStore)</summary>
    internal class TeamsLink
    {
        public string State { get; set; } = "none"; // none | pending | approved | rejected | revoked
        public string Server { get; set; } = "";
        public DateTime SentAt { get; set; } = DateTime.Now;
        public bool Skipped { get; set; }

        // server reply to the pairing
        public string Id { get; set; } = "";
        public string Token { get; set; } = "";        // secret to ask for the outcome of the request
        public string Fingerprint { get; set; } = "";  // certificate fingerprint seen on the first connection: from then on the server must present the same one
        public string Key { get; set; } = "";          // this PC's key, issued after approval
        public string Permission { get; set; } = "read";
        public bool NoCopy { get; set; }
        public string LastError { get; set; } = "";
        /// <summary>"Share my connections with the server" toggle (requests or write permission): ON by default, also after every new pairing
        /// (new TeamsLink) and for links saved before it existed (property missing in the JSON = initial value). With read only it does not matter: always sent</summary>
        public bool SharePersonal { get; set; } = true;
        /// <summary>administrator rule for Claude (MCP server); null = the Teams server has never sent it</summary>
        public Lemora.Service.Mcp.TeamsClaudePolicy? Claude { get; set; }

        private const string DbKey = "teams.link";

        /// <summary>
        /// the PC behaves as Teams (bar, synchronization, sharing): installed in Teams mode, or paired later (even if installed as Local
        /// or portable) with a pending or approved request. The mode chosen at installation only matters for the installation itself (repair, uninstall).
        /// Rare reads (main window startup, Options page): no cache
        /// </summary>
        public static bool IsTeams => (SetupEngine.IsInstalledCopy() && SetupEngine.InstalledMode() == SetupMode.Teams)
                                      || Lemora.Utils.DevOnly.Env("LEMORA_TEAMS_DEV") == "1" // only to try the bar without installing
                                      || Load().State is "pending" or "approved";

        public static TeamsLink Load()
        {
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json))
                    return JsonSerializer.Deserialize<TeamsLink>(json!) ?? new TeamsLink();
            }
            catch { /* unreadable value: start again from "not connected" */ }
            return new TeamsLink();
        }

        public void Save() => AppDbStore.Set(DbKey, JsonSerializer.Serialize(this));

        public string StatusText => SetupText.T(State switch
        {
            "pending" => "tm_st_wait",
            "approved" => "tm_st_ok",
            "rejected" => "tm_st_no",
            "revoked" => "tm_st_rev",
            _ => "tm_st_none",
        });
    }

    /// <summary>Teams server connection error; Kind says what to show the user</summary>
    internal class TeamsException : Exception
    {
        /// <summary>address (not a local network) | unreachable | certificate | code | blocked | input | server</summary>
        public string Kind { get; }
        public int Remaining { get; }
        public DateTime? Until { get; }

        public TeamsException(string kind, string message, int remaining = 0, DateTime? until = null, Exception? inner = null) : base(message, inner)
        {
            Kind = kind;
            Remaining = remaining;
            Until = until;
        }
    }

    /// <summary>
    /// Talks to the Teams server over HTTPS. Rules: only local network addresses (checked when connecting, not only when they are typed);
    /// the server certificate is self-signed: the first time its fingerprint is remembered, afterwards it must be identical.
    /// </summary>
    internal sealed class TeamsServerClient : IDisposable
    {
        public record PairRequest(string Id, string Token, string Fingerprint);
        public record PairStatus(string State, string? Key, string? Permission, bool NoCopy, Lemora.Service.Mcp.TeamsClaudePolicy? Claude = null);

        /// <summary>"claude" field: { enabled, allServers, servers: [id] }; missing = the server does not handle the rule yet</summary>
        internal static Lemora.Service.Mcp.TeamsClaudePolicy? ClaudePolicy(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("claude", out var c) || c.ValueKind != JsonValueKind.Object) return null;
            var p = new Lemora.Service.Mcp.TeamsClaudePolicy { Enabled = Bool(c, "enabled"), AllServers = Bool(c, "allServers") };
            if (c.TryGetProperty("servers", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } id) p.Servers.Add(id);
            return p;
        }

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string? _pinned;

        /// <summary>SHA-256 fingerprint of the certificate met in the last connection</summary>
        public string? SeenFingerprint { get; private set; }

        public const int DefaultPort = 25353;

        public static (string host, int port) ParseServer(string server)
        {
            var s = (server ?? "").Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw new TeamsException("input", "The server only uses HTTPS.");
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            if (s.Length == 0) throw new TeamsException("input", "Server address missing.");
            string host = s; int port = DefaultPort;
            if (s.StartsWith("[")) // IPv6 in square brackets
            {
                int end = s.IndexOf(']');
                if (end < 0) throw new TeamsException("input", "Invalid address.");
                host = s.Substring(1, end - 1);
                if (s.Length > end + 1)
                {
                    if (s[end + 1] != ':' || !int.TryParse(s.Substring(end + 2), out port)) throw new TeamsException("input", "Invalid port.");
                }
            }
            else
            {
                int colon = s.LastIndexOf(':');
                if (colon >= 0 && s.IndexOf(':') == colon) // a single ":" = host:port (more than one = IPv6 without brackets)
                {
                    host = s.Substring(0, colon);
                    if (!int.TryParse(s.Substring(colon + 1), out port)) throw new TeamsException("input", "Invalid port.");
                }
            }
            if (host.Length == 0 || port < 1 || port > 65535) throw new TeamsException("input", "Invalid address.");
            return (host, port);
        }

        /// <summary>10.x, 172.16-31.x, 192.168.x, 169.254.x, 127.x and the IPv6 equivalents (::1, fc00::/7, fe80::/10)</summary>
        public static bool IsLocalAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
            return false;
        }

        public TeamsServerClient(string server, string? pinnedFingerprint = null)
        {
            var (host, port) = ParseServer(server);
            _baseUrl = host.Contains(':') ? $"https://[{host}]:{port}" : $"https://{host}:{port}";
            _pinned = string.IsNullOrWhiteSpace(pinnedFingerprint) ? null : pinnedFingerprint!.Replace(":", "").Trim();
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(8),
                // the address is resolved and checked here, when opening the connection: a name that points to a local PC today and to the internet tomorrow is refused
                ConnectCallback = async (ctx, ct) =>
                {
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    if (addrs.Length == 0 || addrs.Any(a => !IsLocalAddress(a)))
                        throw new TeamsException("address", "The address is not in a local network.");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert == null) return false;
                        using var c2 = new X509Certificate2(cert);
                        var fp = Convert.ToHexString(c2.GetCertHash(HashAlgorithmName.SHA256));
                        SeenFingerprint = fp;
                        return _pinned == null || string.Equals(_pinned, fp, StringComparison.OrdinalIgnoreCase);
                    },
                    // the Teams server certificate is self-signed and checked by fingerprint: the Windows chain is not needed. Without this, on the first
                    // connection of every .NET process it looked up root certificates on the internet and, with outbound traffic blocked (firewall, corporate networks), waited
                    // ~12 s: the request timed out ("The server does not respond") and the first send of the extension log stayed queued
                    CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck, TrustMode = X509ChainTrustMode.CustomRootTrust },
                },
            };
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        }

        public void Dispose() => _http.Dispose();

        private async Task<(int status, JsonElement body)> SendAsync(HttpMethod method, string path, object? payload, string? bearer, CancellationToken ct, string? createKey = null)
        {
            using var req = new HttpRequestMessage(method, _baseUrl + path);
            req.Headers.Add("X-Lemora", "1");
            if (createKey != null) req.Headers.Add("X-Lemora-Create-Key", createKey);
            req.Headers.Add("X-Lemora-Caps", "vnc,requests,personal,sessions,shared"); // shared = device passwords, shared folders and folder credentials; without it the server treats the client as old: no VNC, "requests" permission read as "read", no personal connections nor connection duration
            if (bearer != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            if (payload != null) req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            // log: method, path, outcome and time (Medium); never the content, which carries keys and passwords
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                JsonElement body = default;
                try { if (text.Length > 0) body = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { /* reply without JSON */ }
                Shawn.Utils.SimpleLogHelper.Debug($"Teams: {method} {path} -> {(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms ({text.Length} caratteri)");
                return ((int)resp.StatusCode, body);
            }
            catch (HttpRequestException ex)
            {
                Shawn.Utils.SimpleLogHelper.Debug($"Teams: {method} {path} failed after {sw.ElapsedMilliseconds} ms:{ex.Message}{(ex.InnerException != null ? " / " + ex.InnerException.Message : "")}");
                var te = ex.InnerException as TeamsException;
                if (te != null) throw te;
                if (ex.InnerException is System.Security.Authentication.AuthenticationException)
                    throw new TeamsException("certificate", "The server certificate is not the remembered one.", inner: ex);
                throw new TeamsException("unreachable", "The server does not respond.", inner: ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                Shawn.Utils.SimpleLogHelper.Debug($"Teams: {method} {path} no answer after {sw.ElapsedMilliseconds} ms (timed out)");
                throw new TeamsException("unreachable", "The server does not respond.", inner: ex);
            }
        }

        private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>sends the pairing request; the server replies with id and secret to ask for its outcome</summary>
        public async Task<PairRequest> RequestAsync(string code, string pc, string user, string appVersion, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/pair/request", new { code, pc, user, appVersion }, null, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200:
                    var fp = Str(body, "fingerprint");
                    // the fingerprint the server declares must be the one of the certificate we talked with: a man in the middle cannot present their own
                    if (string.IsNullOrEmpty(SeenFingerprint) || !string.Equals(fp, SeenFingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new TeamsException("certificate", "The certificate fingerprint does not match.");
                    return new PairRequest(Str(body, "id"), Str(body, "token"), fp);
                case 401: throw new TeamsException("code", "Wrong or expired code.", remaining: Int(body, "remaining"));
                case 429:
                    DateTime? until = DateTime.TryParse(Str(body, "until"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u.ToLocalTime() : null;
                    throw new TeamsException("blocked", "Too many attempts.", until: until);
                case 400: throw new TeamsException("input", "Invalid PC or user name.");
                case 403: throw new TeamsException("address", "The server does not accept requests from this address.");
                default: throw new TeamsException("server", $"Unexpected server response ({status}).");
            }
        }

        /// <summary>outcome of the request; with ack=true confirms the key was saved (the server stops sending it)</summary>
        public async Task<PairStatus> StatusAsync(string id, string token, bool ack = false, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/pair/status", new { id, token, ack }, null, ct).ConfigureAwait(false);
            if (status != 200) throw new TeamsException("server", $"Unexpected server response ({status}).");
            return new PairStatus(Str(body, "state"), Str(body, "key") is { Length: > 0 } k ? k : null, Str(body, "permission") is { Length: > 0 } p ? p : null, Bool(body, "noCopy"), ClaudePolicy(body));
        }

        /// <summary>checks that this PC's key is still valid and reads its current permission</summary>
        public async Task<PairStatus> MeAsync(string key, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Get, "/api/client/me", null, key, ct).ConfigureAwait(false);
            if (status == 200) return new PairStatus(Str(body, "state"), null, Str(body, "permission"), Bool(body, "noCopy"), ClaudePolicy(body));
            if (status == 401 && Str(body, "state") == "revoked") return new PairStatus("revoked", null, null, false);
            if (status == 401) throw new TeamsException("badkey", "This PC's key is no longer valid.");
            throw new TeamsException("server", $"Unexpected server response ({status}).");
        }

        /// <summary>withdraws the still pending request: it disappears from the server console</summary>
        public async Task CancelAsync(string id, string token, CancellationToken ct = default)
        {
            var (status, _) = await SendAsync(HttpMethod.Post, "/api/pair/cancel", new { id, token }, null, ct).ConfigureAwait(false);
            if (status is not (200 or 404)) throw new TeamsException("server", $"Unexpected server response ({status}).");
        }

        public record WriteResult(TeamsItem? Item);

        /// <summary>adds (empty id), changes or deletes (null item) a shared entry. Errors: permission | nocopy | conflict | input | revoked | notfound | server | unreachable</summary>
        /// <param name="createKey">new entry: its provisional id, the same at every attempt. If the reply to a creation is lost (slow network) and it is sent again,
        /// the server returns the entry already created instead of making a duplicate</param>
        public async Task<WriteResult> WriteAsync(string key, string id, TeamsItem? item, CancellationToken ct = default, string? createKey = null)
        {
            string path = item == null ? $"/api/client/connections/{id}/delete" : id.Length == 0 ? "/api/client/connections" : $"/api/client/connections/{id}/update";
            object payload = item == null ? new { } : new
            {
                folder = item.Folder, name = item.Name, protocol = item.Protocol, host = item.Host, port = item.Port, user = item.User,
                password = item.Password ?? "", tags = item.Tags, ucfg = item.Ucfg, lusers = item.LUsers.Select(x => new { user = x.User, password = x.Password }).ToList(), baseVersion = id.Length == 0 ? (int?)null : item.Version,
            };
            var (status, body) = await SendAsync(HttpMethod.Post, path, payload, key, ct, id.Length == 0 && item != null ? createKey : null).ConfigureAwait(false);
            switch (status)
            {
                case 200:
                    return new WriteResult(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? ParseItem(it) : null);
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "The PC has been unlinked.");
                case 403: throw new TeamsException(Str(body, "error") is "nocopy" ? "nocopy" : "permission", "Operation not allowed.");
                case 409: throw new TeamsException("conflict", "The entry changed on the server.");
                case 400: throw new TeamsException("input", "Invalid data.");
                case 404: throw new TeamsException("notfound", "The entry no longer exists.");
                default: throw new TeamsException("server", $"Unexpected server response ({status}).");
            }
        }

        // ---- device passwords, shared folders and folder credentials (see TeamsShared). Folder paths travel with "/" without spaces.
        /// <param name="MoreIps">addresses besides Ip; null = the server did not send them (older server)</param>
        public record SharedDevice(string Id, int Version, string Folder, string Name, string Ip, List<Lemora.Service.DeviceUser> Users, bool Deleted, List<string>? MoreIps = null);
        public record SharedFolder(string Path, string Domain, string IpPrefix, int Version, bool Deleted);
        public record SharedCred(string Id, int Version, string Folder, string User, string Password, bool Deleted);
        public class SharedBatch
        {
            public List<SharedDevice> Devices { get; } = new();
            public List<SharedFolder> Folders { get; } = new();
            public List<SharedCred> Creds { get; } = new();
            /// <summary>false = the server does not send this data (old version): nothing to apply</summary>
            public bool Present { get; set; }
        }

        /// <summary>from the last SyncAsync: the shared changes</summary>
        public SharedBatch LastShared { get; private set; } = new SharedBatch();

        private static SharedDevice ParseDevice(JsonElement e)
        {
            var users = new List<Lemora.Service.DeviceUser>();
            if (e.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Array)
                foreach (var u in us.EnumerateArray()) users.Add(new Lemora.Service.DeviceUser { User = Str(u, "user"), Password = Str(u, "password") });
            List<string>? more = null;
            if (e.TryGetProperty("moreIps", out var ms) && ms.ValueKind == JsonValueKind.Array)
                more = ms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            return new SharedDevice(Str(e, "id"), Int(e, "version"), Str(e, "folder"), Str(e, "name"), Str(e, "ip"), users, Bool(e, "deleted"), more);
        }

        private static SharedFolder ParseFolder(JsonElement e) => new(Str(e, "path"), Str(e, "domain"), Str(e, "ipPrefix"), Int(e, "version"), Bool(e, "deleted"));
        private static SharedCred ParseCred(JsonElement e) => new(Str(e, "id"), Int(e, "version"), Str(e, "folder"), Str(e, "user"), Lemora.Utils.DataVault.FromOutside(Str(e, "password")), Bool(e, "deleted"));   // no "lv1:" from the server (security hardening)

        /// <summary>write call on shared items. Errors: revoked | permission | conflict | exists | notempty | into_itself | input | notfound | server | unreachable</summary>
        private async Task<JsonElement> SharedCallAsync(string key, string path, object payload, CancellationToken ct)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, path, payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200: return body;
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "The PC has been unlinked.");
                case 403: throw new TeamsException("permission", "Operation not allowed.");
                case 409: throw new TeamsException(Str(body, "error") is "exists" or "notempty" or "into_itself" ? Str(body, "error") : "conflict", "The server does not accept the change.");
                case 400: throw new TeamsException("input", "Invalid data.");
                case 404: throw new TeamsException("notfound", "No longer exists.");
                default: throw new TeamsException("server", $"Unexpected server response ({status}).");
            }
        }

        /// <summary>adds (create) or changes a password manager entry; returns the entry as it is now on the server</summary>
        public async Task<SharedDevice?> DeviceWriteAsync(string key, SharedDevice d, bool create, CancellationToken ct = default)
        {
            var body = await SharedCallAsync(key, "/api/client/devices", new { id = d.Id, folder = d.Folder, name = d.Name, ip = d.Ip, moreIps = d.MoreIps ?? new List<string>(), users = d.Users.Select(u => new { user = u.User, password = u.Password }), baseVersion = create ? (int?)null : d.Version }, ct).ConfigureAwait(false);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? ParseDevice(it) : null;
        }

        public Task DeviceDeleteAsync(string key, string id, CancellationToken ct = default) => SharedCallAsync(key, $"/api/client/devices/{id}/delete", new { }, ct);

        /// <summary>creates the folder or changes its domain and IP prefix (no permission needed)</summary>
        public async Task FolderUpsertAsync(string key, string path, string domain, string ipPrefix, CancellationToken ct = default) =>
            await SharedCallAsync(key, "/api/client/folders", new { path, domain, ipPrefix }, ct).ConfigureAwait(false);

        public Task FolderDeleteAsync(string key, string path, CancellationToken ct = default) => SharedCallAsync(key, "/api/client/folders/delete", new { path }, ct);

        public Task FolderRenameAsync(string key, string from, string to, CancellationToken ct = default) => SharedCallAsync(key, "/api/client/folders/rename", new { from, to }, ct);

        /// <summary>adds or changes a shared folder credential (null password = keep the current one)</summary>
        public async Task<SharedCred?> CredWriteAsync(string key, SharedCred c, bool create, string? password, CancellationToken ct = default)
        {
            var body = await SharedCallAsync(key, "/api/client/fcreds", new { id = c.Id, folder = c.Folder, user = c.User, password, baseVersion = create ? (int?)null : c.Version }, ct).ConfigureAwait(false);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? ParseCred(it) : null;
        }

        public Task CredDeleteAsync(string key, string id, CancellationToken ct = default) => SharedCallAsync(key, $"/api/client/fcreds/{id}/delete", new { }, ct);

        /// <summary>server log: "credential copied" (NEVER the password)</summary>
        public Task AuditCopyAsync(string key, string what, string device, string user, CancellationToken ct = default) =>
            SharedCallAsync(key, "/api/client/audit", new { kind = "copy", what, device, user }, ct);

        private static TeamsItem ParseItem(JsonElement e) => new TeamsItem
        {
            Id = Str(e, "id"), Version = Int(e, "version"), Folder = Str(e, "folder"), Name = Str(e, "name"), Protocol = Str(e, "protocol"),
            Host = Str(e, "host"), Port = Int(e, "port"), User = Str(e, "user"), Tags = Str(e, "tags"), Ucfg = Str(e, "ucfg"),
            LUsers = e.TryGetProperty("lusers", out var lu) && lu.ValueKind == JsonValueKind.Array
                ? lu.EnumerateArray().Select(x => new TeamsLocalUser { User = Str(x, "user"), Password = Lemora.Utils.DataVault.FromOutside(Str(x, "password")) }).ToList() : new List<TeamsLocalUser>(),
            // an "lv1:" from the server would be decrypted with this PC's key: it is discarded (security hardening)
            Password = e.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String ? Lemora.Utils.DataVault.FromOutside(pw.GetString()) : null,
            Disabled = Bool(e, "disabled"), Deleted = Bool(e, "deleted"),
            OriginPc = e.TryGetProperty("origin", out var o) ? Str(o, "pc") : "", OriginLocalId = e.TryGetProperty("origin", out var o2) ? Str(o2, "localId") : "",
        };

        /// <summary>a connection saved on this PC, as /api/client/personal receives it (plain-text password only over the channel with the pinned certificate)</summary>
        public record PersonalItem(string LocalId, string Folder, string Name, string Protocol, string Host, int Port, string User, string Password, string Tags);
        /// <summary>state of a sent personal connection: pending | published | rejected (with the administrator's note)</summary>
        public record PersonalStatus(string LocalId, string State, string Note);
        /// <summary>from the last SyncAsync: the state of the personal connections sent by this PC (empty if the server does not send it)</summary>
        public List<PersonalStatus> LastPersonal { get; private set; } = new List<PersonalStatus>();

        /// <summary>sends the server the connections saved on this PC (changed or new) and the deleted ones. Replies with the overall state (ok | nocopy | off) and that of each entry</summary>
        public async Task<(string state, Dictionary<string, string> items)> PersonalAsync(string key, bool share, List<PersonalItem> items, List<string> removed, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/client/personal", new { share, items, removed }, key, ct).ConfigureAwait(false);
            if (status == 401 && Str(body, "state") == "revoked") throw new TeamsException("revoked", "The PC has been unlinked.");
            if (status != 200) throw new TeamsException("server", $"Unexpected server response ({status}).");
            var res = new Dictionary<string, string>();
            if (body.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) res[Str(e, "localId")] = Str(e, "state");
            return (Str(body, "state"), res);
        }

        /// <summary>a session to a shared entry: local id, id of the entry on the server, protocol, start and end (null = still in progress), in UTC ISO. NEVER the password</summary>
        public record SessionItem(string Id, string ConnId, string Protocol, string Start, string? End, string? Name = null, string? Host = null, string? Folder = null, bool? Adhoc = null);

        /// <summary>uploads the starts/ends of sessions to shared entries. Never blocking for the synchronization; false if the server does not accept them (old or network)</summary>
        public async Task<bool> SessionsAsync(string key, List<SessionItem> items, CancellationToken ct = default)
        {
            var (status, _) = await SendAsync(HttpMethod.Post, "/api/client/sessions", new { items }, key, ct).ConfigureAwait(false);
            if (status == 401 && ct.IsCancellationRequested == false) return false; // revocation handled by the sync round
            return status == 200;
        }

        /// <summary>uploads the extension log events (TeamsBrowserLog). false if the server does not accept them (old, network, revocation): they stay queued</summary>
        public async Task<bool> BrowserLogAsync(string key, List<TeamsBrowserLog.Item> items, CancellationToken ct = default)
        {
            var (status, _) = await SendAsync(HttpMethod.Post, "/api/client/browserlog", new { items }, key, ct).ConfigureAwait(false);
            return status == 200;
        }

        /// <summary>outcome of a request from this PC ("requests" permission). State: pending | stale | approved | rejected</summary>
        public record ReqStatus(string Id, string State, string Note);

        /// <summary>from the last SyncAsync: this PC's requests and the current permission (empty if the server does not send them)</summary>
        public List<ReqStatus> LastRequests { get; private set; } = new List<ReqStatus>();
        public string LastPermission { get; private set; } = "";
        /// <summary>Claude rule from the last synchronization (null = the server does not send it)</summary>
        public Lemora.Service.Mcp.TeamsClaudePolicy? LastClaude { get; private set; }

        /// <summary>"requests" permission: asks the administrator to change (update), delete (delete) or add (create) an entry. Returns the request id</summary>
        public async Task<string> SendRequestAsync(string key, string kind, string id, int? baseVersion, TeamsItem? item, CancellationToken ct = default)
        {
            var payload = new
            {
                kind, connId = id.Length == 0 ? null : id, baseVersion, folder = item?.Folder, name = item?.Name, protocol = item?.Protocol, host = item?.Host,
                port = item?.Port ?? 0, user = item?.User, password = item == null ? null : item.Password ?? "", tags = item?.Tags, ucfg = item?.Ucfg, lusers = item?.LUsers.Select(x => new { user = x.User, password = x.Password }).ToList(),
            };
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/client/requests", payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200: return Str(body, "id");
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "The PC has been unlinked.");
                case 403: throw new TeamsException("permission", "Operation not allowed.");
                case 400: throw new TeamsException("input", "Invalid data.");
                case 404: throw new TeamsException("notfound", "The entry no longer exists.");
                default: throw new TeamsException("server", $"Unexpected server response ({status}).");
            }
        }

        /// <summary>
        /// changes (item) or withdraws (item null) one of this PC's requests still pending, e.g. the new entry it proposed and then edited or deleted.
        /// Errors: notfound | state (already decided) | input | oldserver (server without this function) | revoked | server
        /// </summary>
        public async Task EditRequestAsync(string key, string reqId, TeamsItem? item, CancellationToken ct = default)
        {
            object payload = item == null ? new { } : new
            {
                kind = "create", folder = item.Folder, name = item.Name, protocol = item.Protocol, host = item.Host, port = item.Port, user = item.User,
                password = item.Password ?? "", tags = item.Tags, ucfg = item.Ucfg, lusers = item.LUsers.Select(x => new { user = x.User, password = x.Password }).ToList(),
            };
            var (status, body) = await SendAsync(HttpMethod.Post, $"/api/client/requests/{Uri.EscapeDataString(reqId)}/{(item == null ? "withdraw" : "update")}", payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200: return;
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "The PC has been unlinked.");
                case 403: throw new TeamsException("permission", "Operation not allowed.");
                case 400: throw new TeamsException("input", "Invalid data.");
                case 409: throw new TeamsException("state", "The administrator has already decided.");
                case 404 when Str(body, "error") == "notfound": throw new TeamsException("notfound", "The request no longer exists on the server.");
                case 404 or 405: throw new TeamsException("oldserver", "The server does not know this function.");   // server not updated yet: no such route
                default: throw new TeamsException("server", $"Unexpected server response ({status}).");
            }
        }

        /// <summary>
        /// "requests" permission: asks the administrator to create, change or delete a folder credential (fcred-*) or a shared device (device-*).
        /// <paramref name="id"/> = id on the server (for device-create the proposed id of this PC's entry). Null password = do not change it. Returns the request id
        /// </summary>
        public async Task<string> SendSharedRequestAsync(string key, string kind, string? id, string folder, string? name, string? ip, string? user, string? password, List<DeviceUser>? users, CancellationToken ct = default, List<string>? moreIps = null)
        {
            var payload = new { kind, connId = string.IsNullOrEmpty(id) ? null : id, folder, name, host = ip, moreIps, user, password, devusers = users?.Select(u => new { user = u.User ?? "", password = u.Password ?? "" }).ToList() };
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/client/requests", payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200: return Str(body, "id");
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "The PC has been unlinked.");
                case 403: throw new TeamsException("permission", "Operation not allowed.");
                case 400 when Str(body, "field") == "kind": throw new TeamsException("oldserver", "The server does not know this request.");   // server not updated yet
                case 400: throw new TeamsException("input", "Invalid data.");
                case 404: throw new TeamsException("notfound", "The entry no longer exists on the server.");
                default: throw new TeamsException("server", $"Unexpected server response ({status}).");
            }
        }


        /// <summary>shared entries changed after revision since (0 = all). revoked=true: the PC was disconnected by the server</summary>
        public async Task<(bool revoked, long rev, List<TeamsItem> items)> SyncAsync(string key, long since, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Get, "/api/client/sync?since=" + since, null, key, ct).ConfigureAwait(false);
            if (status == 401 && Str(body, "state") == "revoked") return (true, 0, new List<TeamsItem>());
            if (status != 200) throw new TeamsException("server", $"Unexpected server response ({status}).");
            var items = new List<TeamsItem>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) items.Add(ParseItem(e));
            LastRequests = new List<ReqStatus>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("requests", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
                foreach (var e in reqs.EnumerateArray()) LastRequests.Add(new ReqStatus(Str(e, "id"), Str(e, "state"), Str(e, "note")));
            LastPermission = Str(body, "permission");
            LastClaude = ClaudePolicy(body);
            LastShared = new SharedBatch();
            if (body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("devices", out var dv) && dv.ValueKind == JsonValueKind.Array) { LastShared.Present = true; foreach (var e in dv.EnumerateArray()) LastShared.Devices.Add(ParseDevice(e)); }
                if (body.TryGetProperty("folders", out var fl) && fl.ValueKind == JsonValueKind.Array) { LastShared.Present = true; foreach (var e in fl.EnumerateArray()) LastShared.Folders.Add(ParseFolder(e)); }
                if (body.TryGetProperty("fcreds", out var fc) && fc.ValueKind == JsonValueKind.Array) { LastShared.Present = true; foreach (var e in fc.EnumerateArray()) LastShared.Creds.Add(ParseCred(e)); }
            }
            LastPersonal = new List<PersonalStatus>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("personal", out var pers) && pers.ValueKind == JsonValueKind.Array)
                foreach (var e in pers.EnumerateArray()) LastPersonal.Add(new PersonalStatus(Str(e, "localId"), Str(e, "state"), Str(e, "note")));
            return (false, Int(body, "rev"), items);
        }
    }

    /// <summary>
    /// Local copy of the connections shared by the server: it lives in the app's encrypted database (AppDbStore) and feeds the Teams data source (read only).
    /// </summary>
    internal static class TeamsSync
    {
        private class Store
        {
            public long Rev { get; set; }
            public DateTime LastOk { get; set; } = DateTime.MinValue;
            public List<TeamsItem> Items { get; set; } = new List<TeamsItem>();
            /// <summary>requests sent to the administrator ("requests" permission) and not yet decided</summary>
            public List<PendingRequest> Pending { get; set; } = new List<PendingRequest>();
            /// <summary>personal connections: fingerprint (SHA-256 of the values, password included) of the last accepted send, per local id; only what changes is sent again</summary>
            public Dictionary<string, string> PersonalSent { get; set; } = new Dictionary<string, string>();
            /// <summary>last known state of each personal connection (pending | published | rejected): the change from pending shows the notice</summary>
            public Dictionary<string, string> PersonalState { get; set; } = new Dictionary<string, string>();
            /// <summary>server reply to the last send: ok | nocopy | off (empty = never sent or server that does not know the send)</summary>
            public string PersonalMode { get; set; } = "";
            /// <summary>changes made while the server was not responding: applied right away to the local copy and sent at the first successful synchronization, in order</summary>
            public List<QueuedWrite> Queue { get; set; } = new List<QueuedWrite>();
            /// <summary>"requests" on folder credentials and devices made while the server was not answering: sent at the next synchronization</summary>
            public List<QueuedShared> SharedQueue { get; set; } = new List<QueuedShared>();
        }

        internal class QueuedWrite
        {
            public string Qid { get; set; } = Guid.NewGuid().ToString("N");
            /// <summary>id of the entry on the server; "local-..." = new entry not yet created on the server; empty = new entry request</summary>
            public string Id { get; set; } = "";
            public TeamsItem? Item { get; set; }   // null = deletion
            public string Name { get; set; } = "";
            public string Mode { get; set; } = "write";   // write | requests
            /// <summary>new entry request made offline: id of the provisional copy shown in the list with the clock ("req-q..."); empty otherwise</summary>
            public string ProvId { get; set; } = "";
        }

        private const string TempPrefix = "local-";
        /// <summary>provisional id of a new entry waiting for the administrator ("req-" + request id)</summary>
        internal const string ProvisionalPrefix = "req-";
        private static readonly object StoreLock = new object();

        /// <summary>overlays the queued changes on the local copy ("write" permission): this way a synchronization round does not erase them and the entry is usable right away</summary>
        private static void ApplyQueue(Store s)
        {
            var byId = s.Items.ToDictionary(x => x.Id);
            foreach (var q in s.Queue.Where(x => x.Mode == "write"))
            {
                if (q.Item == null) byId.Remove(q.Id);
                else byId[q.Id] = q.Item;
            }
            s.Items = byId.Values.ToList();
        }

        /// <summary>
        /// "Requests" permission with the server unreachable: the request waits in the queue. A new entry is shown right away with the clock, as when the
        /// request goes out (before: nothing appeared and the save looked lost); when the request leaves it takes the real "req-" id (<see cref="RequestSent"/>)
        /// </summary>
        internal static QueuedWrite QueueRequest(List<TeamsItem> items, List<QueuedWrite> queue, string id, TeamsItem? item, string name)
        {
            var q = new QueuedWrite { Id = id, Item = item, Name = name, Mode = "requests" };
            if (id.Length == 0 && item != null)
            {
                var prov = JsonSerializer.Deserialize<TeamsItem>(JsonSerializer.Serialize(item))!;
                prov.Id = q.ProvId = ProvisionalPrefix + "q" + q.Qid;
                prov.Version = 0;
                items.Add(prov);
            }
            queue.Add(q);
            return q;
        }

        /// <summary>a queued request reached the server: the provisional copy made offline takes the request id (it goes away when the administrator decides). Returns the id for the pending request</summary>
        internal static string RequestSent(List<TeamsItem> items, QueuedWrite q, string reqId)
        {
            var prov = q.ProvId.Length > 0 ? items.FirstOrDefault(x => x.Id == q.ProvId) : null;
            if (prov == null) return q.Id;
            prov.Id = ProvisionalPrefix + reqId;
            return prov.Id;
        }

        /// <summary>server unreachable: the change stays on this PC and will be sent by itself. null = queued</summary>
        private static string? Enqueue(string id, TeamsItem? item, string mode, out string newId)
        {
            newId = id;
            Store store;
            lock (StoreLock)
            {
                store = LoadStore();
                var cur = store.Items.FirstOrDefault(x => x.Id == id);
                var name = item?.Name ?? cur?.Name ?? "";
                if (mode == "write")
                {
                    if (id.Length == 0) id = newId = TempPrefix + Guid.NewGuid().ToString("N");
                    if (item != null) item.Id = id;
                    var old = store.Queue.Where(x => x.Mode == "write" && x.Id == id).ToList();
                    var isNew = id.StartsWith(TempPrefix);
                    store.Queue.RemoveAll(x => old.Contains(x));
                    // an entry that never reached the server and was then deleted: there is nothing to send
                    if (!(isNew && item == null)) store.Queue.Add(new QueuedWrite { Id = id, Item = item, Name = name, Mode = mode });
                    if (isNew && item == null) store.Items.RemoveAll(x => x.Id == id);
                }
                else QueueRequest(store.Items, store.Queue, id, item, name);
                ApplyQueue(store);
                SaveStore(store);
            }
            _source?.SetItems(store.Items, store.LastOk);
            ToastService.Show(SetupText.T(mode == "requests" ? "tm_queued_req" : "tm_queued"), ToastKind.Warning);
            _ = Task.Run(async () => { try { await RunAsync(); } catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Debug("Teams: sync after a queued change failed: " + ex.Message); } });
            return null;
        }

        /// <summary>sends the queued changes to the server, one at a time and in order. Server unreachable or PC disconnected: it stops (retries on the next round). Refused by the server: notice and entry restored to the server state. true if something changed locally</summary>
        private static async Task<bool> FlushQueueAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            bool changed = false;
            while (true)
            {
                QueuedWrite? q;
                lock (StoreLock) q = LoadStore().Queue.FirstOrDefault();
                if (q == null) return changed;
                TeamsItem? saved = null; string? reqId = null; string? rejected = null;
                try
                {
                    if (q.Mode == "reqedit")
                        await c.EditRequestAsync(link.Key, q.Id.Substring(ProvisionalPrefix.Length), q.Item, ct);   // already applied locally: nothing else to do
                    else if (q.Mode == "requests")
                    {
                        var kind = q.Item == null ? "delete" : q.Id.Length == 0 ? "create" : "update";
                        reqId = await c.SendRequestAsync(link.Key, kind, q.Id, q.Item?.Version, q.Item, ct);
                    }
                    else if (link.Permission != "write") rejected = "permission";   // the permission changed in the meantime
                    else saved = q.Id.StartsWith(TempPrefix) ? (await c.WriteAsync(link.Key, "", q.Item, ct, createKey: q.Id)).Item : (await c.WriteAsync(link.Key, q.Id, q.Item, ct)).Item;
                }
                catch (TeamsException ex) when (ex.Kind is "unreachable" or "certificate") { throw; }
                catch (TeamsException ex) when (ex.Kind is "revoked" or "server") { return changed; }   // the following synchronization handles the revocation; the queue stays for the next round
                catch (TeamsException ex) { rejected = ex.Kind; }

                lock (StoreLock)
                {
                    var s = LoadStore();
                    s.Queue.RemoveAll(x => x.Qid == q.Qid);
                    if (rejected != null)
                    {
                        // the other changes to the same entry no longer make sense; the full synchronization restores the entry as it is on the server
                        s.Queue.RemoveAll(x => x.Id == q.Id && q.Id.Length > 0);
                        if (q.Id.StartsWith(TempPrefix)) s.Items.RemoveAll(x => x.Id == q.Id);
                        if (q.ProvId.Length > 0) s.Items.RemoveAll(x => x.Id == q.ProvId);
                        s.Rev = 0;
                        ToastService.Show(string.Format(SetupText.T("tm_queue_rejected"), q.Name, ErrorText(rejected)), ToastKind.Warning);
                    }
                    else if (reqId != null)
                    {
                        var connId = RequestSent(s.Items, q, reqId);
                        s.Pending.Add(new PendingRequest { Id = reqId, Kind = q.Item == null ? "delete" : q.Id.Length == 0 ? "create" : "update", ConnId = connId, Name = q.Name });
                    }
                    else if (saved != null)
                    {
                        // the new entry now has its id on the server: the queued changes on it receive it too
                        s.Items.RemoveAll(x => x.Id == q.Id);
                        s.Items.Add(saved);
                        foreach (var o in s.Queue.Where(x => x.Id == q.Id)) { o.Id = saved.Id; if (o.Item != null) { o.Item.Id = saved.Id; o.Item.Version = saved.Version; } }
                    }
                    SaveStore(s);
                }
                changed = true;
            }
        }

        internal class PendingRequest
        {
            public string Id { get; set; } = "";
            public string Kind { get; set; } = "";   // update | delete | create
            public string ConnId { get; set; } = ""; // empty for a new entry
            public string Name { get; set; } = "";
        }

        /// <summary>server ids of the entries with a pending request: the list shows the clock next to the cloud</summary>
        private static HashSet<string> _pendingIds = new HashSet<string>();

        /// <summary>app id ("teams-...")</summary>
        public static bool IsPending(string appId) => appId.StartsWith("teams-") && _pendingIds.Contains(appId.Substring(6));

        private static void SaveStore(Store s)
        {
            AppDbStore.Set(DbKey, JsonSerializer.Serialize(s));
            Derive(s);
        }

        /// <summary>
        /// local ids of this PC's connections already published as Teams entries (origin = this PC): the local entry is not shown while the Teams one exists
        /// (DataSourceService.GetServers skips it). No local data is deleted: if the Teams entry disappears (deleted, disabled, PC disconnected) the local one shows up again.
        /// </summary>
        private static HashSet<string> _replaced = new HashSet<string>();
        public static bool IsReplaced(string localId) => _replaced.Contains(localId);

        /// <summary>local ids of this PC's connections sent as a proposal and not yet decided by the administrator: clock on the local entry</summary>
        private static HashSet<string> _personalPending = new HashSet<string>();
        public static bool IsPersonalPending(string localId) => _personalPending.Contains(localId);

        private static void Derive(Store s)
        {
            _pendingIds = s.Pending.Where(x => x.ConnId.Length > 0).Select(x => x.ConnId).Concat(s.Queue.Where(x => x.Id.Length > 0).Select(x => x.Id))
                .Concat(s.Queue.Where(x => x.ProvId.Length > 0).Select(x => x.ProvId)).ToHashSet();
            var pp = s.PersonalState.Where(x => x.Value == "pending").Select(x => x.Key).ToHashSet();
            if (!pp.SetEquals(_personalPending))
            {
                _personalPending = pp;
                // the local entries already shown are not reloaded: only the clock is notified
                Stylet.Execute.OnUIThread(() =>
                {
                    foreach (var vm in IoC.TryGet<GlobalData>()?.VmItemList.ToList() ?? new List<Lemora.View.ProtocolBaseViewModel>()) vm.RaisePropertyChanged(nameof(Lemora.View.ProtocolBaseViewModel.IsTeamsPending));
                });
            }
            var me = TeamsLink.Load().Id;
            // only the local entries this PC really proposed and the server published: a hostile server cannot hide any entry
            // by putting its id in origin (security hardening)
            _replaced = me.Length == 0 ? new HashSet<string>() : s.Items.Where(x => x.OriginPc == me && x.OriginLocalId.Length > 0 && !x.Deleted && !x.Disabled && IsPublishedHere(s, x.OriginLocalId)).Select(x => x.OriginLocalId).ToHashSet();
        }

        private const string DbKey = "teams.items";
        public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
        private static TeamsSource? _source;

        private static Store LoadStore()
        {
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json))
                    return JsonSerializer.Deserialize<Store>(json!) ?? new Store();
            }
            catch { /* unreadable copy: start from scratch and download again */ }
            return new Store();
        }

        private static void Publish(Store s)
        {
            Derive(s);
            var ds = IoC.TryGet<DataSourceService>();
            if (ds == null) return;
            if (_source == null)
            {
                _source = new TeamsSource();
                _source.SetItems(s.Items, s.LastOk);
                RefreshPermission(TeamsLink.Load());
                ds.AddOrUpdateDataSource(_source, connectTimeOutSeconds: 1); // reloads the list
            }
            else
            {
                _source.SetItems(s.Items, s.LastOk);
                RefreshPermission(TeamsLink.Load());
                IoC.TryGet<GlobalData>()?.ReloadAll();
            }
        }

        /// <summary>paths (with all upper levels) of the folders used by the visible entries</summary>
        private static HashSet<string> FolderPaths(IEnumerable<TeamsItem> items)
        {
            var set = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var i in items.Where(x => !x.Disabled && !x.Deleted))
            {
                var nodes = i.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                for (var n = 1; n <= nodes.Count; n++) set.Add(FolderService.ToPath(nodes.Take(n)));
            }
            return set;
        }

        /// <summary>
        /// Folders that had Teams entries before and no longer have any (moved, renamed, disabled, deleted, PC disconnected):
        /// if they contain no local entries they disappear from the folder list and from the tree (which recreated them empty even after a restart).
        /// Folders that never had Teams entries, or with local entries, are not touched. Must be called BEFORE reloading the list.
        /// </summary>
        internal static void DropEmptyTeamsFolders(IEnumerable<TeamsItem> before, IEnumerable<TeamsItem> after)
        {
            var gone = FolderPaths(before);
            gone.ExceptWith(FolderPaths(after));
            if (gone.Count == 0) return;
            var local = IoC.TryGet<GlobalData>()?.VmItemList.Where(x => x.Server.DataSource is not TeamsSource).Select(x => x.Server.TreeNodes).ToList() ?? new List<List<string>>();
            foreach (var path in gone)
            {
                if (local.Any(n => FolderService.IsIn(n, path))) continue;
                FolderService.ForgetIfBare(path);
                LocalityTreeViewService.ForgetFolder(FolderService.ToNodes(path));
            }
        }

        /// <summary>entries visible before and no longer after (disabled or deleted by the administrator): discreet notice with the names</summary>
        internal static void NotifyGone(IEnumerable<TeamsItem> before, IEnumerable<TeamsItem> after)
        {
            var names = GoneNames(before, after);
            if (names.Count > 0) ToastService.Show(string.Format(SetupText.T("tm_items_gone"), string.Join(", ", names)), ToastKind.Warning);
        }

        /// <summary>
        /// names of the vanished entries. The provisional copies ("req-") of a pending new entry do not count: once approved, the real entry arrives in its place
        /// with the server id (before it showed as "deleted by the administrator"); once refused, the refusal notice is already there
        /// </summary>
        internal static List<string> GoneNames(IEnumerable<TeamsItem> before, IEnumerable<TeamsItem> after)
        {
            var still = after.Where(x => !x.Disabled && !x.Deleted).Select(x => x.Id).ToHashSet();
            return before.Where(x => !x.Disabled && !x.Deleted && !x.Id.StartsWith(ProvisionalPrefix) && !still.Contains(x.Id)).Select(x => x.Name).ToList();
        }

        /// <summary>" · synced at HH:mm" from the last successful contact (empty if there has not been one yet)</summary>
        public static string LastSyncText => LoadStore().LastOk is var t && t > DateTime.MinValue ? " · " + t.ToString(t.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm") : "";

        /// <summary>"Connections received" row of the status window: how many visible entries and time of the last successful synchronization (before the first round: "waiting")</summary>
        public static string ReceivedText()
        {
            var s = LoadStore();
            if (s.LastOk == DateTime.MinValue) return SetupText.T("tm_ok_recv_v");
            int n = s.Items.Count(x => !x.Disabled && !x.Deleted);
            return string.Format(SetupText.T(n == 1 ? "tm_ok_recv_1" : "tm_ok_recv_n"), n, s.LastOk.ToString(s.LastOk.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm"));
        }

        /// <summary>"Share my connections" toggle (status window and Options): saves, when turned off warns that published entries stay, and syncs right away</summary>
        public static async Task SetSharePersonalAsync(bool on)
        {
            var l = TeamsLink.Load();
            if (l.SharePersonal == on) return;
            l.SharePersonal = on;
            l.Save();
            ShareChanged?.Invoke(); // the main window status bar updates right away, without waiting for the sync (bug list #113)
            if (!on) ToastService.Show(SetupText.T("tm_share_kept"));
            try { await RunAsync(); } catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: sync after the toggle failed: " + ex.Message); }
            ShareChanged?.Invoke(); // and again after the sync (time, "nocopy" mode)
        }

        /// <summary>"Share my connections" toggle changed: the main window redraws the Teams bar</summary>
        public static event Action? ShareChanged;

        /// <summary>the Teams origin is writable with the "write" permission; with "requests" Edit and Delete remain, but saving becomes a request to the administrator</summary>
        public static void RefreshPermission(TeamsLink link) => _source?.SetWritable(link.State == "approved" && link.Permission is "write" or "requests");

        /// <summary>at startup: shows the saved copy right away (even without network, even empty: with write permission it is needed to add the first entry)</summary>
        public static void ShowSaved()
        {
            if (TeamsLink.Load().State != "approved") return;
            Publish(LoadStore());
        }

        /// <summary>Delete confirmation: with the "requests" permission a Teams entry is not deleted, the administrator is asked</summary>
        public static string DeleteConfirmText(IEnumerable<Lemora.Model.Protocol.Base.ProtocolBase> servers, string defaultKey) =>
            servers.Any(s => s.DataSource is TeamsSource && !s.Id.StartsWith("teams-" + ProvisionalPrefix)) && TeamsLink.Load().Permission == "requests" ? SetupText.T("tm_req_delete_confirm") : IoC.Translate(defaultKey);

        /// <summary>user-facing text for a write error</summary>
        public static string ErrorText(string kind) => SetupText.T(kind switch
        {
            "permission" => "tm_wr_permission",
            "nocopy" => "tm_wr_nocopy",
            "conflict" => "tm_wr_conflict",
            "input" => "tm_wr_input",
            "notfound" => "tm_wr_notfound",
            "revoked" => "tm_wr_revoked",
            "unreachable" or "certificate" or "address" => "tm_wr_unreachable",
            "protocol" => "tm_wr_protocol",
            "oldserver" => "tm_wr_oldserver",
            "state" => "tm_wr_decided",
            _ => "tm_err_server",
        });

        /// <summary>
        /// the new entry waiting for the administrator was edited (item) or deleted (null) on this PC: the provisional copy changes right away,
        /// and the pending request follows it (same name in the notices; deleted = no longer pending)
        /// </summary>
        internal static void ApplyPendingEdit(List<TeamsItem> items, List<PendingRequest> pending, string provId, TeamsItem? item)
        {
            items.RemoveAll(x => x.Id == provId);
            if (item == null) { pending.RemoveAll(p => p.ConnId == provId); return; }
            var copy = JsonSerializer.Deserialize<TeamsItem>(JsonSerializer.Serialize(item))!;
            copy.Id = provId;
            copy.Version = 0;
            items.Add(copy);
            foreach (var p in pending.Where(p => p.ConnId == provId)) p.Name = item.Name;
        }

        /// <summary>
        /// Edit or deletion of a new entry still waiting for the administrator (before: refused until the decision).
        /// Never sent (made offline): it changes in the queue. Already sent: the request on the server is updated or withdrawn;
        /// server unreachable: the change waits in the queue and leaves at the next synchronization.
        /// </summary>
        private static string? EditPending(TeamsLink link, string provId, TeamsItem? item)
        {
            lock (StoreLock)
            {
                var s = LoadStore();
                var queued = s.Queue.FirstOrDefault(x => x.ProvId == provId);
                if (queued != null)
                {
                    if (item == null) s.Queue.Remove(queued);
                    else { queued.Item = item; queued.Name = item.Name; }
                    ApplyPendingEdit(s.Items, s.Pending, provId, item);
                    SaveStore(s);
                    _source?.SetItems(s.Items, s.LastOk);
                    return null;
                }
            }
            var reqId = provId.Substring(ProvisionalPrefix.Length);
            try
            {
                if (QuickOffline(link)) throw new TeamsException("unreachable", "The server does not respond.");
                Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    await c.EditRequestAsync(link.Key, reqId, item);
                }).GetAwaiter().GetResult();
                Store st;
                lock (StoreLock)
                {
                    st = LoadStore();
                    ApplyPendingEdit(st.Items, st.Pending, provId, item);
                    SaveStore(st);
                }
                _source?.SetItems(st.Items, st.LastOk);
                ToastService.Show(SetupText.T(item == null ? "tm_req_withdrawn" : "tm_req_edited"));
                return null;
            }
            catch (TeamsException ex) when (ex.Kind is "unreachable" or "server")
            {
                Store st;
                lock (StoreLock)
                {
                    st = LoadStore();
                    st.Queue.RemoveAll(x => x.Mode == "reqedit" && x.Id == provId);   // only the latest edit (or the withdrawal) is sent
                    st.Queue.Add(new QueuedWrite { Id = provId, Item = item, Name = item?.Name ?? st.Items.FirstOrDefault(x => x.Id == provId)?.Name ?? "", Mode = "reqedit" });
                    ApplyPendingEdit(st.Items, st.Pending, provId, item);
                    SaveStore(st);
                }
                _source?.SetItems(st.Items, st.LastOk);
                ToastService.Show(SetupText.T("tm_queued_req"), ToastKind.Warning);
                _ = Task.Run(async () => { try { await RunAsync(); } catch (Exception e2) { Shawn.Utils.SimpleLogHelper.Debug("Teams: sync after a queued change failed: " + e2.Message); } });
                return null;
            }
            catch (TeamsException ex)
            {
                return ErrorText(ex.Kind);
            }
        }

        /// <summary>
        /// Writes an entry to the server (empty id = new; null item = delete) and updates the local copy right away.
        /// Blocks until the server answers (at most 15 seconds): it is called by the editor's save, which waits for the outcome.
        /// Returns null if it went well, otherwise the error text; newId = server id of the created entry.
        /// </summary>
        public static string? Write(string id, TeamsItem? item, out string newId)
        {
            newId = id;
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return ErrorText("revoked");
            if (id.StartsWith(ProvisionalPrefix)) return EditPending(link, id, item);   // new entry still pending: edited or deleted, the request follows
            if (link.Permission == "requests") return SendRequest(link, id, item);
            if (id.StartsWith(TempPrefix)) return Enqueue(id, item, "write", out newId);   // entry created offline, not yet on the server: work only on the queue
            if (link.Permission != "write") return ErrorText("permission");
            if (link.NoCopy && id.Length == 0 && item != null) return ErrorText("nocopy");
            // new entry: provisional id chosen now, sent as creation key and kept if it ends up in the queue, so a retry does not duplicate it
            var createKey = id.Length == 0 && item != null ? TempPrefix + Guid.NewGuid().ToString("N") : null;
            try
            {
                if (QuickOffline(link)) throw new TeamsException("unreachable", "The server does not respond.");
                var res = Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    return await c.WriteAsync(link.Key, id, item, createKey: createKey);
                }).GetAwaiter().GetResult();
                var store = LoadStore();
                var before = store.Items;
                var byId = store.Items.ToDictionary(x => x.Id);
                if (item == null) byId.Remove(id);
                else if (res.Item != null) { byId[res.Item.Id] = res.Item; newId = res.Item.Id; }
                store.Items = byId.Values.ToList();
                SaveStore(store);
                DropEmptyTeamsFolders(before, store.Items);
                _source?.SetItems(store.Items, store.LastOk);
                return null;
            }
            catch (TeamsException ex) when (ex.Kind is "unreachable" or "server")   // "server" = unexpected reply (e.g. error 500): for the user it is the same, the server is not working
            {
                return Enqueue(createKey ?? id, item, "write", out newId);
            }
            catch (TeamsException ex)
            {
                return ErrorText(ex.Kind);
            }
        }

        /// <summary>
        /// "Requests" permission: instead of writing, sends the request to the administrator. The local copy does not change (the entry stays as it is with the
        /// "pending" clock); when the administrator approves, the new entry arrives from the server with the sync.
        /// </summary>
        private static string? SendRequest(TeamsLink link, string id, TeamsItem? item)
        {
            var kind = item == null ? "delete" : id.Length == 0 ? "create" : "update";
            var store = LoadStore();
            var cur = store.Items.FirstOrDefault(x => x.Id == id);
            try
            {
                if (QuickOffline(link)) throw new TeamsException("unreachable", "The server does not respond.");
                var reqId = Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    return await c.SendRequestAsync(link.Key, kind, id, cur?.Version, item);
                }).GetAwaiter().GetResult();
                var connId = id;
                if (kind == "create" && item != null)
                {
                    // the new entry shows right away where and how it was saved (with the "pending" clock); if the administrator approves the real one arrives, if refused it disappears
                    var prov = JsonSerializer.Deserialize<TeamsItem>(JsonSerializer.Serialize(item))!;
                    prov.Id = connId = ProvisionalPrefix + reqId;
                    prov.Version = 0;
                    store.Items.Add(prov);
                }
                store.Pending.Add(new PendingRequest { Id = reqId, Kind = kind, ConnId = connId, Name = item?.Name ?? cur?.Name ?? "" });
                SaveStore(store);
                _source?.SetItems(store.Items, store.LastOk); // the list is reread: the entry returns as it was, with the clock
                ToastService.Show(SetupText.T(kind switch { "delete" => "tm_req_sent_delete", "create" => "tm_req_sent_create", _ => "tm_req_sent" }));
                return null;
            }
            catch (TeamsException ex) when (ex.Kind is "unreachable" or "server")
            {
                return Enqueue(id, item, "requests", out _);
            }
            catch (TeamsException ex)
            {
                return ErrorText(ex.Kind);
            }
        }

        /// <summary>
        /// "Requests" permission: folder credentials and shared devices are not written, they are requested from the administrator (create, edit, delete).
        /// The local copy does not change: once approved it arrives with the sync, if refused the notice with the note appears. null = sent, otherwise the error text.
        /// </summary>
        public static string? RequestShared(string kind, string? id, string folder, string? name, string? ip, string? user, string? password, List<DeviceUser>? users, string label, List<string>? moreIps = null)
        {
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return ErrorText("revoked");
            try
            {
                if (QuickOffline(link)) throw new TeamsException("unreachable", "The server does not respond.");
                var reqId = Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    return await c.SendSharedRequestAsync(link.Key, kind, id, TeamsShared.ToServerPath(folder), name, ip, user, password, users, default, moreIps);
                }).GetAwaiter().GetResult();
                var store = LoadStore();
                store.Pending.Add(new PendingRequest { Id = reqId, Kind = kind, ConnId = "", Name = label });
                SaveStore(store);
                ToastService.Show(SetupText.T(kind == "folder-rename" ? "tm_req_sent_folder" : kind.EndsWith("delete") ? "tm_req_sent_delete" : kind.EndsWith("create") ? "tm_req_sent_shared_new" : "tm_req_sent"));
                return null;
            }
            catch (TeamsException ex) when (ex.Kind is "unreachable" or "server")
            {
                // server unreachable: the request waits on this PC and leaves at the next synchronization (before: "not saved" and lost)
                lock (StoreLock)
                {
                    var s = LoadStore();
                    s.SharedQueue.Add(new QueuedShared { Kind = kind, Id = id, Folder = folder, Name = name, Ip = ip, MoreIps = moreIps, User = user, Password = password, Users = users, Label = label });
                    SaveStore(s);
                }
                ToastService.Show(SetupText.T("tm_queued_req"), ToastKind.Warning);
                _ = Task.Run(async () => { try { await RunAsync(); } catch (Exception e2) { Shawn.Utils.SimpleLogHelper.Debug("Teams: sync after a queued request failed: " + e2.Message); } });
                return null;
            }
            catch (TeamsException ex) { return ErrorText(ex.Kind); }
        }

        /// <summary>
        /// the server surely does not answer: the last sync said so, or a quick TCP knock (1.5 s) fails. The saves called from the windows
        /// then queue at once: before they waited for the HTTP timeout (8-15 s) with the app frozen and the notice late
        /// </summary>
        internal static bool QuickOffline(TeamsLink link)
        {
            if (link.LastError == "unreachable") { _ = Task.Run(async () => { try { await RunAsync(); } catch { /* next round */ } }); return true; }   // retried in the background: if it is back, the queue leaves
            var i = link.Server.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(link.Server[(i + 1)..], out var port)) return false;
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                return !tcp.ConnectAsync(link.Server[..i].Trim('[', ']'), port).Wait(1500) || !tcp.Connected;
            }
            catch { return true; }
        }

        /// <summary>for the tests: requests on credentials/devices waiting to be sent</summary>
        internal static int SharedQueueCount => LoadStore().SharedQueue.Count;

        /// <summary>request on a folder credential or a device made while the server was not answering (fields as in <see cref="RequestShared"/>)</summary>
        internal class QueuedShared
        {
            public string Kind { get; set; } = "";
            public string? Id { get; set; }
            public string Folder { get; set; } = "";
            public string? Name { get; set; }
            public string? Ip { get; set; }
            public List<string>? MoreIps { get; set; }
            public string? User { get; set; }
            public string? Password { get; set; }
            public List<DeviceUser>? Users { get; set; }
            public string Label { get; set; } = "";
        }

        private const string AutoReqKey = "teams.credsautoreq";

        /// <summary>
        /// "Requests" permission: folder credentials that exist only on this PC (the server does not know them) and are not already requested go into the
        /// request queue, once per link. Case seen on a real folder: the folder domain changed while the creation requests were waiting, the credentials were renamed
        /// only here ("NEWDOMAIN\user"), the server got "OLDDOMAIN\user"; these were then deleted as old ones and the server was left with none.
        /// Rejected by the administrator = not requested again (it can be requested again by editing it).
        /// </summary>
        internal static int QueueLocalOnlyCreds(TeamsLink link)
        {
            if (link.Permission != "requests") return 0;
            var known = TeamsShared.KnownCreds();
            int n = 0;
            lock (StoreLock)
            {
                var s = LoadStore();
                var done = AppDbStore.TryGet(AutoReqKey, out var j) && !string.IsNullOrEmpty(j) ? JsonSerializer.Deserialize<HashSet<string>>(j!) ?? new() : new HashSet<string>();
                foreach (var cr in Lemora.Service.FolderCredentialService.Snapshot())
                {
                    var folder = TeamsShared.FromServerPath(TeamsShared.ToServerPath(cr.FolderPath));
                    if (folder.Length == 0 || known.Contains((folder.ToLowerInvariant(), cr.UserName.ToLowerInvariant()))) continue;
                    var label = Lemora.Service.FolderCredentialService.MakeName(cr.FolderPath, cr.UserName);
                    var key = (link.Id + "|" + folder + "|" + cr.UserName).ToLowerInvariant();
                    if (done.Contains(key)) continue;
                    // already asked (by Add or by a previous round) and still open: no duplicate
                    if (s.Pending.Any(p => p.Kind == "fcred-create" && p.Name == label) || s.SharedQueue.Any(q => q.Kind == "fcred-create" && q.Label == label)) { done.Add(key); continue; }
                    var pw = Lemora.Service.FolderCredentialService.GetDecryptedById(cr.DatabaseId)?.Password ?? "";
                    s.SharedQueue.Add(new QueuedShared { Kind = "fcred-create", Folder = cr.FolderPath, User = cr.UserName, Password = pw, Label = label });
                    done.Add(key);
                    n++;
                }
                if (n > 0) SaveStore(s);
                AppDbStore.Set(AutoReqKey, JsonSerializer.Serialize(done));
            }
            if (n > 0) Shawn.Utils.SimpleLogHelper.Warning($"Teams: {n} folder credentials only on this PC sent as requests to the administrator");
            return n;
        }

        /// <summary>sends the queued credential/device requests, in order. Unreachable: it stops (next round). Refused: notice and dropped. true if any left the queue</summary>
        private static async Task<bool> FlushSharedAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            bool changed = false;
            while (true)
            {
                QueuedShared? q;
                lock (StoreLock) q = LoadStore().SharedQueue.FirstOrDefault();
                if (q == null) return changed;
                string? reqId = null, rejected = null;
                try { reqId = await c.SendSharedRequestAsync(link.Key, q.Kind, q.Id, TeamsShared.ToServerPath(q.Folder), q.Name, q.Ip, q.User, q.Password, q.Users, ct, q.MoreIps); }
                catch (TeamsException ex) when (ex.Kind is "unreachable" or "certificate") { throw; }
                catch (TeamsException ex) when (ex.Kind is "revoked" or "server") { return changed; }
                catch (TeamsException ex) { rejected = ex.Kind; }
                lock (StoreLock)
                {
                    var s = LoadStore();
                    if (s.SharedQueue.Count > 0) s.SharedQueue.RemoveAt(0);
                    if (reqId != null) s.Pending.Add(new PendingRequest { Id = reqId, Kind = q.Kind, ConnId = "", Name = q.Label });
                    SaveStore(s);
                }
                if (rejected != null) ToastService.Show(string.Format(SetupText.T("tm_queue_rejected"), q.Label, ErrorText(rejected)), ToastKind.Warning);
                changed = true;
            }
        }

        /// <summary>outcome of the pending requests: approved (the entry already arrives with the sync) or refused (notice with the note). true if any was closed</summary>
        internal static bool ApplyRequestOutcomes(List<PendingRequest> pending, List<TeamsServerClient.ReqStatus> server, List<TeamsItem>? items = null)
        {
            var byId = server.ToDictionary(x => x.Id);
            var closed = pending.Where(p => byId.TryGetValue(p.Id, out var s) ? s.State is "approved" or "rejected" or "withdrawn" : server.Count > 0).ToList(); // missing from the list = decided more than 30 days ago
            foreach (var p in closed)
            {
                pending.Remove(p);
                // new entry shown right away while waiting for the administrator: once decided, the provisional copy goes away (approved: the real one arrives from the server; refused: it disappears)
                if (p.Kind == "create" && p.ConnId.StartsWith(ProvisionalPrefix)) items?.RemoveAll(x => x.Id == p.ConnId);
                if (byId.TryGetValue(p.Id, out var s) && s.State == "rejected")
                    ToastService.Show(string.Format(SetupText.T(s.Note.Length > 0 ? "tm_req_rejected_note" : "tm_req_rejected"), p.Name, s.Note), ToastKind.Warning);
            }
            return closed.Count > 0;
        }

        // ---- this PC's personal connections (rule in Installer/TEAMS-DESIGN.md section 3) ----

        /// <summary>are they sent? read only always, requests/write only with the toggle on; never with "do not copy"</summary>
        internal static bool SendsPersonal(TeamsLink link) => link.State == "approved" && !link.NoCopy && (link.Permission == "read" || link.SharePersonal);

        /// <summary>line for the Teams bar (" · shares the saved connections...") with requests/write when this PC is sending them to the server; empty otherwise</summary>
        public static string SharingText(TeamsLink link) =>
            !SendsPersonal(link) || link.Permission == "read" || LoadStore().PersonalMode == "nocopy" ? "" : " · " + SetupText.T(link.Permission == "requests" ? "tm_share_bar_req" : "tm_share_bar"); // read only: nothing in the bar, only the line in Options. Requests: nothing is published until the admin approves (bug list #116)

        /// <summary>RDP, SSH and VNC connections of the local origin (not Teams, no other protocols) with the plain-text password, ready to send</summary>
        internal static List<TeamsServerClient.PersonalItem> LocalItems()
        {
            var list = new List<TeamsServerClient.PersonalItem>();
            var src = IoC.TryGet<DataSourceService>()?.LocalDataSource;
            if (src == null) return list;
            foreach (var vm in src.CachedProtocols.ToList()) // the copy already read: rereading here would make the list lose its "needs reload" signal
            {
                var t = vm.Server.GetType();
                if (t != typeof(Lemora.Model.Protocol.RDP) && t != typeof(Lemora.Model.Protocol.SSH) && t != typeof(Lemora.Model.Protocol.VNC)) continue;
                if (vm.Server.DoNotSync) continue;   // "do not sync": never to the server (if it was already sent it drops out of the list and the server removes its copy)
                var clone = (Lemora.Model.Protocol.Base.ProtocolBase)vm.Server.Clone();
                clone.DecryptToConnectLevel();
                if (clone is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd u || !int.TryParse(u.Port, out var port) || string.IsNullOrWhiteSpace(u.Address)) continue;
                bool vnc = clone is Lemora.Model.Protocol.VNC;
                list.Add(new TeamsServerClient.PersonalItem(clone.Id, string.Join("/", clone.TreeNodes), clone.DisplayName, clone is Lemora.Model.Protocol.SSH ? "ssh" : vnc ? "vnc" : "rdp",
                    u.Address.Trim(), port, vnc ? "" : u.UserName ?? "", u.Password ?? "", string.Join(",", clone.Tags)));
            }
            return list;
        }

        internal static string Fingerprint(TeamsServerClient.PersonalItem i) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(i))));

        /// <summary>
        /// What to send: new or changed entries (fingerprint different from the last accepted send) and the sent ids that are no longer on the PC.
        /// If sharing was off (or the server replied "nocopy"/"off") the fingerprints are empty: everything is sent again.
        /// </summary>
        internal static (List<TeamsServerClient.PersonalItem> changed, List<string> removed) PersonalDiff(List<TeamsServerClient.PersonalItem> local, Dictionary<string, string> sent)
        {
            var changed = local.Where(i => !sent.TryGetValue(i.LocalId, out var h) || h != Fingerprint(i)).ToList();
            var ids = local.Select(i => i.LocalId).ToHashSet();
            return (changed, sent.Keys.Where(k => !ids.Contains(k)).ToList());
        }

        /// <summary>sends the personal connections (before downloading the entries: with write the published ones arrive in the same round). Never blocking for the sync</summary>
        private static async Task SendPersonalAsync(TeamsServerClient c, TeamsLink link, Store store, List<TeamsServerClient.PersonalItem> local, CancellationToken ct)
        {
            try
            {
                if (!SendsPersonal(link))
                {
                    // toggle off (requests/write): the server is told only once to delete the unpublished copies
                    if (!link.NoCopy && store.PersonalMode is "ok" or "") { var (st, _) = await c.PersonalAsync(link.Key, false, new(), new(), ct); store.PersonalMode = st; }
                    store.PersonalSent.Clear();
                    return;
                }
                if (store.PersonalMode != "ok") store.PersonalSent.Clear();
                var (changed, removed) = PersonalDiff(local, store.PersonalSent);
                if (changed.Count == 0 && removed.Count == 0 && store.PersonalMode == "ok") return;
                var (state, items) = await c.PersonalAsync(link.Key, true, changed, removed, ct);
                store.PersonalMode = state;
                if (state != "ok") { store.PersonalSent.Clear(); return; }
                foreach (var i in changed.Where(i => items.ContainsKey(i.LocalId))) store.PersonalSent[i.LocalId] = Fingerprint(i);
                foreach (var id in removed) { store.PersonalSent.Remove(id); store.PersonalState.Remove(id); }
            }
            catch (TeamsException ex)
            {
                Shawn.Utils.SimpleLogHelper.Warning("Teams: sending personal connections failed: " + ex.Message); // old server (404) or network: retried at the next round
            }
        }

        /// <summary>sends the duration of connections to Teams entries (TeamsSessionLog): starts and ends changed since the last round. Never blocking, never the password</summary>
        private static async Task SendSessionsAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            try
            {
                var changed = TeamsSessionLog.Changed();
                if (changed.Count == 0) return;
                if (await c.SessionsAsync(link.Key, changed, ct)) TeamsSessionLog.MarkSent(changed);
            }
            catch (TeamsException ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: sending connection durations failed: " + ex.Message); } // old server (404) or network: retried
        }

        /// <summary>
        /// local entry that this PC sent to the server (PersonalSent) and that is published: only for these does the origin of a shared entry count
        /// (hide the local one, remove it when the shared one is deleted). The server alone is not enough: it knows the ids of all local entries
        /// </summary>
        private static bool IsPublishedHere(Store s, string localId) =>
            s.PersonalSent.ContainsKey(localId) && s.PersonalState.TryGetValue(localId, out var st) && st == "published";

        /// <summary>discreet notice when the administrator decides on a personal connection (from "pending" to published or refused). true if something changed</summary>
        internal static bool ApplyPersonalOutcomes(Dictionary<string, string> known, List<TeamsServerClient.PersonalStatus> server, IReadOnlyDictionary<string, string> names)
        {
            bool any = false;
            foreach (var s in server)
            {
                known.TryGetValue(s.LocalId, out var was);
                if (was == s.State) continue;
                any = true;
                known[s.LocalId] = s.State;
                if (was != "pending") continue; // first time seen (or published right away with write): no notice
                var name = names.TryGetValue(s.LocalId, out var n) ? n : s.LocalId;
                if (s.State == "published") ToastService.Show(string.Format(SetupText.T("tm_pers_approved"), name));
                else if (s.State == "rejected") ToastService.Show(string.Format(SetupText.T(s.Note.Length > 0 ? "tm_pers_rejected_note" : "tm_pers_rejected"), name, s.Note), ToastKind.Warning);
            }
            return any;
        }

        /// <summary>unlinking: deletes the copy and removes the data source</summary>
        public static void Forget()
        {
            DropEmptyTeamsFolders(LoadStore().Items, new List<TeamsItem>());   // folders holding only Teams entries go away with them
            DevicePasswordService.RemoveServerCopies();   // device passwords that came from the server go away with the link
            TeamsShared.RemoveServerCreds();              // and so do folder credentials
            TeamsBrowserLog.Clear();                      // and the extension log not yet sent
            AppDbStore.Set(DbKey, null);
            _pendingIds = new HashSet<string>();
            Derive(new Store()); // no more clocks on local entries
            _replaced = new HashSet<string>(); // replaced local entries show up again
            if (_source == null) return;
            IoC.TryGet<DataSourceService>()?.RemoveDataSource(_source.DataSourceName);
            _source = null;
        }

        private static readonly SemaphoreSlim RunGate = new SemaphoreSlim(1, 1);

        /// <summary>one sync round; true if the entries changed. Without network the saved copy stays. One round at a time: extra ones wait (so the folder queue always goes out)</summary>
        public static async Task<bool> RunAsync(CancellationToken ct = default)
        {
            await RunGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await RunCoreAsync(ct).ConfigureAwait(false); }
            finally { RunGate.Release(); }
        }

        private static async Task<bool> RunCoreAsync(CancellationToken ct)
        {
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0)
            {
                if (link.State is "revoked" or "rejected" or "none" && (_source != null || LoadStore().Items.Count > 0)) Forget();
                return false;
            }
            var store = LoadStore();
            var before = store.Items;
            try
            {
                using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                var local = LocalItems();
                await SendPersonalAsync(c, link, store, local, ct);
                await SendSessionsAsync(c, link, ct);
                try { await TeamsBrowserLog.FlushAsync(c, link, ct); }   // extension log left in the queue: never blocking
                catch (TeamsException ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: sending the extension log failed: " + ex.Message); }
                try { await TeamsShared.FlushFoldersAsync(c, link, ct); await TeamsShared.UploadLocalAsync(c, link, ct); }   // queued folders and device passwords made before linking: never blocking for the sync
                catch (TeamsException ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: sending folders and device passwords failed: " + ex.Message); }
                bool flushed = await FlushQueueAsync(c, link, ct);   // first the changes made offline, then download: the server already has them when it answers
                QueueLocalOnlyCreds(link);
                if (await FlushSharedAsync(c, link, ct)) flushed = true;
                if (flushed) { store = LoadStore(); before = store.Items; }
                var (revoked, rev, items) = await c.SyncAsync(link.Key, store.Rev, ct);
                if (revoked)
                {
                    link.State = "revoked"; link.Key = ""; link.Save();
                    Forget();
                    return true;
                }
                if (rev < store.Rev) // the server restarted from scratch: download everything again
                {
                    store = new Store { Pending = store.Pending, PersonalSent = store.PersonalSent, PersonalState = store.PersonalState, PersonalMode = store.PersonalMode, Queue = store.Queue, SharedQueue = store.SharedQueue };
                    DevicePasswordService.MarkAllLocal();   // device passwords must be uploaded again: the server no longer has them
                    TeamsShared.ResetUploaded();            // and the same goes for folders
                    (_, rev, items) = await c.SyncAsync(link.Key, 0, ct);
                }
                TeamsShared.Apply(c.LastShared);   // device passwords, folders and folder credentials (before recording the revision: if they are not applied it is retried)
                var byId = store.Items.ToDictionary(x => x.Id);
                foreach (var i in items)
                {
                    if (i.Deleted) byId.Remove(i.Id);
                    else if (!byId.TryGetValue(i.Id, out var old) || old.Version <= i.Version) byId[i.Id] = i;
                }
                bool changed = items.Count > 0 || flushed;
                store.Items = byId.Values.ToList();
                store.Rev = rev;
                store.LastOk = DateTime.Now;
                // approved deletion of an entry born from a saved connection of THIS PC: it disappears from the server and from everyone, so the local original
                // (hidden while the shared copy existed) must be removed too, otherwise it reappears as a "not synced" entry
                var approvedDeletes = store.Pending.Where(p => p.Kind == "delete" && c.LastRequests.Any(s => s.Id == p.Id && s.State == "approved")).Select(p => p.ConnId).ToHashSet();
                var originals = before.Where(x => approvedDeletes.Contains(x.Id) && x.OriginPc == link.Id && x.OriginLocalId.Length > 0 && IsPublishedHere(store, x.OriginLocalId)).Select(x => x.OriginLocalId).ToList();
                if (originals.Count > 0)
                {
                    var del = IoC.TryGet<DataSourceService>()?.LocalDataSource?.Database_DeleteServer(originals);
                    Shawn.Utils.SimpleLogHelper.Info($"Teams: deletion approved, also removed {originals.Count} saved connections of this PC (result {(del?.IsSuccess == true ? "ok" : "failed")})");
                }
                var itemsAfter = store.Items.ToList();
                bool reqClosed = ApplyRequestOutcomes(store.Pending, c.LastRequests, itemsAfter); // after taking the entries: an approval removes the clock when the new entry is already there
                if (itemsAfter.Count != store.Items.Count) store.Items = itemsAfter;
                ApplyPersonalOutcomes(store.PersonalState, c.LastPersonal, local.GroupBy(i => i.LocalId).ToDictionary(g => g.Key, g => g.First().Name));
                // the server lists all of this PC's proposals: those no longer there (toggle off, entry removed) are no longer pending
                var listed = c.LastPersonal.Select(p => p.LocalId).ToHashSet();
                foreach (var gone in store.PersonalState.Keys.Where(k => !listed.Contains(k)).ToList()) store.PersonalState.Remove(gone);
                lock (StoreLock)
                {
                    var latest = LoadStore();
                    store.Queue = latest.Queue;   // changes queued during the sync: they must not be lost or erased by the downloaded copy
                    store.SharedQueue = latest.SharedQueue;
                    ApplyQueue(store);
                    SaveStore(store);
                }
                if (link.LastError.Length > 0) { link.LastError = ""; link.Save(); }
                if (c.LastPermission.Length > 0 && c.LastPermission != link.Permission) { link.Permission = c.LastPermission; link.Save(); }
                if (c.LastClaude != null && JsonSerializer.Serialize(c.LastClaude) != JsonSerializer.Serialize(link.Claude)) { link.Claude = c.LastClaude; link.Save(); Lemora.Service.Mcp.McpServer.ApplyPolicy(); }
                if (reqClosed && !changed && _source != null) { _source.SetItems(store.Items, store.LastOk); IoC.TryGet<GlobalData>()?.ReloadAll(); } // only the clock to remove
                if (changed)
                {
                    DropEmptyTeamsFolders(before, store.Items);   // before reloading: the tree must not recreate them
                    NotifyGone(before, store.Items);
                }
                if (changed || _source == null) Publish(store);
                else RefreshPermission(link);
                Shawn.Utils.SimpleLogHelper.Info($"Teams: sync succeeded (rev {rev}, {items.Count} entries changed, {store.Items.Count} shared, permission {link.Permission})");
                return changed;
            }
            catch (TeamsException ex)
            {
                // first error of a series: visible at Base level too; the following identical ones only at Medium (without network it would repeat every round)
                if (link.LastError != ex.Kind) { Shawn.Utils.SimpleLogHelper.Warning($"Teams: sync failed ({ex.Kind}): {ex.Message}"); link.LastError = ex.Kind; link.Save(); }
                else Shawn.Utils.SimpleLogHelper.Debug($"Teams: sync failed again ({ex.Kind}): {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>periodic link check: pending request -> approved, refused; linked PC -> permission updated or unlinked</summary>
    internal static class TeamsLinkService
    {
        /// <summary>applies the server outcome to the link; true if something changed (and must be saved)</summary>
        public static bool Apply(TeamsLink link, TeamsServerClient.PairStatus s)
        {
            string before = $"{link.State}|{link.Permission}|{link.NoCopy}|{link.Key}|{JsonSerializer.Serialize(link.Claude)}";
            switch (link.State)
            {
                case "pending":
                    if (s.State == "approved" && s.Key != null) { link.State = "approved"; link.Key = s.Key; link.Permission = s.Permission ?? "read"; link.NoCopy = s.NoCopy; link.Claude = s.Claude; } // read | requests | write
                    else if (s.State == "approved") link.State = "revoked"; // approved but the key was already delivered and this PC does not have it: a new pairing is needed
                    else if (s.State is "rejected" or "revoked") link.State = s.State;
                    break;
                case "approved":
                    if (s.State == "revoked") { link.State = "revoked"; link.Key = ""; }
                    else if (s.State == "approved") { link.Permission = s.Permission ?? link.Permission; link.NoCopy = s.NoCopy; if (s.Claude != null) link.Claude = s.Claude; }
                    break;
            }
            return before != $"{link.State}|{link.Permission}|{link.NoCopy}|{link.Key}|{JsonSerializer.Serialize(link.Claude)}";
        }

        /// <summary>cancels the pending request on the server; if the server does not answer the request stays in the console (the administrator can refuse it)</summary>
        public static async Task CancelAsync(TeamsLink link)
        {
            if (link.State != "pending" || link.Id.Length == 0 || link.Token.Length == 0 || link.Server.Length == 0) return;
            try
            {
                using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                await c.CancelAsync(link.Id, link.Token);
            }
            catch (TeamsException) { /* without network there is nothing else to do */ }
        }

        /// <summary>one check round; true if the state changed. Without network nothing changes (the last known state stays)</summary>
        public static async Task<bool> PollAsync(CancellationToken ct = default)
        {
            var link = TeamsLink.Load();
            if (link.State is not ("pending" or "approved") || link.Server.Length == 0) return false;
            try
            {
                using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                var s = link.State == "pending" ? await c.StatusAsync(link.Id, link.Token, false, ct) : await c.MeAsync(link.Key, ct);
                bool wasPending = link.State == "pending";
                var stateBefore = $"{link.State}/{link.Permission}";
                bool changed = Apply(link, s);
                if ($"{link.State}/{link.Permission}" != stateBefore)
                    Lemora.Utils.ActivityLog.Write($"Teams: link to server {link.Server} from {stateBefore} to {link.State}/{link.Permission}");
                if (link.LastError.Length > 0) { link.LastError = ""; changed = true; }
                if (changed) { link.Save(); TeamsSync.RefreshPermission(link); Lemora.Service.Mcp.McpServer.ApplyPolicy(); } // the key is saved BEFORE confirming: if something stops here the server sends it again
                if (wasPending && link.State == "approved")
                {
                    try { await c.StatusAsync(link.Id, link.Token, true, ct); } catch (TeamsException) { /* next time: not serious, the key is already here */ }
                }
                return changed;
            }
            catch (TeamsException ex) when (ex.Kind == "badkey" && link.State == "approved" && link.Id.Length > 0 && link.Token.Length > 0)
            {
                // the server invalidated the key (it had stayed in plain text in its database): a new one is fetched with the
                // pairing secret, which never left this PC. The server hands it over only if the key is not confirmed
                try
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    var s = await c.StatusAsync(link.Id, link.Token, false, ct);
                    if (s.State != "approved" || s.Key == null) return false;
                    link.Key = s.Key; link.LastError = ""; link.Save();
                    Lemora.Utils.ActivityLog.Write($"Teams: this PC's key renewed by server {link.Server}");
                    try { await c.StatusAsync(link.Id, link.Token, true, ct); } catch (TeamsException) { /* the first use does the confirmation */ }
                    return true;
                }
                catch (TeamsException e2) { Shawn.Utils.SimpleLogHelper.Warning($"Teams: key renewal failed ({e2.Kind})"); return false; }
            }
            catch (TeamsException ex)
            {
                if (link.LastError != ex.Kind) { Shawn.Utils.SimpleLogHelper.Warning($"Teams: link check failed ({ex.Kind}): {ex.Message}"); link.LastError = ex.Kind; link.Save(); }
                else Shawn.Utils.SimpleLogHelper.Debug($"Teams: link check failed again ({ex.Kind})");
                return false;
            }
        }
    }
}
