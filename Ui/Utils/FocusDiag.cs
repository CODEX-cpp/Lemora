using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace Lemora
{
    /// <summary>keyboard focus diagnostics (FOCUS-DIAG log): WPF element with focus and Win32 window receiving the keys</summary>
    public static class FocusDiag
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public int l, t, r, b;
        }

        private static string Win(IntPtr h)
        {
            if (h == IntPtr.Zero) return "none";
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            GetWindowThreadProcessId(h, out var pid);
            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
            return $"{h} {sb} ({proc})";
        }

        public static string Describe()
        {
            try
            {
                var wpf = Keyboard.FocusedElement is System.Windows.FrameworkElement fe ? $"{fe.GetType().Name} '{fe.Name}'" : Keyboard.FocusedElement?.GetType().Name ?? "none";
                var fg = GetForegroundWindow();
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                var tid = GetWindowThreadProcessId(fg, out _);
                var focus = GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
                return $"WPF={wpf}; foreground window={Win(fg)}; keyboard focus window={Win(focus)}";
            }
            catch (Exception e) { return "error " + e.GetType().Name; }
        }
    }
}
