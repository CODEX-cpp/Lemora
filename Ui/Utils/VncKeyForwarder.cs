using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Lemora.Service;
using Lemora.Service.Mcp;
using Lemora.View;
using Lemora.View.Host;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Keyboard hook of the sessions (VNC and RDP with FreeRDP): with the keyboard inside the session, each combination of the
    /// "Key mapping" page goes to the remote PC or stays on this PC, as decided by the page (defaults of the protocol)
    /// or by the connection (keys changed by hand). The name is the old one: at first it was only for VNC.
    /// - VNC: the library's own hook compares the control with the root window that has focus, and in a WPF application they never match.
    /// - FreeRDP: started without its keyboard grab (all or nothing), so the choice per key is made here.
    /// The Windows key is held back until the next key: only then it is known which combination it is (Win+R, Win+L...).
    /// </summary>
    public static class VncKeyForwarder
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_F4 = 0x73;
        private const int VK_SNAPSHOT = 0x2C, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_V = 0x56;
        private const int LLKHF_ALTDOWN = 0x20, LLKHF_INJECTED = 0x10;
        // X11 keysyms (the ones the RFB protocol expects; FreeRDP sessions convert them back to Windows keys)
        private const uint XK_Super_L = 0xFFEB, XK_Tab = 0xFF09, XK_Escape = 0xFF1B, XK_Alt_L = 0xFFE9, XK_F4 = 0xFFC1;

        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, Input[] inputs, int size);
        [DllImport("user32.dll")] private static extern bool LockWorkStation();

        /// <summary>dwExtraInfo of the keys Lemora types again on this PC ("LEMO")</summary>
        private static readonly IntPtr OwnMark = new(0x4C454D4F);
        private static readonly bool AcceptInjected = DevOnly.Env("LEMORA_KEYS_TEST_INJECTED") == "1";

        private static readonly LowLevelProc ProcDelegate = HookProc;   // the delegate must stay alive while the hook is installed
        private static IntPtr _hook = IntPtr.Zero;

        /// <summary>the VNC client that has focus now (replaceable in tests)</summary>
        public static Func<VncHost?> GetFocusedHost = DefaultFocusedHost;

        /// <summary>the FreeRDP session that has the keyboard now (replaceable in tests)</summary>
        internal static Func<IMcpScreen?> GetFocusedRdp = DefaultFocusedRdp;

        /// <summary>keys pressed "on this PC" are typed again with SendInput (replaceable in tests: list of (vk, up))</summary>
        internal static Action<(int vk, bool up)[]> InjectLocal = DefaultInject;

        /// <summary>call from the UI thread (the low-level hook runs on the thread that installs it)</summary>
        public static void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, ProcDelegate, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) SimpleLogHelper.Warning("VncKeyForwarder: keyboard hook not installed (" + Marshal.GetLastWin32Error() + ")");
            // Windows does not see the key we forward: the library must know a shortcut is in progress (Caps Lock ignored: Win+R does not become Win+Shift+R)
            VncSharpCore.RemoteDesktop.ExtraShortcutModifierDown = () => WinForwardedDown;
        }

        /// <summary>the Windows key is pressed on the remote PC (forwarded by us)</summary>
        public static bool WinForwardedDown { get { lock (Forwarded) return _win is { SentRemote: true }; } }

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
        // any open FreeRDP session (main window, detached, full screen): the one whose process has the keyboard
        private static IMcpScreen? DefaultFocusedRdp() =>
            IoC.TryGet<SessionControlService>()?.ConnectionId2Hosts.Values.OfType<IntegrateHost>().FirstOrDefault(h => h.IsFreeRdp && h.KeyboardInSession());

        /// <summary>the VNC tab shown in the active window, even if focus is on a toolbar button (for Alt+Tab: "inside the VNC tab" means the whole tab)</summary>
        public static Func<VncHost?> GetActiveVncHost = () => ActiveHost() as VncHost;

        private static VncHost? _altSentBy;   // the VNC session we sent Alt (pressed) to for Alt+Tab, Alt+Esc, Alt+F4
        private static bool _swallowV;   // the V key of Ctrl+Shift+V was taken for "Paste files": its release does not go further either
        private static bool _winHeld;   // the Windows key is pressed (to log only combinations with Windows)
        private static bool _cycling;   // Ctrl+Tab kept on this PC: Lemora's tabs are cycled until Ctrl is released

        /// <summary>Windows key held back: where it goes is decided by the next key (or by its release, if alone)</summary>
        private sealed class WinHold
        {
            public required IMcpScreen Host;
            public required int Vk;
            public bool SentRemote, InjectedLocal, Combo;
        }
        private static WinHold? _win;

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
                    var extra = Marshal.ReadIntPtr(lParam, 16);   // dwExtraInfo (after vkCode, scanCode, flags, time)
                    // keys typed by programs are never touched. Development build, for the sandbox tests (keys typed by the test tool):
                    // they are treated as real ones, except the ones Lemora types again itself (marked with OwnMark)
                    if ((flags & LLKHF_INJECTED) != 0 && extra != OwnMark && AcceptInjected) flags &= ~LLKHF_INJECTED;
                    var swallowed = TryForward(vk, flags, up);   // true = the key does not reach Windows
                    // KEY-DIAG: only the Windows key, Print Screen and the keys pressed while Windows is held are logged (combinations, not typed text).
                    // Real state of the Windows key asked to the system: if the release does not reach the hook (session switch, lock screen)
                    // a rebuilt state would stay "pressed" and the log would record every typed key, passwords included (security hardening)
                    _winHeld = (!up && vk is VK_LWIN or VK_RWIN) || ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0 || _win != null;
                    if (vk is VK_LWIN or VK_RWIN or VK_SNAPSHOT || (_winHeld && !up))
                    {
                        var line = $"KEY-DIAG: vk=0x{vk:X2} scan=0x{scan:X2} {(up ? "rilascio" : "pressione")} iniettato={(flags & LLKHF_INJECTED) != 0} inoltrato_al_remoto={swallowed} Shift={(GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0} Ctrl={(GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0} Alt={(GetAsyncKeyState(VK_MENU) & 0x8000) != 0}";
                        System.Threading.ThreadPool.QueueUserWorkItem(_ => Lemora.Utils.Diag.Log(line));   // outside the hook: it must answer within a few milliseconds
                    }
                    if (swallowed) return new IntPtr(1);
                }
            }
            catch (Exception e) { SimpleLogHelper.Error(e); }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>keys forwarded to the server and not yet released: the release always follows its press (to the server if the press went there, to Windows otherwise)</summary>
        private static readonly Dictionary<int, IMcpScreen> Forwarded = new();
        /// <summary>keys whose press was used here (kept on this PC with a Lemora action, or typed again): their release is not passed on either</summary>
        private static readonly HashSet<int> SwallowUp = new();

        private static uint SymOfKey(int vk) => vk switch { VK_LWIN or VK_RWIN => XK_Super_L, VK_TAB => XK_Tab, VK_F4 => XK_F4, _ => XK_Escape };
        private static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or >= 0xA0 and <= 0xA5;
        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <returns>true = key handled here and not to be passed to Windows</returns>
        public static bool TryForward(int vk, int flags, bool up)
        {
            if ((flags & LLKHF_INJECTED) != 0) return false;   // keys generated by programs (ours too): never resend them
            lock (Forwarded) return up ? Release(vk) : Press(vk, flags);
        }

        private static bool Release(int vk)
        {
            // without these rules the press could go to Windows and the release to the server (or the reverse): a "stuck" key
            // on one of the two computers, which turns the following keys into shortcuts
            if (vk == VK_V && _swallowV) { _swallowV = false; return true; }
            // Alt sent by us together with Alt+Tab: always released when Alt is let go here (a double release is harmless), so it does not stay "pressed" on the remote PC
            if (_altSentBy != null && vk is VK_MENU or 0xA4 or 0xA5) { var ah = _altSentBy; _altSentBy = null; ah.SendKey(XK_Alt_L, false); return false; }
            if (_cycling && vk is VK_CONTROL or 0xA2 or 0xA3) { _cycling = false; Ui(() => IoC.TryGet<MainWindowViewModel>()?.EndTabCycle()); return false; }
            if (vk is VK_LWIN or VK_RWIN && _win != null && _win.Vk == vk) { EndWin(); return true; }
            if (SwallowUp.Remove(vk)) return true;
            if (Raw.Remove(vk, out var rh)) { rh.PostVk(vk, false); return true; }
            if (!Forwarded.Remove(vk, out var h)) return false;
            h.SendKey(SymOfKey(vk), false);
            return true;
        }

        private static bool Press(int vk, int flags)
        {
            bool ctrl = Down(VK_CONTROL), shift = Down(VK_SHIFT), alt = (flags & LLKHF_ALTDOWN) != 0 || Down(VK_MENU);

            // ---- Windows key: held back until the next key decides the combination
            if (vk is VK_LWIN or VK_RWIN)
            {
                if (_win != null) return true;   // auto-repeat while held
                IMcpScreen? target = GetFocusedHost() ?? GetFocusedRdp();
                // Win with focus on a toolbar button or on the file panel: the VNC tab is still the active one. Focus moves to the remote screen,
                // so the next letter (Win+R) also arrives there and not on this PC
                if (target == null && GetActiveVncHost() is { } tabHost && !tabHost.FileTransferVm.IsNavigatorOpen)
                {
                    target = tabHost;
                    Ui(tabHost.FocusRemote);
                }
                if (target == null) return false;
                _win = new WinHold { Host = target, Vk = vk };
                return true;
            }
            if (_win != null && !IsModifier(vk))
            {
                var w = _win;
                w.Combo = true;
                var id = KeyMap.WinCombo(vk, shift);
                if (KeyMap.ToRemote(w.Host.ProtocolServer, id))
                {
                    // Windows on the remote PC, then this key: VNC goes on as usual, FreeRDP gets it by the same way as Windows (order kept)
                    if (!w.SentRemote) { w.Host.SendKey(XK_Super_L, true); w.SentRemote = true; }
                    return w.Host is IntegrateHost ih && PostRaw(ih, vk, ctrl, shift, alt);
                }
                SwallowUp.Add(vk);
                // Win+L typed again is not accepted by Windows (secure combination): the lock is asked directly
                if (id == "winL") { System.Threading.ThreadPool.QueueUserWorkItem(_ => LockWorkStation()); return true; }
                var keys = new List<(int, bool)>();
                if (!w.InjectedLocal) { keys.Add((w.Vk, false)); w.InjectedLocal = true; }
                keys.Add((vk, false)); keys.Add((vk, true));
                InjectLocal(keys.ToArray());
                return true;
            }

            // ---- combinations with Alt / Ctrl
            var combo = KeyMap.ModCombo(vk, ctrl, alt, shift);
            if (combo != null)
            {
                var vnc = GetFocusedHost() ?? (combo == "altTab" ? GetActiveVncHost() : null);
                IMcpScreen? target = vnc ?? GetFocusedRdp();
                if (combo == "altTab") Diag.Log($"KEY-DIAG: Alt+Tab, active session={(target != null ? target.ProtocolServer.DisplayName : "none (stays on this PC)")}");
                if (target != null)
                {
                    if (KeyMap.ToRemote(target.ProtocolServer, combo))
                    {
                        // Windows would take these for itself (or Alt+F4 would close the window here): sent to the session directly
                        if (KeyMap.IsSystemCombo(combo) || combo == "altF4")
                        {
                            if (target is IntegrateHost ih) return PostRaw(ih, vk, ctrl, shift, alt);
                            // the VNC library may not have sent Alt yet: the server receives it now, the release of Alt (above) rebalances it
                            if (vnc != null && alt && _altSentBy == null) { vnc.SendKey(XK_Alt_L, true); _altSentBy = vnc; }
                            Forwarded[vk] = target;
                            target.SendKey(SymOfKey(vk), true);
                            return true;
                        }
                        return false;   // the others reach the session as any key
                    }
                    if (KeyMap.IsSystemCombo(combo)) return false;   // on this PC: Windows does it
                    SwallowUp.Add(vk);
                    switch (combo)
                    {
                        case "altF4": Ui(() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)?.Close()); break;
                        case "ctrlW": Ui(() => IoC.TryGet<MainWindowViewModel>()?.CloseCurrentTab()); break;
                        case "ctrlTab": _cycling = true; Ui(() => IoC.TryGet<MainWindowViewModel>()?.CycleTabsByRecent()); break;
                        // Ctrl+D, Ctrl+L: nothing to do in Lemora, they simply do not reach the remote PC
                    }
                    return true;
                }
            }

            // Ctrl+V with files on the clipboard (copied in File Explorer): "Paste files" in the file transfer, the session does not get the key.
            // With text on the clipboard Ctrl+V stays the usual one (the text goes to the remote PC).
            if (vk == VK_V && ctrl && !alt)
            {
                var fh = GetFocusedHost();
                if (fh != null && Lemora.Service.FileTransfer.FileTransferShell.ClipboardHasFiles())
                {
                    _swallowV = true;
                    Ui(() => fh.FileTransferVm.RequestPaste());
                    return true;
                }
            }
            return false;
        }

        /// <summary>FreeRDP keys sent as messages: the release goes the same way</summary>
        private static readonly Dictionary<int, IntegrateHost> Raw = new();

        /// <summary>
        /// key of a combination to FreeRDP as a message, with the modifiers held down before it (they went through the keyboard queue,
        /// which FreeRDP may read only later: sent again here, a second press of a held key changes nothing). Their release stays the real one
        /// </summary>
        private static bool PostRaw(IntegrateHost ih, int vk, bool ctrl, bool shift, bool alt)
        {
            if (ctrl) ih.PostVk(0xA2, true);
            if (shift) ih.PostVk(0xA0, true);
            if (alt) ih.PostVk(0xA4, true);
            ih.PostVk(vk, true);
            Raw[vk] = ih;
            return true;
        }

        /// <summary>Windows key released: alone it is "win"; after a combination the parts that went somewhere are released there</summary>
        private static void EndWin()
        {
            var w = _win!;
            _win = null;
            if (!w.Combo)
            {
                if (KeyMap.ToRemote(w.Host.ProtocolServer, "win")) { w.Host.SendKey(XK_Super_L, true); w.Host.SendKey(XK_Super_L, false); }
                else InjectLocal(new[] { (w.Vk, false), (w.Vk, true) });   // Start menu of this PC
                return;
            }
            if (w.SentRemote) w.Host.SendKey(XK_Super_L, false);
            if (w.InjectedLocal) InjectLocal(new[] { (w.Vk, true) });
        }

        /// <summary>outside the hook: it must answer within a few milliseconds</summary>
        private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint type;
            public KeybdInput ki;
            private readonly long _pad;   // INPUT is a union with MOUSEINPUT (larger): SendInput checks the size
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeybdInput
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private static void DefaultInject((int vk, bool up)[] keys)
        {
            var inputs = keys.Select(k => new Input
            {
                type = 1,   // INPUT_KEYBOARD
                ki = new KeybdInput { wVk = (ushort)k.vk, dwFlags = (k.up ? 2u : 0u) | (k.vk is VK_LWIN or VK_RWIN ? 1u : 0u), dwExtraInfo = OwnMark },   // KEYUP, EXTENDEDKEY
            }).ToArray();
            // after the hook has answered: the typed keys then follow the key being processed
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
                    SimpleLogHelper.Warning("Key mapping: keys for this PC not sent (" + Marshal.GetLastWin32Error() + ")");
            });
        }
    }
}
