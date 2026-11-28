using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Lemora.Model.Protocol.Base;
using Lemora.Service.Locality;
using Lemora.Utils;
using Newtonsoft.Json;
using Stylet;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Claude's security in Lemora. The settings (Claude page) decide: which folders and servers Claude can use, and whether it can open unsaved
    /// addresses. With Teams the administrator decides too: they can turn Claude off for the user or limit the shared entries (see <see cref="TeamsClaudePolicy"/>).
    /// Everything in the encrypted database (key "mcp"), never in a file. Plus: the "Stop Claude" panel and the activity log.
    /// </summary>
    public static class McpGuard
    {
        private const string DbKey = "mcp";

        public class Store
        {
            public bool Enabled { get; set; }
            public int Port { get; set; } = McpServer.DefaultPort;
            public string Token { get; set; } = "";
            /// <summary>
            /// Starting rule ("All connections"): every folder and server inherits it from the row above, unless explicitly chosen.
            /// By default everything is allowed (including entries and folders added later): the user removes what they do not want.
            /// </summary>
            public bool All { get; set; } = true;
            /// <summary>explicitly allowed folders ("Customer/Site"), even inside an excluded folder</summary>
            public HashSet<string> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>explicitly excluded folders</summary>
            public HashSet<string> ExcludedFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>explicitly allowed servers (even inside an excluded folder)</summary>
            public HashSet<string> Servers { get; set; } = new();
            /// <summary>explicitly excluded servers</summary>
            public HashSet<string> Excluded { get; set; } = new();
            /// <summary>unsaved IP addresses or names (apri_indirizzo)</summary>
            public bool AllowAddresses { get; set; } = true;
            /// <summary>"Enable control of VNC connections": all VNC connections together (saved and by address); VNC entries do not appear in the tree</summary>
            public bool AllowVnc { get; set; } = true;
            /// <summary>2 = "everything allowed with exceptions" rules; older settings are switched to all on</summary>
            public int Version { get; set; }   // missing in old JSON = 0
            /// <summary>
            /// where the user moved the Pause/Stop panel inside the session: distance (pixels at 100%) from the right edge and the top of the remote screen.
            /// null = default, top right
            /// </summary>
            public double? ControlsRight { get; set; }
            public double? ControlsTop { get; set; }
            /// <summary>texts of the Pause/Stop panel, chosen in Settings > Appearance; empty = the default ones</summary>
            public string? ControlsTextActive { get; set; }
            public string? ControlsTextPaused { get; set; }
            /// <summary>"Stop Claude" panel when Lemora is not in the foreground (Settings > MCP Server); off = never</summary>
            public bool BannerEnabled { get; set; } = true;
            /// <summary>where the user moved the "Stop Claude" panel (screen coordinates in pixels at 100%); null = top center</summary>
            public double? BannerLeft { get; set; }
            public double? BannerTop { get; set; }
        }

        private static readonly object Lock = new();
        private static Store? _store;
        private static volatile bool _stopped;
        private static Window? _banner;

        public static Store Settings
        {
            get
            {
                lock (Lock)
                {
                    if (_store != null) return _store;
                    try { if (AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json)) _store = JsonConvert.DeserializeObject<Store>(json!); }
                    catch { /* regenerated */ }
                    _store ??= new Store { Version = 2 };
                    if (_store.Version < 2)
                    {
                        // first settings (only what was checked): from now on everything is allowed by default
                        _store.All = true; _store.AllowAddresses = true;
                        _store.Folders = new(); _store.ExcludedFolders = new(); _store.Servers = new(); _store.Excluded = new();
                        _store.Version = 2;
                        SaveLocked();
                    }
                    _store.Folders = new HashSet<string>(_store.Folders ?? new(), StringComparer.OrdinalIgnoreCase);
                    _store.ExcludedFolders = new HashSet<string>(_store.ExcludedFolders ?? new(), StringComparer.OrdinalIgnoreCase);
                    _store.Servers ??= new(); _store.Excluded ??= new();
                    if (string.IsNullOrEmpty(_store.Token)) { _store.Token = NewToken(); SaveLocked(); }
                    return _store;
                }
            }
        }

        /// <summary>saves the settings after a change made from the Claude page</summary>
        public static void Save() { lock (Lock) SaveLocked(); }
        private static void SaveLocked() { if (_store != null) AppDbStore.Set(DbKey, JsonConvert.SerializeObject(_store)); }

        private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>new token: Claude must be reconnected with the new command</summary>
        public static void RegenerateToken() { lock (Lock) { Settings.Token = NewToken(); SaveLocked(); } Log("access token regenerated"); }

        /// <summary>token required on every call; for tests it can be forced with LEMORA_MCP_TOKEN</summary>
        public static string Token => Lemora.Utils.DevOnly.Env("LEMORA_MCP_TOKEN") is { Length: >= 16 } t ? t : Settings.Token;   // forced token: development builds only

        public static bool IsStopped => _stopped;

        // ---------------------------------------------------------------- pause and stop of a single session (panel inside the session window)

        /// <summary>paused servers (id): the user took control; Claude waits until the user presses Resume</summary>
        private static readonly HashSet<string> Paused = new();
        /// <summary>servers (id) taken away from Claude with Stop, until Lemora closes</summary>
        private static readonly HashSet<string> Blocked = new();

        /// <summary>what happened to a session by the user's hand (pause, resume, stop): McpTools adds it to the news for Claude</summary>
        public static event Action<ProtocolBase, string>? UserAction;

        public const string PausedReason = "the user paused Claude on this session and took control: do nothing here until the user presses \"Resume\" (\"Riprendi\" in Italian) " +
                                           "in the session window (ask in the chat whether you should wait)";
        public const string BlockedReason = "the user took this session away from Claude (Stop): you can no longer use this PC while Lemora stays open. Ask the user in the chat";

        public static string DefaultTextActive => IoC.Translate("mcp_text_active_default");
        public static string DefaultTextPaused => IoC.Translate("mcp_text_paused_default");
        public static string TextActive => string.IsNullOrWhiteSpace(Settings.ControlsTextActive) ? DefaultTextActive : Settings.ControlsTextActive!;
        public static string TextPaused => string.IsNullOrWhiteSpace(Settings.ControlsTextPaused) ? DefaultTextPaused : Settings.ControlsTextPaused!;
        /// <summary>the texts changed in the settings: the open panels update right away</summary>
        public static event Action? TextsChanged;
        public static void SetTexts(string? active, string? paused)
        {
            var s = Settings;
            s.ControlsTextActive = string.IsNullOrWhiteSpace(active) ? null : active.Trim();
            s.ControlsTextPaused = string.IsNullOrWhiteSpace(paused) ? null : paused.Trim();
            Save();
            TextsChanged?.Invoke();
        }

        /// <summary>
        /// pause and stop keys of a session: always its id and, when it has address and port, also 'protocol|address|port'.
        /// So stopping or pausing a saved entry also applies to a session opened by address on the same PC, and vice versa
        /// (previously the saved entry used the id and the unsaved address a different key: after Stop, apri_indirizzo on the same PC opened a free session).
        /// </summary>
        /// <summary>
        /// all 'protocol|address|port' keys of an entry: main address and alternate addresses, each one also with the short name only
        /// (pc39.acme.local and pc39 are the same PC; trailing dot removed). Security: with the main address only, apri_indirizzo on an
        /// alternate address bypassed exclusion and Stop. Two PCs of different customers with the same short name (srv.a.local, srv.b.local) are
        /// treated as the same: Stop or Pause on one applies to the other too (more cautious, never less).
        /// The address is normalized first (<see cref="HostForms"/>): localhost, 127.x.x.x, ::1, 2130706433, 0x7f000001 and this PC's own IPs are all "loopback"
        /// (the external test bypassed Stop and exclusions on 127.0.0.1 with localhost or 127.0.0.2). IP and name of the same remote PC are matched by
        /// <see cref="Aliases"/> (DNS), not here: this runs for every saved entry and must stay fast
        /// </summary>
        internal static HashSet<string> AddrKeys(ProtocolBase s)
        {
            var keys = new HashSet<string>();
            if (s is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort a) return keys;
            void Add(string? addr, string port)
            {
                foreach (var h in HostForms(addr))
                {
                    keys.Add($"{s.Protocol}|{h}|{port}");
                    // also the PC alone, without protocol or port: Stop/Pause on RDP apply even if Claude comes back to the same PC with VNC or SSH,
                    // and an entry's exclusion applies to apri_indirizzo with another protocol (security hardening)
                    keys.Add($"host|{h}");
                }
            }
            var mainPort = a.GetPort().ToString();
            Add(a.Address, mainPort);
            foreach (var c in a.AlternateCredentials)
                Add(string.IsNullOrWhiteSpace(c.Address) ? a.Address : c.Address, string.IsNullOrWhiteSpace(c.Port) ? mainPort : c.Port.Trim());
            return keys;
        }

        /// <summary>
        /// the forms of an address that mean the same PC: an IP in its standard writing (any notation .NET accepts: 127.1, 2130706433, 0x7f000001, [::1]),
        /// "loopback" for this PC (localhost, 127/8, ::1, 0.0.0.0, its own IPs and name), a name lowercase without trailing dot plus its short name
        /// </summary>
        internal static IEnumerable<string> HostForms(string? addr)
        {
            var h = (addr ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
            if (h.Length == 0) yield break;
            if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) { yield return "loopback"; yield break; }
            if (System.Net.IPAddress.TryParse(h, out var ip))
            {
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                yield return IsThisPc(ip) ? "loopback" : ip.ToString();
                yield break;
            }
            yield return h;
            var shortName = h.IndexOf('.') > 0 ? h.Substring(0, h.IndexOf('.')) : h;
            if (shortName != h) yield return shortName;
            if (shortName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) yield return "loopback";
        }

        private static bool IsThisPc(System.Net.IPAddress ip)
            => System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any) || LocalIps().Contains(ip);

        private static (DateTime at, HashSet<System.Net.IPAddress> ips) _localIps = (DateTime.MinValue, new());
        /// <summary>this PC's own addresses (its LAN IP reaches it like 127.0.0.1); re-read every minute: network cards and VPNs change</summary>
        private static HashSet<System.Net.IPAddress> LocalIps()
        {
            var c = _localIps;
            if (DateTime.UtcNow - c.at < TimeSpan.FromMinutes(1)) return c.ips;
            var ips = new HashSet<System.Net.IPAddress>();
            try
            {
                foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in n.GetIPProperties().UnicastAddresses) ips.Add(u.Address.IsIPv4MappedToIPv6 ? u.Address.MapToIPv4() : u.Address);
            }
            catch { /* no network information: only the loopback forms */ }
            _localIps = (DateTime.UtcNow, ips);
            return ips;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, HashSet<string> forms)> AliasCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// the other names of the same remote PC, from DNS: the IPs of a name and the name (PTR) of an IP. So Stop on "pc39" also blocks 192.168.1.39 and
        /// an entry excluded by IP also excludes apri_indirizzo by name (and vice versa when the IP has a PTR record). Only for the security keys, never to connect.
        /// shortcut: the answers are kept for the life of Lemora and re-asked after 5 minutes (an old answer only blocks more, never less); on the UI thread
        /// only what is already known is used (no DNS wait in the interface)
        /// </summary>
        internal static HashSet<string> Aliases(ProtocolBase s)
        {
            var forms = new HashSet<string>();
            if (s is not ProtocolBaseWithAddressPort a) return forms;
            foreach (var addr in new[] { a.Address }.Concat(a.AlternateCredentials.Select(c => c.Address)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
            {
                var key = addr.Trim();
                var cached = AliasCache.TryGetValue(key, out var c);
                var onUi = System.Windows.Application.Current?.Dispatcher.CheckAccess() == true;
                if (!onUi && (!cached || DateTime.UtcNow - c.at > TimeSpan.FromMinutes(5)))
                {
                    var found = Resolve(key);
                    if (cached) found.UnionWith(c.forms);   // more cautious: an old answer stays
                    AliasCache[key] = c = (DateTime.UtcNow, found);
                }
                if (AliasCache.TryGetValue(key, out c)) forms.UnionWith(c.forms);
            }
            return forms;
        }

        private static HashSet<string> Resolve(string addr)
        {
            var forms = new HashSet<string>();
            var first = HostForms(addr).FirstOrDefault();
            if (first is null or "loopback") return forms;   // this PC: already one key
            try
            {
                if (System.Net.IPAddress.TryParse(first, out var ip))
                {
                    var t = System.Net.Dns.GetHostEntryAsync(ip);
                    if (t.Wait(1500)) { forms.UnionWith(HostForms(t.Result.HostName)); foreach (var x in t.Result.Aliases) forms.UnionWith(HostForms(x)); }
                }
                else
                {
                    var t = System.Net.Dns.GetHostAddressesAsync(addr.Trim().TrimEnd('.'));
                    if (t.Wait(1500)) foreach (var x in t.Result) forms.UnionWith(HostForms(x.ToString()));
                }
            }
            catch { /* name not known or no PTR: only the written forms */ }
            return forms;
        }

        private static List<string> Keys(ProtocolBase s)
        {
            var keys = new List<string> { s.Id };
            keys.AddRange(AddrKeys(s));
            keys.AddRange(Aliases(s).Select(f => "host|" + f));
            return keys;
        }

        public static bool IsPaused(ProtocolBase s) { var keys = Keys(s); lock (Lock) return keys.Any(Paused.Contains); }
        public static bool IsBlocked(ProtocolBase s) { var keys = Keys(s); lock (Lock) return keys.Any(Blocked.Contains); }

        public static void SetPaused(ProtocolBase s, bool on)
        {
            var keys = Keys(s);
            lock (Lock) foreach (var k in keys) { if (on) Paused.Add(k); else Paused.Remove(k); }
            Log($"{(on ? "paused by the user" : "resumed by the user")} on {ActivityLog.Describe(s)}");
            UserAction?.Invoke(s, on ? "paused: the user took control" : "resumed: the user gives you control back, look at the screen before continuing");
        }

        public static void Block(ProtocolBase s)
        {
            var keys = Keys(s);
            lock (Lock) foreach (var k in keys) { Blocked.Add(k); Paused.Remove(k); }
            Log($"taken away by the user on {ActivityLog.Describe(s)} (Stop)");
            UserAction?.Invoke(s, "blocked: the user took this session away from you (Stop)");
            // a running script ends too: it was going on with other PCs after the user had said Stop (external test)
            McpTools.StopScripts(ScriptStoppedBySessionStop);
        }

        public const string ScriptStoppedBySessionStop = "the user took a session away from Claude (Stop) while the script was running: ask the user in the chat before going on";

        public static void Log(string message) => ActivityLog.Write("Claude: " + message);

        // ---------------------------------------------------------------- rules

        /// <summary>
        /// The rule for a server: first the Teams administrator (total block or shared entry not allowed), then the user's choices.
        /// Returns null if Claude can use it, otherwise the reason (to show to Claude and on the page).
        /// </summary>
        public static string? Denied(ProtocolBase server, TeamsClaudePolicy.Snapshot? teams = null)
        {
            teams ??= TeamsClaudePolicy.Current();
            if (teams.BlocksAll) return TeamsClaudePolicy.BlockedAllReason;
            if (teams.Blocks(server)) return "the Teams administrator does not allow Claude to use this connection";
            var s = Settings;
            if (server is Lemora.Model.Protocol.VNC && !s.AllowVnc)
                return "control of VNC connections is not enabled (Lemora settings > MCP Server)";
            if (server is Lemora.Model.Protocol.VNC && !server.IsTmpSession()) return null;   // saved VNC entries depend only on the switch
            if (server.IsTmpSession())
            {
                if (!s.AllowAddresses) return "opening unsaved addresses is not allowed (Lemora settings > MCP Server > unsaved addresses)";
                // same PC as a saved entry (same protocol, address, port): its rules apply too (exclusion, Teams),
                // otherwise apri_indirizzo would bypass that entry's exclusion or Teams ban
                var keys = AddrKeys(server);
                keys.UnionWith(Aliases(server).Select(f => "host|" + f));   // the same PC under another name or IP (DNS)
                foreach (var saved in IoC.Get<Lemora.Model.GlobalData>().VmItemList.Select(v => v.Server).Where(x => !x.IsTmpSession() && AddrKeys(x).Overlaps(keys)))
                    if (Denied(saved, teams) is { } why) return why;
                return null;
            }
            return ServerAllowed(server) ? null : "this connection is excluded for Claude (Lemora settings > MCP Server)";
        }

        /// <summary>the explicit choice of the closest folder (from the innermost outward); null = none, "All connections" applies</summary>
        private static bool? FolderDecision(IReadOnlyList<string>? treeNodes)
        {
            if (treeNodes == null) return null;
            var s = Settings;
            for (var n = treeNodes.Count; n >= 1; n--)
            {
                var path = string.Join("/", treeNodes.Take(n));
                if (s.Folders.Contains(path)) return true;
                if (s.ExcludedFolders.Contains(path)) return false;
            }
            return null;
        }

        /// <summary>a folder (or the direct contents of a folder) is allowed</summary>
        public static bool FolderAllowed(IReadOnlyList<string>? treeNodes) => FolderDecision(treeNodes) ?? Settings.All;

        /// <summary>explicit choice on the server, otherwise that of its folder</summary>
        public static bool ServerAllowed(ProtocolBase server)
        {
            var s = Settings;
            if (s.Excluded.Contains(server.Id)) return false;
            if (s.Servers.Contains(server.Id)) return true;
            return FolderAllowed(server.TreeNodes);
        }

        public const string StoppedReason = "Claude was stopped by the user in Lemora: ask the user to press \"Resume Claude\" (\"Riattiva\" in Italian) in Lemora's panel";

        /// <summary>
        /// Quick check to repeat at every step of long tools (paste, type, read clipboard): null = can continue,
        /// otherwise the reason ("Stop Claude", Stop or Pause pressed in the meantime). Previously it was checked only at the start of the tool
        /// (security hardening: after Pause keys and Enter still arrived).
        /// </summary>
        public static string? Halted(ProtocolBase s) => _stopped ? StoppedReason : IsBlocked(s) ? BlockedReason : IsPaused(s) ? PausedReason : null;

        /// <summary>every action on a device goes through here: rules, "Stop Claude" and always-visible panel</summary>
        public static Task<(bool ok, string why)> EnsureAllowed(ProtocolBase server, string action)
        {
            if (_stopped) return Task.FromResult((false, StoppedReason));
            if (IsBlocked(server)) return Task.FromResult((false, BlockedReason));
            if (IsPaused(server)) return Task.FromResult((false, PausedReason));
            var why = Denied(server);
            if (why != null)
            {
                Log($"{action} on {ActivityLog.Describe(server)} not allowed: {why}");
                return Task.FromResult((false, why));
            }
            return Task.FromResult((true, ""));   // the panel appears only after a successful connection (ShowBanner from McpTools), not on every request
        }

        /// <summary>"Stop Claude": no action gets through until the user presses Resume Claude</summary>
        public static void Stop()
        {
            _stopped = true;
            Log("stopped by the user");
            McpTools.StopScripts(StoppedReason);
            ShowBanner();
            TextsChanged?.Invoke();   // the panels inside the sessions say "Claude is stopped" (they still said "Claude is using this session")
        }

        private static void Resume()
        {
            _stopped = false;
            Log("resumed by the user");
            ShowBanner();
            TextsChanged?.Invoke();
        }

        /// <summary>
        /// panel visible while Claude has a connected RDP/VNC session: it says what is happening and lets the user stop it. At the top center of the screen
        /// (like screen-sharing notices): at the bottom right it covered the Teams bar. Solid background, never transparent.
        /// </summary>
        /// <summary>
        /// the panel is needed when Lemora is not in front (the user works in another program while Claude drives their PCs): with Lemora in the foreground
        /// it disappears, the controls are inside the session window. It always stays if Claude is stopped, so it can be resumed
        /// </summary>
        private static bool _wanted, _hooked;

        /// <summary>Lemora is in the foreground: the Activated/Deactivated events tell (during Deactivated the windows' IsActive may still be true)</summary>
        private static bool _appActive = true;

        private static void ApplyBanner()
        {
            if (_banner == null) return;
            var appActive = _appActive;
            // also while Claude is opening a connection, even with Lemora in front: the Pause/Stop strip exists only on a connected session,
            // and during a long connection (20-60 s) the user had no way to stop Claude (fifth external test)
            if (_wanted && Settings.BannerEnabled && (!appActive || _stopped || _opening > 0)) { if (!_banner.IsVisible) _banner.Show(); }
            else if (_banner.IsVisible) _banner.Hide();
        }

        private static int _opening;

        /// <summary>Claude starts (true) or ends (false) opening a connection: the "Stop Claude" panel is shown meanwhile</summary>
        internal static void Opening(bool on)
        {
            Interlocked.Add(ref _opening, on ? 1 : -1);
            if (on) ShowBanner();
            else Execute.OnUIThread(() => { try { ApplyBanner(); } catch { /* closing */ } });
        }

        public static void ShowBanner()
        {
            if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") == "1") return;
            Execute.OnUIThread(() =>
            {
                try
                {
                    _wanted = true;
                    if (!_hooked && System.Windows.Application.Current is { } app)
                    {
                        _hooked = true;
                        // Activated/Deactivated: Lemora comes to the foreground or goes behind another program (a detached session window counts too)
                        _appActive = app.Windows.Cast<Window>().Any(w => w.IsActive);
                        // if the only active window is the panel itself (clicked) Lemora is not "in front": the panel must stay
                        app.Activated += (_, _) => { _appActive = app.Windows.Cast<Window>().Any(w => w.IsActive && !ReferenceEquals(w, _banner)) || _banner?.IsActive != true; ApplyBanner(); };
                        app.Deactivated += (_, _) => { _appActive = false; ApplyBanner(); };
                    }
                    if (_banner == null)
                    {
                        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontSize = 13 };
                        text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
                        var button = new Button { Padding = new Thickness(12, 4, 12, 4), MinWidth = 90 };
                        button.Click += (_, _) => { if (_stopped) Resume(); else Stop(); };
                        var grip = new TextBlock { Text = "⠿", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Opacity = 0.6 };
                        grip.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
                        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { grip, text, button } };
                        var border = new Border { Padding = new Thickness(10, 10, 10, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel,
                                                  Cursor = System.Windows.Input.Cursors.SizeAll, ToolTip = IoC.Translate("mcp_drag_to_move") };
                        border.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
                        border.SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush");
                        button.Cursor = System.Windows.Input.Cursors.Arrow;
                        // movable by hand (DragMove would activate the window): on release the position goes into the encrypted database
                        System.Windows.Point grab = default; var dragging = false;
                        border.MouseLeftButtonDown += (_, e) =>
                        {
                            for (var d = e.OriginalSource as DependencyObject; d != null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                                if (d is Button) return;
                            grab = e.GetPosition(border);
                            dragging = border.CaptureMouse();
                            e.Handled = true;
                        };
                        border.MouseMove += (_, e) =>
                        {
                            if (!dragging || _banner == null) return;
                            var p = border.PointToScreen(e.GetPosition(border));
                            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(border);
                            _banner.Left = p.X / dpi.DpiScaleX - grab.X;
                            _banner.Top = p.Y / dpi.DpiScaleY - grab.Y;
                        };
                        border.MouseLeftButtonUp += (_, _) =>
                        {
                            if (!dragging || _banner == null) return;
                            dragging = false;
                            border.ReleaseMouseCapture();
                            Settings.BannerLeft = _banner.Left; Settings.BannerTop = _banner.Top;
                            Save();
                        };
                        _banner = new Window
                        {
                            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,   // only the rounded corners are transparent
                            Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                            SizeToContent = SizeToContent.WidthAndHeight, Content = border, Title = "Claude",
                        };
                        _banner.Tag = (text, button);
                        // WS_EX_NOACTIVATE: pressing "Stop Claude" does not bring Lemora to the foreground (otherwise the panel would vanish under the finger)
                        _banner.SourceInitialized += (_, _) =>
                        {
                            var hwnd = new System.Windows.Interop.WindowInteropHelper(_banner).Handle;
                            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000);
                            // and WM_MOUSEACTIVATE -> MA_NOACTIVATE: on click the window is not activated (tested: clicking it made the panel disappear)
                            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                            {
                                if (msg != 0x21) return IntPtr.Zero;
                                handled = true;
                                return (IntPtr)3;
                            });
                        };
                        _banner.Loaded += (_, _) =>
                        {
                            // where the user left it, if it is still on a screen; otherwise top center
                            var s = Settings;
                            if (s.BannerLeft is { } x && s.BannerTop is { } y &&
                                x >= SystemParameters.VirtualScreenLeft && y >= SystemParameters.VirtualScreenTop &&
                                x + 40 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && y + 20 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                            {
                                _banner.Left = x; _banner.Top = y;
                                return;
                            }
                            var area = SystemParameters.WorkArea;
                            _banner.Left = area.Left + (area.Width - _banner.ActualWidth) / 2;
                            _banner.Top = area.Top + 8;
                        };
                    }
                    var (t, b) = ((TextBlock, Button))_banner.Tag;
                    t.Text = IoC.Translate(_stopped ? "mcp_banner_stopped" : "mcp_banner_active");
                    b.Content = IoC.Translate(_stopped ? "mcp_banner_resume" : "mcp_banner_stop");
                    ApplyBanner();
                }
                catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("MCP: \"Stop Claude\" panel not shown: " + e.Message); }
            });
        }

        // ---------------------------------------------------------------- sessions in Claude's hands (yellow tab with "AI", border and cursor on screen)

        private static HashSet<string> _claudeIds = new();

        /// <summary>the list of Claude's sessions changed (on the UI thread)</summary>
        public static event Action? ClaudeSessionsChanged;

        public static bool IsClaude(string connectionId) { lock (Lock) return _claudeIds.Contains(connectionId); }

        /// <summary>the connected sessions Claude is using: marks the tabs and puts border and cursor over the remote screen</summary>
        internal static void SetClaudeHosts(IReadOnlyCollection<Lemora.View.Host.ProtocolHosts.HostBase> hosts)
        {
            var ids = hosts.Select(h => h.ConnectionId).ToHashSet();
            lock (Lock)
            {
                if (ids.SetEquals(_claudeIds)) return;
                _claudeIds = ids;
            }
            Shawn.Utils.SimpleLogHelper.Warning($"MCP: Claude sessions now {ids.Count}");   // the marks follow this list (tests read it)
            Execute.OnUIThread(() =>
            {
                try { ClaudeSessionsChanged?.Invoke(); McpOverlay.Sync(hosts); }
                catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("MCP: Claude session marks not updated: " + e.Message); }
            });
        }

        /// <summary>no Claude session connected (or server off): the panel disappears, unless Claude is stopped (it stays so it can be resumed)</summary>
        /// <summary>switch on the MCP Server page: the panel appears or disappears right away</summary>
        public static void SetBannerEnabled(bool on)
        {
            Settings.BannerEnabled = on;
            Save();
            Log(on ? "\"Stop Claude\" panel turned on" : "\"Stop Claude\" panel turned off");
            Execute.OnUIThread(ApplyBanner);
        }

        public static void HideBanner(bool evenIfStopped = false)
        {
            if (evenIfStopped) SetClaudeHosts(Array.Empty<Lemora.View.Host.ProtocolHosts.HostBase>());
            if (_stopped && !evenIfStopped) return;
            Execute.OnUIThread(() => { try { _wanted = false; _banner?.Hide(); } catch { /* already closed */ } });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
