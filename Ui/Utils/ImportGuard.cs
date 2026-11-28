using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Shawn.Utils;
using Lemora.Service;

namespace Lemora.Utils
{
    /// <summary>
    /// Import from file (JSON, .db): entries that start programs on this PC (security hardening).
    /// Before, they came in unseen and ran at the first open.
    /// Now they are listed with the real program and arguments (not the display name nor the ******) and the user chooses.
    /// </summary>
    public static class ImportGuard
    {
        private const int MaxLine = 300, MaxItems = 10;

        /// <summary>one line for each thing that would start a local program; empty = nothing to ask</summary>
        public static List<string> Describe(IEnumerable<ProtocolBase> list)
        {
            var lines = new List<string>();
            foreach (var p in list)
            {
                var name = string.IsNullOrWhiteSpace(p.DisplayName) ? "?" : p.DisplayName.Trim();
                if (p is LocalApp app)
                {
                    // program and arguments computed as at launch (%LM_*% macros already replaced with the entry's address, user..., the "secrets" too):
                    // the file can hide the real command in those fields (security hardening)
                    lines.Add(Cut($"{name}: {IoC.Translate("import_guard_app")} {app.GetExePath()} {app.GetArguments(false).Trim()}"));
                }
                if (!string.IsNullOrWhiteSpace(p.CommandBeforeConnected))
                    lines.Add(Cut($"{name}: {IoC.Translate("import_guard_before")} {Expanded(p, p.CommandBeforeConnected)}"));
                if (!string.IsNullOrWhiteSpace(p.CommandAfterDisconnected))
                    lines.Add(Cut($"{name}: {IoC.Translate("import_guard_after")} {Expanded(p, p.CommandAfterDisconnected)}"));
                // network paths (\\server\...): Windows connects to that server by itself, sending the user's password hash (security hardening)
                foreach (var f in NetworkPaths(p))
                    lines.Add(Cut($"{name}: {IoC.Translate("import_guard_unc")} {f}"));
                // PuTTY/KiTTY session file: its keys end up in the session used ("local" proxy = command run here). Security hardening.
                if (p is Lemora.Utils.PuTTY.IPuttyConnectable pc)
                    foreach (var f in new[] { pc.ExternalSessionConfigPath, pc.ExternalKittySessionConfigPath }.Where(f => !string.IsNullOrWhiteSpace(f)))
                        lines.Add(Cut($"{name}: {IoC.Translate("import_guard_session")} {f}"));
                // RD gateway chosen by the file: it would receive the entry's credentials (or the folder ones offered with a click). Security hardening.
                if (HasFileGateway(p, out var gw))
                    lines.Add(Cut($"{name}: {IoC.Translate("import_guard_gateway")} {gw}"));
            }
            return lines;
        }

        public static bool HasFileGateway(ProtocolBase p, out string host)
        {
            host = p is RDP { GatewayMode: EGatewayMode.UseTheseGatewayServerSettings or EGatewayMode.AutomaticallyDetectGatewayServerSettings } r
                ? (string.IsNullOrWhiteSpace(r.GatewayHostName) ? "(automatico)" : r.GatewayHostName.Trim()) : "";
            return host.Length > 0;
        }

        /// <summary>removes the local apps and clears the before/after commands; returns how many entries were skipped</summary>
        public static int Strip(List<ProtocolBase> list)
        {
            int skipped = list.RemoveAll(p => p is LocalApp);
            foreach (var p in list)
            {
                p.CommandBeforeConnected = "";
                p.CommandAfterDisconnected = "";
                p.HideCommandBeforeConnectedWindow = false;
                if (p is Lemora.Utils.PuTTY.IPuttyConnectable pc) { pc.ExternalSessionConfigPath = ""; pc.ExternalKittySessionConfigPath = ""; }
                if (p is ProtocolBaseWithAddressPortUserPwd k)
                {
                    if (IsUnc(k.PrivateKey)) k.PrivateKey = "";
                    foreach (var u in k.Users) if (IsUnc(u.PrivateKey)) u.PrivateKey = "";
                    foreach (var c in k.AlternateCredentials) if (IsUnc(c.PrivateKeyPath)) c.PrivateKeyPath = "";
                }
                if (p is RDP r && HasFileGateway(r, out _))
                {
                    r.GatewayMode = EGatewayMode.DoNotUseGateway;
                    r.GatewayHostName = "";
                }
            }
            return skipped;
        }

        /// <summary>
        /// If the file contains programs to start, shows them and asks: "Import all" keeps them, "Import without programs"
        /// (also closing the window) removes them from list. Call before saving.
        /// </summary>
        public static void Ask(List<ProtocolBase> list)
        {
            foreach (var p in list) Clean(p);
            var lines = Describe(list);
            if (lines.Count == 0) return;
            var shown = string.Join("\n\n", lines.Take(MaxItems).Select(l => "• " + l));
            if (lines.Count > MaxItems) shown += "\n\n" + IoC.Translate("import_guard_more", (lines.Count - MaxItems).ToString());
            var text = IoC.Translate("import_guard_text") + "\n\n" + shown + "\n\n" + IoC.Translate("import_guard_question");
            bool all = MessageBoxHelper.Confirm(text, IoC.Translate("import_guard_all"), IoC.Translate("import_guard_without"), IoC.Translate("import_guard_title"));
            SimpleLogHelper.Info($"Import with local programs ({lines.Count}): {(all ? "imported" : "removed")}");
            if (!all) Strip(list);
        }

        /// <summary>the command with %LM_*% replaced as the program sees them (environment variables); the original text stays in front if different</summary>
        private static string Expanded(ProtocolBase p, string cmd)
        {
            var x = OtherNameAttributeExtensions.Replace(p, ProtocolBaseWithAddressPort.NormalizeMacros(cmd));
            return x == cmd ? cmd : $"{cmd}  →  {x}";
        }

        /// <summary>
        /// cleanup for every import (JSON, CSV, .rdp, .db), without asking: no "lv1:" ciphertexts taken from another database and no links
        /// to this PC's folder credentials, which the file knows only by id (security hardening). Folder credentials are linked again from the UI
        /// </summary>
        internal static void Clean(ProtocolBase p)
        {
            p.DropVaultTextsFromOutside();
            if (p is ProtocolBaseWithAddressPortUserPwd u)
            {
                u.ActiveFolderCredentialIds = new List<string>();
                if (u.DefaultUserKey.StartsWith("F:", System.StringComparison.Ordinal)) u.DefaultUserKey = "";
            }
        }

        /// <summary>
        /// network path: two separators at the start in any combination (\\, //, /\, \/) or an NT device prefix (\??\): Windows
        /// treats them all as \\server\share (security hardening)
        /// </summary>
        internal static bool IsUnc(string? s)
        {
            if (s == null) return false;
            var t = s.Trim();
            static bool Sep(char c) => c == '\\' || c == '/';
            return (t.Length >= 2 && Sep(t[0]) && Sep(t[1])) || t.StartsWith(@"\??\", System.StringComparison.Ordinal);
        }

        /// <summary>private keys and programs on a network path (local apps are already listed with their program)</summary>
        private static IEnumerable<string> NetworkPaths(ProtocolBase p)
        {
            if (p is LocalApp app && IsUnc(app.GetExePath())) yield return app.GetExePath();
            if (p is ProtocolBaseWithAddressPortUserPwd k)
            {
                if (IsUnc(k.PrivateKey)) yield return k.PrivateKey;
                foreach (var u in k.Users) if (IsUnc(u.PrivateKey)) yield return u.PrivateKey;
                foreach (var c in k.AlternateCredentials) if (IsUnc(c.PrivateKeyPath)) yield return c.PrivateKeyPath;
            }
        }

        private static string Cut(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= MaxLine ? s : s.Substring(0, MaxLine) + "…";
        }
    }
}
