using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>a theme of the built-in terminal: colors of the screen, of the command boxes and the 16 ANSI colors</summary>
    public class TerminalTheme
    {
        public string Key = "", NameKey = "";
        /// <summary>name shown to the user, from the language files</summary>
        public string Name => IoC.Translate(NameKey);
        public string Body = "", Input = "", Background = "", Foreground = "", Cursor = "", Selection = "", Header = "", HeaderText = "", Border = "";
        public string[] Ansi = Array.Empty<string>();
    }

    /// <summary>
    /// Settings of the built-in SSH terminal. They live in the encrypted database (AppDbStore, key "terminal"):
    /// read once and kept in memory, never inside a binding.
    /// </summary>
    public class TerminalSettings
    {
        // appearance
        public string Theme = "fluent";
        public string FontFamily = "Cascadia Mono";
        public double FontSize = 13.5;
        public double LineHeight = 1.15;
        public string CursorStyle = "block";      // block | bar | underline
        public bool CursorBlink = true;
        // special keys
        public bool SpecialKeysBar = false;
        /// <summary>keys shown in the bar: ctrlc, ctrld, ctrlz, esc, tab, fkeys, arrows, custom</summary>
        public string SpecialKeys = "ctrlc,ctrld,ctrlz,esc,tab";
        public string CustomKeyLabel = "";
        public string CustomKeyText = "";
        // behavior
        public int Scrollback = 10000;
        public string Bell = "visual";            // none | visual | sound
        public bool CopyOnSelect = false;
        public bool WarnMultiLinePaste = true;
        // command blocks
        /// <summary>on by default: after connecting, a line that enables the command markers (bash/zsh) is sent to the server</summary>
        public bool ShellIntegration = true;
        /// <summary>version of the saved settings (2 = shell integration on by default)</summary>
        public int Ver = 2;
        /// <summary>automatically collapses outputs with at least this many lines (0 = never)</summary>
        public int AutoCollapseLines = 0;
        public bool ShowDurationTime = true;
        public bool ShowCopySaveButtons = true;
        // security
        public bool AllowServerClipboard = false;
        public bool ConfirmLinks = true;
        public bool VerifyHostKey = true;
        // history (of the session)
        public int HistoryLimit = 500;

        // ---------- themes
        private static string[] Std(params string[] a) => a;
        public static readonly TerminalTheme[] Themes =
        {
            new() { Key = "fluent", NameKey = "terminal_theme_fluent", Body = "#0b0b0b", Input = "#1b1b1b", Background = "#101010", Foreground = "#dcdcdc", Cursor = "#60CDFF", Selection = "#60CDFF55", Header = "#303030", HeaderText = "#ffffff", Border = "#6a6a6a",
                Ansi = Std("#1e1e1e", "#ff9b94", "#6ccb5f", "#f4d35e", "#7aa2f7", "#c792ea", "#56d4dd", "#d4d4d4", "#6a6a6a", "#ffb3ad", "#8ee07f", "#ffe27a", "#9ab8ff", "#d7a8f5", "#7ee5ec", "#ffffff") },
            new() { Key = "caldo", NameKey = "terminal_theme_warm", Body = "#151311", Input = "#26221f", Background = "#1c1a18", Foreground = "#e6dfd4", Cursor = "#e0a96d", Selection = "#e0a96d44", Header = "#35302b", HeaderText = "#f3ece0", Border = "#6f665c",
                Ansi = Std("#26221f", "#e8857d", "#9bc27a", "#e3b866", "#7ea4c9", "#c39ad0", "#7fc4bd", "#d9d0c2", "#6f665c", "#f09a92", "#b0d68f", "#f0cc85", "#98bddf", "#d6b1e0", "#9ad8d1", "#f6efe4") },
            new() { Key = "blunotte", NameKey = "terminal_theme_night_blue", Body = "#0a0f1a", Input = "#162038", Background = "#0e1422", Foreground = "#cfd8ea", Cursor = "#7aa2f7", Selection = "#7aa2f744", Header = "#1f2a44", HeaderText = "#e8eefc", Border = "#4a5a80",
                Ansi = Std("#161d30", "#ff8f9a", "#7fd6a0", "#f2cf6b", "#6f9bff", "#b794f4", "#5ed1e6", "#c5cfe4", "#4a5a80", "#ffaab3", "#9ae8b8", "#ffe08a", "#8fb2ff", "#cbaaf8", "#82e0f0", "#f4f7ff") },
            new() { Key = "classico", NameKey = "terminal_theme_classic", Body = "#000000", Input = "#0a1a0c", Background = "#000000", Foreground = "#4cd964", Cursor = "#4cd964", Selection = "#4cd96444", Header = "#0d2a12", HeaderText = "#7dff95", Border = "#2f7f3f",
                Ansi = Std("#000000", "#ff5f5f", "#4cd964", "#e6e65c", "#5f8fff", "#d268d2", "#4fd6d6", "#b8e8c0", "#2f7f3f", "#ff8080", "#7dff95", "#ffff80", "#80a8ff", "#e68ae6", "#80f0f0", "#e8ffec") },
            new() { Key = "contrasto", NameKey = "terminal_theme_high_contrast", Body = "#000000", Input = "#1a1a1a", Background = "#000000", Foreground = "#ffffff", Cursor = "#ffff00", Selection = "#ffffff55", Header = "#1a1a1a", HeaderText = "#ffffff", Border = "#ffffff",
                Ansi = Std("#000000", "#ff5555", "#55ff55", "#ffff55", "#5599ff", "#ff55ff", "#55ffff", "#ffffff", "#808080", "#ff8080", "#80ff80", "#ffff80", "#80b0ff", "#ff80ff", "#80ffff", "#ffffff") },
            new() { Key = "chiaro", NameKey = "terminal_theme_light", Body = "#ffffff", Input = "#f0f0f0", Background = "#fafafa", Foreground = "#383a42", Cursor = "#0067c0", Selection = "#0067c033", Header = "#e5e5e5", HeaderText = "#1f1f1f", Border = "#a0a1a7",
                Ansi = Std("#383a42", "#d03a30", "#2f8a2f", "#a06a00", "#2a6fd6", "#8e24aa", "#0b7f9c", "#a0a1a7", "#4f525e", "#e06c75", "#4e9a4e", "#b8860b", "#4a8cf0", "#a64ac9", "#2a9db8", "#ffffff") },
        };

        public TerminalTheme CurrentTheme => Themes.FirstOrDefault(x => x.Key == Theme) ?? Themes[0];

        /// <summary>what the terminal page needs: xterm options, theme colors and switches</summary>
        public object ToPage() => new
        {
            theme = CurrentTheme,
            fontFamily = $"\"{FontFamily}\", Consolas, monospace",
            fontSize = FontSize,
            lineHeight = LineHeight,
            cursorStyle = CursorStyle,
            cursorBlink = CursorBlink,
            scrollback = Scrollback,
            autoCollapse = AutoCollapseLines,
            showDuration = ShowDurationTime,
            showButtons = ShowCopySaveButtons,
            shellIntegration = ShellIntegration,
            specialKeysBar = SpecialKeysBar,
            specialKeys = SpecialKeys.Split(',', StringSplitOptions.RemoveEmptyEntries),
            customKeyLabel = CustomKeyLabel,
            customKeyText = CustomKeyText,
            bell = Bell,
            copyOnSelect = CopyOnSelect,
            allowServerClipboard = AllowServerClipboard,
            confirmLinks = ConfirmLinks,
            historyLimit = HistoryLimit,
        };

        // ---------- storage
        private const string DbKey = "terminal";
        private static TerminalSettings? _current;
        private static bool _fromDb;

        /// <summary>current settings (read from the database on first request; if the database is not ready, defaults are used and it is retried next time)</summary>
        public static TerminalSettings Current
        {
            get
            {
                if (_current != null && _fromDb) return _current;
                TerminalSettings? s = null;
                try
                {
                    if (AppDbStore.TryGet(DbKey, out var json))
                    {
                        _fromDb = true;
                        if (!string.IsNullOrEmpty(json)) s = JsonConvert.DeserializeObject<TerminalSettings>(json);
                    }
                }
                catch (Exception e) { SimpleLogHelper.Warning("Terminal: settings not readable, using the defaults. " + e.Message); _fromDb = true; }
                return _current = Sanitize(s ?? new TerminalSettings());
            }
        }

        public static event Action? Changed;

        public static void Save(TerminalSettings s)
        {
            _current = Sanitize(s); _fromDb = true;
            try { AppDbStore.Set(DbKey, JsonConvert.SerializeObject(_current)); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
            Changed?.Invoke();
        }

        public static TerminalSettings Defaults() => new();

        private static TerminalSettings Sanitize(TerminalSettings s)
        {
            if (s.Ver < 2) { s.ShellIntegration = true; s.Ver = 2; }     // old saves: the "off" value was just the default at the time
            if (Themes.All(x => x.Key != s.Theme)) s.Theme = "fluent";
            if (string.IsNullOrWhiteSpace(s.FontFamily)) s.FontFamily = "Cascadia Mono";
            s.FontSize = Math.Min(28, Math.Max(8, s.FontSize));
            s.LineHeight = Math.Min(2, Math.Max(1, s.LineHeight));
            if (s.CursorStyle != "bar" && s.CursorStyle != "underline") s.CursorStyle = "block";
            s.Scrollback = Math.Min(100000, Math.Max(500, s.Scrollback));
            s.AutoCollapseLines = Math.Max(0, s.AutoCollapseLines);
            if (s.Bell != "none" && s.Bell != "sound") s.Bell = "visual";
            s.HistoryLimit = Math.Min(100000, Math.Max(10, s.HistoryLimit));
            s.SpecialKeys ??= ""; s.CustomKeyLabel ??= ""; s.CustomKeyText ??= "";
            return s;
        }

        public TerminalSettings Clone() => JsonConvert.DeserializeObject<TerminalSettings>(JsonConvert.SerializeObject(this))!;
    }
}
