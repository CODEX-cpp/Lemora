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
            foreach (var (name, t) in Presets()) Themes.Add(name, t);

            // nomi di prima (in inglese, uno col nome del vecchio progetto): chi li aveva scelti ritrova il tema corrispondente
            if (OldNames.TryGetValue(defaultTheme.ThemeName ?? "", out var renamed)) defaultTheme.ThemeName = renamed;
            // un tema dell'elenco prende sempre i colori aggiornati (i colori salvati nel profilo potrebbero essere di una versione vecchia)
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

        // ---------------------------------------------------------------- temi costruiti con una regola
        // Ogni tema = un'idea: la tinta dei grigi (tono h, saturazione s) e un accento scelto in armonia con lei
        // (affine = tono vicino, complementare = tono opposto). Tutte le sfumature escono dalle stesse proporzioni di luminosita',
        // cosi' i temi hanno la stessa struttura e cambiano solo il carattere. I contrasti li controlla Tests/Service/ThemePaletteTests.

        /// <summary>elenco ordinato: prima i tre scuri, poi i chiari</summary>
        public static List<(string Name, ThemeConfig Theme)> Presets() => new()
        {
            ("Grafite", Dark(210, 0.03, 199, 0.95, 0.68)),   // grigio neutro, accento azzurro cielo (il tema di sempre)
            ("Notte", Dark(226, 0.24, 262, 0.72, 0.76)),     // blu notte, accento lavanda (affine)
            ("Asfalto", Dark(30, 0.05, 38, 0.92, 0.60)),     // asfalto caldo, accento ambra da segnaletica
            ("Carta", Light(40, 0.10, 212, 0.80, 0.40)),     // carta avorio, inchiostro blu (complementare)
            ("Ghiaccio", Light(205, 0.32, 190, 0.90, 0.29)), // azzurro ghiaccio, accento petrolio (affine)
            ("Lavanda", Light(265, 0.30, 268, 0.48, 0.44)),  // lavanda, accento viola (stessa famiglia)
            ("Ardesia", Light(210, 0.16, 18, 0.72, 0.39)),   // ardesia grigio-azzurra, accento rame (complementare)
            ("Vino", Light(345, 0.32, 340, 0.62, 0.36)),     // rosato, accento bordeaux (stessa famiglia)
            ("Bosco", Light(150, 0.18, 156, 0.50, 0.30)),    // salvia, accento verde bosco (stessa famiglia)
            ("Terra", Light(35, 0.32, 72, 0.60, 0.28)),      // sabbia, accento oliva (affine, caldo)
        };

        private static ThemeConfig Light(double h, double s, double ah, double asat, double al)
        {
            // carta opaca, non bianco: il pannello piu' chiaro resta sotto il 95% di luminosita' (il bianco pieno abbaglia),
            // i livelli sono distanziati quanto basta perche' pannelli e barre si distinguano, il testo e' quasi nero
            var text = Hsl(h, Math.Min(0.35, s + 0.15), 0.12);
            // l'accento si usa anche come scritta: si scurisce quanto basta per leggersi sullo sfondo piu' scuro del tema
            while (al > 0.15 && Contrast(Hsl(ah, asat, al), Hsl(h, s, 0.82)) < 4.6) al -= 0.01;
            var accent = Hsl(ah, asat, al);
            return new ThemeConfig
            {
                BackgroundColor = Hsl(h, s, 0.905), PrimaryLightColor = Hsl(h, s, 0.945),
                PrimaryMidColor = Hsl(h, s, 0.865), PrimaryDarkColor = Hsl(h, s, 0.82),
                PrimaryTextColor = text, BackgroundTextColor = text,
                AccentMidColor = accent, AccentLightColor = Hsl(ah, asat, al + 0.08), AccentDarkColor = Hsl(ah, asat, al - 0.07),
                AccentTextColor = BestOn(accent, "#FFFFFFFF", Hsl(ah, asat, 0.10)),
            };
        }

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

        /// <summary>colori delle etichette dei protocolli: chiari e pastello sui temi scuri, pieni e scuri sui temi chiari (se no si leggono male)</summary>
        public static (string Rdp, string Ssh, string Vnc, string Sftp, string Other) ProtocolColors(ThemeConfig t)
        {
            if (IsDark(t)) return ("#FF8AB4F8", "#FF81C995", "#FFF6AEA9", "#FFFDD663", "#FFC4C7C5");
            return (Hsl(216, 0.70, 0.36), Hsl(145, 0.62, 0.22), Hsl(4, 0.66, 0.36), Hsl(36, 0.95, 0.24), Hsl(210, 0.08, 0.32));
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

        /// <summary>luminanza relativa WCAG 2.x</summary>
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

            // sfumature della grafica a isole (FluentIslands.xaml): create qui, perche' quelle scritte nel XAML con
            // Color="{DynamicResource ...}" restano col colore del primo tema e non seguono il cambio di tema
            void Tint(string key, string hex, double opacity)
            {
                var b = new SolidColorBrush(ColorAndBrushHelper.HexColorToMediaColor(hex)) { Opacity = opacity };
                b.Freeze();
                SetKey(rd, key, b);
            }
            Tint("IslandBorderBrush", theme.PrimaryTextColor, 0.13);
            Tint("IslandDividerBrush", theme.PrimaryTextColor, 0.08);
            Tint("IslandHoverBrush", theme.PrimaryTextColor, 0.07);
            Tint("IslandCardBrush", theme.PrimaryTextColor, 0.06);
            Tint("IslandSubCardBrush", theme.PrimaryTextColor, 0.03);
            Tint("IslandSelectedBrush", theme.AccentMidColor, 0.17);
            Tint("IslandAvatarBrush", theme.AccentMidColor, 0.16);
            Tint("IslandSecondaryTextBrush", theme.PrimaryTextColor, 0.7);
            Tint("IslandWindowBorderBrush", theme.PrimaryTextColor, 0.12);
            Tint("IslandButtonBorderBrush", theme.PrimaryTextColor, 0.35);
            Tint("ScrollThumbBrush", theme.PrimaryTextColor, 0.34);
            Tint("ScrollThumbHoverBrush", theme.PrimaryTextColor, 0.58);
            Tint("ScrollTrackBrush", theme.PrimaryTextColor, 0.06);
            // etichette dei protocolli leggibili sia sui temi chiari sia su quelli scuri
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
            Lemora.Utils.FontScale.Instance.Scale = theme.FontSize / 12.0;   // anche i testi con dimensione fissa nei XAML ({FontPx 13}) seguono la scelta





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
            ThemeApplied?.Invoke();
        }

        /// <summary>scatta quando un tema e' stato applicato (serve a cambiare il logo dell'interfaccia tra versione per fondo scuro e per fondo chiaro)</summary>
        public static event Action? ThemeApplied;

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
}
