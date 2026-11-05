using System;
using System.Linq;
using Lemora.Service;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// Ogni tema deve restare leggibile: contrasti WCAG del testo, dell'accento (usato anche come testo: titoli delle sezioni, link)
    /// e delle etichette dei protocolli sugli sfondi dove compaiono.
    /// </summary>
    public class ThemePaletteTests
    {
        // testo secondario = testo principale al 70% sopra lo sfondo (IslandSecondaryTextBrush)
        private static string Blend(string fg, string bg, double a)
        {
            var f = Shawn.Utils.Wpf.ColorAndBrushHelper.HexColorToMediaColor(fg); var b = Shawn.Utils.Wpf.ColorAndBrushHelper.HexColorToMediaColor(bg);
            byte M(byte x, byte y) => (byte)Math.Round(x * a + y * (1 - a));
            return $"#FF{M(f.R, b.R):X2}{M(f.G, b.G):X2}{M(f.B, b.B):X2}";
        }

        [Fact]
        public void TenThemes_ThreeDark_NamesInItalian()
        {
            var p = ThemeService.Presets();
            Assert.Equal(10, p.Count);
            Assert.Equal(3, p.Count(x => ThemeService.IsDark(x.Theme)));
            Assert.Equal(p.Count, p.Select(x => x.Name).Distinct().Count());
        }

        [Fact]
        public void EveryTheme_IsReadable()
        {
            var problems = new System.Collections.Generic.List<string>();   // tutti insieme: si correggono in un giro solo
            foreach (var (name, t) in ThemeService.Presets())
            {
                void Min(double need, string a, string b, string what)
                {
                    var c = ThemeService.Contrast(a, b);
                    if (c < need) problems.Add($"{name}: {what} {a} su {b} contrasto {c:F2} < {need}");
                }
                foreach (var bg in new[] { t.BackgroundColor, t.PrimaryMidColor, t.PrimaryLightColor })
                {
                    Min(7.0, t.PrimaryTextColor, bg, "testo su sfondo");
                    Min(4.5, Blend(t.PrimaryTextColor, bg, 0.7), bg, "testo secondario");
                    Min(4.5, t.AccentMidColor, bg, "accento usato come testo");
                    var pc = ThemeService.ProtocolColors(t);
                    foreach (var (proto, col) in new[] { ("RDP", pc.Rdp), ("SSH", pc.Ssh), ("VNC", pc.Vnc), ("SFTP", pc.Sftp), ("altri", pc.Other) })
                        Min(4.5, col, bg, "etichetta " + proto);
                }
                Min(4.5, t.AccentTextColor, t.AccentMidColor, "testo sul pulsante principale");
                Min(3.0, t.AccentTextColor, t.AccentDarkColor, "testo sul pulsante premuto");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        }
    }
}
