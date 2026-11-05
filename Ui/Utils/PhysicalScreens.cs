using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lemora.Utils
{
    /// <summary>
    /// Rettangoli dei monitor in PIXEL FISICI, indipendenti dalla scala di Windows e dall'allineamento DPI del programma.
    /// Screen.Bounds, nel programma, e' in pixel "logici" (virtualizzati): con monitor a scale diverse (es. 100% e 125%) i monitor
    /// risultano con dimensioni e posizioni che non coincidono con quelle vere, e uno schermo intero su piu' monitor usciva sbagliato.
    /// </summary>
    public static class PhysicalScreens
    {
        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        /// <summary>tutti i monitor: nome del dispositivo (come Screen.DeviceName) e rettangolo fisico</summary>
        public static List<(string Device, Rectangle Bounds)> All()
        {
            var list = new List<(string, Rectangle)>();
            var old = SetThreadDpiAwarenessContext(PerMonitorV2);
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, hdc, rect, data) =>
                {
                    var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(monitor, ref mi))
                        list.Add((mi.szDevice, Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom)));
                    return true;
                }, IntPtr.Zero);
            }
            finally
            {
                if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
            }
            return list;
        }

        /// <summary>"1,3" -> indici 0-based dei monitor che esistono davvero ora (numeri fuori elenco, doppi o non validi vengono ignorati)</summary>
        public static List<int> ParseSelection(string? text)
        {
            var count = Screen.AllScreens.Length;
            return (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var n) ? n - 1 : -1)
                .Where(i => i >= 0 && i < count).Distinct().OrderBy(i => i).ToList();
        }

        /// <summary>rettangolo che racchiude i monitor indicati</summary>
        public static Rectangle UnionOf(IEnumerable<int> indexes)
        {
            var screens = Screen.AllScreens;
            return indexes.Select(i => screens[i].Bounds).Aggregate(Rectangle.Union);
        }

        /// <summary>true se i monitor hanno scale di Windows diverse (rapporto tra pixel fisici e logici non uguale per tutti)</summary>
        public static bool HasMixedScaling()
        {
            var physical = All();
            var ratios = new List<double>();
            foreach (var screen in Screen.AllScreens)
            {
                foreach (var p in physical)
                    if (string.Equals(p.Device, screen.DeviceName, StringComparison.OrdinalIgnoreCase) && screen.Bounds.Width > 0)
                        ratios.Add((double)p.Bounds.Width / screen.Bounds.Width);
            }
            return ratios.Count > 1 && ratios.Max() - ratios.Min() > 0.01;
        }
}
}
