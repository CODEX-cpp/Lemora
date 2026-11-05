using System;
using System.IO;

namespace Lemora.Utils
{
    /// <summary>
    /// Traccia della chiusura dell'app: ogni passo va SUBITO su file (exit-trace.log accanto ai log), perche' il registro normale
    /// si scrive a blocchi e se il processo resta appeso o viene ucciso le ultime righe si perdono.
    /// Nelle prove (LEMORA_BREADCRUMB) i passi vanno anche nel file delle briciole.
    /// </summary>
    public static class ExitDiag
    {
        private static readonly object Lock = new();
        private static string? _file;

        /// <summary>chiamato all'avvio con la cartella dei log scelta (AppData o portatile)</summary>
        public static void Init(string logDir) => _file = Path.Combine(logDir, "exit-trace.log");

        public static void Mark(string what)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}][T:{Environment.CurrentManagedThreadId}] USCITA: {what}{Environment.NewLine}";
                lock (Lock)
                {
                    if (_file != null) File.AppendAllText(_file, line);
                    var crumb = Environment.GetEnvironmentVariable("LEMORA_BREADCRUMB");
                    if (!string.IsNullOrEmpty(crumb)) File.AppendAllText(crumb, line);
                }
            }
            catch { /* solo diagnosi */ }
        }
    }
}
