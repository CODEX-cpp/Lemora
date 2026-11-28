using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Shawn.Utils.Wpf;

namespace Lemora.Service
{
    public class ThemeService
    {
        private readonly ResourceDictionary _appResourceDictionary;
        public ThemeConfig CurrentTheme;
        public Dictionary<string, ThemeConfig> Themes { get; } = new Dictionary<string, ThemeConfig>();
        public ThemeService(ResourceDictionary appResourceDictionary, ThemeConfig defaultTheme)
        {
            _appResourceDictionary = appResourceDictionary;
            foreach (var (name, t) in Presets()) { t.ThemeName = name; Themes.Add(name, t); }   // the name finds the light themes' own colors

            // former names (in English, one with the old project's name): whoever had chosen them gets the matching theme
            if (OldNames.TryGetValue(defaultTheme.ThemeName ?? "", out var renamed)) defaultTheme.ThemeName = renamed;
            // a theme from the list always gets the updated colors (the colors saved in the profile might be from an old version)
            if (Themes.TryGetValue(defaultTheme.ThemeName ?? "", out var preset)) CopyColors(preset, defaultTheme);
            else { defaultTheme.ThemeName = "Grafite"; CopyColors(Themes["Grafite"], defaultTheme); }

            CurrentTheme = defaultTheme;
            ApplyTheme(defaultTheme);
        }

        private static readonly Dictionary<string, string> OldNames = new()
        {
            ["FluentDark"] = "Grafite", ["Dark"] = "Notte", ["Asphalt"] = "Asfalto", ["Light"] = "Carta",
            ["SecretKey"] = "Lavanda", ["Greystone"] = "Ardesia", ["Wine"] = "Vino", ["Forest"] = "Bosco", ["Soil"] = "Terra",
        };

        public static void CopyColors(ThemeConfig from, ThemeConfig to)
        {
            to.PrimaryMidColor = from.PrimaryMidColor; to.PrimaryLightColor = from.PrimaryLightColor; to.PrimaryDarkColor = from.PrimaryDarkColor;
            to.PrimaryTextColor = from.PrimaryTextColor; to.AccentMidColor = from.AccentMidColor; to.AccentLightColor = from.AccentLightColor;
            to.AccentDarkColor = from.AccentDarkColor; to.AccentTextColor = from.AccentTextColor; to.BackgroundColor = from.BackgroundColor;
            to.BackgroundTextColor = from.BackgroundTextColor;
        }

        // ---------------------------------------------------------------- themes
        // Dark themes are built from a rule: the tint of the grays (hue h, saturation s) and an accent chosen in harmony with it,
        // all shades from the same lightness proportions. Light themes have hand-made colors (LightThemes below).
        // Contrasts are checked by Tests/Service/ThemePaletteTests.

        /// <summary>ordered list: first the four dark ones, then the light ones</summary>
        public static List<(string Name, ThemeConfig Theme)> Presets() => new()
        {
            ("Grafite", Dark(210, 0.03, 199, 0.95, 0.68)),   // neutral gray, sky blue accent (the original theme)
            ("Notte", Dark(226, 0.24, 262, 0.72, 0.76)),     // midnight blue, lavender accent (analogous)
            ("Asfalto", Dark(30, 0.05, 38, 0.92, 0.60)),     // warm asphalt, road-sign amber accent
            ("Vermiglio", Site()),                           // the colors of the Lemora website (dark theme): near black, vermilion accent
            ("Carta", FromTokens(LightThemes["Carta"])),       // warm paper, blue
            ("Ghiaccio", FromTokens(LightThemes["Ghiaccio"])), // cold light blue, brick
            ("Lavanda", FromTokens(LightThemes["Lavanda"])),   // lilac, ochre
            ("Ardesia", FromTokens(LightThemes["Ardesia"])),   // blue-gray, burnt orange
            ("Vino", FromTokens(LightThemes["Vino"])),         // soft pink, petrol green
            ("Bosco", FromTokens(LightThemes["Bosco"])),       // sage green, brick red
            ("Terra", FromTokens(LightThemes["Terra"])),       // sand, olive, yellow selection
            ("Classico", FromTokens(LightThemes["Classico"])), // Windows 98/2000 colors (no 3D bevels), navy selection with white text
        };

        // ---------------------------------------------------------------- the colors of every part of the window (all themes)
        // Light themes: every color chosen by hand. Three distinct surface levels in the theme's hue (content lightest, then nav,
        // then frame), accent and selection in a muted OPPOSITE hue, used only for actions and selection; the selected item also
        // gets a 3px bar in the accent color (the selection/panel contrast is low on purpose, so the bar must never be left out).
        // Dark themes: the same parts, computed from their base colors (Derived), with today's transparencies.

        /// <summary>
        /// colors of every part of the window. A color can carry its own transparency (#AARRGGBB). Empty optional fields:
        /// OnSel = text, BarSel = accent, SectionTitle = secondary text, Card = nav, SubCard = nav at 50%, Avatar = selection,
        /// WindowBorder = border, toggle off = transparent with the control border and a knob in the secondary text
        /// </summary>
        public record ThemeTokens(string Frame, string Bar, string Nav, string Content, string Hover, string Selected,
            string Border, string BorderControl, string Divider, string Text, string Text2, string Placeholder,
            string Accent, string AccentHover, string Danger = "#B3261E", string Ok = "#1E7A3E", string OnSel = "", string BarSel = "",
            string Input = "#FFFFFF", string SectionTitle = "", string Card = "", string SubCard = "", string Avatar = "", string WindowBorder = "",
            string ToggleOffTrack = "#00000000", string ToggleOffBorder = "", string ToggleOffKnob = "", string ToggleOnKnob = "#FFFFFF")
        {
            public string SelText => OnSel.Length > 0 ? OnSel : Text;
            public string SelBar => BarSel.Length > 0 ? BarSel : Accent;
        }

        public static readonly Dictionary<string, ThemeTokens> LightThemes = new()
        {
            ["Carta"] = new("#DBD5C9", "#F6F4F0", "#F0EEEA", "#FDFCFB", "#E8E6E1", "#D0DDF0", "#C0BDB7", "#848077", "#E2DFDA", "#23211E", "#55524D", "#6B6964", "#2C5A9D", "#1B488A"),
            ["Ghiaccio"] = new("#C4DBE1", "#EFF6F7", "#E9F0F1", "#FBFDFD", "#E0E8EA", "#EED5D1", "#B6C0C2", "#768386", "#DAE1E3", "#1D2324", "#4D5456", "#636A6C", "#9A463A", "#86352A"),
            ["Lavanda"] = new("#DAD2E5", "#F6F3F9", "#F0EDF3", "#FDFCFE", "#E7E5EB", "#E8DBBB", "#BFBCC4", "#827E89", "#E1DEE5", "#232125", "#545158", "#6A676E", "#70570F", "#5D4700"),
            ["Ardesia"] = new("#C8D8E9", "#F0F5FA", "#EBEFF4", "#FBFDFE", "#E2E7ED", "#F2D5C3", "#B8BFC6", "#79818B", "#DBE0E6", "#1E2226", "#4E5359", "#646A6F", "#9E4502", "#853901"),
            ["Vino"] = new("#E4D0D2", "#F8F3F3", "#F2EDED", "#FEFCFC", "#EAE5E5", "#CAE2D9", "#C3BCBC", "#877E7E", "#E3DEDE", "#242021", "#565151", "#6D6768", "#206553", "#075443"),
            ["Bosco"] = new("#CADBCD", "#F1F6F2", "#EBF0EC", "#FBFDFB", "#E2E8E3", "#F0D5CF", "#B9C0BA", "#7A837B", "#DCE1DD", "#1F231F", "#4F5450", "#656B66", "#8F392A", "#7B2719"),
            ["Terra"] = new("#E0D5BC", "#F8F4EC", "#F2EEE6", "#FEFCF9", "#EAE6DC", "#E9DBB1", "#C3BDB0", "#87806F", "#E3DFD5", "#24211A", "#565248", "#6D695E", "#4C5E08", "#3D4C00"),
            ["Classico"] = new("#C0C0C0", "#C0C0C0", "#DFDFDF", "#FFFFFF", "#CFCFCF", "#000080", "#808080", "#6E6E6E", "#C0C0C0", "#101010", "#404040", "#595959", "#000080", "#1A1AA0",
                Danger: "#8B0000", Ok: "#006400", OnSel: "#FFFFFF", BarSel: "#FFFFFF"),
        };

        private static string Argb(string hex) => hex.Length == 7 ? "#FF" + hex[1..] : hex;

        /// <summary>the 10 base colors of a light theme: frame = PrimaryDark, content = Background, nav = PrimaryMid, fields = PrimaryLight (white)</summary>
        private static ThemeConfig FromTokens(ThemeTokens k) => new()
        {
            PrimaryDarkColor = Argb(k.Frame), BackgroundColor = Argb(k.Content), PrimaryMidColor = Argb(k.Nav), PrimaryLightColor = "#FFFFFFFF",
            PrimaryTextColor = Argb(k.Text), BackgroundTextColor = Argb(k.Text),
            AccentMidColor = Argb(k.Accent), AccentLightColor = Argb(k.AccentHover), AccentDarkColor = Argb(k.AccentHover), AccentTextColor = "#FFFFFFFF",
        };

        /// <summary>hand-made colors of a light theme, only if its colors are still those of the list (a theme whose colors were changed by hand uses the formulas)</summary>
        public static ThemeTokens? HandMade(ThemeConfig t) =>
            LightThemes.TryGetValue(t.ThemeName ?? "", out var k) && string.Equals(FromTokens(k).BackgroundColor, t.BackgroundColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(FromTokens(k).AccentMidColor, t.AccentMidColor, StringComparison.OrdinalIgnoreCase) ? k : null;

        /// <summary>the colors of every part for any theme: hand-made ones for the light themes of the list, otherwise computed from the 10 base colors</summary>
        public static ThemeTokens TokensOf(ThemeConfig t) => HandMade(t) ?? Derived(t);

        /// <summary>
        /// parts computed from the base colors as the app always did: frame = PrimaryDark, toolbar = PrimaryMid, panels = Background,
        /// fields = PrimaryLight, borders/hover/selection/secondary text = the text (or accent) with a transparency
        /// </summary>
        private static ThemeTokens Derived(ThemeConfig t)
        {
            string A(string hex, double a) => $"#{(byte)Math.Round(a * 255):X2}{hex[^6..]}";   // same color, with its transparency
            string text = t.PrimaryTextColor, accent = t.AccentMidColor;
            var dark = IsDark(t);
            return new ThemeTokens(t.PrimaryDarkColor, t.PrimaryMidColor, t.BackgroundColor, t.BackgroundColor, A(text, 0.07), A(accent, 0.17),
                A(text, 0.13), A(text, 0.35), A(text, 0.08), text, A(text, 0.7), A(text, 0.55), accent, t.AccentLightColor,
                Danger: dark ? "#FF9B94" : "#B3261E", Ok: dark ? "#6CCB5F" : "#1E7A3E", Input: t.PrimaryLightColor,
                SectionTitle: accent, Card: A(text, 0.06), SubCard: A(text, 0.03), Avatar: A(accent, 0.16), WindowBorder: A(text, 0.12),
                // the toggle of the dark themes: filled gray track, light knob, dark knob on the accent
                ToggleOffTrack: dark ? "#4A4A4A" : "#00000000", ToggleOffKnob: dark ? "#B8B8B8" : "", ToggleOffBorder: dark ? "#00000000" : "",
                ToggleOnKnob: dark ? "#00283B" : "#FFFFFF");
        }

        /// <summary>
        /// exact colors of the website's dark theme (its tokens.css): page background = window background,
        /// the site's surfaces = panels, the same vermilion and the same dark text on the accent
        /// </summary>
        private static ThemeConfig Site() => new()
        {
            // background lighter than the site's #121312 (too black next to the orange); accent a touch lighter so it stays readable as text on it
            PrimaryDarkColor = "#FF181917", BackgroundColor = "#FF1F201E", PrimaryMidColor = "#FF262725", PrimaryLightColor = "#FF2D2E2B",
            PrimaryTextColor = "#FFE9E7E1", BackgroundTextColor = "#FFE9E7E1",
            AccentMidColor = "#FFF47A4C", AccentLightColor = "#FFF69168", AccentDarkColor = "#FFD95E30", AccentTextColor = "#FF1A0C05",
        };


        private static ThemeConfig Dark(double h, double s, double ah, double asat, double al)
        {
            var text = Hsl(h, Math.Min(0.20, s), 0.93);
            var accent = Hsl(ah, asat, al);
            return new ThemeConfig
            {
                BackgroundColor = Hsl(h, s, 0.118), PrimaryMidColor = Hsl(h, s, 0.15),
                PrimaryLightColor = Hsl(h, s, 0.21), PrimaryDarkColor = Hsl(h, s, 0.075),
                PrimaryTextColor = text, BackgroundTextColor = text,
                AccentMidColor = accent, AccentLightColor = Hsl(ah, asat, Math.Min(0.92, al + 0.09)), AccentDarkColor = Hsl(ah, asat, al - 0.10),
                AccentTextColor = BestOn(accent, Hsl(ah, asat, 0.10), "#FFFFFFFF"),
            };
        }

        /// <summary>protocol label colors: light and pastel on dark themes, solid and dark on light themes (otherwise they are hard to read)</summary>
        public static (string Rdp, string Ssh, string Vnc, string Sftp, string Other) ProtocolColors(ThemeConfig t)
        {
            if (IsDark(t)) return ("#FF8AB4F8", "#FF81C995", "#FFF6AEA9", "#FFFDD663", "#FFC4C7C5");
            return ("#FF1B5FAA", "#FF166A33", "#FFB3261E", "#FF7A4F00", "#FF4F5963");   // at least 4.5:1 on every surface of the light themes
        }

        public static bool IsDark(ThemeConfig t) => Luminance(ColorAndBrushHelper.HexColorToMediaColor(t.BackgroundColor)) < 0.3;

        public static string Hsl(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360 / 360.0;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Ch(double t) { t = t < 0 ? t + 1 : t > 1 ? t - 1 : t; return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p; }
            byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return $"#FF{B(Ch(h + 1 / 3.0)):X2}{B(Ch(h)):X2}{B(Ch(h - 1 / 3.0)):X2}";
        }

        /// <summary>WCAG 2.x relative luminance</summary>
        public static double Luminance(Color c)
        {
            double L(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
        }

        public static double Contrast(string a, string b)
        {
            double la = Luminance(ColorAndBrushHelper.HexColorToMediaColor(a)), lb = Luminance(ColorAndBrushHelper.HexColorToMediaColor(b));
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static string BestOn(string bg, string first, string second) => Contrast(first, bg) >= 4.5 || Contrast(first, bg) >= Contrast(second, bg) ? first : second;

        public void ApplyTheme(ThemeConfig theme)
        {
            const string resourceTypeKey = "__Resource_Type_Key";
            const string resourceTypeValue = "__Resource_Type_Value=theme";
            void SetKey(IDictionary rd, string key, object value)
            {
                if (!rd.Contains(key))
                    rd.Add(key, value);
                else
                    rd[key] = value;
            }

            // create new theme resources
            var rd = new ResourceDictionary();
            SetKey(rd, resourceTypeKey, resourceTypeValue);
            SetKey(rd, "PrimaryMidColor", ColorAndBrushHelper.HexColorToMediaColor(theme.PrimaryMidColor));
            SetKey(rd, "PrimaryLightColor", ColorAndBrushHelper.HexColorToMediaColor(theme.PrimaryLightColor));
            SetKey(rd, "PrimaryDarkColor", ColorAndBrushHelper.HexColorToMediaColor(theme.PrimaryDarkColor));
            SetKey(rd, "PrimaryTextColor", ColorAndBrushHelper.HexColorToMediaColor(theme.PrimaryTextColor));
            SetKey(rd, "AccentMidColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentMidColor));
            SetKey(rd, "AccentLightColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentLightColor));
            SetKey(rd, "AccentDarkColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentDarkColor));
            SetKey(rd, "AccentTextColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentTextColor));
            SetKey(rd, "BackgroundColor", ColorAndBrushHelper.HexColorToMediaColor(theme.BackgroundColor));
            SetKey(rd, "BackgroundTextColor", ColorAndBrushHelper.HexColorToMediaColor(theme.BackgroundTextColor));


            SetKey(rd, "PrimaryMidBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.PrimaryMidColor));
            SetKey(rd, "PrimaryLightBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.PrimaryLightColor));
            SetKey(rd, "PrimaryDarkBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.PrimaryDarkColor));
            SetKey(rd, "PrimaryTextBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.PrimaryTextColor));
            SetKey(rd, "AccentMidBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.AccentMidColor));
            SetKey(rd, "AccentLightBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.AccentLightColor));
            SetKey(rd, "AccentDarkBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.AccentDarkColor));
            SetKey(rd, "AccentTextBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.AccentTextColor));
            SetKey(rd, "BackgroundBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.BackgroundColor));
            SetKey(rd, "BackgroundTextBrush", ColorAndBrushHelper.ColorToMediaBrush(theme.BackgroundTextColor));

            // shades of the island-style graphics (FluentIslands.xaml): created here, because those written in XAML with
            // Color="{DynamicResource ...}" keep the first theme's color and do not follow theme changes
            void Tint(string key, string hex, double opacity)
            {
                var b = new SolidColorBrush(ColorAndBrushHelper.HexColorToMediaColor(hex)) { Opacity = opacity };
                b.Freeze();
                SetKey(rd, key, b);
            }
            // every part of the window from the theme's table (hand-made for the light themes, computed for the others)
            var k = TokensOf(theme);
            void Part(string key, string hex, string fallback = "") => Tint(key, Argb(hex.Length > 0 ? hex : fallback), 1);
            Part("IslandBorderBrush", k.Border);
            Part("IslandWindowBorderBrush", k.WindowBorder, k.Border);
            Part("IslandDividerBrush", k.Divider);
            Part("IslandButtonBorderBrush", k.BorderControl);
            Part("IslandHoverBrush", k.Hover);
            Part("IslandCardBrush", k.Card, k.Nav);
            if (k.SubCard.Length > 0) Part("IslandSubCardBrush", k.SubCard); else Tint("IslandSubCardBrush", Argb(k.Nav), 0.5);
            Part("IslandSelectedBrush", k.Selected);
            Part("IslandAvatarBrush", k.Avatar, k.OnSel.Length > 0 ? k.Hover : k.Selected);   // the folder initial is in the accent: not on a navy square
            Part("IslandSecondaryTextBrush", k.Text2);
            Part("IslandPlaceholderBrush", k.Placeholder);
            Part("IslandNavBrush", k.Nav);
            Part("ToolbarBrush", k.Bar);
            Part("IslandSelectedTextBrush", k.SelText);
            Part("IslandSelectedAccentBrush", k.OnSel, k.Accent);
            Part("SelectionBarBrush", k.SelBar);          // bar on the left of the selected item
            Part("SectionTitleBrush", k.SectionTitle, k.Text2);
            Part("ToggleOffTrackBrush", k.ToggleOffTrack);
            Part("ToggleOffBorderBrush", k.ToggleOffBorder, k.BorderControl);
            Part("ToggleOffKnobBrush", k.ToggleOffKnob, k.Text2);
            Part("ToggleOnKnobBrush", k.ToggleOnKnob);
            Part("DangerBrush", k.Danger);
            Part("OkBrush", k.Ok);
            Tint("ScrollThumbBrush", theme.PrimaryTextColor, 0.34);
            Tint("ScrollThumbHoverBrush", theme.PrimaryTextColor, 0.58);
            Tint("ScrollTrackBrush", theme.PrimaryTextColor, 0.06);
            // protocol labels readable on both light and dark themes
            var pc = ProtocolColors(theme);
            SetKey(rd, "ProtocolRdpBrush", ColorAndBrushHelper.ColorToMediaBrush(pc.Rdp));
            SetKey(rd, "ProtocolSshBrush", ColorAndBrushHelper.ColorToMediaBrush(pc.Ssh));
            SetKey(rd, "ProtocolVncBrush", ColorAndBrushHelper.ColorToMediaBrush(pc.Vnc));
            SetKey(rd, "ProtocolSftpBrush", ColorAndBrushHelper.ColorToMediaBrush(pc.Sftp));
            SetKey(rd, "ProtocolOtherBrush", ColorAndBrushHelper.ColorToMediaBrush(pc.Other));

            SetKey(rd, "PrimaryColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentMidColor));
            SetKey(rd, "DarkPrimaryColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentDarkColor));
            //SetKey(rd, "PrimaryDarkColor", ColorAndBrushHelper.HexColorToMediaColor(theme.AccentTextColor));

            var font = GetFontFamily(theme.FontFamily);
            SetKey(rd, "GlobalFontFamily", font);
            theme.FontSize = Math.Max(10, theme.FontSize);
            double globalFontSizeSmall = Math.Min(20.0, theme.FontSize - 2.0);
            double globalFontSizeBody = Math.Min(20.0, theme.FontSize);
            double globalFontSizeSubtitle = Math.Min(20.0, theme.FontSize + 2.0);
            double globalFontSizeTitle = Math.Min(20.0, theme.FontSize + 6.0);
            SetKey(rd, "GlobalFontSizeTitle", globalFontSizeTitle);
            SetKey(rd, "GlobalFontSizeSubtitle", globalFontSizeSubtitle);
            SetKey(rd, "GlobalFontSizeBody", globalFontSizeBody);
            SetKey(rd, "GlobalFontSizeSmall", globalFontSizeSmall);
            Lemora.Utils.FontScale.Instance.Scale = theme.FontSize / 12.0;   // texts with a fixed size in XAML ({FontPx 13}) also follow the choice





            // remove old theme resources
            var rs = _appResourceDictionary.MergedDictionaries.Where(o =>
                (o?.Source?.IsAbsoluteUri == true && o.Source.AbsolutePath.ToLower().IndexOf("Default.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
                || o?[resourceTypeKey]?.ToString() == resourceTypeValue).ToArray();
            foreach (var r in rs)
            {
                _appResourceDictionary.MergedDictionaries.Remove(r);
            }

            // add new theme resources
            _appResourceDictionary.MergedDictionaries.Add(rd);
            SelectedOnText = TokensOf(theme) is { OnSel.Length: > 0 } sk ? Argb(sk.OnSel) : null;
            SelectedLook.Refresh();
            ThemeApplied?.Invoke();
        }

        /// <summary>fires when a theme has been applied (used to switch the UI logo between the dark and light background versions)</summary>
        public static event Action? ThemeApplied;

        /// <summary>text color on a selected item when it is not the normal text (Classico: white on navy), otherwise null</summary>
        public static string? SelectedOnText { get; private set; }

        private static FontFamily GetFontFamily(string name)
        {
            // set default font family
            var fontFamily = Fonts.SystemFontFamilies.FirstOrDefault(x => string.Equals(x.Source, name, StringComparison.CurrentCultureIgnoreCase));
            fontFamily ??= Fonts.SystemFontFamilies.FirstOrDefault(x => string.Equals(x.Source, "Microsoft YaHei", StringComparison.CurrentCultureIgnoreCase));
            fontFamily ??= Fonts.SystemFontFamilies.FirstOrDefault(x => x.Source.EndsWith("YaHei", StringComparison.OrdinalIgnoreCase));
            fontFamily ??= Fonts.SystemFontFamilies.FirstOrDefault(x => x.Source.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0);
            fontFamily ??= Fonts.SystemFontFamilies.FirstOrDefault(x => x.Source.IndexOf("雅黑", StringComparison.OrdinalIgnoreCase) >= 0);
            fontFamily ??= Fonts.SystemFontFamilies.FirstOrDefault(x => x.Source.IndexOf("雅黑", StringComparison.OrdinalIgnoreCase) >= 0);

            return fontFamily ?? Fonts.SystemFontFamilies.First();
        }
    }

    /// <summary>
    /// SelectedLook.On="True" on the background of a selected item (set by its trigger). Only on themes whose selection needs another
    /// text color (Classico: navy selection) the item's text, secondary text, labels and protocol colors become that color, because
    /// their normal colors cannot be read on the selection. On every other theme it does nothing.
    /// </summary>
    public static class SelectedLook
    {
        public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached("On", typeof(bool), typeof(SelectedLook),
            new PropertyMetadata(false, (d, _) => { if (d is FrameworkElement el) Apply(el); }));
        public static void SetOn(DependencyObject d, bool value) => d.SetValue(OnProperty, value);
        public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);

        private static readonly string[] Keys = { "PrimaryTextBrush", "BackgroundTextBrush", "IslandSecondaryTextBrush", "IslandPlaceholderBrush", "AccentMidBrush",
            "IslandButtonBorderBrush", "ProtocolRdpBrush", "ProtocolSshBrush", "ProtocolVncBrush", "ProtocolSftpBrush", "ProtocolOtherBrush" };
        private static readonly List<WeakReference<FrameworkElement>> Selected = new();   // to update them if the theme changes while selected

        private static void Apply(FrameworkElement el)
        {
            if (GetOn(el) && !Selected.Exists(w => w.TryGetTarget(out var x) && ReferenceEquals(x, el))) Selected.Add(new WeakReference<FrameworkElement>(el));
            var on = GetOn(el) && ThemeService.SelectedOnText != null;
            // part of a control's template (button, list item): the text is the control's content, so the colors go on the control
            // (only its resources: its own Foreground setter then finds the white brush). Otherwise (data template, plain border):
            // the selection background is often an empty Border next to the texts, so the colors go on its parent
            var control = el.TemplatedParent as System.Windows.Controls.Control;
            if (control == null && el is System.Windows.Controls.Border { Child: null } && VisualTreeHelper.GetParent(el) is FrameworkElement parent) el = parent;
            FrameworkElement target = control ?? el;
            if (on)
            {
                var b = new SolidColorBrush(ColorAndBrushHelper.HexColorToMediaColor(ThemeService.SelectedOnText!)); b.Freeze();
                foreach (var k in Keys) target.Resources[k] = b;
                if (control == null) el.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, b);   // text that inherits the color
            }
            else if (target.Resources.Contains(Keys[0]))
            {
                foreach (var k in Keys) target.Resources.Remove(k);
                if (control == null) el.ClearValue(System.Windows.Documents.TextElement.ForegroundProperty);
            }
        }

        /// <summary>after a theme change: items still selected get the new theme's look</summary>
        internal static void Refresh()
        {
            foreach (var w in Selected.ToArray())
                if (w.TryGetTarget(out var el)) Apply(el);
            Selected.RemoveAll(w => !w.TryGetTarget(out var el) || !GetOn(el));
        }
    }
}
