using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lemora.Utils
{
    /// <summary>
    /// Stops the flashing of a window's button on the Windows taskbar. Lemora never starts it: it can be left on by a session
    /// (the RDP client) and clicking the button does not stop it, so it is reset when the window becomes active again or a session closes.
    /// </summary>
    public static class TaskbarFlash
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        public static void Stop(Window window)
        {
            try
            {
                var h = new WindowInteropHelper(window).Handle;
                if (h == IntPtr.Zero) return;
                var f = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = h, dwFlags = 0 };   // 0 = FLASHW_STOP
                FlashWindowEx(ref f);
            }
            catch { /* cosmetic only */ }
        }
    }
}
