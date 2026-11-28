using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Renci.SshNet;
using Renci.SshNet.Common;
using Lemora.Model.Protocol;
using Lemora.Service;
using Shawn.Utils;
using Stylet;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// Built-in SSH terminal: xterm.js inside a WebView2 (page and scripts embedded in the executable) and SSH.NET for the connection.
    /// The WebView2 writes its cache to its own temporary folder, deleted on close.
    /// </summary>
    public class TerminalHost : HostBase
    {
        private readonly SSH _ssh;
        private readonly WebView2 _web = new();
        private SshClient? _client;
        private ShellStream? _shell;
        private int _cols = 80, _rows = 24;
        private bool _started, _closing, _shellIntegration;
        private readonly TaskCompletionSource<bool> _ready = new();

        /// <summary>SHA256 fingerprint of the server (shown in the session bar padlock)</summary>
        public string Fingerprint { get; private set; } = "";
        public bool IsHistoryOpen { get; private set; }
        public event Action? HistoryChanged;
        public void ToggleHistory() => SendToPage(new { t = "histToggle" });
        public void ShowSearch() => SendToPage(new { t = "search" });

        /// <summary>opens a link written in the terminal: http/https only, with confirmation (can be turned off in the settings)</summary>
        private void OpenLink(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
            if (IsDemo) { LastOpenedLink = u.AbsoluteUri; return; }
            if (TerminalSettings.Current.ConfirmLinks && !Lemora.Utils.MessageBoxHelper.Confirm(string.Format(IoC.Translate("term_open_link_question"), u.AbsoluteUri), IoC.Translate("term_open"), IoC.Translate("Cancel"), IoC.Translate("term_open_link_title"))) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception e) { SimpleLogHelper.Warning("Terminal: link " + e.Message); }
        }
        internal string LastOpenedLink = "";

        /// <summary>last snapshot of the page state (automatic test)</summary>
        internal string LastDump = "";
        internal event Action<JsonElement>? MessageFromPage;

        private static bool _cleaned;

        public static TerminalHost Create(SSH ssh)
        {
            TerminalHost? v = null;
            if (!_cleaned) { _cleaned = true; CleanOldDataFolders(); }   // folders left by processes already closed
            Execute.OnUIThreadSync(() => v = new TerminalHost(ssh));
            return v!;
        }

        private TerminalHost(SSH ssh) : base(ssh, false)
        {
            _ssh = ssh;
            Focusable = true;
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x10, 0x10);
            _web.CreationProperties = CreateProps();
            Content = _web;   // no GotFocus handler: the page focus bubbles up to the host and sent "focus" back to the page endlessly
            TerminalSettings.Changed += OnSettingsChanged;
        }

        // ---------- WebView2 data folder: temporary, one per process, cleaned up on close and on the next startup
        private static string Root => Path.Combine(Path.GetTempPath(), "Lemora-terminal");
        private static string DataFolder() => Path.Combine(Root, Environment.ProcessId.ToString());

        /// <summary>same options for all terminal WebView2s (sessions and settings preview): they must match to share the same environment</summary>
        internal static CoreWebView2CreationProperties CreateProps() => new() { UserDataFolder = DataFolder(), AdditionalBrowserArguments = "--disable-features=msWebOOUI,msPdfOOUI" };

        private void OnSettingsChanged() => Execute.OnUIThread(() => SendToPage(new { t = "settings", s = TerminalSettings.Current.ToPage() }));

        internal static void CleanOldDataFolders()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    if (int.TryParse(Path.GetFileName(d), out var pid))
                    {
                        try { System.Diagnostics.Process.GetProcessById(pid); continue; } catch { /* the process is gone */ }
                    }
                    try { Directory.Delete(d, true); } catch { /* still in use */ }
                }
            }
            catch (Exception e) { SimpleLogHelper.Warning(e.Message); }
        }

        internal static string BuildHtml()
        {
            static string Res(string n)
            {
                using var s = typeof(TerminalHost).Assembly.GetManifestResourceStream("Terminal." + n) ?? throw new FileNotFoundException(n);
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd();
            }
            // string.Replace with a plain string: the script text is never interpreted
            return Res("terminal.html")
                .Replace("/*XTERM_CSS*/", Res("xterm.css"))
                .Replace("/*XTERM_JS*/", Res("xterm.js"))
                .Replace("/*FIT_JS*/", Res("addon-fit.js"))
                .Replace("/*SER_JS*/", Res("addon-serialize.js"))
                .Replace("/*SEARCH_JS*/", Res("addon-search.js"))
                .Replace("/*LINKS_JS*/", Res("addon-web-links.js"))
                .Replace("/*APP_JS*/", Res("terminal.js"));
        }

        // ---------- HostBase
        public override void Conn()
        {
            Status = ProtocolHostStatus.Connecting;
            if (_started) return;
            _started = true;
            if (IsLoaded) _ = StartAsync();
            else Loaded += async (_, _) => { if (_client == null && !_closing) await StartAsync(); };
        }

        public override void ReConn()
        {
            if (_closing) return;
            DisposeSsh();
            Status = ProtocolHostStatus.Connecting;
            _ = ConnectSshAsync();
        }

        public override void Close()
        {
            _closing = true;
            TerminalSettings.Changed -= OnSettingsChanged;
            DisposeSsh();
            try { _web.Dispose(); } catch { /* already closed */ }
            ActivityReason ??= "closed by the user";   // compared by the MCP server (McpTools): keep this exact value
            Status = ProtocolHostStatus.Disconnected;
            base.Close();
        }

        public override void FocusOnMe()
        {
            Execute.OnUIThread(() =>
            {
                try { _web.Focus(); _web.CoreWebView2?.PostWebMessageAsJson("{\"t\":\"focus\"}"); } catch { /* not ready yet */ }
            });
        }

        public override ProtocolHostType GetProtocolHostType() => ProtocolHostType.Native;
        public override IntPtr GetHostHwnd() => IntPtr.Zero;

        // ---------- page
        private async Task StartAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async();
                var s = _web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsGeneralAutofillEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                // there is only one page and it cannot navigate elsewhere or open other windows
                _web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true; };
                _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                _web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                _web.CoreWebView2.WebMessageReceived += OnMessage;
                if (Lemora.Utils.DevOnly.Env("LEMORA_DEMO") != null)   // tests only: the page console into the log
                {
                    await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                    await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Debugger.enable", "{}");
                    _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Debugger.paused").DevToolsProtocolEventReceived += (_, a) =>
                    {
                        using var d = JsonDocument.Parse(a.ParameterObjectAsJson);
                        var fr = new List<string>();
                        foreach (var f in d.RootElement.GetProperty("callFrames").EnumerateArray().Take(10))
                            fr.Add($"{f.GetProperty("functionName").GetString()}@{f.GetProperty("location").GetProperty("lineNumber").GetInt32()}:{f.GetProperty("location").GetProperty("columnNumber").GetInt32()}");
                        _pausedStack.TrySetResult(string.Join(" < ", fr));
                    };
                    _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, a) => SimpleLogHelper.Warning("Terminal console: " + a.ParameterObjectAsJson);
                }
                _web.NavigateToString(BuildHtml());
                await _ready.Task;
                await ConnectSshAsync();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                ActivityReason = "terminal not started: " + e.Message;
                Status = ProtocolHostStatus.Disconnected;
                Lemora.Utils.MessageBoxHelper.ErrorAlert(string.Format(IoC.Translate("term_error"), e.Message));
            }
        }

        private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "ready":
                    _cols = m.GetProperty("cols").GetInt32(); _rows = m.GetProperty("rows").GetInt32();
                    _shellIntegration = TerminalSettings.Current.ShellIntegration;     // read here, on the UI thread: the connection runs on another one
                    SendToPage(new { t = "settings", s = TerminalSettings.Current.ToPage() });
                    _ready.TrySetResult(true);
                    break;
                case "openSettings":
                    TerminalSettingsWindow.ShowSingle(Window.GetWindow(this));
                    break;
                case "size":
                    _cols = m.GetProperty("cols").GetInt32(); _rows = m.GetProperty("rows").GetInt32();
                    try { _shell?.ChangeWindowSize((uint)_cols, (uint)_rows, 0, 0); } catch { /* session closed */ }
                    break;
                case "in":
                    LastInput = m.GetProperty("d").GetString() ?? "";
                    try { _shell?.Write(LastInput); } catch { /* session closed */ }
                    break;
                case "paste":
                    {
                        string txt;
                        if (IsDemo) txt = DemoPasteText;                       // in tests the user's real clipboard is not touched
                        else { try { txt = Clipboard.GetText() ?? ""; } catch { txt = ""; } }
                        // pasting several lines into a shell runs them one after another: ask for confirmation (can be turned off in the settings).
                        // Security: even a single trailing newline or a lone CR counts as Enter, and an end-of-paste marker (ESC[201~)
                        // inside the text would exit the shell's "paste" mode: the markers are stripped, all the Enters are counted
                        txt = StripPasteMarkers(txt);
                        var enters = CountEnters(txt);
                        if (enters > 0 && TerminalSettings.Current.WarnMultiLinePaste && !IsDemo
                            && !Lemora.Utils.MessageBoxHelper.Confirm(enters == 1 && txt.TrimEnd('\r', '\n').IndexOfAny(new[] { '\r', '\n' }) < 0
                                    ? IoC.Translate("term_paste_trailing_newline")
                                    : string.Format(IoC.Translate("term_paste_lines"), enters + (txt.EndsWith('\n') || txt.EndsWith('\r') ? 0 : 1)),
                                    IoC.Translate("term_paste"), IoC.Translate("Cancel"), IoC.Translate("term_paste_lines_title")))
                            break;
                        if (txt.Length > 0) SendToPage(new { t = "pasteText", text = txt });
                        break;
                    }
                case "link":
                    OpenLink(m.GetProperty("uri").GetString() ?? "");
                    break;
                case "beep":
                    System.Media.SystemSounds.Beep.Play();
                    break;
                case "sel":
                    _sel.TrySetResult(m.GetProperty("text").GetString() ?? "");
                    break;
                case "dump":
                    LastDump = m.GetRawText();
                    break;
                case "histState":
                    IsHistoryOpen = m.GetProperty("on").GetBoolean();
                    HistoryChanged?.Invoke();
                    break;
                case "err":
                    SimpleLogHelper.Warning("Terminal (page): " + m.GetProperty("msg").GetString());
                    break;
                case "copy":
                    CopyToClipboard(m.GetProperty("text").GetString() ?? "", m.TryGetProperty("html", out var h) ? h.GetString() : null);
                    break;
                case "save":
                    SaveOutput(m.GetProperty("name").GetString() ?? "output.txt", m.GetProperty("text").GetString() ?? "");
                    break;
            }
            MessageFromPage?.Invoke(m.Clone());
        }

        // ---------- clipboard and saving a command's output
        internal static string? LastClipboardText;   // automatic tests: what was copied

        private static bool IsDemo => Lemora.Utils.DevOnly.Env("LEMORA_DEMO") != null;
        internal static string DemoPasteText = "echo pasted-by-test";
        internal string LastInput = "";
        private TaskCompletionSource<string> _sel = new();

        private static void CopyToClipboard(string text, string? html)
        {
            LastClipboardText = text;
            if (IsDemo) return;   // in tests the user's real clipboard is not touched
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                if (!string.IsNullOrEmpty(html)) data.SetData(DataFormats.Html, CfHtml(html));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception e) { SimpleLogHelper.Warning("Terminal: clipboard " + e.Message); }
        }

        /// <summary>Windows clipboard "HTML Format": header with the byte offsets of the fragment</summary>
        internal static string CfHtml(string fragment)
        {
            const string head = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string pre = "<html><body><!--StartFragment-->", post = "<!--EndFragment--></body></html>";
            var headLen = string.Format(head, 0, 0, 0, 0).Length;
            var startHtml = headLen;
            var startFrag = startHtml + Encoding.UTF8.GetByteCount(pre);
            var endFrag = startFrag + Encoding.UTF8.GetByteCount(fragment);
            var endHtml = endFrag + Encoding.UTF8.GetByteCount(post);
            return string.Format(head, startHtml, endHtml, startFrag, endFrag) + pre + fragment + post;
        }

        private void SaveOutput(string name, string text)
        {
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var dir = Path.Combine(docs, "Output terminale");   // existing folder name on users' PCs: keep it
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = name,
                    DefaultExt = ".txt",
                    Filter = IoC.Translate("term_save_filter"),
                    InitialDirectory = Directory.Exists(dir) ? dir : docs,   // the dedicated folder is used if it exists: it is not created automatically
                    OverwritePrompt = true,
                };
                if (dlg.ShowDialog(Window.GetWindow(this)) == true)
                    File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            }
            catch (Exception e) { Lemora.Utils.MessageBoxHelper.ErrorAlert(e.Message); }
        }

        // ---------- helpers for automatic tests (DevDemo)
        internal void TestWrite(string s) { try { _shell?.Write(s); } catch { /* session closed */ } }
        internal bool TestIsConnected => _shell != null && _client?.IsConnected == true;
        // real browser events (debug protocol), without the user's mouse or keyboard
        private Task<string> Cdp(string method, string json) => _web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json);
        internal async Task<string> TestSelectDragAsync(int rowFromTop)
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify((()=>{const r=document.querySelector('.xterm-screen').getBoundingClientRect();return {l:r.left,t:r.top,w:r.width,h:r.height,rows:document.querySelectorAll('.xterm-rows>div').length}})())");
            using var doc = JsonDocument.Parse(JsonDocument.Parse(raw).RootElement.GetString()!);
            var g = doc.RootElement;
            double l = g.GetProperty("l").GetDouble(), t = g.GetProperty("t").GetDouble(), h = g.GetProperty("h").GetDouble(), rows = g.GetProperty("rows").GetInt32();
            double y = t + (rowFromTop + 0.5) * h / rows, x1 = l + 20, x2 = l + 220;
            string Ev(string type, double x, int buttons) => $"{{\"type\":\"{type}\",\"x\":{x.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"y\":{y.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"button\":\"left\",\"buttons\":{buttons},\"clickCount\":1}}";
            await Cdp("Input.dispatchMouseEvent", Ev("mousePressed", x1, 1));
            await Cdp("Input.dispatchMouseEvent", Ev("mouseMoved", (x1 + x2) / 2, 1));
            await Cdp("Input.dispatchMouseEvent", Ev("mouseMoved", x2, 1));
            await Cdp("Input.dispatchMouseEvent", Ev("mouseReleased", x2, 0));
            return await GetSelectionAsync();
        }
        internal async Task<string> TestEval(string js)
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(js);
            return raw.Trim('"');
        }
        internal async Task TestWheel(double deltaY)
        {
            var geo = await TestEval("(()=>{const r=document.querySelector('.xterm-screen').getBoundingClientRect();return Math.round(r.left+100)+','+Math.round(r.top+100)})()");
            var xy = geo.Split(',');
            await Cdp("Input.dispatchMouseEvent", $"{{\"type\":\"mouseWheel\",\"x\":{xy[0]},\"y\":{xy[1]},\"deltaX\":0,\"deltaY\":{deltaY.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        }
        internal async Task<string> GetSelectionAsync()
        {
            _sel = new TaskCompletionSource<string>();
            SendToPage(new { t = "getSel" });
            return await Task.WhenAny(_sel.Task, Task.Delay(3000)) == _sel.Task ? await _sel.Task : "(no reply)";
        }
        /// <summary>real key with Ctrl (and Shift): protocol modifiers = Ctrl 2, Shift 8</summary>
        internal async Task TestCtrlKeyAsync(string key, int vk, bool shift = false)
        {
            var mod = 2 + (shift ? 8 : 0);
            var ch = shift ? key.ToUpperInvariant() : key;
            await Cdp("Input.dispatchKeyEvent", $"{{\"type\":\"rawKeyDown\",\"modifiers\":{mod},\"key\":\"{ch}\",\"code\":\"Key{key.ToUpperInvariant()}\",\"windowsVirtualKeyCode\":{vk}}}");
            await Cdp("Input.dispatchKeyEvent", $"{{\"type\":\"keyUp\",\"modifiers\":{mod},\"key\":\"{ch}\",\"code\":\"Key{key.ToUpperInvariant()}\",\"windowsVirtualKeyCode\":{vk}}}");
        }

        internal async Task<string> TestProbe()
        {
            var r = _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({vis:document.visibilityState,decos:document.querySelectorAll('.xterm-decoration').length,rows:document.querySelectorAll('.xterm-rows>div').length,w:innerWidth,h:innerHeight})");
            if (await Task.WhenAny(r, Task.Delay(4000)) == r) return await r;
            // the page is stuck: pause it with the debugger (enabled at startup) and read where it is running
            _ = _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Debugger.pause", "{}");
            var stack = await Task.WhenAny(_pausedStack.Task, Task.Delay(6000)) == _pausedStack.Task ? await _pausedStack.Task : "(no reply from the debugger)";
            return "script not responding; stack: " + stack;
        }
        private readonly TaskCompletionSource<string> _pausedStack = new();

        internal async Task TestCapture(string path)
        {
            // browser debug protocol: does not depend on the visible window (the screenshot of a hidden window hung)
            var cap = _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
            if (await Task.WhenAny(cap, Task.Delay(8000)) != cap) throw new TimeoutException("screenshot not completed in 8 s");
            using var doc = JsonDocument.Parse(await cap);
            File.WriteAllBytes(path, Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!));
        }

        internal void SendToPage(object msg)
        {
            var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { IncludeFields = true });
            Execute.OnUIThread(() => { try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* page closed */ } });
        }

        // ---------- SSH
        private async Task ConnectSshAsync()
        {
            _hostKeyRefused = false;
            SendToPage(new { t = "msg", d = string.Format(IoC.Translate("term_connecting"), _ssh.Address, _ssh.GetPort()) });
            try
            {
                await Task.Run(() =>
                {
                    var user = _ssh.UserName ?? "";
                    var methods = new List<AuthenticationMethod>();
                    if (!string.IsNullOrEmpty(_ssh.PrivateKey) && File.Exists(_ssh.PrivateKey))
                        methods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(_ssh.PrivateKey, string.IsNullOrEmpty(_ssh.Password) ? null : _ssh.Password)));
                    if (!string.IsNullOrEmpty(_ssh.Password))
                    {
                        methods.Add(new PasswordAuthenticationMethod(user, _ssh.Password));
                        var ki = new KeyboardInteractiveAuthenticationMethod(user);
                        ki.AuthenticationPrompt += (_, a) => { foreach (var p in a.Prompts) p.Response = _ssh.Password; };
                        methods.Add(ki);
                    }
                    if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(user));

                    var info = new ConnectionInfo(_ssh.Address, _ssh.GetPort(), user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
                    var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                    // server fingerprint: first connection = ask and remember it (encrypted database), then compare; if it changes, warn
                    client.HostKeyReceived += (_, a) => { Fingerprint = a.FingerPrintSHA256; a.CanTrust = TrustHostKey(a.FingerPrintSHA256); };
                    client.ErrorOccurred += (_, a) => OnLost(a.Exception.Message);
                    client.Connect();
                    _client = client;
                    var shell = client.CreateShellStream("xterm-256color", (uint)_cols, (uint)_rows, 0, 0, 8192);
                    shell.DataReceived += (_, a) => SendToPage(new { t = "out", d = Convert.ToBase64String(a.Data) });
                    shell.Closed += (_, _) => OnLost(IoC.Translate("term_closed_by_server"));
                    _shell = shell;
                    if (_shellIntegration || Lemora.Utils.DevOnly.Env("LEMORA_TERM_INJECT") == "1") shell.Write(ShellIntegrationSnippet);
                    else if (!string.IsNullOrWhiteSpace(_ssh.StartupAutoCommand)) shell.WriteLine(_ssh.StartupAutoCommand);
                });
                Status = ProtocolHostStatus.Connected;
                FocusOnMe();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("Terminal: " + e.Message);
                SendToPage(new { t = "msg", d = _hostKeyRefused ? IoC.Translate("term_conn_cancelled_fingerprint") : string.Format(IoC.Translate("term_conn_failed"), e.Message) });
                ActivityReason = _hostKeyRefused ? "server fingerprint not confirmed" : $"{e.GetType().Name}: {e.Message}";
                Status = ProtocolHostStatus.Disconnected;
            }
        }

        /// <summary>strips the start/end-of-paste markers (ESC[200~ and ESC[201~): only the terminal adds them, never the copied text</summary>
        // no escape characters in the pasted text (ESC and 8-bit CSI): stripping only the end-of-paste markers in a single pass could be bypassed
        // by nesting them ("ESC[2" + "ESC[201~" + "01~" becomes "ESC[201~" again) and the shell ran the rest (security hardening)
        internal static string StripPasteMarkers(string s)
        {
            string prev;
            do
            {
                prev = s;
                s = s.Replace("\u001b[200~", "").Replace("\u001b[201~", "").Replace("\u009b200~", "").Replace("\u009b201~", "");
            } while (s != prev);
            // and no other control characters (C0 and C1) except tab and newline: Ctrl+O, Ctrl+J/M as characters, Ctrl+X Ctrl+E...
            // in shells without paste mode they run the line without counting as Enter (security hardening)
            s = s.Replace("\u001b", "").Replace("\u009b", "");
            return s.Any(c => (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n'))
                ? new string(s.Where(c => !char.IsControl(c) || c == '\t' || c == '\r' || c == '\n').ToArray())
                : s;
        }

        /// <summary>how many Enters reach the shell: CR+LF, lone CR and lone LF count one each</summary>
        internal static int CountEnters(string s)
        {
            var n = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\r') { n++; if (i + 1 < s.Length && s[i + 1] == '\n') i++; }
                else if (s[i] == '\n') n++;
            }
            return n;
        }

        private bool _hostKeyRefused;

        /// <summary>true if we can proceed with this server (fingerprint known and matching, or confirmed by the user)</summary>
        private bool TrustHostKey(string fingerprint)
        {
            if (IsDemo) return true;   // in tests no window opens and nothing is written to the database
            var ok = TerminalKnownHosts.Trust(_ssh.Address, _ssh.GetPort(), fingerprint,
                _ssh.IsTmpSession() || string.IsNullOrEmpty(_ssh.Id) ? null : _ssh.Id);   // saved entry: its fingerprint on its own
            _hostKeyRefused = !ok;
            return ok;
        }

        private void OnLost(string why)
        {
            if (_closing) return;
            SendToPage(new { t = "msg", d = why });
            Execute.OnUIThread(() => { if (Status == ProtocolHostStatus.Connected) { ActivityReason = why; Status = ProtocolHostStatus.Disconnected; } });
        }

        private void DisposeSsh()
        {
            var sh = _shell; var cl = _client;
            _shell = null; _client = null;
            try { sh?.Dispose(); } catch { }
            try { cl?.Disconnect(); cl?.Dispose(); } catch { }
        }

        /// <summary>
        /// Enables the command markers (OSC 133) in bash and zsh: a line with a leading space (it does not end up in the history) and then the screen is cleared.
        /// </summary>
        internal static string ShellIntegrationText => ShellIntegrationSnippet;
        private const string ShellIntegrationSnippet =
            " if [ -n \"$BASH_VERSION\" ]; then __1r_pc(){ printf '\\033]133;D;%s\\a\\n\\033]7;file://%s%s\\a\\033]133;A\\a' \"$?\" \"$HOSTNAME\" \"$PWD\"; }; PS0='\\033]133;C\\a'; PS1=\"$PS1\"'\\[\\033]133;B\\a\\]'; PROMPT_COMMAND=\"__1r_pc${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"; " +
            "elif [ -n \"$ZSH_VERSION\" ]; then precmd(){ printf '\\033]133;D;%s\\a\\n\\033]7;file://%s%s\\a\\033]133;A\\a' \"$?\" \"$HOST\" \"$PWD\"; }; preexec(){ printf '\\033]133;C\\a'; }; PS1=\"$PS1\"$'%{\\e]133;B\\a%}'; fi; clear\n";
    }
}
