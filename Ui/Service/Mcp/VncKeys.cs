using System;
using System.Collections.Generic;

namespace Lemora.Service.Mcp
{
    /// <summary>from characters and key names (Italian or English) to the X11 keysyms the VNC protocol transmits</summary>
    public static class VncKeys
    {
        private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["invio"] = 0xff0d, ["enter"] = 0xff0d, ["return"] = 0xff0d,
            ["tab"] = 0xff09, ["esc"] = 0xff1b, ["escape"] = 0xff1b,
            ["backspace"] = 0xff08, ["indietro"] = 0xff08,
            ["canc"] = 0xffff, ["del"] = 0xffff, ["delete"] = 0xffff, ["ins"] = 0xff63, ["insert"] = 0xff63,
            ["home"] = 0xff50, ["inizio"] = 0xff50, ["end"] = 0xff57, ["fine"] = 0xff57,
            ["pagesu"] = 0xff55, ["pageup"] = 0xff55, ["pggiu"] = 0xff56, ["pagegiu"] = 0xff56, ["pagedown"] = 0xff56, ["pgsu"] = 0xff55,
            ["su"] = 0xff52, ["up"] = 0xff52, ["giu"] = 0xff54, ["giù"] = 0xff54, ["down"] = 0xff54,
            ["sinistra"] = 0xff51, ["left"] = 0xff51, ["destra"] = 0xff53, ["right"] = 0xff53,
            ["spazio"] = 0x20, ["space"] = 0x20, ["stamp"] = 0xff61, ["printscreen"] = 0xff61, ["menu"] = 0xff67,
        };

        private static readonly Dictionary<string, uint> Modifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0xffe3, ["control"] = 0xffe3, ["alt"] = 0xffe9, ["altgr"] = 0xfe03,
            ["shift"] = 0xffe1, ["maiusc"] = 0xffe1, ["win"] = 0xffeb, ["super"] = 0xffeb, ["meta"] = 0xffeb,
        };

        /// <summary>Latin-1 matches the keysyms; the rest of Unicode uses 0x01000000 + code point</summary>
        public static uint FromChar(char c) => c switch
        {
            '\n' => 0xff0d,
            '\t' => 0xff09,
            < (char)0x100 => c,
            _ => 0x01000000u | c,
        };

        /// <summary>"ctrl+alt+del", "win+r", "f5", "a": modifiers first, a single final key</summary>
        public static bool TryParseChord(string text, out uint[] modifiers, out uint key, out string bad)
        {
            var mods = new List<uint>();
            modifiers = Array.Empty<uint>(); key = 0; bad = "";
            var parts = text.Trim().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (text.Trim() == "+") parts = new[] { "+" };
            if (parts.Length == 0) { bad = "(empty)"; return false; }
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                var last = i == parts.Length - 1;
                if (!last)
                {
                    if (!Modifiers.TryGetValue(p, out var m)) { bad = p; return false; }
                    mods.Add(m);
                    continue;
                }
                if (Modifiers.TryGetValue(p, out var alone)) key = alone;
                else if (Named.TryGetValue(p, out var k)) key = k;
                else if (p.Length is 2 or 3 && (p[0] is 'f' or 'F') && int.TryParse(p.Substring(1), out var f) && f is >= 1 and <= 12) key = 0xffbe + (uint)(f - 1);
                else if (p.Length == 1) key = FromChar(char.ToLowerInvariant(p[0]));   // with modifiers the lowercase letter is needed (ctrl+c, not ctrl+C)
                else { bad = p; return false; }
            }
            modifiers = mods.ToArray();
            return true;
        }
    }
}
