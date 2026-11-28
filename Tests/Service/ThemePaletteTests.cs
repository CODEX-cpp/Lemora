using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Service;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// Every theme must stay readable: WCAG contrast of the text, of the accent (also used as text: section titles, links)
    /// and of the protocol labels on the backgrounds where they appear.
    /// </summary>
    public class ThemePaletteTests
    {
        // secondary text on dark themes = primary text at 70% over the background (IslandSecondaryTextBrush)
        private static string Blend(string fg, string bg, double a)
        {
            var f = Shawn.Utils.Wpf.ColorAndBrushHelper.HexColorToMediaColor(fg); var b = Shawn.Utils.Wpf.ColorAndBrushHelper.HexColorToMediaColor(bg);
            byte M(byte x, byte y) => (byte)Math.Round(x * a + y * (1 - a));
            return $"#FF{M(f.R, b.R):X2}{M(f.G, b.G):X2}{M(f.B, b.B):X2}";
        }

        [Fact]
        public void TwelveThemes_FourDark_EightLightWithTheirColors()
        {
            var p = ThemeService.Presets();
            Assert.Equal(12, p.Count);
            Assert.Equal(4, p.Count(x => ThemeService.IsDark(x.Theme)));
            Assert.Equal(p.Count, p.Select(x => x.Name).Distinct().Count());
            foreach (var (name, t) in p.Where(x => !ThemeService.IsDark(x.Theme)))
            {
                t.ThemeName = name;
                Assert.NotNull(ThemeService.HandMade(t));   // every light theme uses its hand-made colors
            }
        }

        [Fact]
        public void DarkThemes_AreReadable()
        {
            var problems = new List<string>();   // all together: they get fixed in a single pass
            foreach (var (name, t) in ThemeService.Presets().Where(x => ThemeService.IsDark(x.Theme)))
            {
                void Min(double need, string a, string b, string what)
                {
                    var c = ThemeService.Contrast(a, b);
                    if (c < need) problems.Add($"{name}: {what} {a} on {b} contrast {c:F2} < {need}");
                }
                foreach (var bg in new[] { t.BackgroundColor, t.PrimaryMidColor, t.PrimaryLightColor })
                {
                    Min(7.0, t.PrimaryTextColor, bg, "text on background");
                    Min(4.5, Blend(t.PrimaryTextColor, bg, 0.7), bg, "secondary text");
                    Min(4.5, t.AccentMidColor, bg, "accent used as text");
                    var pc = ThemeService.ProtocolColors(t);
                    foreach (var (proto, col) in new[] { ("RDP", pc.Rdp), ("SSH", pc.Ssh), ("VNC", pc.Vnc), ("SFTP", pc.Sftp), ("others", pc.Other) })
                        Min(4.5, col, bg, "label " + proto);
                }
                Min(4.5, t.AccentTextColor, t.AccentMidColor, "text on the main button");
                Min(3.0, t.AccentTextColor, t.AccentDarkColor, "text on the pressed button");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        }

        /// <summary>light themes: the pairs of the color spec (WCAG 2.2: text 4.5:1, field borders and controls 3:1)</summary>
        [Fact]
        public void LightThemes_AreReadable()
        {
            var problems = new List<string>();
            foreach (var (name, k) in ThemeService.LightThemes)
            {
                void Min(double need, string a, string b, string what)
                {
                    var c = ThemeService.Contrast(a, b);
                    if (c < need) problems.Add($"{name}: {what} {a} on {b} contrast {c:F2} < {need}");
                }
                var surfaces = name == "Classico" ? new[] { k.Content, k.Nav, "#FFFFFF" } : new[] { k.Content, k.Nav, k.Bar, "#FFFFFF", k.Selected };
                foreach (var bg in new[] { k.Content, k.Nav, k.Bar, "#FFFFFF" }) Min(7.0, k.Text, bg, "text");
                foreach (var bg in new[] { k.Content, k.Nav, "#FFFFFF" }) { Min(4.5, k.Text2, bg, "secondary text"); Min(4.5, k.Placeholder, bg, "placeholder"); }
                foreach (var bg in new[] { k.Content, k.Nav }) { Min(4.5, k.Accent, bg, "accent"); Min(4.5, k.Danger, bg, "error red"); Min(4.5, k.Ok, bg, "green"); }
                Min(4.5, "#FFFFFF", k.Accent, "text on the main button");
                Min(4.5, "#FFFFFF", k.AccentHover, "text on the button under the mouse");
                Min(4.5, k.SelText, k.Selected, "text of the selected item");
                Min(3.0, k.BorderControl, "#FFFFFF", "field border");
                Min(3.0, k.SelBar, k.Selected, "selection bar");
                var t = new ThemeConfig { BackgroundColor = "#FF" + k.Content[1..] };
                var pc = ThemeService.ProtocolColors(t);
                foreach (var bg in surfaces)
                    foreach (var (proto, col) in new[] { ("RDP", pc.Rdp), ("SSH", pc.Ssh), ("VNC", pc.Vnc), ("SFTP", pc.Sftp), ("others", pc.Other) })
                        Min(4.5, col, bg, "label " + proto);
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        }
    }
}
