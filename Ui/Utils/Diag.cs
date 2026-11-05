using System;
using System.Collections.Generic;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Righe diagnostiche dettagliate (tempi di avvio, VNC, RDP, tastiera): si scrivono nel log solo con la "modalita' diagnostica"
    /// (Impostazioni generali) o con LEMORA_DIAG=1. Prima che la configurazione sia letta le righe restano in coda
    /// e si scrivono (o si scartano) appena si sa se la modalita' e' accesa. Gli errori veri restano sempre nel log normale.
    /// </summary>
    public static class Diag
    {
        private static readonly object Lock = new();
        private static readonly List<string> Pending = new();
        private static bool? _enabled;

        public static bool Enabled { get { lock (Lock) return _enabled == true; } }

        public static void Log(string message)
        {
            lock (Lock)
            {
                if (_enabled == null)
                {
                    if (Pending.Count < 500) Pending.Add(message);
                    return;
                }
                if (_enabled == false) return;
            }
            SimpleLogHelper.Warning(message);
        }

        /// <summary>
        /// I tre livelli del log delle Impostazioni: 0 Base (avvisi ed errori), 1 Medio (anche gli eventi, predefinito), 2 Approfondito (tutto, comprese le righe
        /// diagnostiche). Il Registro attivita' (ActivityLog) si scrive a tutti e tre: e' una riga Warning e Base e' il livello piu' basso.
        /// </summary>
        public static void ApplyLogDetail(int detail)
        {
            SimpleLogHelper.WriteLogLevel = detail <= 0 ? SimpleLogHelper.EnumLogLevel.Warning : SimpleLogHelper.EnumLogLevel.Debug;
            SimpleLogHelper.PrintLogLevel = SimpleLogHelper.WriteLogLevel;
            Configure(detail >= 2);
        }

        public static string LogDetailName(int detail) => detail <= 0 ? "Base" : detail >= 2 ? "Approfondito" : "Medio";

        /// <summary>profili di prima della scelta a tre livelli: diagnostica accesa = Approfondito, Debug/Info = Medio, il resto = Base</summary>
        public static int LogDetailFromOld(int oldLevel, bool diagnosticMode)
            => diagnosticMode ? 2 : oldLevel <= (int)SimpleLogHelper.EnumLogLevel.Info ? 1 : 0;

        /// <summary>chiamata quando la configurazione e' nota; accende o spegne la modalita' e svuota la coda</summary>
        public static void Configure(bool on)
        {
            on = on || Environment.GetEnvironmentVariable("LEMORA_DIAG") == "1";
            string[] queued;
            lock (Lock)
            {
                _enabled = on;
                queued = Pending.ToArray();
                Pending.Clear();
            }
            if (on) foreach (var m in queued) SimpleLogHelper.Warning(m);
        }
    }
}
