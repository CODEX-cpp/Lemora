using System;
using System.Diagnostics;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Diagnosi dell'avvio lento: ogni segno scrive nel registro quanti millisecondi sono passati dall'avvio del processo
    /// (e dal segno precedente), cosi' si vede in quale fase l'avvio perde tempo. Livello Warning come le altre righe "-DIAG".
    /// </summary>
    public static class StartDiag
    {
        private static readonly DateTime ProcessStart = SafeStart();
        private static DateTime _last = DateTime.MinValue;
        private static readonly object ProcessStartLock = new();

        private static DateTime SafeStart()
        {
            try { return Process.GetCurrentProcess().StartTime; } catch { return DateTime.Now; }
        }

        public static void Mark(string what)
        {
            try
            {
                var now = DateTime.Now;
                var step = _last == DateTime.MinValue ? 0 : (now - _last).TotalMilliseconds;
                _last = now;
                var line = $"START-DIAG: +{(now - ProcessStart).TotalMilliseconds:0} ms dall'avvio (+{step:0} ms dal segno precedente): {what}";
                Lemora.Utils.Diag.Log(line);
                // solo prove: ogni segno subito su file, anche prima che il registro sia pronto (serve a vedere dove si ferma un avvio bloccato)
                var crumb = Environment.GetEnvironmentVariable("LEMORA_BREADCRUMB");
                if (!string.IsNullOrEmpty(crumb)) lock (ProcessStartLock) System.IO.File.AppendAllText(crumb, $"[T:{Environment.CurrentManagedThreadId}] {line}{Environment.NewLine}");
            }
            catch { /* solo diagnosi */ }
        }
    }
}
