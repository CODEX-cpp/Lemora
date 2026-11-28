using System;
using Lemora.Model.Protocol.Base;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Activity log: successful, dropped, closed and failed connections, with the reason. Always written, at any log level
    /// (it is a Warning line and Base, the lowest level, writes warnings). Every line goes to disk at once (SimpleLogHelper opens and closes the file).
    /// Never passwords: only entry name, protocol and address.
    /// </summary>
    public static class ActivityLog
    {
        public const string Prefix = "Activity log: ";

        public static void Write(string message)
        {
            try { SimpleLogHelper.Warning(Prefix + message); } catch { /* the log must never stop a session */ }
        }

        public static string Describe(ProtocolBase s)
            => s is ProtocolBaseWithAddressPort p ? $"{s.DisplayName} ({s.Protocol} {p.Address}:{p.Port})" : $"{s.DisplayName} ({s.Protocol})";

        /// <summary>
        /// like <see cref="Describe"/> plus the user used (never the password): with "Change credentials" the user changes and before, the log did not show
        /// which one had worked (e.g. domain user rejected, local user succeeded)
        /// </summary>
        private static string DescribeWithUser(ProtocolBase s)
        {
            if (s is not ProtocolBaseWithAddressPortUserPwd u || string.IsNullOrWhiteSpace(u.UserName)) return Describe(s);
            var domain = s is Lemora.Model.Protocol.RDP r && !string.IsNullOrWhiteSpace(r.Domain) && !u.UserName.Contains('\\') && !u.UserName.Contains('@') ? r.Domain + "\\" : "";
            return $"{Describe(s)} as {domain}{u.UserName}";
        }

        /// <summary>
        /// the same facts as the log, for whoever must react (the server for Claude): kind "collegata" (connected), "ricollegata" (reconnected), "caduta" (dropped), "non_riuscita" (failed), "annullata" (cancelled):
        /// these kind values are identifiers compared by the MCP server, keep them as they are.
        /// Nothing secret may be written in the reason.
        /// </summary>
        public static event Action<ProtocolBase, string, string?>? Happened;

        /// <summary>only the notification, without a log line (e.g. credential prompt cancelled)</summary>
        public static void Notify(ProtocolBase s, string kind, string? reason)
        {
            try { Happened?.Invoke(s, kind, reason); } catch { /* listeners must never stop a session */ }
        }

        public static void Connected(ProtocolBase s, bool again)
        {
            Write((again ? "Reconnected to " : "Connected to ") + DescribeWithUser(s));
            Notify(s, again ? "ricollegata" : "collegata", null);
        }

        public static void Disconnected(ProtocolBase s, string? reason, TimeSpan? lasted)
        {
            var why = string.IsNullOrWhiteSpace(reason) ? "connection dropped, unknown reason" : reason;
            Write($"Disconnected from {DescribeWithUser(s)} ({why})" + (lasted is { } t ? $" after {Duration(t)}" : ""));
            Notify(s, "caduta", why);
        }

        public static void Failed(ProtocolBase s, string? reason)
        {
            var why = string.IsNullOrWhiteSpace(reason) ? "unknown reason" : reason;
            Write($"Connection to {DescribeWithUser(s)} failed ({why})");
            Notify(s, "non_riuscita", why);
        }

        private static string Duration(TimeSpan t) => t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";
    }
}
