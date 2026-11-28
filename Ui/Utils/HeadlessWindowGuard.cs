using System;
using System.Runtime.InteropServices;

namespace Lemora.Utils
{
    /// <summary>
    /// Only with LEMORA_HEADLESS=1 (used to map the UI in the background): every window just shown goes
    /// off screen, and if the app takes the foreground it immediately gives it back to the window that had it before.
    /// Windows activate normally (menus and popups work), but the user does not lose focus.
    /// Without the variable it does nothing.
    /// </summary>
    internal static class HeadlessWindowGuard
    {
        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        // kept in a field: the GC must not free them while Windows calls them back
        private static WinEventProc? _show;
        private static IntPtr _lastForeign; // last foreground window that does not belong to the app

        public static void InstallIfRequested()
        {
            if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") != "1") return;
            var ownPid = (uint)Environment.ProcessId;
            _lastForeign = GetForegroundWindow();

            // dedicated thread with its own message loop: reacts at once even when the UI thread is busy (startup, loading)
            var t = new System.Threading.Thread(() =>
            {
                Hook(ownPid);
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            }) { IsBackground = true, Name = "HeadlessWindowGuard" };
            t.Start();
        }

        private static void Hook(uint ownPid)
        {
            // EVENT_OBJECT_SHOW: every top-level window just shown goes off screen, with no taskbar icon
            _show = (hook, evt, hwnd, idObject, idChild, thread, time) =>
            {
                if (idObject != 0 || GetAncestor(hwnd, GA_PARENT) != GetDesktopWindow()) return;
                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            };
            SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _show, ownPid, 0, WINEVENT_OUTOFCONTEXT);

            // check every 10 ms: if the foreground passes to the app, it is given back at once to whoever had it
            // (the system event arrived too late and the user's keystrokes ended up in the app)
            new System.Threading.Thread(() =>
            {
                while (true)
                {
                    var h = GetForegroundWindow();
                    GetWindowThreadProcessId(h, out var pid);
                    if (pid != ownPid) { if (h != IntPtr.Zero) _lastForeign = h; }
                    else if (_lastForeign != IntPtr.Zero && IsWindow(_lastForeign)) SetForegroundWindow(_lastForeign);
                    System.Threading.Thread.Sleep(10);
                }
            }) { IsBackground = true, Name = "HeadlessForegroundWatch" }.Start();
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0, GA_PARENT = 1;
        private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
