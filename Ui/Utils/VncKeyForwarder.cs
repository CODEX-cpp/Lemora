using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Lemora.View;
using Lemora.View.Host;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Con il fuoco dentro lo schermo VNC, i tasti che Windows si prende per se' (Windows, Alt+Tab, Ctrl+Esc) vanno al computer remoto.
    /// La libreria VNC ci prova con un proprio gancio di tastiera, ma confronta il controllo con la finestra radice che ha il fuoco e in un'applicazione
    /// WPF non coincidono mai: il tasto Windows apriva il menu Start del PC locale. Qui il gancio e' nostro e ricava il client dalla finestra attiva.
    /// </summary>
    public static class VncKeyForwarder
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const int LLKHF_ALTDOWN = 0x20, LLKHF_INJECTED = 0x10;
        // keysym X11 (quelli che il protocollo RFB si aspetta)
        private const uint XK_Super_L = 0xFFEB, XK_Super_R = 0xFFEC, XK_Tab = 0xFF09, XK_Escape = 0xFF1B, XK_Alt_L = 0xFFE9;

        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

        private static readonly LowLevelProc ProcDelegate = HookProc;   // il delegato deve restare vivo finche' il gancio e' installato
        private static IntPtr _hook = IntPtr.Zero;

        /// <summary>il client VNC che ha il fuoco ora (sostituibile nelle prove)</summary>
        public static Func<VncHost?> GetFocusedHost = DefaultFocusedHost;

        /// <summary>da chiamare dal thread dell'interfaccia (il gancio di basso livello gira sul thread che lo installa)</summary>
        public static void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, ProcDelegate, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) SimpleLogHelper.Warning("VncKeyForwarder: gancio di tastiera non installato (" + Marshal.GetLastWin32Error() + ")");
            // Windows non vede il tasto che inoltriamo noi: la libreria deve sapere che c'e' una scorciatoia in corso (Bloc Maiusc ignorato: Win+R non diventa Win+Maiusc+R)
            VncSharpCore.RemoteDesktop.ExtraShortcutModifierDown = () => WinForwardedDown;
        }

        /// <summary>il tasto Windows e' premuto sul PC remoto (inoltrato da noi)</summary>
        public static bool WinForwardedDown { get { lock (Forwarded) return Forwarded.ContainsKey(VK_LWIN) || Forwarded.ContainsKey(VK_RWIN); } }

        private static HostBase? ActiveHost()
        {
            var window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            return window switch
            {
                MainWindowView => IoC.TryGet<MainWindowViewModel>()?.ActiveSession,
                DetachedSessionWindow dw => dw.Host,
                _ => null,
            };
        }

        private static VncHost? DefaultFocusedHost() => ActiveHost() is VncHost { HasRemoteFocus: true } v ? v : null;

        /// <summary>la scheda VNC mostrata nella finestra attiva, anche se il fuoco e' su un pulsante della barra (serve ad Alt+Tab: "dentro la scheda VNC" e' tutta la scheda)</summary>
        public static Func<VncHost?> GetActiveVncHost = () => ActiveHost() as VncHost;

        private const int VK_SNAPSHOT = 0x2C, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_V = 0x56;
        private static VncHost? _altSentBy;   // la sessione a cui abbiamo mandato Alt (premuto) per Alt+Tab
        private static bool _swallowV;   // il tasto V di Ctrl+Maiusc+V e' stato preso per "Incolla file": anche il suo rilascio non va oltre
        private static bool _winHeld;   // il tasto Windows e' premuto (per registrare solo le combinazioni con Windows)

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    // KBDLLHOOKSTRUCT: vkCode (int), scanCode (int), flags (int), ...
                    var vk = Marshal.ReadInt32(lParam, 0);
                    var scan = Marshal.ReadInt32(lParam, 4);
                    var flags = Marshal.ReadInt32(lParam, 8);
                    var up = wParam.ToInt32() is WM_KEYUP or WM_SYSKEYUP;
                    var swallowed = TryForward(vk, flags, up);   // true = il tasto non arriva a Windows
                    // KEY-DIAG (temporaneo): si registrano solo il tasto Windows, Stamp e i tasti premuti mentre Windows e' premuto (combinazioni, non testo digitato),
                    // per capire cosa vede Windows quando "Win+R" apre la cattura schermo
                    if (vk is VK_LWIN or VK_RWIN) _winHeld = !up;
                    if (vk is VK_LWIN or VK_RWIN or VK_SNAPSHOT || (_winHeld && !up))
                    {
                        var line = $"KEY-DIAG: vk=0x{vk:X2} scan=0x{scan:X2} {(up ? "rilascio" : "pressione")} iniettato={(flags & LLKHF_INJECTED) != 0} inoltrato_al_remoto={swallowed} Shift={(GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0} Ctrl={(GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0} Alt={(GetAsyncKeyState(VK_MENU) & 0x8000) != 0}";
                        System.Threading.ThreadPool.QueueUserWorkItem(_ => Lemora.Utils.Diag.Log(line));   // fuori dal gancio: deve rispondere in pochi millisecondi
                    }
                    if (swallowed) return new IntPtr(1);
                }
            }
            catch (Exception e) { SimpleLogHelper.Error(e); }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>tasti inoltrati al server e non ancora rilasciati: il rilascio segue sempre la sua pressione (al server se la pressione e' andata la', a Windows altrimenti)</summary>
        private static readonly System.Collections.Generic.Dictionary<int, VncHost> Forwarded = new();

        private static uint? SymFor(int vk, int flags) => vk switch
        {
            VK_LWIN => XK_Super_L,
            VK_RWIN => XK_Super_R,
            VK_ESCAPE when (flags & LLKHF_ALTDOWN) == 0 && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 => XK_Escape,   // Ctrl+Esc
            _ => null,
        };

        private static uint SymOfKey(int vk) => vk switch { VK_LWIN => XK_Super_L, VK_RWIN => XK_Super_R, VK_TAB => XK_Tab, _ => XK_Escape };

        /// <returns>true = tasto inviato al server e da non passare a Windows</returns>
        public static bool TryForward(int vk, int flags, bool up)
        {
            if ((flags & LLKHF_INJECTED) != 0) return false;   // tasti generati da programmi (anche i nostri): mai riinviarli
            lock (Forwarded)
            {
                if (up)
                {
                    // senza questa regola la pressione poteva andare a Windows e il rilascio al server (o il contrario): un tasto Windows "incastrato"
                    // su uno dei due computer, che trasforma i tasti successivi in scorciatoie
                    if (vk == VK_V && _swallowV) { _swallowV = false; return true; }
                    // Alt mandato da noi insieme ad Alt+Tab: si rilascia sempre quando Alt viene lasciato qui (il rilascio doppio e' innocuo), cosi' non resta "premuto" sul PC remoto
                    if (_altSentBy != null && vk is 0x12 or 0xA4 or 0xA5) { var ah = _altSentBy; _altSentBy = null; ah.SendKey(XK_Alt_L, false); return false; }
                    if (!Forwarded.Remove(vk, out var h)) return false;
                    h.SendKey(SymOfKey(vk), false);
                    return true;
                }
                // Alt+Tab dentro una scheda VNC: va al PC remoto invece di aprire il selettore di finestre di questo PC (si puo' disattivare nelle impostazioni)
                if (vk == VK_TAB && ((flags & LLKHF_ALTDOWN) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0))
                {
                    var tabHost = IoC.TryGet<Lemora.Service.ConfigurationService>()?.General.SendAltTabToVnc == false ? null : GetActiveVncHost();
                    Lemora.Utils.Diag.Log($"KEY-DIAG: Alt+Tab, scheda VNC attiva={(tabHost != null ? tabHost.ProtocolServer.DisplayName : "nessuna (resta a questo PC)")}");
                    if (tabHost != null)
                    {
                        Forwarded[vk] = tabHost;
                        tabHost.SendKey(XK_Alt_L, true);    // il server riceve Alt anche se la libreria non l'ha ancora mandato: il rilascio (qui sopra) riequilibra
                        _altSentBy = tabHost;
                        tabHost.SendKey(XK_Tab, true);
                        return true;
                    }
                }
                // Ctrl+V con dei file negli appunti (copiati in Esplora risorse): "Incolla file" nel trasferimento file, la sessione non riceve il tasto.
                // Con testo negli appunti Ctrl+V resta quello di sempre (il testo va al PC remoto).
                if (vk == VK_V && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) == 0)
                {
                    var fh = GetFocusedHost();
                    if (fh != null && Lemora.Service.FileTransfer.FileTransferShell.ClipboardHasFiles())
                    {
                        _swallowV = true;
                        Application.Current?.Dispatcher.BeginInvoke(() => fh.FileTransferVm.RequestPaste());   // fuori dal gancio: deve rispondere in pochi millisecondi
                        return true;
                    }
                }
                var sym = SymFor(vk, flags);
                if (sym == null) return false;
                var host = GetFocusedHost();
                // Win con il fuoco su un pulsante della barra o sul pannello file: la scheda VNC e' comunque quella attiva. Il tasto va al PC remoto
                // e il fuoco passa allo schermo remoto, cosi' anche la lettera successiva (Win+R) arriva li' e non a questo PC
                if (host == null && vk is VK_LWIN or VK_RWIN && GetActiveVncHost() is { } tabHost2 && !tabHost2.FileTransferVm.IsNavigatorOpen)
                {
                    host = tabHost2;
                    Application.Current?.Dispatcher.BeginInvoke(() => tabHost2.FocusRemote());
                }
                if (host == null) return false;
                Forwarded[vk] = host;
                host.SendKey(sym.Value, true);
                return true;
            }
        }
    }
}
