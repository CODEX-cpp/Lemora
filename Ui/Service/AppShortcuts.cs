using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Lemora.View;
using Lemora.View.Host;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>
    /// Scorciatoie da tastiera "da browser" (Ctrl+Tab, Ctrl+W, Ctrl+T...). Si agganciano ai messaggi di tastiera del thread dell'interfaccia:
    /// arrivano qui PRIMA che li prenda il client remoto (VNC e RDP sono finestre native che altrimenti si tengono ogni tasto).
    /// Un tasto che non serve in quel momento (es. Ctrl+D con una sessione aperta) non viene toccato e va al computer remoto.
    /// </summary>
    public static class AppShortcuts
    {
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const int VK_F4 = 0x73, VK_F5 = 0x74, VK_F11 = 0x7A;
        private const int VK_ADD = 0x6B, VK_SUBTRACT = 0x6D, VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD;

        [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

        /// <summary>stato dei tasti modificatori (sostituibile nelle prove)</summary>
        public static Func<int, bool> IsKeyDown = vk => (GetKeyState(vk) & 0x8000) != 0;

        /// <summary>finestra che riceve i tasti (sostituibile nelle prove)</summary>
        public static Func<Window?> GetActiveWindow = () => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            // ThreadFilterMessage scatta PRIMA di ThreadPreprocessMessage e, se qualcuno segna il messaggio come gestito, i successivi non lo vedono piu':
            // il controllo che ospita VNC/RDP (WindowsFormsHost) si prende i tasti della finestra nativa nel secondo, quindi qui si arriva per primi.
            // Va chiamato all'avvio, prima che nasca qualsiasi finestra che ospita controlli nativi.
            ComponentDispatcher.ThreadFilterMessage += OnMessage;
        }

        private static bool _deactivatedHooked;

        private static MainWindowViewModel? MainVm() => IoC.TryGet<MainWindowViewModel>();

        private static void OnMessage(ref MSG msg, ref bool handled)
        {
            if (!_deactivatedHooked && Application.Current != null)
            {
                _deactivatedHooked = true;
                // se si esce dalla finestra mentre si scorrono le schede con Ctrl+Tab, il ciclo finisce
                Application.Current.Deactivated += (_, _) => MainVm()?.EndTabCycle();
            }
            if (msg.message == WM_KEYUP || msg.message == WM_SYSKEYUP)
            {
                // Ctrl rilasciato: la scheda raggiunta con Ctrl+Tab diventa la piu' recente
                if ((int)msg.wParam == VK_CONTROL) MainVm()?.EndTabCycle();
                return;
            }
            if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;
            try { handled = TryHandle((int)msg.wParam); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <returns>true = il tasto e' stato usato dal programma e non va inoltrato</returns>
        public static bool TryHandle(int vk)
        {
            var vm = MainVm();
            if (vm == null) return false;
            var window = GetActiveWindow();
            HostBase? host;
            bool isMain;
            if (window is MainWindowView) { host = vm.ActiveSession; isMain = true; }
            else if (window is DetachedSessionWindow dw) { host = dw.Host; isMain = false; }
            else return false;   // finestre di dialogo e altro: nessuna scorciatoia

            bool ctrl = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT), alt = IsKeyDown(VK_MENU);
            // Ctrl+Alt+P: gestore password della cartella sulla sessione RDP/VNC (prima del blocco dei tasti con Alt)
            if (ctrl && alt && !shift && vk == 'P' && Lemora.View.Host.DevicePasswordDrawer.Toggle(host)) return true;
            if (alt) return false;

            if (ctrl && !shift)
            {
                switch (vk)
                {
                    case VK_TAB:
                        if (!isMain) return false;
                        vm.CycleTabsByRecent();   // alla scheda usata piu' di recente, non alla prossima in ordine
                        return true;
                    case 'W':
                    case VK_F4:
                        // dentro una sessione Ctrl+W / Ctrl+F4 vanno al computer remoto (chiudono la scheda del browser del cliente);
                        // il programma li usa solo sulle schede senza sessione (Home)
                        if (host != null) return false;
                        if (isMain) vm.CloseCurrentTab();
                        return true;
                    case 'T': if (!isMain) return false; vm.NewHomeTab(); return true;
                    case 'F':
                        // dentro una sessione Ctrl+F va al computer remoto (cerca nella pagina del browser del cliente): la ricerca del programma c'e' solo in Home
                        if (host != null) return false;
                        return isMain && vm.FocusSearch();
                    case 'N': return isMain && vm.ShortcutAddServer();
                    case 'I': return isMain && vm.ShortcutSettings();
                    case 'D': return isMain && vm.ShortcutFavorite();
                    case 'R': return host != null && Reload(host);
                    case '1': case 0x61: return host != null && vm.ShortcutSessionView(host, 1);
                    case '2': case 0x62: return host != null && vm.ShortcutSessionView(host, 2);
                    case '3': case 0x63: return host != null && vm.ShortcutSessionView(host, 3);
                    case '4': case 0x64: return host != null && vm.ShortcutSessionView(host, 4);
                    case VK_OEM_PLUS: case VK_ADD:
                        if (host is not { HasViewControls: true }) return false; host.ZoomIn(); return true;
                    case VK_OEM_MINUS: case VK_SUBTRACT:
                        if (host is not { HasViewControls: true }) return false; host.ZoomOut(); return true;
                    case '0': case 0x60:
                        if (host is not { HasViewControls: true }) return false; host.ZoomReset(); return true;
                }
            }
            else if (!ctrl && !shift)
            {
                switch (vk)
                {
                    case VK_F5:
                        // nel file manager F5 aggiorna la cartella
                        return host != null && host is not FileTransmitHost && Reload(host);
                    case VK_F11:
                        if (host != null && vm.ShortcutSessionView(host, 2)) return true;
                        if (window.WindowState != WindowState.Minimized) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                        return true;
                }
            }
            return false;
        }

        /// <summary>riconnette una sessione caduta, e ricarica (scollega e ricollega) una che non lo e'</summary>
        private static bool Reload(HostBase host)
        {
            host.ReConn();
            return true;
        }
    }
}
