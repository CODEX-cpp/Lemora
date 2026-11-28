using System;
using System.IO;

namespace Lemora.Utils
{
    /// <summary>
    /// Trace of the app shutdown: every step goes to a file AT ONCE (exit-trace.log next to the logs), because the normal log
    /// is written in blocks and if the process hangs or is killed the last lines are lost.
    /// In tests (LEMORA_BREADCRUMB) the steps also go to the breadcrumb file.
    /// </summary>
    public static class ExitDiag
    {
        private static readonly object Lock = new();
        private static string? _file;

        /// <summary>called at startup with the chosen log folder (AppData or portable)</summary>
        public static void Init(string logDir) => _file = Path.Combine(logDir, "exit-trace.log");

        public static void Mark(string what)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}][T:{Environment.CurrentManagedThreadId}] EXIT: {what}{Environment.NewLine}";
                lock (Lock)
                {
                    if (_file != null) File.AppendAllText(_file, line);
                    var crumb = DevOnly.Env("LEMORA_BREADCRUMB");   // development only: in the elevated copy it wrote as administrator wherever the variable said (security hardening)
                    if (!string.IsNullOrEmpty(crumb)) File.AppendAllText(crumb, line);
                }
            }
            catch { /* diagnostics only */ }
        }
    }
}
