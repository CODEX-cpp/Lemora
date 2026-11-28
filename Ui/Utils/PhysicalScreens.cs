using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lemora.Utils
{
    /// <summary>
    /// Monitor rectangles in PHYSICAL PIXELS, independent of the Windows scale and of the program's DPI awareness.
    /// Screen.Bounds, in the program, is in "logical" (virtualized) pixels: with monitors at different scales (e.g. 100% and 125%) the monitors
    /// get sizes and positions that do not match the real ones, and a full screen across several monitors came out wrong.
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

        /// <summary>all monitors: device name (like Screen.DeviceName) and physical rectangle</summary>
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

        /// <summary>"1,3" -> 0-based indexes of the monitors that really exist now (numbers out of range, duplicates or invalid ones are ignored)</summary>
        public static List<int> ParseSelection(string? text)
        {
            var count = Screen.AllScreens.Length;
            return (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var n) ? n - 1 : -1)
                .Where(i => i >= 0 && i < count).Distinct().OrderBy(i => i).ToList();
        }

        /// <summary>rectangle enclosing the given monitors</summary>
        public static Rectangle UnionOf(IEnumerable<int> indexes)
        {
            var screens = Screen.AllScreens;
            return indexes.Select(i => screens[i].Bounds).Aggregate(Rectangle.Union);
        }

        /// <summary>true if the monitors have different Windows scales (ratio between physical and logical pixels not the same for all)</summary>
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
