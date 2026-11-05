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
    /// Terminale SSH integrato: xterm.js dentro una WebView2 (pagina e script incorporati nell'eseguibile) e SSH.NET per il collegamento.
    /// La WebView2 scrive la sua cache in una cartella temporanea propria, cancellata alla chiusura.
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

        /// <summary>impronta SHA256 del server (si vede nel lucchetto della barra sessione)</summary>
        public string Fingerprint { get; private set; } = "";
        public bool IsHistoryOpen { get; private set; }
        public event Action? HistoryChanged;
        public void ToggleHistory() => SendToPage(new { t = "histToggle" });
        public void ShowSearch() => SendToPage(new { t = "search" });

        /// <summary>apre un link scritto nel terminale: solo http/https, con conferma (si spegne nelle impostazioni)</summary>
        private void OpenLink(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
            if (IsDemo) { LastOpenedLink = u.AbsoluteUri; return; }
            if (TerminalSettings.Current.ConfirmLinks && !Lemora.Utils.MessageBoxHelper.Confirm($"Aprire questo indirizzo nel browser?\n\n{u.AbsoluteUri}", "Apri", "Annulla", "Apri link")) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception e) { SimpleLogHelper.Warning("Terminale: link " + e.Message); }
        }
        internal string LastOpenedLink = "";

        /// <summary>ultima fotografia dello stato della pagina (prova automatica)</summary>
        internal string LastDump = "";
        internal event Action<JsonElement>? MessageFromPage;

        private static bool _cleaned;

        public static TerminalHost Create(SSH ssh)
        {
            TerminalHost? v = null;
            if (!_cleaned) { _cleaned = true; CleanOldDataFolders(); }   // cartelle lasciate da processi gia' chiusi
            Execute.OnUIThreadSync(() => v = new TerminalHost(ssh));
            return v!;
        }

        private TerminalHost(SSH ssh) : base(ssh, false)
        {
            _ssh = ssh;
            Focusable = true;
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x10, 0x10);
            _web.CreationProperties = CreateProps();
            Content = _web;   // niente gestore GotFocus: il focus della pagina risale all'host e rimandava "focus" alla pagina all'infinito
            TerminalSettings.Changed += OnSettingsChanged;
        }

        // ---------- cartella dati della WebView2: temporanea, una per processo, ripulita alla chiusura e all'avvio successivo
        private static string Root => Path.Combine(Path.GetTempPath(), "Lemora-terminal");
        private static string DataFolder() => Path.Combine(Root, Environment.ProcessId.ToString());

        /// <summary>stesse opzioni per tutte le WebView2 del terminale (sessioni e anteprima delle impostazioni): devono coincidere per condividere lo stesso ambiente</summary>
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
                        try { System.Diagnostics.Process.GetProcessById(pid); continue; } catch { /* il processo non c'e' piu' */ }
                    }
                    try { Directory.Delete(d, true); } catch { /* ancora in uso */ }
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
            // string.Replace con funzione: il testo degli script non viene mai interpretato
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
            try { _web.Dispose(); } catch { /* gia' chiusa */ }
            ActivityReason ??= "chiusa dall'utente";
            Status = ProtocolHostStatus.Disconnected;
            base.Close();
        }

        public override void FocusOnMe()
        {
            Execute.OnUIThread(() =>
            {
                try { _web.Focus(); _web.CoreWebView2?.PostWebMessageAsJson("{\"t\":\"focus\"}"); } catch { /* non ancora pronta */ }
            });
        }

        public override ProtocolHostType GetProtocolHostType() => ProtocolHostType.Native;
        public override IntPtr GetHostHwnd() => IntPtr.Zero;

        // ---------- pagina
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
                // la pagina e' una sola e non puo' portare altrove, ne' aprire altre finestre
                _web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true; };
                _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                _web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                _web.CoreWebView2.WebMessageReceived += OnMessage;
                if (Environment.GetEnvironmentVariable("LEMORA_DEMO") != null)   // solo nelle prove: la console della pagina nel registro
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
                    _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, a) => SimpleLogHelper.Warning("Terminale console: " + a.ParameterObjectAsJson);
                }
                _web.NavigateToString(BuildHtml());
                await _ready.Task;
                await ConnectSshAsync();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                ActivityReason = "terminale non avviato: " + e.Message;
                Status = ProtocolHostStatus.Disconnected;
                Lemora.Utils.MessageBoxHelper.ErrorAlert("Terminale: " + e.Message);
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
                    _shellIntegration = TerminalSettings.Current.ShellIntegration;     // letto qui, sul thread dell'interfaccia: la connessione gira in un altro
                    SendToPage(new { t = "settings", s = TerminalSettings.Current.ToPage() });
                    _ready.TrySetResult(true);
                    break;
                case "openSettings":
                    TerminalSettingsWindow.ShowSingle(Window.GetWindow(this));
                    break;
                case "size":
                    _cols = m.GetProperty("cols").GetInt32(); _rows = m.GetProperty("rows").GetInt32();
                    try { _shell?.ChangeWindowSize((uint)_cols, (uint)_rows, 0, 0); } catch { /* sessione chiusa */ }
                    break;
                case "in":
                    LastInput = m.GetProperty("d").GetString() ?? "";
                    try { _shell?.Write(LastInput); } catch { /* sessione chiusa */ }
                    break;
                case "paste":
                    {
                        string txt;
                        if (IsDemo) txt = DemoPasteText;                       // nelle prove non si tocca l'appunto vero dell'utente
                        else { try { txt = Clipboard.GetText() ?? ""; } catch { txt = ""; } }
                        // incollare piu' righe in una shell le esegue una dopo l'altra: si chiede conferma (si spegne nelle impostazioni)
                        var lines = txt.TrimEnd('\r', '\n').Split('\n').Length;
                        if (lines > 1 && TerminalSettings.Current.WarnMultiLinePaste && !IsDemo
                            && !Lemora.Utils.MessageBoxHelper.Confirm($"Stai incollando {lines} righe: la shell le eseguirà una dopo l'altra.\n\nIncollare?", "Incolla", "Annulla", "Incolla più righe"))
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
                    SimpleLogHelper.Warning("Terminale (pagina): " + m.GetProperty("msg").GetString());
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

        // ---------- appunti e salvataggio dell'output di un comando
        internal static string? LastClipboardText;   // prove automatiche: cosa e' stato copiato

        private static bool IsDemo => Environment.GetEnvironmentVariable("LEMORA_DEMO") != null;
        internal static string DemoPasteText = "echo incollato-dal-test";
        internal string LastInput = "";
        private TaskCompletionSource<string> _sel = new();

        private static void CopyToClipboard(string text, string? html)
        {
            LastClipboardText = text;
            if (IsDemo) return;   // nelle prove non si tocca l'appunto vero dell'utente
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                if (!string.IsNullOrEmpty(html)) data.SetData(DataFormats.Html, CfHtml(html));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception e) { SimpleLogHelper.Warning("Terminale: appunti " + e.Message); }
        }

        /// <summary>formato "HTML Format" degli appunti di Windows: intestazione con le posizioni in byte del frammento</summary>
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
                var dir = Path.Combine(docs, "Output terminale");
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = name,
                    DefaultExt = ".txt",
                    Filter = "File di testo (*.txt)|*.txt|Tutti i file (*.*)|*.*",
                    InitialDirectory = Directory.Exists(dir) ? dir : docs,   // la cartella dedicata si usa se c'e': non si crea da sola
                    OverwritePrompt = true,
                };
                if (dlg.ShowDialog(Window.GetWindow(this)) == true)
                    File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            }
            catch (Exception e) { Lemora.Utils.MessageBoxHelper.ErrorAlert(e.Message); }
        }

        // ---------- aiuti per le prove automatiche (DevDemo)
        internal void TestWrite(string s) { try { _shell?.Write(s); } catch { /* sessione chiusa */ } }
        internal bool TestIsConnected => _shell != null && _client?.IsConnected == true;
        // eventi veri del browser (protocollo di debug), senza mouse ne' tastiera dell'utente
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
            return await Task.WhenAny(_sel.Task, Task.Delay(3000)) == _sel.Task ? await _sel.Task : "(nessuna risposta)";
        }
        /// <summary>tasto vero con Ctrl (e Maiusc): modifiers del protocollo = Ctrl 2, Maiusc 8</summary>
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
            // la pagina e' bloccata: si ferma col debugger (attivato all'avvio) e si legge dove sta girando
            _ = _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Debugger.pause", "{}");
            var stack = await Task.WhenAny(_pausedStack.Task, Task.Delay(6000)) == _pausedStack.Task ? await _pausedStack.Task : "(nessuna risposta dal debugger)";
            return "script non risponde; stack: " + stack;
        }
        private readonly TaskCompletionSource<string> _pausedStack = new();

        internal async Task TestCapture(string path)
        {
            // protocollo di debug del browser: non dipende dalla finestra visibile (la foto a finestra nascosta si bloccava)
            var cap = _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
            if (await Task.WhenAny(cap, Task.Delay(8000)) != cap) throw new TimeoutException("foto non completata in 8 s");
            using var doc = JsonDocument.Parse(await cap);
            File.WriteAllBytes(path, Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!));
        }

        internal void SendToPage(object msg)
        {
            var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { IncludeFields = true });
            Execute.OnUIThread(() => { try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* pagina chiusa */ } });
        }

        // ---------- SSH
        private async Task ConnectSshAsync()
        {
            _hostKeyRefused = false;
            SendToPage(new { t = "msg", d = $"Connessione a {_ssh.Address}:{_ssh.GetPort()} ..." });
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
                    // impronta del server: primo collegamento = si chiede e si ricorda (database cifrato), poi si confronta; se cambia, avviso
                    client.HostKeyReceived += (_, a) => { Fingerprint = a.FingerPrintSHA256; a.CanTrust = TrustHostKey(a.FingerPrintSHA256); };
                    client.ErrorOccurred += (_, a) => OnLost(a.Exception.Message);
                    client.Connect();
                    _client = client;
                    var shell = client.CreateShellStream("xterm-256color", (uint)_cols, (uint)_rows, 0, 0, 8192);
                    shell.DataReceived += (_, a) => SendToPage(new { t = "out", d = Convert.ToBase64String(a.Data) });
                    shell.Closed += (_, _) => OnLost("Sessione chiusa dal server.");
                    _shell = shell;
                    if (_shellIntegration || Environment.GetEnvironmentVariable("LEMORA_TERM_INJECT") == "1") shell.Write(ShellIntegrationSnippet);
                    else if (!string.IsNullOrWhiteSpace(_ssh.StartupAutoCommand)) shell.WriteLine(_ssh.StartupAutoCommand);
                });
                Status = ProtocolHostStatus.Connected;
                FocusOnMe();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("Terminale: " + e.Message);
                SendToPage(new { t = "msg", d = _hostKeyRefused ? "Collegamento annullato: impronta del server non confermata." : "Collegamento non riuscito: " + e.Message });
                ActivityReason = _hostKeyRefused ? "impronta del server non confermata" : $"{e.GetType().Name}: {e.Message}";
                Status = ProtocolHostStatus.Disconnected;
            }
        }

        private bool _hostKeyRefused;

        /// <summary>true se si puo' proseguire con questo server (impronta nota e uguale, oppure confermata dall'utente)</summary>
        private bool TrustHostKey(string fingerprint)
        {
            if (IsDemo) return true;   // nelle prove non si apre nessuna finestra e non si scrive nel database
            if (!TerminalSettings.Current.VerifyHostKey) return true;   // verifica spenta nelle impostazioni (sconsigliato)
            var host = _ssh.Address; var port = _ssh.GetPort();
            var ok = false;
            Execute.OnUIThreadSync(() =>
            {
                var known = TerminalKnownHosts.Get(host, port);
                if (known == fingerprint) { ok = true; return; }
                ok = known == null
                    ? Lemora.Utils.MessageBoxHelper.Confirm($"Primo collegamento a {host}:{port}.\n\nImpronta del server (SHA256):\n{fingerprint}\n\nSe conosci questa impronta, o ti fidi del server, continua: verrà ricordata per i prossimi collegamenti.", "Continua", "Annulla", "Nuovo server SSH")
                    : Lemora.Utils.MessageBoxHelper.Confirm($"ATTENZIONE: l'impronta del server {host}:{port} è cambiata.\n\nSalvata:\n{known}\n\nOra:\n{fingerprint}\n\nPuò essere un server reinstallato oppure qualcuno che si finge il server. Continua solo se sai perché è cambiata.", "Fidati e continua", "Annulla", "Impronta del server cambiata");
                if (ok) TerminalKnownHosts.Set(host, port, fingerprint);
            });
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
        /// Attiva i marcatori di comando (OSC 133) in bash e zsh: una riga con spazio iniziale (non finisce nella cronologia) e poi si pulisce lo schermo.
        /// </summary>
        internal static string ShellIntegrationText => ShellIntegrationSnippet;
        private const string ShellIntegrationSnippet =
            " if [ -n \"$BASH_VERSION\" ]; then __1r_pc(){ printf '\\033]133;D;%s\\a\\n\\033]7;file://%s%s\\a\\033]133;A\\a' \"$?\" \"$HOSTNAME\" \"$PWD\"; }; PS0='\\033]133;C\\a'; PS1=\"$PS1\"'\\[\\033]133;B\\a\\]'; PROMPT_COMMAND=\"__1r_pc${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"; " +
            "elif [ -n \"$ZSH_VERSION\" ]; then precmd(){ printf '\\033]133;D;%s\\a\\n\\033]7;file://%s%s\\a\\033]133;A\\a' \"$?\" \"$HOST\" \"$PWD\"; }; preexec(){ printf '\\033]133;C\\a'; }; PS1=\"$PS1\"$'%{\\e]133;B\\a%}'; fi; clear\n";
    }
}
