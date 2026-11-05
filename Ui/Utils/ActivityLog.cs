using System;
using Lemora.Model.Protocol.Base;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Registro attivita': collegamenti riusciti, caduti, chiusi e falliti, con il motivo. Si scrive sempre, a qualsiasi livello di log
    /// (e' una riga Warning e Base, il livello piu' basso, scrive gli avvisi). Ogni riga va subito su disco (SimpleLogHelper apre e chiude il file).
    /// Mai password: solo nome della voce, protocollo e indirizzo.
    /// </summary>
    public static class ActivityLog
    {
        public const string Prefix = "Registro attività: ";

        public static void Write(string message)
        {
            try { SimpleLogHelper.Warning(Prefix + message); } catch { /* il registro non deve mai fermare una sessione */ }
        }

        public static string Describe(ProtocolBase s)
            => s is ProtocolBaseWithAddressPort p ? $"{s.DisplayName} ({s.Protocol} {p.Address}:{p.Port})" : $"{s.DisplayName} ({s.Protocol})";

        public static void Connected(ProtocolBase s, bool again) => Write((again ? "Ricollegato a " : "Connesso a ") + Describe(s));
        public static void Disconnected(ProtocolBase s, string? reason, TimeSpan? lasted)
            => Write($"Disconnesso da {Describe(s)} ({(string.IsNullOrWhiteSpace(reason) ? "connessione caduta, motivo non noto" : reason)})"
                     + (lasted is { } t ? $" dopo {Duration(t)}" : ""));
        public static void Failed(ProtocolBase s, string? reason)
            => Write($"Collegamento a {Describe(s)} non riuscito ({(string.IsNullOrWhiteSpace(reason) ? "motivo non noto" : reason)})");

        private static string Duration(TimeSpan t) => t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";
    }
}
