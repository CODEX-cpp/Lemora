using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// UI probe: every 250 ms it asks the UI thread to run a minimal action; if it answers after more than 400 ms
    /// it writes "UI-STALL: interface frozen for X ms" to the log with the end time. Used to link freezes (opening/closing tabs)
    /// to the log lines that precede them. Negligible cost.
    /// </summary>
    public static class UiStallProbe
    {
        private const int IntervalMs = 250, ThresholdMs = 400;
        private static Timer? _timer;
        private static int _pending;

        public static void Install()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _pending, 1) == 1) return;   // the previous request has not been served yet: the freeze is ongoing
                var sw = Stopwatch.StartNew();
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        Interlocked.Exchange(ref _pending, 0);
                        var ms = sw.ElapsedMilliseconds;
                        if (ms >= ThresholdMs)
                        {
                            SimpleLogHelper.Warning($"UI-STALL: interface frozen for {ms} ms (ended now)");
                            StartDiag.Stall(ms, DateTime.Now);   // also in avvio.log while the startup is being recorded
                        }
                    }));
                }
                catch { Interlocked.Exchange(ref _pending, 0); }
            }, null, 2000, IntervalMs);
        }
    }
}
