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
    /// <summary>stato del collegamento di questo PC a un server Teams, salvato nel database cifrato (AppDbStore)</summary>
    internal class TeamsLink
    {
        public string State { get; set; } = "none"; // none | pending | approved | rejected | revoked
        public string Server { get; set; } = "";
        public DateTime SentAt { get; set; } = DateTime.Now;
        public bool Skipped { get; set; }

        // risposta del server all'abbinamento
        public string Id { get; set; } = "";
        public string Token { get; set; } = "";        // segreto per chiedere l'esito della richiesta
        public string Fingerprint { get; set; } = "";  // impronta del certificato vista al primo collegamento: da qui in poi il server deve presentare lo stesso
        public string Key { get; set; } = "";          // chiave di questo PC, rilasciata dopo l'approvazione
        public string Permission { get; set; } = "read";
        public bool NoCopy { get; set; }
        public string LastError { get; set; } = "";
        /// <summary>interruttore "Condividi le mie connessioni con il server" (permesso richieste o scrittura): ACCESO di default, anche dopo ogni nuovo abbinamento
        /// (nuovo TeamsLink) e per i collegamenti salvati prima che esistesse (proprieta' assente nel JSON = valore iniziale). Con sola lettura non conta: si invia sempre</summary>
        public bool SharePersonal { get; set; } = true;

        private const string DbKey = "teams.link";

        /// <summary>
        /// il PC si comporta da Teams (barra, sincronizzazione, condivisione): installato in modalita' Teams, oppure abbinato dopo (anche se installato Locale
        /// o portatile) con una richiesta in attesa o approvata. La modalita' scelta all'installazione serve solo all'installazione stessa (ripara, disinstalla).
        /// Letture rare (avvio della finestra principale, pagina Opzioni): nessuna cache
        /// </summary>
        public static bool IsTeams => (SetupEngine.IsInstalledCopy() && SetupEngine.InstalledMode() == SetupMode.Teams)
                                      || Environment.GetEnvironmentVariable("LEMORA_TEAMS_DEV") == "1" // solo per provare la barra senza installare
                                      || Load().State is "pending" or "approved";

        public static TeamsLink Load()
        {
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json))
                    return JsonSerializer.Deserialize<TeamsLink>(json!) ?? new TeamsLink();
            }
            catch { /* valore illeggibile: si riparte da "non collegato" */ }
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

    /// <summary>errore di collegamento al server Teams; Kind dice cosa mostrare all'utente</summary>
    internal class TeamsException : Exception
    {
        /// <summary>address (non e' una rete locale) | unreachable | certificate | code | blocked | input | server</summary>
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
    /// Parla col server Teams in HTTPS. Regole: solo indirizzi della rete locale (controllati al momento di collegarsi, non solo al momento di scriverli);
    /// il certificato del server e' autofirmato: la prima volta se ne ricorda l'impronta, dopo deve essere identico.
    /// </summary>
    internal sealed class TeamsServerClient : IDisposable
    {
        public record PairRequest(string Id, string Token, string Fingerprint);
        public record PairStatus(string State, string? Key, string? Permission, bool NoCopy);

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string? _pinned;

        /// <summary>impronta SHA-256 del certificato incontrato nell'ultima connessione</summary>
        public string? SeenFingerprint { get; private set; }

        public const int DefaultPort = 25353;

        public static (string host, int port) ParseServer(string server)
        {
            var s = (server ?? "").Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw new TeamsException("input", "Il server usa solo HTTPS.");
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            if (s.Length == 0) throw new TeamsException("input", "Indirizzo del server mancante.");
            string host = s; int port = DefaultPort;
            if (s.StartsWith("[")) // IPv6 tra parentesi quadre
            {
                int end = s.IndexOf(']');
                if (end < 0) throw new TeamsException("input", "Indirizzo non valido.");
                host = s.Substring(1, end - 1);
                if (s.Length > end + 1)
                {
                    if (s[end + 1] != ':' || !int.TryParse(s.Substring(end + 2), out port)) throw new TeamsException("input", "Porta non valida.");
                }
            }
            else
            {
                int colon = s.LastIndexOf(':');
                if (colon >= 0 && s.IndexOf(':') == colon) // un solo ":" = host:porta (piu' di uno = IPv6 senza parentesi)
                {
                    host = s.Substring(0, colon);
                    if (!int.TryParse(s.Substring(colon + 1), out port)) throw new TeamsException("input", "Porta non valida.");
                }
            }
            if (host.Length == 0 || port < 1 || port > 65535) throw new TeamsException("input", "Indirizzo non valido.");
            return (host, port);
        }

        /// <summary>10.x, 172.16-31.x, 192.168.x, 169.254.x, 127.x e gli equivalenti IPv6 (::1, fc00::/7, fe80::/10)</summary>
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
                // l'indirizzo si risolve e si controlla qui, al momento di aprire la connessione: un nome che oggi punta a un PC locale e domani a internet viene rifiutato
                ConnectCallback = async (ctx, ct) =>
                {
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    if (addrs.Length == 0 || addrs.Any(a => !IsLocalAddress(a)))
                        throw new TeamsException("address", "L'indirizzo non e' in una rete locale.");
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
                },
            };
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        }

        public void Dispose() => _http.Dispose();

        private async Task<(int status, JsonElement body)> SendAsync(HttpMethod method, string path, object? payload, string? bearer, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, _baseUrl + path);
            req.Headers.Add("X-Lemora", "1");
            req.Headers.Add("X-Lemora-Caps", "vnc,requests,personal,sessions,shared"); // shared = password dei dispositivi, cartelle e credenziali di cartella condivise; senza, il server tratta il client come vecchio: niente VNC, permesso "requests" letto come "read", niente connessioni personali ne' durata dei collegamenti
            if (bearer != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            if (payload != null) req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            // log: metodo, percorso, esito e tempo (Medio); mai il contenuto, che porta chiavi e password
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                JsonElement body = default;
                try { if (text.Length > 0) body = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { /* risposta senza JSON */ }
                Shawn.Utils.SimpleLogHelper.Debug($"Teams: {method} {path} -> {(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms ({text.Length} caratteri)");
                return ((int)resp.StatusCode, body);
            }
            catch (HttpRequestException ex)
            {
                Shawn.Utils.SimpleLogHelper.Debug($"Teams: {method} {path} non riuscita dopo {sw.ElapsedMilliseconds} ms: {ex.Message}{(ex.InnerException != null ? " / " + ex.InnerException.Message : "")}");
                var te = ex.InnerException as TeamsException;
                if (te != null) throw te;
                if (ex.InnerException is System.Security.Authentication.AuthenticationException)
                    throw new TeamsException("certificate", "Il certificato del server non e' quello ricordato.", inner: ex);
                throw new TeamsException("unreachable", "Il server non risponde.", inner: ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                Shawn.Utils.SimpleLogHelper.Debug($"Teams: {method} {path} senza risposta dopo {sw.ElapsedMilliseconds} ms (tempo scaduto)");
                throw new TeamsException("unreachable", "Il server non risponde.", inner: ex);
            }
        }

        private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>manda la richiesta di abbinamento; il server risponde con id e segreto per chiederne l'esito</summary>
        public async Task<PairRequest> RequestAsync(string code, string pc, string user, string appVersion, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/pair/request", new { code, pc, user, appVersion }, null, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200:
                    var fp = Str(body, "fingerprint");
                    // l'impronta che il server dichiara deve essere quella del certificato con cui si e' parlato: chi sta in mezzo non puo' presentarne uno suo
                    if (string.IsNullOrEmpty(SeenFingerprint) || !string.Equals(fp, SeenFingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new TeamsException("certificate", "L'impronta del certificato non corrisponde.");
                    return new PairRequest(Str(body, "id"), Str(body, "token"), fp);
                case 401: throw new TeamsException("code", "Codice non corretto o scaduto.", remaining: Int(body, "remaining"));
                case 429:
                    DateTime? until = DateTime.TryParse(Str(body, "until"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var u) ? u.ToLocalTime() : null;
                    throw new TeamsException("blocked", "Troppi tentativi.", until: until);
                case 400: throw new TeamsException("input", "Nome del PC o dell'utente non valido.");
                case 403: throw new TeamsException("address", "Il server non accetta richieste da questo indirizzo.");
                default: throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            }
        }

        /// <summary>esito della richiesta; con ack=true conferma di aver salvato la chiave (il server smette di mandarla)</summary>
        public async Task<PairStatus> StatusAsync(string id, string token, bool ack = false, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/pair/status", new { id, token, ack }, null, ct).ConfigureAwait(false);
            if (status != 200) throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            return new PairStatus(Str(body, "state"), Str(body, "key") is { Length: > 0 } k ? k : null, Str(body, "permission") is { Length: > 0 } p ? p : null, Bool(body, "noCopy"));
        }

        /// <summary>controlla che la chiave di questo PC valga ancora e ne legge il permesso attuale</summary>
        public async Task<PairStatus> MeAsync(string key, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Get, "/api/client/me", null, key, ct).ConfigureAwait(false);
            if (status == 200) return new PairStatus(Str(body, "state"), null, Str(body, "permission"), Bool(body, "noCopy"));
            if (status == 401 && Str(body, "state") == "revoked") return new PairStatus("revoked", null, null, false);
            throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
        }

        /// <summary>ritira la richiesta ancora in attesa: sparisce dalla console del server</summary>
        public async Task CancelAsync(string id, string token, CancellationToken ct = default)
        {
            var (status, _) = await SendAsync(HttpMethod.Post, "/api/pair/cancel", new { id, token }, null, ct).ConfigureAwait(false);
            if (status is not (200 or 404)) throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
        }

        public record WriteResult(TeamsItem? Item);

        /// <summary>aggiunge (id vuoto), modifica o cancella (item null) una voce condivisa. Errori: permission | nocopy | conflict | input | revoked | notfound | server | unreachable</summary>
        public async Task<WriteResult> WriteAsync(string key, string id, TeamsItem? item, CancellationToken ct = default)
        {
            string path = item == null ? $"/api/client/connections/{id}/delete" : id.Length == 0 ? "/api/client/connections" : $"/api/client/connections/{id}/update";
            object payload = item == null ? new { } : new
            {
                folder = item.Folder, name = item.Name, protocol = item.Protocol, host = item.Host, port = item.Port, user = item.User,
                password = item.Password ?? "", tags = item.Tags, baseVersion = id.Length == 0 ? (int?)null : item.Version,
            };
            var (status, body) = await SendAsync(HttpMethod.Post, path, payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200:
                    return new WriteResult(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? ParseItem(it) : null);
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "Il PC e' stato scollegato.");
                case 403: throw new TeamsException(Str(body, "error") is "nocopy" ? "nocopy" : "permission", "Operazione non permessa.");
                case 409: throw new TeamsException("conflict", "La voce e' cambiata sul server.");
                case 400: throw new TeamsException("input", "Dati non validi.");
                case 404: throw new TeamsException("notfound", "La voce non c'e' piu'.");
                default: throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            }
        }

        // ---- password dei dispositivi, cartelle e credenziali di cartella condivise (vedi TeamsShared). I percorsi di cartella viaggiano con "/" senza spazi.
        public record SharedDevice(string Id, int Version, string Folder, string Name, string Ip, List<Lemora.Service.DeviceUser> Users, bool Deleted);
        public record SharedFolder(string Path, string Domain, string IpPrefix, int Version, bool Deleted);
        public record SharedCred(string Id, int Version, string Folder, string User, string Password, bool Deleted);
        public class SharedBatch
        {
            public List<SharedDevice> Devices { get; } = new();
            public List<SharedFolder> Folders { get; } = new();
            public List<SharedCred> Creds { get; } = new();
            /// <summary>false = il server non manda questi dati (versione vecchia): niente da applicare</summary>
            public bool Present { get; set; }
        }

        /// <summary>dall'ultima SyncAsync: le novita' condivise</summary>
        public SharedBatch LastShared { get; private set; } = new SharedBatch();

        private static SharedDevice ParseDevice(JsonElement e)
        {
            var users = new List<Lemora.Service.DeviceUser>();
            if (e.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Array)
                foreach (var u in us.EnumerateArray()) users.Add(new Lemora.Service.DeviceUser { User = Str(u, "user"), Password = Str(u, "password") });
            return new SharedDevice(Str(e, "id"), Int(e, "version"), Str(e, "folder"), Str(e, "name"), Str(e, "ip"), users, Bool(e, "deleted"));
        }

        private static SharedFolder ParseFolder(JsonElement e) => new(Str(e, "path"), Str(e, "domain"), Str(e, "ipPrefix"), Int(e, "version"), Bool(e, "deleted"));
        private static SharedCred ParseCred(JsonElement e) => new(Str(e, "id"), Int(e, "version"), Str(e, "folder"), Str(e, "user"), Str(e, "password"), Bool(e, "deleted"));

        /// <summary>chiamata di scrittura sulle cose condivise. Errori: revoked | permission | conflict | exists | notempty | into_itself | input | notfound | server | unreachable</summary>
        private async Task<JsonElement> SharedCallAsync(string key, string path, object payload, CancellationToken ct)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, path, payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200: return body;
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "Il PC e' stato scollegato.");
                case 403: throw new TeamsException("permission", "Operazione non permessa.");
                case 409: throw new TeamsException(Str(body, "error") is "exists" or "notempty" or "into_itself" ? Str(body, "error") : "conflict", "Il server non accetta la modifica.");
                case 400: throw new TeamsException("input", "Dati non validi.");
                case 404: throw new TeamsException("notfound", "Non c'e' piu'.");
                default: throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            }
        }

        /// <summary>aggiunge (create) o modifica una voce del gestore password; restituisce la voce com'e' ora sul server</summary>
        public async Task<SharedDevice?> DeviceWriteAsync(string key, SharedDevice d, bool create, CancellationToken ct = default)
        {
            var body = await SharedCallAsync(key, "/api/client/devices", new { id = d.Id, folder = d.Folder, name = d.Name, ip = d.Ip, users = d.Users.Select(u => new { user = u.User, password = u.Password }), baseVersion = create ? (int?)null : d.Version }, ct).ConfigureAwait(false);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? ParseDevice(it) : null;
        }

        public Task DeviceDeleteAsync(string key, string id, CancellationToken ct = default) => SharedCallAsync(key, $"/api/client/devices/{id}/delete", new { }, ct);

        /// <summary>crea la cartella o ne cambia dominio e prefisso IP (non serve alcun permesso)</summary>
        public async Task FolderUpsertAsync(string key, string path, string domain, string ipPrefix, CancellationToken ct = default) =>
            await SharedCallAsync(key, "/api/client/folders", new { path, domain, ipPrefix }, ct).ConfigureAwait(false);

        public Task FolderDeleteAsync(string key, string path, CancellationToken ct = default) => SharedCallAsync(key, "/api/client/folders/delete", new { path }, ct);

        public Task FolderRenameAsync(string key, string from, string to, CancellationToken ct = default) => SharedCallAsync(key, "/api/client/folders/rename", new { from, to }, ct);

        /// <summary>aggiunge o modifica una credenziale condivisa di cartella (password null = resta quella che c'e')</summary>
        public async Task<SharedCred?> CredWriteAsync(string key, SharedCred c, bool create, string? password, CancellationToken ct = default)
        {
            var body = await SharedCallAsync(key, "/api/client/fcreds", new { id = c.Id, folder = c.Folder, user = c.User, password, baseVersion = create ? (int?)null : c.Version }, ct).ConfigureAwait(false);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object ? ParseCred(it) : null;
        }

        public Task CredDeleteAsync(string key, string id, CancellationToken ct = default) => SharedCallAsync(key, $"/api/client/fcreds/{id}/delete", new { }, ct);

        /// <summary>registro del server: "credenziale copiata" (MAI la password)</summary>
        public Task AuditCopyAsync(string key, string what, string device, string user, CancellationToken ct = default) =>
            SharedCallAsync(key, "/api/client/audit", new { kind = "copy", what, device, user }, ct);

        private static TeamsItem ParseItem(JsonElement e) => new TeamsItem
        {
            Id = Str(e, "id"), Version = Int(e, "version"), Folder = Str(e, "folder"), Name = Str(e, "name"), Protocol = Str(e, "protocol"),
            Host = Str(e, "host"), Port = Int(e, "port"), User = Str(e, "user"), Tags = Str(e, "tags"),
            Password = e.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String ? pw.GetString() : null,
            Disabled = Bool(e, "disabled"), Deleted = Bool(e, "deleted"),
            OriginPc = e.TryGetProperty("origin", out var o) ? Str(o, "pc") : "", OriginLocalId = e.TryGetProperty("origin", out var o2) ? Str(o2, "localId") : "",
        };

        /// <summary>una connessione salvata su questo PC, come la riceve /api/client/personal (password in chiaro solo sul canale col certificato fissato)</summary>
        public record PersonalItem(string LocalId, string Folder, string Name, string Protocol, string Host, int Port, string User, string Password, string Tags);
        /// <summary>stato di una connessione personale inviata: pending | published | rejected (con la nota dell'amministratore)</summary>
        public record PersonalStatus(string LocalId, string State, string Note);
        /// <summary>dall'ultima SyncAsync: lo stato delle connessioni personali inviate da questo PC (vuoto se il server non lo manda)</summary>
        public List<PersonalStatus> LastPersonal { get; private set; } = new List<PersonalStatus>();

        /// <summary>manda al server le connessioni salvate su questo PC (cambiate o nuove) e quelle eliminate. Risponde lo stato generale (ok | nocopy | off) e quello di ogni voce</summary>
        public async Task<(string state, Dictionary<string, string> items)> PersonalAsync(string key, bool share, List<PersonalItem> items, List<string> removed, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/client/personal", new { share, items, removed }, key, ct).ConfigureAwait(false);
            if (status == 401 && Str(body, "state") == "revoked") throw new TeamsException("revoked", "Il PC e' stato scollegato.");
            if (status != 200) throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            var res = new Dictionary<string, string>();
            if (body.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) res[Str(e, "localId")] = Str(e, "state");
            return (Str(body, "state"), res);
        }

        /// <summary>un collegamento a una voce condivisa: id locale, id della voce sul server, protocollo, inizio e fine (null = ancora in corso), in UTC ISO. MAI la password</summary>
        public record SessionItem(string Id, string ConnId, string Protocol, string Start, string? End);

        /// <summary>carica gli inizi/fini dei collegamenti alle voci condivise. Mai bloccante per la sincronizzazione; false se il server non li accetta (vecchio o rete)</summary>
        public async Task<bool> SessionsAsync(string key, List<SessionItem> items, CancellationToken ct = default)
        {
            var (status, _) = await SendAsync(HttpMethod.Post, "/api/client/sessions", new { items }, key, ct).ConfigureAwait(false);
            if (status == 401 && ct.IsCancellationRequested == false) return false; // revoca gestita dal giro di sync
            return status == 200;
        }

        /// <summary>esito di una richiesta di questo PC (permesso "requests"). State: pending | stale | approved | rejected</summary>
        public record ReqStatus(string Id, string State, string Note);

        /// <summary>dall'ultima SyncAsync: le richieste di questo PC e il permesso attuale (vuoto se il server non li manda)</summary>
        public List<ReqStatus> LastRequests { get; private set; } = new List<ReqStatus>();
        public string LastPermission { get; private set; } = "";

        /// <summary>permesso "requests": chiede all'amministratore di modificare (update), eliminare (delete) o aggiungere (create) una voce. Restituisce l'id della richiesta</summary>
        public async Task<string> SendRequestAsync(string key, string kind, string id, int? baseVersion, TeamsItem? item, CancellationToken ct = default)
        {
            var payload = new
            {
                kind, connId = id.Length == 0 ? null : id, baseVersion, folder = item?.Folder, name = item?.Name, protocol = item?.Protocol, host = item?.Host,
                port = item?.Port ?? 0, user = item?.User, password = item == null ? null : item.Password ?? "", tags = item?.Tags,
            };
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/client/requests", payload, key, ct).ConfigureAwait(false);
            switch (status)
            {
                case 200: return Str(body, "id");
                case 401 when Str(body, "state") == "revoked": throw new TeamsException("revoked", "Il PC e' stato scollegato.");
                case 403: throw new TeamsException("permission", "Operazione non permessa.");
                case 400: throw new TeamsException("input", "Dati non validi.");
                case 404: throw new TeamsException("notfound", "La voce non c'e' piu'.");
                default: throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            }
        }

        /// <summary>voci condivise cambiate dopo la revisione since (0 = tutte). revoked=true: il PC e' stato scollegato dal server</summary>
        public async Task<(bool revoked, long rev, List<TeamsItem> items)> SyncAsync(string key, long since, CancellationToken ct = default)
        {
            var (status, body) = await SendAsync(HttpMethod.Get, "/api/client/sync?since=" + since, null, key, ct).ConfigureAwait(false);
            if (status == 401 && Str(body, "state") == "revoked") return (true, 0, new List<TeamsItem>());
            if (status != 200) throw new TeamsException("server", $"Risposta inattesa del server ({status}).");
            var items = new List<TeamsItem>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) items.Add(ParseItem(e));
            LastRequests = new List<ReqStatus>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("requests", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
                foreach (var e in reqs.EnumerateArray()) LastRequests.Add(new ReqStatus(Str(e, "id"), Str(e, "state"), Str(e, "note")));
            LastPermission = Str(body, "permission");
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
    /// Copia locale delle connessioni condivise dal server: sta nel database cifrato dell'app (AppDbStore) e alimenta l'origine dati Teams (sola lettura).
    /// </summary>
    internal static class TeamsSync
    {
        private class Store
        {
            public long Rev { get; set; }
            public DateTime LastOk { get; set; } = DateTime.MinValue;
            public List<TeamsItem> Items { get; set; } = new List<TeamsItem>();
            /// <summary>richieste mandate all'amministratore (permesso "requests") e non ancora decise</summary>
            public List<PendingRequest> Pending { get; set; } = new List<PendingRequest>();
            /// <summary>connessioni personali: impronta (SHA-256 dei valori, password compresa) dell'ultimo invio accettato, per id locale; si rimanda solo cio' che cambia</summary>
            public Dictionary<string, string> PersonalSent { get; set; } = new Dictionary<string, string>();
            /// <summary>ultimo stato noto di ogni connessione personale (pending | published | rejected): il cambio da pending fa comparire l'avviso</summary>
            public Dictionary<string, string> PersonalState { get; set; } = new Dictionary<string, string>();
            /// <summary>risposta del server all'ultimo invio: ok | nocopy | off (vuoto = mai inviato o server che non conosce l'invio)</summary>
            public string PersonalMode { get; set; } = "";
        }

        internal class PendingRequest
        {
            public string Id { get; set; } = "";
            public string Kind { get; set; } = "";   // update | delete | create
            public string ConnId { get; set; } = ""; // vuoto per una voce nuova
            public string Name { get; set; } = "";
        }

        /// <summary>id del server delle voci con una richiesta in attesa: la lista mostra l'orologio accanto alla nuvola</summary>
        private static HashSet<string> _pendingIds = new HashSet<string>();

        /// <summary>id dell'app ("teams-...")</summary>
        public static bool IsPending(string appId) => appId.StartsWith("teams-") && _pendingIds.Contains(appId.Substring(6));

        private static void SaveStore(Store s)
        {
            AppDbStore.Set(DbKey, JsonSerializer.Serialize(s));
            Derive(s);
        }

        /// <summary>
        /// id locali delle connessioni di questo PC gia' pubblicate come voci Teams (origine = questo PC): la voce locale non si mostra finche' c'e' quella Teams
        /// (DataSourceService.GetServers la salta). Nessun dato locale si cancella: se la voce Teams sparisce (eliminata, disabilitata, PC scollegato) la locale ricompare.
        /// </summary>
        private static HashSet<string> _replaced = new HashSet<string>();
        public static bool IsReplaced(string localId) => _replaced.Contains(localId);

        /// <summary>id locali delle connessioni di questo PC inviate come proposta e non ancora decise dall'amministratore: orologio sulla voce locale</summary>
        private static HashSet<string> _personalPending = new HashSet<string>();
        public static bool IsPersonalPending(string localId) => _personalPending.Contains(localId);

        private static void Derive(Store s)
        {
            _pendingIds = s.Pending.Where(x => x.ConnId.Length > 0).Select(x => x.ConnId).ToHashSet();
            var pp = s.PersonalState.Where(x => x.Value == "pending").Select(x => x.Key).ToHashSet();
            if (!pp.SetEquals(_personalPending))
            {
                _personalPending = pp;
                // le voci locali gia' mostrate non si ricaricano: si avvisa solo l'orologio
                Stylet.Execute.OnUIThread(() =>
                {
                    foreach (var vm in IoC.TryGet<GlobalData>()?.VmItemList.ToList() ?? new List<Lemora.View.ProtocolBaseViewModel>()) vm.RaisePropertyChanged(nameof(Lemora.View.ProtocolBaseViewModel.IsTeamsPending));
                });
            }
            var me = TeamsLink.Load().Id;
            _replaced = me.Length == 0 ? new HashSet<string>() : s.Items.Where(x => x.OriginPc == me && x.OriginLocalId.Length > 0 && !x.Deleted && !x.Disabled).Select(x => x.OriginLocalId).ToHashSet();
        }

        private const string DbKey = "teams.items";
        public static readonly TimeSpan Period = TimeSpan.FromMinutes(20);
        private static TeamsSource? _source;

        private static Store LoadStore()
        {
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json))
                    return JsonSerializer.Deserialize<Store>(json!) ?? new Store();
            }
            catch { /* copia illeggibile: si riparte da zero e si riscarica */ }
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
                ds.AddOrUpdateDataSource(_source, connectTimeOutSeconds: 1); // ricarica l'elenco
            }
            else
            {
                _source.SetItems(s.Items, s.LastOk);
                RefreshPermission(TeamsLink.Load());
                IoC.TryGet<GlobalData>()?.ReloadAll();
            }
        }

        /// <summary>percorsi (con tutti i livelli superiori) delle cartelle usate dalle voci visibili</summary>
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
        /// Cartelle che avevano voci Teams prima e non ne hanno piu' (spostate, rinominate, disabilitate, eliminate, PC scollegato):
        /// se non contengono voci locali spariscono dall'elenco cartelle e dall'albero (che le ricreava vuote anche dopo il riavvio).
        /// Le cartelle che non hanno mai avuto voci Teams, o con voci locali, non si toccano. Va chiamata PRIMA di ricaricare l'elenco.
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

        /// <summary>voci visibili prima e non piu' dopo (disabilitate o eliminate dall'amministratore): avviso discreto con i nomi</summary>
        internal static void NotifyGone(IEnumerable<TeamsItem> before, IEnumerable<TeamsItem> after)
        {
            var still = after.Where(x => !x.Disabled && !x.Deleted).Select(x => x.Id).ToHashSet();
            var names = before.Where(x => !x.Disabled && !x.Deleted && !still.Contains(x.Id)).Select(x => x.Name).ToList();
            if (names.Count > 0) ToastService.Show(string.Format(SetupText.T("tm_items_gone"), string.Join(", ", names)), ToastKind.Warning);
        }

        /// <summary>" · sincronizzato alle HH:mm" dall'ultimo contatto riuscito (vuoto se non c'e' ancora stato)</summary>
        public static string LastSyncText => LoadStore().LastOk is var t && t > DateTime.MinValue ? " · " + t.ToString(t.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm") : "";

        /// <summary>riga "Connessioni ricevute" della finestra di stato: quante voci visibili e ora dell'ultima sincronizzazione riuscita (prima del primo giro: "in attesa")</summary>
        public static string ReceivedText()
        {
            var s = LoadStore();
            if (s.LastOk == DateTime.MinValue) return SetupText.T("tm_ok_recv_v");
            int n = s.Items.Count(x => !x.Disabled && !x.Deleted);
            return string.Format(SetupText.T(n == 1 ? "tm_ok_recv_1" : "tm_ok_recv_n"), n, s.LastOk.ToString(s.LastOk.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm"));
        }

        /// <summary>interruttore "Condividi le mie connessioni" (finestra di stato e Opzioni): salva, allo spegnimento avvisa che le voci pubblicate restano, e sincronizza subito</summary>
        public static async Task SetSharePersonalAsync(bool on)
        {
            var l = TeamsLink.Load();
            if (l.SharePersonal == on) return;
            l.SharePersonal = on;
            l.Save();
            ShareChanged?.Invoke(); // la barra di stato della finestra principale si aggiorna subito, senza aspettare il sync (ERRORI 113)
            if (!on) ToastService.Show(SetupText.T("tm_share_kept"));
            try { await RunAsync(); } catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: sincronizzazione dopo l'interruttore non riuscita: " + ex.Message); }
            ShareChanged?.Invoke(); // e di nuovo dopo il sync (ora, modo "nocopy")
        }

        /// <summary>interruttore "Condividi le mie connessioni" cambiato: la finestra principale ridisegna la barra Teams</summary>
        public static event Action? ShareChanged;

        /// <summary>l'origine Teams si puo' scrivere con il permesso "scrittura"; con "richieste" Modifica ed Elimina restano, ma il salvataggio diventa una richiesta all'amministratore</summary>
        public static void RefreshPermission(TeamsLink link) => _source?.SetWritable(link.State == "approved" && link.Permission is "write" or "requests");

        /// <summary>all'avvio: mostra subito la copia salvata (anche senza rete, anche vuota: con il permesso di scrittura serve per aggiungere la prima voce)</summary>
        public static void ShowSaved()
        {
            if (TeamsLink.Load().State != "approved") return;
            Publish(LoadStore());
        }

        /// <summary>conferma di Elimina: con il permesso "richieste" una voce Teams non si elimina, si chiede all'amministratore</summary>
        public static string DeleteConfirmText(IEnumerable<Lemora.Model.Protocol.Base.ProtocolBase> servers, string defaultKey) =>
            servers.Any(s => s.DataSource is TeamsSource) && TeamsLink.Load().Permission == "requests" ? SetupText.T("tm_req_delete_confirm") : IoC.Translate(defaultKey);

        /// <summary>testo per l'utente di un errore di scrittura</summary>
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
            _ => "tm_err_server",
        });

        /// <summary>
        /// Scrive una voce sul server (id vuoto = nuova; item null = cancella) e aggiorna subito la copia locale.
        /// Blocca finche' il server risponde (al massimo 15 secondi): e' chiamata dal salvataggio dell'editor, che aspetta l'esito.
        /// Restituisce null se e' andata bene, altrimenti il testo dell'errore; newId = id sul server della voce creata.
        /// </summary>
        public static string? Write(string id, TeamsItem? item, out string newId)
        {
            newId = id;
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return ErrorText("revoked");
            if (link.Permission == "requests") return SendRequest(link, id, item);
            if (link.Permission != "write") return ErrorText("permission");
            if (link.NoCopy && id.Length == 0 && item != null) return ErrorText("nocopy");
            try
            {
                var res = Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    return await c.WriteAsync(link.Key, id, item);
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
            catch (TeamsException ex)
            {
                return ErrorText(ex.Kind);
            }
        }

        /// <summary>
        /// Permesso "richieste": invece di scrivere manda la richiesta all'amministratore. La copia locale non cambia (la voce resta com'e' con l'orologio
        /// "in attesa"); quando l'amministratore approva la voce nuova arriva dal server con la sincronizzazione.
        /// </summary>
        private static string? SendRequest(TeamsLink link, string id, TeamsItem? item)
        {
            var kind = item == null ? "delete" : id.Length == 0 ? "create" : "update";
            var store = LoadStore();
            var cur = store.Items.FirstOrDefault(x => x.Id == id);
            try
            {
                var reqId = Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    return await c.SendRequestAsync(link.Key, kind, id, cur?.Version, item);
                }).GetAwaiter().GetResult();
                store.Pending.Add(new PendingRequest { Id = reqId, Kind = kind, ConnId = id, Name = item?.Name ?? cur?.Name ?? "" });
                SaveStore(store);
                _source?.SetItems(store.Items, store.LastOk); // l'elenco si rilegge: torna la voce com'era, con l'orologio
                ToastService.Show(SetupText.T(kind switch { "delete" => "tm_req_sent_delete", "create" => "tm_req_sent_create", _ => "tm_req_sent" }));
                return null;
            }
            catch (TeamsException ex)
            {
                return ErrorText(ex.Kind);
            }
        }

        /// <summary>esito delle richieste in attesa: approvate (la voce arriva gia' con la sincronizzazione) o rifiutate (avviso con la nota). true se qualcuna si e' chiusa</summary>
        internal static bool ApplyRequestOutcomes(List<PendingRequest> pending, List<TeamsServerClient.ReqStatus> server)
        {
            var byId = server.ToDictionary(x => x.Id);
            var closed = pending.Where(p => byId.TryGetValue(p.Id, out var s) ? s.State is "approved" or "rejected" : server.Count > 0).ToList(); // assente dall'elenco = decisa da oltre 30 giorni
            foreach (var p in closed)
            {
                pending.Remove(p);
                if (byId.TryGetValue(p.Id, out var s) && s.State == "rejected")
                    ToastService.Show(string.Format(SetupText.T(s.Note.Length > 0 ? "tm_req_rejected_note" : "tm_req_rejected"), p.Name, s.Note), ToastKind.Warning);
            }
            return closed.Count > 0;
        }

        // ---- connessioni personali di questo PC (regola in Installer/TEAMS-DESIGN.md sezione 3) ----

        /// <summary>si inviano? sola lettura sempre, richieste/scrittura solo con l'interruttore acceso; mai con "non copiare"</summary>
        internal static bool SendsPersonal(TeamsLink link) => link.State == "approved" && !link.NoCopy && (link.Permission == "read" || link.SharePersonal);

        /// <summary>riga per la barra Teams (" · condivide le connessioni salvate...") con richieste/scrittura quando questo PC le sta mandando al server; vuota altrimenti</summary>
        public static string SharingText(TeamsLink link) =>
            !SendsPersonal(link) || link.Permission == "read" || LoadStore().PersonalMode == "nocopy" ? "" : " · " + SetupText.T(link.Permission == "requests" ? "tm_share_bar_req" : "tm_share_bar"); // sola lettura: niente in barra, solo la riga in Opzioni. Richieste: nulla e' pubblicato finche' l'admin non approva (ERRORI 116)

        /// <summary>connessioni RDP, SSH e VNC dell'origine locale (non Teams, non altri protocolli) con la password in chiaro, pronte per l'invio</summary>
        internal static List<TeamsServerClient.PersonalItem> LocalItems()
        {
            var list = new List<TeamsServerClient.PersonalItem>();
            var src = IoC.TryGet<DataSourceService>()?.LocalDataSource;
            if (src == null) return list;
            foreach (var vm in src.CachedProtocols.ToList()) // la copia gia' letta: rileggere qui farebbe perdere all'elenco il segnale "da ricaricare"
            {
                var t = vm.Server.GetType();
                if (t != typeof(Lemora.Model.Protocol.RDP) && t != typeof(Lemora.Model.Protocol.SSH) && t != typeof(Lemora.Model.Protocol.VNC)) continue;
                if (vm.Server.DoNotSync) continue;   // "non sincronizzare": mai al server (se era gia' stata inviata sparisce dall'elenco e il server ne toglie la copia)
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
        /// Cosa mandare: le voci nuove o cambiate (impronta diversa dall'ultimo invio accettato) e gli id inviati che non ci sono piu' sul PC.
        /// Se la condivisione era spenta (o il server rispondeva "nocopy"/"off") le impronte sono vuote: si rimanda tutto.
        /// </summary>
        internal static (List<TeamsServerClient.PersonalItem> changed, List<string> removed) PersonalDiff(List<TeamsServerClient.PersonalItem> local, Dictionary<string, string> sent)
        {
            var changed = local.Where(i => !sent.TryGetValue(i.LocalId, out var h) || h != Fingerprint(i)).ToList();
            var ids = local.Select(i => i.LocalId).ToHashSet();
            return (changed, sent.Keys.Where(k => !ids.Contains(k)).ToList());
        }

        /// <summary>invio delle connessioni personali (prima di scaricare le voci: con la scrittura quelle pubblicate arrivano nello stesso giro). Mai bloccante per la sincronizzazione</summary>
        private static async Task SendPersonalAsync(TeamsServerClient c, TeamsLink link, Store store, List<TeamsServerClient.PersonalItem> local, CancellationToken ct)
        {
            try
            {
                if (!SendsPersonal(link))
                {
                    // interruttore spento (richieste/scrittura): una volta sola si dice al server di cancellare le copie non pubblicate
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
                Shawn.Utils.SimpleLogHelper.Warning("Teams: invio delle connessioni personali non riuscito: " + ex.Message); // server vecchio (404) o rete: si riprova al prossimo giro
            }
        }

        /// <summary>invio della durata dei collegamenti alle voci Teams (TeamsSessionLog): inizi e fini cambiati dall'ultimo giro. Mai bloccante, mai la password</summary>
        private static async Task SendSessionsAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            try
            {
                var changed = TeamsSessionLog.Changed();
                if (changed.Count == 0) return;
                if (await c.SessionsAsync(link.Key, changed, ct)) TeamsSessionLog.MarkSent(changed);
            }
            catch (TeamsException ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: invio della durata dei collegamenti non riuscito: " + ex.Message); } // server vecchio (404) o rete: si riprova
        }

        /// <summary>avviso discreto quando l'amministratore decide una connessione personale (da "in attesa" a pubblicata o rifiutata). true se qualcosa e' cambiato</summary>
        internal static bool ApplyPersonalOutcomes(Dictionary<string, string> known, List<TeamsServerClient.PersonalStatus> server, IReadOnlyDictionary<string, string> names)
        {
            bool any = false;
            foreach (var s in server)
            {
                known.TryGetValue(s.LocalId, out var was);
                if (was == s.State) continue;
                any = true;
                known[s.LocalId] = s.State;
                if (was != "pending") continue; // prima volta vista (o pubblicata subito con la scrittura): nessun avviso
                var name = names.TryGetValue(s.LocalId, out var n) ? n : s.LocalId;
                if (s.State == "published") ToastService.Show(string.Format(SetupText.T("tm_pers_approved"), name));
                else if (s.State == "rejected") ToastService.Show(string.Format(SetupText.T(s.Note.Length > 0 ? "tm_pers_rejected_note" : "tm_pers_rejected"), name, s.Note), ToastKind.Warning);
            }
            return any;
        }

        /// <summary>scollegamento: cancella la copia e toglie l'origine dati</summary>
        public static void Forget()
        {
            DropEmptyTeamsFolders(LoadStore().Items, new List<TeamsItem>());   // le cartelle delle sole voci Teams se ne vanno con loro
            DevicePasswordService.RemoveServerCopies();   // le password dei dispositivi arrivate dal server se ne vanno con il collegamento
            AppDbStore.Set(DbKey, null);
            _pendingIds = new HashSet<string>();
            Derive(new Store()); // niente piu' orologi sulle voci locali
            _replaced = new HashSet<string>(); // le voci locali sostituite ricompaiono
            if (_source == null) return;
            IoC.TryGet<DataSourceService>()?.RemoveDataSource(_source.DataSourceName);
            _source = null;
        }

        private static readonly SemaphoreSlim RunGate = new SemaphoreSlim(1, 1);

        /// <summary>un giro di sincronizzazione; true se le voci sono cambiate. Senza rete resta la copia salvata. Un giro alla volta: quelli in piu' aspettano (cosi' la coda delle cartelle parte sempre)</summary>
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
                try { await TeamsShared.FlushFoldersAsync(c, link, ct); await TeamsShared.UploadLocalAsync(c, link, ct); }   // cartelle in coda e password dei dispositivi fatte prima del collegamento: mai bloccanti per la sincronizzazione
                catch (TeamsException ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: invio di cartelle e password dei dispositivi non riuscito: " + ex.Message); }
                var (revoked, rev, items) = await c.SyncAsync(link.Key, store.Rev, ct);
                if (revoked)
                {
                    link.State = "revoked"; link.Key = ""; link.Save();
                    Forget();
                    return true;
                }
                if (rev < store.Rev) // il server e' ripartito da zero: si riscarica tutto
                {
                    store = new Store { Pending = store.Pending, PersonalSent = store.PersonalSent, PersonalState = store.PersonalState, PersonalMode = store.PersonalMode };
                    DevicePasswordService.MarkAllLocal();   // le password dei dispositivi vanno ricaricate: il server non le ha piu'
                    TeamsShared.ResetUploaded();            // e lo stesso vale per le cartelle
                    (_, rev, items) = await c.SyncAsync(link.Key, 0, ct);
                }
                TeamsShared.Apply(c.LastShared);   // password dei dispositivi, cartelle e credenziali di cartella (prima di segnare la revisione: se non si applicano si riprova)
                var byId = store.Items.ToDictionary(x => x.Id);
                foreach (var i in items)
                {
                    if (i.Deleted) byId.Remove(i.Id);
                    else if (!byId.TryGetValue(i.Id, out var old) || old.Version <= i.Version) byId[i.Id] = i;
                }
                bool changed = items.Count > 0;
                store.Items = byId.Values.ToList();
                store.Rev = rev;
                store.LastOk = DateTime.Now;
                bool reqClosed = ApplyRequestOutcomes(store.Pending, c.LastRequests); // dopo aver preso le voci: un'approvazione toglie l'orologio quando la voce nuova c'e' gia'
                ApplyPersonalOutcomes(store.PersonalState, c.LastPersonal, local.GroupBy(i => i.LocalId).ToDictionary(g => g.Key, g => g.First().Name));
                // il server elenca tutte le proposte di questo PC: quelle che non ci sono piu' (interruttore spento, voce tolta) non sono piu' in attesa
                var listed = c.LastPersonal.Select(p => p.LocalId).ToHashSet();
                foreach (var gone in store.PersonalState.Keys.Where(k => !listed.Contains(k)).ToList()) store.PersonalState.Remove(gone);
                SaveStore(store);
                if (link.LastError.Length > 0) { link.LastError = ""; link.Save(); }
                if (c.LastPermission.Length > 0 && c.LastPermission != link.Permission) { link.Permission = c.LastPermission; link.Save(); }
                if (reqClosed && !changed && _source != null) { _source.SetItems(store.Items, store.LastOk); IoC.TryGet<GlobalData>()?.ReloadAll(); } // solo l'orologio da togliere
                if (changed)
                {
                    DropEmptyTeamsFolders(before, store.Items);   // prima di ricaricare: l'albero non deve ricrearle
                    NotifyGone(before, store.Items);
                }
                if (changed || _source == null) Publish(store);
                else RefreshPermission(link);
                Shawn.Utils.SimpleLogHelper.Info($"Teams: sincronizzazione riuscita (rev {rev}, {items.Count} voci cambiate, {store.Items.Count} condivise, permesso {link.Permission})");
                return changed;
            }
            catch (TeamsException ex)
            {
                // primo errore di una serie: si vede anche a livello Base; i successivi uguali solo a Medio (senza rete si ripeterebbe ogni giro)
                if (link.LastError != ex.Kind) { Shawn.Utils.SimpleLogHelper.Warning($"Teams: sincronizzazione non riuscita ({ex.Kind}): {ex.Message}"); link.LastError = ex.Kind; link.Save(); }
                else Shawn.Utils.SimpleLogHelper.Debug($"Teams: sincronizzazione non riuscita, ancora ({ex.Kind}): {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>controllo periodico del collegamento: richiesta in attesa -> approvata, rifiutata; PC collegato -> permesso aggiornato o scollegato</summary>
    internal static class TeamsLinkService
    {
        /// <summary>applica l'esito del server al collegamento; true se qualcosa e' cambiato (e va salvato)</summary>
        public static bool Apply(TeamsLink link, TeamsServerClient.PairStatus s)
        {
            string before = $"{link.State}|{link.Permission}|{link.NoCopy}|{link.Key}";
            switch (link.State)
            {
                case "pending":
                    if (s.State == "approved" && s.Key != null) { link.State = "approved"; link.Key = s.Key; link.Permission = s.Permission ?? "read"; link.NoCopy = s.NoCopy; } // read | requests | write
                    else if (s.State == "approved") link.State = "revoked"; // approvato ma la chiave e' gia' stata consegnata e questo PC non l'ha: serve un nuovo abbinamento
                    else if (s.State is "rejected" or "revoked") link.State = s.State;
                    break;
                case "approved":
                    if (s.State == "revoked") { link.State = "revoked"; link.Key = ""; }
                    else if (s.State == "approved") { link.Permission = s.Permission ?? link.Permission; link.NoCopy = s.NoCopy; }
                    break;
            }
            return before != $"{link.State}|{link.Permission}|{link.NoCopy}|{link.Key}";
        }

        /// <summary>annulla la richiesta in attesa sul server; se il server non risponde la richiesta resta in console (l'amministratore puo' rifiutarla)</summary>
        public static async Task CancelAsync(TeamsLink link)
        {
            if (link.State != "pending" || link.Id.Length == 0 || link.Token.Length == 0 || link.Server.Length == 0) return;
            try
            {
                using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                await c.CancelAsync(link.Id, link.Token);
            }
            catch (TeamsException) { /* senza rete non c'e' altro da fare */ }
        }

        /// <summary>un giro di controllo; true se lo stato e' cambiato. Senza rete non cambia nulla (resta l'ultimo stato noto)</summary>
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
                    Lemora.Utils.ActivityLog.Write($"Teams: collegamento al server {link.Server} da {stateBefore} a {link.State}/{link.Permission}");
                if (link.LastError.Length > 0) { link.LastError = ""; changed = true; }
                if (changed) { link.Save(); TeamsSync.RefreshPermission(link); } // la chiave si salva PRIMA di confermare: se qualcosa si ferma qui il server la manda ancora
                if (wasPending && link.State == "approved")
                {
                    try { await c.StatusAsync(link.Id, link.Token, true, ct); } catch (TeamsException) { /* la prossima volta: non e' grave, la chiave e' gia' qui */ }
                }
                return changed;
            }
            catch (TeamsException ex)
            {
                if (link.LastError != ex.Kind) { Shawn.Utils.SimpleLogHelper.Warning($"Teams: controllo del collegamento non riuscito ({ex.Kind}): {ex.Message}"); link.LastError = ex.Kind; link.Save(); }
                else Shawn.Utils.SimpleLogHelper.Debug($"Teams: controllo del collegamento non riuscito, ancora ({ex.Kind})");
                return false;
            }
        }
    }
}
