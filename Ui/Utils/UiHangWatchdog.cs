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
    /// DIAGNOSI TEMPORANEA (UI-DIAG): un thread di servizio controlla che l'interfaccia risponda. Se si blocca scrive nel log quanto dura,
    /// con handle GDI/USER (una perdita di handle blocca le finestre native come il client VNC), memoria e thread; ogni 15 minuti scrive anche
    /// una riga di stato per vedere l'andamento. Per toglierla: cancellare questo file e la chiamata in AppInit.
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
                return $"GDI={GetGuiResources(p.Handle, 0)} USER={GetGuiResources(p.Handle, 1)} handle={p.HandleCount} thread={p.Threads.Count} memoria={p.PrivateMemorySize64 / (1024 * 1024)} MB";
            }
            catch (Exception e) { return "(info non disponibili: " + e.Message + ")"; }
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
                    catch { return; }   // l'applicazione si sta chiudendo
                    var t0 = DateTime.UtcNow;
                    if (!answered.Wait(5000))
                    {
                        SimpleLogHelper.Warning($"UI-DIAG: l'interfaccia non risponde da 5 s. {Info()}");
                        while (!answered.Wait(30000))
                            SimpleLogHelper.Warning($"UI-DIAG: l'interfaccia e' ancora bloccata da {(DateTime.UtcNow - t0).TotalSeconds:0} s. {Info()}");
                        SimpleLogHelper.Warning($"UI-DIAG: l'interfaccia risponde di nuovo dopo {(DateTime.UtcNow - t0).TotalSeconds:0} s. {Info()}");
                    }
                    // memoria privata oltre 2 GB (il 28/10 una sessione VNC e' arrivata a 7 GB): si forza una pulizia con compattazione e si scrive
                    // quanto e' tornato indietro; se non scende, non e' memoria da pulire ma una perdita vera
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
                                Lemora.Utils.Diag.Log($"UI-DIAG: memoria privata {before} MB, dopo la pulizia forzata {p.PrivateMemorySize64 / (1024 * 1024)} MB (gestita {GC.GetTotalMemory(false) / (1024 * 1024)} MB)");
                            }
                        }
                        catch (Exception e) { SimpleLogHelper.Debug("UI-DIAG: controllo memoria non riuscito: " + e.Message); }
                    }
                    if (DateTime.UtcNow - lastBeat > TimeSpan.FromMinutes(15))
                    {
                        lastBeat = DateTime.UtcNow;
                        Lemora.Utils.Diag.Log($"UI-DIAG: stato periodico. {Info()}");
                    }
                }
            })
            { IsBackground = true, Name = "UiHangWatchdog" };
            thread.Start();
            Lemora.Utils.Diag.Log($"UI-DIAG: controllo dell'interfaccia attivo. {Info()}");
        }
    }
}
