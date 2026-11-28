using System;
using System.Collections.Generic;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Detailed diagnostic lines (startup times, VNC, RDP, keyboard): written to the log only with "diagnostic mode"
    /// (general settings) or with LEMORA_DIAG=1. Before the configuration is read the lines stay queued
    /// and are written (or dropped) as soon as it is known whether the mode is on. Real errors always stay in the normal log.
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
        /// The three log levels in Settings: 0 Base (warnings and errors), 1 Medio (events too, default), 2 Approfondito (everything, including the
        /// diagnostic lines). The activity log (ActivityLog) is written at all three: it is a Warning line and Base is the lowest level.
        /// </summary>
        public static void ApplyLogDetail(int detail)
        {
            SimpleLogHelper.WriteLogLevel = detail <= 0 ? SimpleLogHelper.EnumLogLevel.Warning : SimpleLogHelper.EnumLogLevel.Debug;
            SimpleLogHelper.PrintLogLevel = SimpleLogHelper.WriteLogLevel;
            Configure(detail >= 2);
        }

        public static string LogDetailName(int detail) => detail <= 0 ? "Base" : detail >= 2 ? "Approfondito" : "Medio";

        /// <summary>profiles from before the three-level choice: diagnostics on = Approfondito, Debug/Info = Medio, the rest = Base</summary>
        public static int LogDetailFromOld(int oldLevel, bool diagnosticMode)
            => diagnosticMode ? 2 : oldLevel <= (int)SimpleLogHelper.EnumLogLevel.Info ? 1 : 0;

        /// <summary>called when the configuration is known; turns the mode on or off and empties the queue</summary>
        public static void Configure(bool on)
        {
            on = on || Lemora.Utils.DevOnly.Env("LEMORA_DIAG") == "1";
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
