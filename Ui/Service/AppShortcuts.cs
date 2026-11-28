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
    /// Browser-style keyboard shortcuts (Ctrl+Tab, Ctrl+W, Ctrl+T...). They hook into the keyboard messages of the UI thread:
    /// they arrive here BEFORE the remote client gets them (VNC and RDP are native windows that would otherwise keep every key).
    /// A key that is not needed at that moment (e.g. Ctrl+D with a session open) is left untouched and goes to the remote computer.
    /// </summary>
    public static class AppShortcuts
    {
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const int VK_F4 = 0x73, VK_F5 = 0x74, VK_F11 = 0x7A;
        private const int VK_ADD = 0x6B, VK_SUBTRACT = 0x6D, VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD;

        [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

        /// <summary>state of the modifier keys (replaceable in tests)</summary>
        public static Func<int, bool> IsKeyDown = vk => (GetKeyState(vk) & 0x8000) != 0;

        /// <summary>window that receives the keys (replaceable in tests)</summary>
        public static Func<Window?> GetActiveWindow = () => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            // ThreadFilterMessage fires BEFORE ThreadPreprocessMessage and, if someone marks the message as handled, the later ones no longer see it:
            // the control hosting VNC/RDP (WindowsFormsHost) takes the native window's keys in the second one, so here we get there first.
            // Must be called at startup, before any window hosting native controls is created.
            ComponentDispatcher.ThreadFilterMessage += OnMessage;
        }

        private static bool _deactivatedHooked;

        private static MainWindowViewModel? MainVm() => IoC.TryGet<MainWindowViewModel>();

        private static void OnMessage(ref MSG msg, ref bool handled)
        {
            if (!_deactivatedHooked && Application.Current != null)
            {
                _deactivatedHooked = true;
                // if the user leaves the window while cycling tabs with Ctrl+Tab, the cycle ends
                Application.Current.Deactivated += (_, _) => MainVm()?.EndTabCycle();
            }
            if (msg.message == WM_KEYUP || msg.message == WM_SYSKEYUP)
            {
                // Ctrl released: the tab reached with Ctrl+Tab becomes the most recent
                if ((int)msg.wParam == VK_CONTROL) MainVm()?.EndTabCycle();
                return;
            }
            if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;
            try { handled = TryHandle((int)msg.wParam); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <returns>true = the key was used by the program and must not be forwarded</returns>
        public static bool TryHandle(int vk)
        {
            var vm = MainVm();
            if (vm == null) return false;
            var window = GetActiveWindow();
            HostBase? host;
            bool isMain;
            if (window is MainWindowView) { host = vm.ActiveSession; isMain = true; }
            else if (window is DetachedSessionWindow dw) { host = dw.Host; isMain = false; }
            else return false;   // dialogs and others: no shortcuts

            bool ctrl = IsKeyDown(VK_CONTROL), shift = IsKeyDown(VK_SHIFT), alt = IsKeyDown(VK_MENU);
            // Ctrl+Alt+P: folder password manager on the RDP/VNC session (before the block on Alt keys)
            if (ctrl && alt && !shift && vk == 'P' && Lemora.View.Host.DevicePasswordDrawer.Toggle(host)) return true;
            if (alt) return false;

            if (ctrl && !shift)
            {
                switch (vk)
                {
                    case VK_TAB:
                        if (!isMain) return false;
                        vm.CycleTabsByRecent();   // to the most recently used tab, not the next one in order
                        return true;
                    case 'W':
                    case VK_F4:
                        // inside a session Ctrl+W / Ctrl+F4 go to the remote computer (they close the customer's browser tab);
                        // the program uses them only on tabs without a session (Home)
                        if (host != null) return false;
                        if (isMain) vm.CloseCurrentTab();
                        return true;
                    case 'T': if (!isMain) return false; vm.NewHomeTab(); return true;
                    case 'F':
                        // inside a session Ctrl+F goes to the remote computer (searches the customer's browser page): the program's search exists only in Home
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
                        // in the file manager F5 refreshes the folder
                        return host != null && host is not FileTransmitHost && Reload(host);
                    case VK_F11:
                        if (host != null && vm.ShortcutSessionView(host, 2)) return true;
                        if (window.WindowState != WindowState.Minimized) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                        return true;
                }
            }
            return false;
        }

        /// <summary>reconnects a dropped session, and reloads (disconnects and reconnects) one that is not</summary>
        private static bool Reload(HostBase host)
        {
            host.ReConn();
            return true;
        }
    }
}
