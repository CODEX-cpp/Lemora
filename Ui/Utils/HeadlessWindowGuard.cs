using System;
using System.Runtime.InteropServices;

namespace Lemora.Utils
{
    /// <summary>
    /// Solo con LEMORA_HEADLESS=1 (usato per mappare l'interfaccia in background): ogni finestra appena mostrata va
    /// fuori schermo, e se l'app prende il primo piano lo restituisce subito alla finestra che lo aveva prima.
    /// Le finestre si attivano normalmente (menu e popup funzionano), ma l'utente non perde il focus.
    /// Senza la variabile non fa nulla.
    /// </summary>
    internal static class HeadlessWindowGuard
    {
        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        // tenuti in campo: il GC non deve liberarli mentre Windows li richiama
        private static WinEventProc? _show;
        private static IntPtr _lastForeign; // ultima finestra in primo piano che non è dell'app

        public static void InstallIfRequested()
        {
            if (Environment.GetEnvironmentVariable("LEMORA_HEADLESS") != "1") return;
            var ownPid = (uint)Environment.ProcessId;
            _lastForeign = GetForegroundWindow();

            // thread dedicato con proprio ciclo di messaggi: reagisce subito anche quando il thread dell'interfaccia è occupato (avvio, caricamenti)
            var t = new System.Threading.Thread(() =>
            {
                Hook(ownPid);
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            }) { IsBackground = true, Name = "HeadlessWindowGuard" };
            t.Start();
        }

        private static void Hook(uint ownPid)
        {
            // EVENT_OBJECT_SHOW: ogni finestra di primo livello appena mostrata va fuori schermo, senza icona nella barra
            _show = (hook, evt, hwnd, idObject, idChild, thread, time) =>
            {
                if (idObject != 0 || GetAncestor(hwnd, GA_PARENT) != GetDesktopWindow()) return;
                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            };
            SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _show, ownPid, 0, WINEVENT_OUTOFCONTEXT);

            // controllo ogni 10 ms: se il primo piano passa all'app, lo si restituisce subito a chi lo aveva
            // (l'evento di sistema arrivava troppo tardi e i tasti dell'utente finivano nell'app)
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
