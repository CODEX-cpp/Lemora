using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Lemora.Service.Locality;
using Lemora.Utils;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.Service.Browser
{
    /// <summary>
    /// Lemora side of the browser extension: registers the "native messaging host" for Chrome, Edge and Firefox and answers on the user's pipe.
    /// The extension sees ONLY the entries of the device password manager (the ones in the sessions' "Password" popup), from all folders:
    /// it looks them up for the open page, fills in user and password, and can save, edit and delete them with the same rules as the folder page
    /// (Teams: permissions and sync included). It must be paired first: Lemora asks the user for confirmation and gives it a key.
    /// </summary>
    public static class BrowserBridge
    {
        private const string PairedKey = "browser.paired";
        private static Thread? _thread;
        private static volatile bool _stop;
        private static int _live;   // connections in progress (their instances keep the pipe alive)

        /// <summary>call when the app is ready (vault open or not: with the vault locked it answers "locked")</summary>
        public static void Start()
        {
            if (_thread != null || Environment.GetEnvironmentVariable("LEMORA_NO_BROWSER") == "1") return;
            try { RegisterNativeHost(); } catch (Exception e) { SimpleLogHelper.Warning("Browser extension: registration failed: " + e.Message); }
            BrowserExtensionSetup.OfferAfterSetup();
            _thread = new Thread(Loop) { IsBackground = true, Name = "Lemora browser bridge" };
            _thread.Start();
        }

        public static void Stop() => _stop = true;

        // ---------------------------------------------------------------- registration (HKCU only: no administrator rights)

        private static readonly string[] ChromiumKeys =
        {
            @"Software\Google\Chrome\NativeMessagingHosts\",
            @"Software\Microsoft\Edge\NativeMessagingHosts\",
            @"Software\Chromium\NativeMessagingHosts\",
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\",
        };

        internal static void RegisterNativeHost()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // the browser will silently launch this exe on every page: only from a folder a normal user cannot modify
            // (installed copy). A portable copy, on a USB stick or shared, is not registered (security hardening). Development builds are registered
            if (!DevOnly.Enabled && !Lemora.Setup.SetupEngine.InstallDirStillSafe(Path.GetDirectoryName(exe)!))
            {
                SimpleLogHelper.Warning("Browser extension: this copy of Lemora is not in a protected folder (installation), the extension is not registered");
                return;
            }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Assert.APP_NAME, "browser");
            Directory.CreateDirectory(dir);
            var chrome = Path.Combine(dir, BrowserProtocol.HostName + ".chrome.json");
            var firefox = Path.Combine(dir, BrowserProtocol.HostName + ".firefox.json");
            File.WriteAllText(chrome, JsonConvert.SerializeObject(new
            {
                name = BrowserProtocol.HostName,
                description = "Lemora: device passwords",
                path = exe,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{BrowserProtocol.ChromeExtensionId}/" },
            }, Formatting.Indented));
            File.WriteAllText(firefox, JsonConvert.SerializeObject(new
            {
                name = BrowserProtocol.HostName,
                description = "Lemora: device passwords",
                path = exe,
                type = "stdio",
                allowed_extensions = new[] { BrowserProtocol.FirefoxExtensionId },
            }, Formatting.Indented));
            foreach (var k in ChromiumKeys)
                using (var key = Registry.CurrentUser.CreateSubKey(k + BrowserProtocol.HostName)) key.SetValue("", chrome);
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\NativeMessagingHosts\" + BrowserProtocol.HostName)) key.SetValue("", firefox);
        }

        // ---------------------------------------------------------------- pipe

        private static void Loop()
        {
            // the first instance is exclusive (FirstPipeInstance): if another user has already created a pipe with this name,
            // we do not attach to theirs (it would have their ACL and pose as Lemora); later instances join ours
            bool own = false;
            while (!_stop)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    var sec = new PipeSecurity();
                    sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(BrowserProtocol.UserSid), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                    server = NamedPipeServerStreamAcl.Create(BrowserProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                        Volatile.Read(ref _live) > 0 ? PipeOptions.None : PipeOptions.FirstPipeInstance, 0, 0, sec);   // none of our connections in progress = the pipe must be created now, by us
                    own = true;
                    server.WaitForConnection();
                    var s = server; server = null;
                    Interlocked.Increment(ref _live);
                    new Thread(() => { try { Serve(s); } finally { Interlocked.Decrement(ref _live); } }) { IsBackground = true, Name = "Lemora browser client" }.Start();
                }
                catch (Exception e)
                {
                    server?.Dispose();
                    if (!own) SimpleLogHelper.Warning("Browser extension: pipe already exists or not available (" + e.Message + "): maybe another copy of Lemora or another user. Retrying in 10 s");
                    else SimpleLogHelper.Warning("Browser extension: pipe not available (" + e.Message + "), retrying in 10 s");
                    // the pipe stays ours while a connection is being served; when none is left the next instance is exclusive again
                    own = Volatile.Read(ref _live) > 0;
                    Thread.Sleep(own ? 1000 : 10000);
                }
            }
        }

        private static void Serve(NamedPipeServerStream s)
        {
            using (s)
            {
                try
                {
                    // who connected: only this user's Lemora host, launched by a browser
                    var browser = BrowserProtocol.CallerBrowser(s, out var why);
                    if (browser == null)
                    {
                        SimpleLogHelper.Warning("Browser extension: connection refused (" + why + ")");
                        BrowserProtocol.Write(s, Err("notallowed").ToString(Formatting.None));
                        return;
                    }
                    while (s.IsConnected)
                    {
                        var msg = BrowserProtocol.Read(s);
                        if (msg == null) return;
                        string reply;
                        try
                        {
                            var req = JObject.Parse(msg);
                            var res = Handle(req, browser);
                            reply = res.ToString(Formatting.None);
                            // what the extension asked and got (never keys or passwords): needed to understand "connected in the browser, not in Lemora"
                            var t = req.Value<string>("type") ?? "";
                            if (t is "hello" or "list" or "pair" or "match" or "confirm")
                                SimpleLogHelper.Warning($"Browser extension: {t} from {Path.GetFileName(browser)} -> ok={res.Value<bool?>("ok")} error={res.Value<string>("error") ?? "-"} paired={res.Value<bool?>("paired")?.ToString() ?? "-"} locked={res.Value<bool?>("locked")?.ToString() ?? "-"} items={(res["items"] as JArray)?.Count.ToString() ?? "-"} key sent={(req.Value<string>("token") ?? "").Length > 0} saved pairings={PairedList.Count}");
                        }
                        catch (Exception e) { SimpleLogHelper.Warning("Browser extension: invalid request (" + e.Message + ")"); reply = Err(IoC.Translate("browser_err_bad_request")).ToString(Formatting.None); }
                        BrowserProtocol.Write(s, reply);
                    }
                }
                catch (IOException) { /* extension closed */ }
            }
        }

        // ---------------------------------------------------------------- requests

        private static JObject Ok(object? data = null)
        {
            var o = data == null ? new JObject() : JObject.FromObject(data);
            o["ok"] = true;
            return o;
        }

        private static JObject Err(string error) => new() { ["ok"] = false, ["error"] = error };

        /// <param name="browserExe">executable of the browser that launched the host (from BrowserProtocol.CallerBrowser, not from the message)</param>
        internal static JObject Handle(JObject req, string browserExe)
        {
            var type = req.Value<string>("type") ?? "";
            switch (type)
            {
                // locked: the database may not be open yet, pairing is read after unlocking
                case "hello":
                    if (!DataVault.IsUnlocked) return Ok(new { app = Assert.APP_DISPLAY_NAME, locked = true, paired = false });
                    var paired = IsPaired(req.Value<string>("token"), browserExe, touch: false);
                    // Firefox extension older than the one inside Lemora: offer the new one (Firefox only: it is the only one Lemora can install)
                    // only when the extension says its version: up to 1.0.8 the save prompt sends a hello without it, and Lemora offered the update for nothing ("version ?")
                    if (paired && !string.IsNullOrEmpty(req.Value<string>("version")) && string.Equals(Path.GetFileName(browserExe), "firefox.exe", StringComparison.OrdinalIgnoreCase))
                        BrowserExtensionSetup.CheckOutdated(req.Value<string>("version"));
                    return Ok(new { app = Assert.APP_DISPLAY_NAME, locked = false, paired, confirmed = paired && Confirmed(req.Value<string>("token")), hello = paired && VaultUnlock.HelloSetUp, theme = Theme() });
                case "pair": return Pair(browserExe);
                case "open": ShowLemora(); return Ok();
            }
            if (!DataVault.IsUnlocked) return Err("locked");   // first: while locked the pairing database may not be open
            if (!IsPaired(req.Value<string>("token"), browserExe, touch: true)) return Err("unpaired");
            if (type == "confirm") return Confirm(req.Value<string>("token")!);
            if (type == "confirmpw")
            {
                // the master password typed in the extension window (no Lemora window); never logged
                var res = VaultUnlock.CheckPassword(req.Value<string>("password") ?? "");
                if (res != "ok") return Err(res);
                lock (ConfirmedUntil) ConfirmedUntil[Hash(req.Value<string>("token")!)] = DateTime.UtcNow.AddMinutes(ConfirmMinutes);
                return Ok();
            }
            // "already saved?" for the password just typed in the page: only a yes/no, nothing saved leaves Lemora.
            // Outside the 20-minute confirmation, otherwise after 20 minutes credentials already saved were offered again
            if (type == "known") return Ok(new { known = Known(req.Value<string>("url") ?? "", req.Value<string>("user") ?? "", req.Value<string>("password") ?? "") });
            // every 20 minutes the person is asked again (Windows Hello or master password) before the extension sees anything
            if (!Confirmed(req.Value<string>("token"))) return Err("confirm");
            switch (type)
            {
                case "match": return Ok(new { items = Match(req.Value<string>("url") ?? "").Select(Public) });
                case "list": return Ok(new { items = DevicePasswordService.All().Where(e => DevicePasswordService.Matches(e, req.Value<string>("q") ?? "")).OrderBy(e => e.Folder).ThenBy(e => e.Name).Take(300).Select(Public) });
                case "folders": return Ok(new { folders = FolderService.AllPaths().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase) });
                // usage log for the Teams server (TeamsBrowserLog): page origin from the extension's background, browser from the process
                case "secret": return Secret(req.Value<string>("id") ?? "", req.Value<string>("user") ?? "", "fill", req.Value<string>("origin"), browserExe);
                case "copy": return Copy(req.Value<string>("id") ?? "", req.Value<string>("user") ?? "", req.Value<string>("origin"), browserExe);
                case "save": return Save(req["entry"] as JObject, req.Value<bool?>("capture") == true, req.Value<string>("origin"), browserExe);
                case "delete": return Delete(req.Value<string>("id") ?? "", req.Value<string>("origin"), browserExe);
                default: return Err(IoC.Translate("browser_err_unknown_request"));
            }
        }

        private static object Public(DeviceEntry e) => new { id = e.Id, name = e.Name, folder = e.Folder, ip = e.Ip, ips = e.AllIps(), users = e.Users.Select(u => u.User), shared = e.OnServer };

        private static object Theme()
        {
            var t = IoC.TryGet<ConfigurationService>()?.Theme;
            static string C(string? hex) => string.IsNullOrEmpty(hex) ? "" : "#" + hex.TrimStart('#')[^6..];
            // same colors as the app window (light themes too): the extension derives lines, secondary text and status colors from them
            return t == null ? new { } : new { accent = C(t.AccentMidColor), accentText = C(t.AccentTextColor), background = C(t.BackgroundColor), surface = C(t.PrimaryMidColor),
                raised = C(t.PrimaryLightColor), text = C(t.PrimaryTextColor), dark = ThemeService.IsDark(t) };
        }

        // ---- extension pairing: random key, only its hash is kept in the database

        /// <summary>
        /// a paired extension: key hash, browser (executable name, read from the process and not from the message),
        /// created, last used. The key is valid only from the same browser and expires after ExpireDays days without use
        /// </summary>
        public sealed class Paired
        {
            public string Id { get; set; } = "";
            public string Hash { get; set; } = "";
            public string Browser { get; set; } = "";
            public string Exe { get; set; } = "";
            public DateTime Created { get; set; }
            public DateTime LastUsed { get; set; }
        }
        public const int ExpireDays = 90;

        private static readonly object PairLock = new();

        private static List<Paired> LoadPaired()
        {
            try { if (AppDbStore.TryGet(PairedKey, out var j) && !string.IsNullOrEmpty(j)) return JsonConvert.DeserializeObject<List<Paired>>(j!) ?? new(); }
            catch { /* unreadable: pair again */ }
            return new();
        }

        private static void SavePaired(List<Paired> list) => AppDbStore.Set(PairedKey, list.Count == 0 ? null : JsonConvert.SerializeObject(list));

        private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        /// <summary>valid key, from the same browser it was paired with, not expired. touch: updates last use (at most once a day)</summary>
        private static bool IsPaired(string? token, string browserExe, bool touch)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
            var h = Encoding.ASCII.GetBytes(Hash(token));
            var exe = browserExe;   // full path: another chrome.exe of the same user (e.g. copied into Downloads) does not count
            lock (PairLock)
            {
                var list = LoadPaired();
                var now = DateTime.UtcNow;
                var expired = list.RemoveAll(p => (now - (p.LastUsed == default ? p.Created : p.LastUsed)).TotalDays > ExpireDays || p.Exe.Length == 0);
                var hit = list.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.Hash), h));
                var ok = hit != null && string.Equals(hit.Exe, exe, StringComparison.OrdinalIgnoreCase);
                if (hit != null && !ok) SimpleLogHelper.Warning($"Browser extension: key of {hit.Exe} used by {exe}: rejected");
                if (ok && touch && (now - hit!.LastUsed).TotalHours > 24) { hit.LastUsed = now; expired++; }
                if (expired > 0) SavePaired(list);
                return ok;
            }
        }

        private static JObject Pair(string browserExe)
        {
            if (!DataVault.IsUnlocked) return Err("locked");
            lock (PairDialogLock)   // one request at a time: no dialogs stacked on top of each other
            {
                // the confirmation shows the real browser (product name and executable path), not a name chosen by the requester
                var product = BrowserName(browserExe);
                var name = $"{product} ({browserExe})";
                bool ok = false;
                Stylet.Execute.OnUIThreadSync(() =>
                {
                    ShowLemora();
                    ok = MessageBoxHelper.Confirm(string.Format(IoC.Translate("browser_pair_text").Replace("\\n", "\n"), name), IoC.Translate("browser_pair_yes"), IoC.Translate("browser_pair_no"), IoC.Translate("browser_pair_title"), defaultNo: true);
                });
                if (!ok) return Err("refused");
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                lock (PairLock)
                {
                    var list = LoadPaired();
                    list.Add(new Paired { Id = Guid.NewGuid().ToString("N"), Hash = Hash(token), Browser = product, Exe = browserExe, Created = DateTime.UtcNow, LastUsed = DateTime.UtcNow });
                    SavePaired(list);
                }
                SimpleLogHelper.Warning($"Browser extension paired ({product})");
                return Ok(new { token });
            }
        }
        private static readonly object PairDialogLock = new();

        // ---- identity confirmed every ConfirmMinutes: only in memory (a restart of Lemora asks again), per extension key
        public const int ConfirmMinutes = 20;
        private static readonly Dictionary<string, DateTime> ConfirmedUntil = new();

        private static bool Confirmed(string? token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            // Lemora unlocked with Windows Hello or the password in the last 20 minutes: that already proves it is you
            if (DateTime.UtcNow - VaultUnlock.IdentityConfirmedUtc < TimeSpan.FromMinutes(ConfirmMinutes)) return true;
            lock (ConfirmedUntil) return ConfirmedUntil.TryGetValue(Hash(token), out var until) && DateTime.UtcNow < until;
        }

        /// <summary>Lemora asks for Windows Hello or the master password (the same dialog as unlocking); confirmed = the extension sees the passwords for 20 minutes</summary>
        private static JObject Confirm(string token)
        {
            if (Confirmed(token)) return Ok();
            lock (PairDialogLock)   // one dialog at a time (popup and page together)
            {
                if (Confirmed(token)) return Ok();
                bool ok = false;
                // only Windows Hello (it appears by itself over the browser): without it, or if cancelled, the extension shows its own password box
                if (!VaultUnlock.HelloSetUp) return Err("nohello");
                Stylet.Execute.OnUIThreadSync(() => { ok = VaultUnlock.ConfirmWithHello(); });
                if (!ok) return Err("refused");
                lock (ConfirmedUntil) ConfirmedUntil[Hash(token)] = DateTime.UtcNow.AddMinutes(ConfirmMinutes);
                return Ok();
            }
        }

        /// <summary>browser product name (e.g. "Microsoft Edge") from the executable, not from the message; if missing, the file name</summary>
        internal static string BrowserName(string browserExe)
        {
            string product;
            try { product = System.Diagnostics.FileVersionInfo.GetVersionInfo(browserExe).ProductName ?? ""; } catch { product = ""; }
            return string.IsNullOrWhiteSpace(product) ? Path.GetFileNameWithoutExtension(browserExe) : product.Trim();
        }

        /// <summary>unpairs all extensions (from settings)</summary>
        public static void ForgetAll() { lock (PairLock) SavePaired(new()); }
        /// <summary>unpairs a single extension</summary>
        public static void Forget(string id) { lock (PairLock) { var l = LoadPaired(); l.RemoveAll(p => p.Id == id); SavePaired(l); } }
        public static IReadOnlyList<Paired> PairedList { get { lock (PairLock) return LoadPaired().Where(p => p.Exe.Length > 0).ToList(); } }

        private static void ShowLemora()
        {
            // to get above the browser Topmost must be switched on and off: with Activate alone Windows leaves the window behind
            try
            {
                if (DataVault.IsUnlocked) { IoC.TryGet<Lemora.View.MainWindowViewModel>()?.ShowMe(); return; }
                // locked: the password prompt (the last visible window) goes in front, not the main window
                Stylet.Execute.OnUIThread(() =>
                {
                    var w = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().LastOrDefault(x => x.IsVisible);
                    if (w == null) return;
                    if (w.WindowState == System.Windows.WindowState.Minimized) w.WindowState = System.Windows.WindowState.Normal;
                    w.Topmost = true; w.Activate(); w.Topmost = false;
                });
            }
            catch { /* window not ready */ }
        }

        // ---- lookup for the open page

        /// <summary>
        /// host and port of an address ("10.0.0.5", "10.0.0.5:8443", "https://nas.local/login", "[fe80::1]:443"); port 0 = not specified.
        /// No DNS: a name and its IP are not matched (rule: never link names and addresses)
        /// </summary>
        internal static (string host, int port) HostPort(string? s)
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0) return ("", 0);
            if (!t.Contains("://")) t = "lemora://" + t;   // not "x://": .NET reads it as drive X:
            if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return ("", 0);
            var host = u.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();   // IdnHost: international names in punycode, as the browser sends them
            var port = u.IsDefaultPort || u.Port <= 0 ? 0 : u.Port;
            return (host, port);
        }

        /// <summary>
        /// Does the entry apply to the page? Same host (no DNS). Entry with a scheme (https://nas, http://10.0.0.1:8080): same scheme and same port,
        /// the scheme's default if not specified: an https entry is not offered to an http page (anyone on the network can pose as the device)
        /// nor on another port. Entry without a scheme (10.0.0.5, nas:8443): same port if specified, otherwise any.
        /// Only http/https pages: no file://, data:, about:
        /// </summary>
        internal static bool EntryMatches(string? entryIp, string pageUrl)
        {
            if (!Uri.TryCreate(pageUrl ?? "", UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps)) return false;
            var pageHost = page.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (pageHost.Length == 0) return false;
            var t = (entryIp ?? "").Trim();
            if (t.Length == 0) return false;
            var (h, p) = HostPort(t);
            if (h.Length == 0 || h != pageHost) return false;
            var sep = t.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0) return p == 0 || p == page.Port;
            var scheme = t[..sep].ToLowerInvariant();
            if (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps) return false;   // ssh://, rdp://...: not a web page
            if (scheme != page.Scheme) return false;
            return (p == 0 ? (scheme == Uri.UriSchemeHttps ? 443 : 80) : p) == page.Port;
        }

        internal static IEnumerable<DeviceEntry> Match(string url) =>
            DevicePasswordService.All().Where(e => e.AllIps().Any(ip => EntryMatches(ip, url))).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);

        /// <summary>
        /// is the password submitted in the page already saved for this address and user? Compared in here, without sending the password to the extension
        /// and without writing "credential used" in the server log (Lemora did not use it)
        /// </summary>
        private static bool Known(string url, string user, string password)
        {
            if (password.Length == 0) return false;
            var pw = Encoding.UTF8.GetBytes(password);
            return Match(url).Any(e => e.Users.Any(u => u.User == user && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(u.Password ?? ""), pw)));
        }

        /// <param name="kind">for the extension log: fill (filled in the page) | copy</param>
        private static JObject Secret(string id, string user, string kind, string? origin, string browserExe)
        {
            var e = DevicePasswordService.All().FirstOrDefault(x => x.Id == id);
            var u = e?.Users.FirstOrDefault(x => string.Equals(x.User, user, StringComparison.CurrentCulture));
            if (e == null || u == null) return Err("notfound");
            // browser use goes into the extension log (TeamsBrowserLog). There used to be AuditCopy("browser") too, which the server always rejected (400: allowed "what" values password|user|ip)
            Lemora.Setup.TeamsBrowserLog.Note(kind, e, u.User, origin, BrowserName(browserExe));
            return Ok(new { user = u.User, password = u.Password });
        }

        /// <summary>
        /// the password on the clipboard the way Lemora copies it: kept out of history (Win+V), flagged for clipboard-monitoring programs
        /// and cleared after 30 s. From the browser (navigator.clipboard) it would end up in history and in the cloud
        /// </summary>
        private static JObject Copy(string id, string user, string? origin, string browserExe)
        {
            var r = Secret(id, user, "copy", origin, browserExe);
            if (r.Value<bool>("ok") != true) return r;
            bool ok = false;
            Stylet.Execute.OnUIThreadSync(() => ok = Lemora.View.Host.SecretClipboard.Set(r.Value<string>("password") ?? "", 30));
            return ok ? Ok() : Err(IoC.Translate("browser_err_clipboard_busy"));
        }

        /// <param name="capture">credential captured from a login in the page and saved from the extension's prompt (log: "save")</param>
        private static JObject Save(JObject? j, bool capture, string? origin, string browserExe)
        {
            if (j == null) return Err(IoC.Translate("browser_err_bad_request"));
            var id = j.Value<string>("id") ?? "";
            var old = id.Length > 0 ? DevicePasswordService.All().FirstOrDefault(x => x.Id == id) : null;
            if (id.Length > 0 && old == null) return Err("notfound");
            // no "ownerless" passwords: from the extension an entry without a folder cannot be created or edited (the folder is assigned in the app)
            if (old != null && string.IsNullOrWhiteSpace(old.Folder)) return Err(IoC.Translate("browser_err_entry_no_folder"));
            // folder not specified = keep the previous one (an existing entry does not end up in the root because of a missing field)
            var folder = (j["folder"] == null || j["folder"]!.Type == JTokenType.Null ? old?.Folder ?? "" : j.Value<string>("folder") ?? "").Trim();
            if (folder.Length == 0) return Err(IoC.Translate("browser_err_pick_folder"));
            if (!FolderService.AllPaths().Any(p => string.Equals(p, folder, StringComparison.CurrentCultureIgnoreCase))) return Err(IoC.Translate("browser_err_folder_missing"));
            var e = old?.Clone() ?? new DeviceEntry();
            e.Folder = folder;
            e.Name = (j.Value<string>("name") ?? "").Trim();
            // "ips": every address, the main one first (newer extension); otherwise only "ip" and the other addresses stay as they are
            if (j["ips"] is JArray ips) DevicePasswordService.SetIps(e, ips.Select(x => (x.Type == JTokenType.String ? (string?)x : null) ?? "").Take(20));
            else e.Ip = (j.Value<string>("ip") ?? "").Trim();
            if (e.Name.Length == 0 && e.Ip.Length == 0) return Err(IoC.Translate("browser_err_name_or_address"));
            if (j["users"] is JArray users)
            {
                var list = new List<DeviceUser>();
                foreach (var x in users.OfType<JObject>())
                {
                    var name = (x.Value<string>("user") ?? "").Trim();
                    if (name.Length == 0) continue;
                    // missing password = keep the existing one (the extension does not know it until it asks for it)
                    var pw = x.Value<string>("password") ?? old?.Users.FirstOrDefault(u => u.User == (x.Value<string>("was") ?? name))?.Password ?? "";
                    list.Add(new DeviceUser { User = name, Password = pw });
                }
                e.Users = list;
            }
            var err = Lemora.Setup.TeamsShared.SaveDevice(e, isNew: old == null, out var saved);
            if (err != null) return Err(err);
            // "requests": saved stays null, the entry changes only when the administrator approves; it is logged anyway (it is what the user did)
            var who = capture || e.Users.Count == 1 ? e.Users.LastOrDefault()?.User ?? "" : "";   // captured: the added user is the last one
            // new entry merged into an existing one (same folder and address, DevicePasswordService.Prepare): for the log it is an edit of that one
            var merged = old == null && saved != null && saved.Id != e.Id;
            Lemora.Setup.TeamsBrowserLog.Note(capture ? "save" : old == null && !merged ? "create" : "update", saved ?? e, who, origin, BrowserName(browserExe));
            return Ok(new { id = saved?.Id ?? e.Id });
        }

        private static JObject Delete(string id, string? origin, string browserExe)
        {
            var e = DevicePasswordService.All().FirstOrDefault(x => x.Id == id);
            if (e == null) return Err("notfound");
            if (string.IsNullOrWhiteSpace(e.Folder)) return Err(IoC.Translate("browser_err_entry_no_folder"));
            var err = Lemora.Setup.TeamsShared.RemoveDevice(e);
            if (err == null) Lemora.Setup.TeamsBrowserLog.Note("delete", e, "", origin, BrowserName(browserExe));
            return err == null ? Ok() : Err(err);
        }
    }
}
