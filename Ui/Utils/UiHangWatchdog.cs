using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// TEMPORARY DIAGNOSTICS (UI-DIAG): a service thread checks that the UI responds. If it freezes it writes to the log how long it lasts,
    /// with GDI/USER handles (a handle leak freezes native windows such as the VNC client), memory and threads; every 15 minutes it also writes
    /// a status line to see the trend. To remove it: delete this file and the call in AppInit.
    /// </summary>
    public static class UiHangWatchdog
    {
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);   // 0 = GDI, 1 = USER

        private static bool _started;

        private static string Info()
        {
            try
            {
                var p = Process.GetCurrentProcess();
                return $"GDI={GetGuiResources(p.Handle, 0)} USER={GetGuiResources(p.Handle, 1)} handle={p.HandleCount} thread={p.Threads.Count} memory={p.PrivateMemorySize64 / (1024 * 1024)} MB";
            }
            catch (Exception e) { return "(info not available: " + e.Message + ")"; }
        }

        public static void Start()
        {
            if (_started || Application.Current == null) return;
            _started = true;
            var dispatcher = Application.Current.Dispatcher;
            var thread = new Thread(() =>
            {
                var lastBeat = DateTime.UtcNow;
                var lastGc = DateTime.MinValue;
                while (true)
                {
                    Thread.Sleep(2000);
                    using var answered = new ManualResetEventSlim();
                    try { dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => answered.Set())); }
                    catch { return; }   // the application is closing
                    var t0 = DateTime.UtcNow;
                    if (!answered.Wait(5000))
                    {
                        SimpleLogHelper.Warning($"UI-DIAG: the interface has not responded for 5 s. {Info()}");
                        while (!answered.Wait(30000))
                            SimpleLogHelper.Warning($"UI-DIAG: the interface is still frozen after {(DateTime.UtcNow - t0).TotalSeconds:0} s. {Info()}");
                        SimpleLogHelper.Warning($"UI-DIAG: the interface responds again after {(DateTime.UtcNow - t0).TotalSeconds:0} s. {Info()}");
                    }
                    // private memory above 2 GB (a VNC session once reached 7 GB): force a compacting collection and write
                    // how much came back; if it does not go down, it is not memory to clean but a real leak
                    if (DateTime.UtcNow - lastGc > TimeSpan.FromSeconds(60))
                    {
                        try
                        {
                            var p = Process.GetCurrentProcess();
                            var before = p.PrivateMemorySize64 / (1024 * 1024);
                            if (before > 2048)
                            {
                                lastGc = DateTime.UtcNow;
                                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                                GC.Collect(2, GCCollectionMode.Forced, true, true);
                                p.Refresh();
                                Lemora.Utils.Diag.Log($"UI-DIAG: private memory {before} MB, after the forced collection {p.PrivateMemorySize64 / (1024 * 1024)} MB (managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
                            }
                        }
                        catch (Exception e) { SimpleLogHelper.Debug("UI-DIAG: memory check failed: " + e.Message); }
                    }
                    if (DateTime.UtcNow - lastBeat > TimeSpan.FromMinutes(15))
                    {
                        lastBeat = DateTime.UtcNow;
                        Lemora.Utils.Diag.Log($"UI-DIAG: periodic status. {Info()}");
                    }
                }
            })
            { IsBackground = true, Name = "UiHangWatchdog" };
            thread.Start();
            Lemora.Utils.Diag.Log($"UI-DIAG: interface watchdog active. {Info()}");
        }
    }
}
