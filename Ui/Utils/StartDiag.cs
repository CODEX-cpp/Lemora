using System;
using System.Collections.Generic;
using System.Diagnostics;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Slow startup diagnostics: each mark writes to the log how many milliseconds have passed since the process started
    /// (and since the previous mark), so you can see which phase of the startup loses time. Warning level like the other "-DIAG" lines.
    /// In the development build (and with diagnostics on) every startup also gets its own file, avvio.log next to the other logs,
    /// rewritten at each startup: time, ms since start, ms of the step, thread, what happens. Lines starting with "SCREEN:" say what
    /// is on screen from that moment (skeleton, Home, empty window), so the file reads as a timeline of what the user sees.
    /// </summary>
    public static class StartDiag
    {
        private static readonly DateTime ProcessStart = SafeStart();
        private static DateTime _last = DateTime.MinValue;
        private static readonly object ProcessStartLock = new();
        private static readonly List<string> FilePending = new();
        private static string? _file;
        private static bool _fileOff;
        private static DateTime _screenSince = DateTime.MinValue;
        private static string? _screenWhat;

        private static DateTime SafeStart()
        {
            try { return Process.GetCurrentProcess().StartTime; } catch { return DateTime.Now; }
        }

        public static void Mark(string what)
        {
            try
            {
                var now = DateTime.Now;
                double step;
                lock (ProcessStartLock) { step = _last == DateTime.MinValue ? 0 : (now - _last).TotalMilliseconds; _last = now; }
                var line = $"START-DIAG: +{(now - ProcessStart).TotalMilliseconds:0} ms since start (+{step:0} ms since the previous mark): {what}";
                Lemora.Utils.Diag.Log(line);
                // tests only: every mark goes straight to a file, even before the log is ready (shows where a stuck startup stops)
                var crumb = DevOnly.Env("LEMORA_BREADCRUMB");
                if (!string.IsNullOrEmpty(crumb)) lock (ProcessStartLock) System.IO.File.AppendAllText(crumb, $"[T:{Environment.CurrentManagedThreadId}] {line}{Environment.NewLine}");
                ToFile($"{now:HH:mm:ss.fff}  {(now - ProcessStart).TotalMilliseconds,7:0} ms  (+{step,5:0})  T{Environment.CurrentManagedThreadId,-3} {what}");
            }
            catch { /* diagnostics only */ }
        }

        /// <summary>
        /// what the user sees from now on (e.g. "skeleton", "Home"): written as SCREEN line, with how long the previous view stayed on screen
        /// </summary>
        public static void Screen(string what)
        {
            var now = DateTime.Now;
            string prev;
            lock (ProcessStartLock)
            {
                prev = _screenWhat == null ? "" : $" (before: {_screenWhat} for {(now - _screenSince).TotalMilliseconds:0} ms)";
                _screenWhat = what; _screenSince = now;
            }
            Mark($"SCREEN: {what}{prev}");
            // the Home is on screen: 15 s more (what happens right after), then the file is complete
            if (what.StartsWith("Home", StringComparison.Ordinal))
                _closer ??= new System.Threading.Timer(_ => CloseFile("15 s after the Home"), null, 15000, System.Threading.Timeout.Infinite);
        }
        private static System.Threading.Timer? _closer;

        /// <summary>a UI freeze (from UiStallProbe): in the startup file too, so it is visible next to the steps it slows down</summary>
        public static void Stall(long ms, DateTime end)
        {
            if (_file == null && _fileOff) return;
            ToFile($"{end:HH:mm:ss.fff}  {(end - ProcessStart).TotalMilliseconds,7:0} ms           ---  interface frozen for {ms} ms (until here)");
        }

        /// <summary>called when the log folder is known: the lines collected so far go into avvio.log (dev build or diagnostics on)</summary>
        public static void OpenFile(string logDir)
        {
            string[] queued;
            lock (ProcessStartLock)
            {
                if (_file != null || _fileOff) return;
                if (!DevOnly.Enabled && !Diag.Enabled) { _fileOff = true; FilePending.Clear(); return; }
                try
                {
                    System.IO.Directory.CreateDirectory(logDir);
                    _file = System.IO.Path.Combine(logDir, "avvio.log");
                    System.IO.File.WriteAllText(_file, $"Startup of {ProcessStart:dd/MM/yyyy HH:mm:ss.fff} (process {Environment.ProcessId})" + Environment.NewLine
                        + "time          since start (step)   thread  what happens" + Environment.NewLine);
                }
                catch { _fileOff = true; FilePending.Clear(); return; }
                queued = FilePending.ToArray();
                FilePending.Clear();
            }
            try { System.IO.File.AppendAllLines(_file!, queued); } catch { }
        }

        /// <summary>the startup is over: the file stops growing (the app keeps logging to its normal log)</summary>
        public static void CloseFile(string why)
        {
            Mark($"end of the startup log ({why})");
            lock (ProcessStartLock) { _fileOff = true; _file = null; FilePending.Clear(); }
        }

        private static void ToFile(string line)
        {
            lock (ProcessStartLock)
            {
                if (_fileOff) return;
                if (_file == null) { if (FilePending.Count < 2000) FilePending.Add(line); return; }
                try { System.IO.File.AppendAllText(_file, line + Environment.NewLine); } catch { }
            }
        }
    }
}
