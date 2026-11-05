using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lemora.Utils
{
    /// <summary>
    /// Spegne il lampeggio del pulsante di una finestra nella barra di Windows. Lemora non lo accende mai: capita che resti acceso da una sessione
    /// (il client RDP) e che il clic sul pulsante non lo spenga, cosi' si azzera quando la finestra torna attiva o una sessione si chiude.
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
            catch { /* solo estetico */ }
        }
    }
}
