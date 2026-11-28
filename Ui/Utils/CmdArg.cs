using System;
using System.IO;
using System.Linq;
using System.Text;
using Lemora.Model.Protocol.Base;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// values of an entry (user, password, address...) put on a command line: they can come from a .mora file, an import
    /// or a Teams server, so they must not be able to add options or commands (security hardening)
    /// </summary>
    public static class CmdArg
    {
        /// <summary>a single argument according to the Windows rules (CommandLineToArgvW/MSVCRT, also used by PuTTY/KiTTY)</summary>
        public static string Quote(string? value)
        {
            value ??= "";
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes); backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }

        private const string Separators = ";&|<>^";

        /// <summary>
        /// Checks the values of the %LM_...% macros used in an external program template. Returns the macro with the dangerous
        /// value, or null. Always forbidden: control characters and double quotes. Where the macro is not in quotes,
        /// also spaces and ; &amp; | &lt; &gt; ^. With Windows Terminal also ';' (separates wt commands) everywhere.
        /// </summary>
        public static string? UnsafeMacro(ProtocolBase protocol, string template, string exePath)
        {
            var isWt = string.Equals(Path.GetFileName(exePath), "wt.exe", StringComparison.OrdinalIgnoreCase);
            foreach (var kv in OtherNameAttributeExtensions.GetOtherNames(protocol.GetType()))
            {
                var macro = kv.Value;
                var at = template.IndexOf(macro, StringComparison.Ordinal);
                if (at < 0) continue;
                var value = protocol.GetType().GetProperty(kv.Key)?.GetValue(protocol)?.ToString() ?? "";
                if (value.Length == 0) continue;
                if (value.Any(c => char.IsControl(c) || c == '"') || (isWt && value.Contains(';'))) return macro;
                for (; at >= 0; at = template.IndexOf(macro, at + macro.Length, StringComparison.Ordinal))
                {
                    var end = at + macro.Length;
                    var quoted = at > 0 && template[at - 1] == '"' && end < template.Length && template[end] == '"';
                    if (!quoted && value.Any(c => char.IsWhiteSpace(c) || Separators.IndexOf(c) >= 0)) return macro;
                }
            }
            return null;
        }
    }
}
