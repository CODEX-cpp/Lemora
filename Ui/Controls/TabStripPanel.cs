using System;
using System.Windows;
using System.Windows.Controls;

namespace Lemora.Controls
{
    /// <summary>
    /// pannello delle schede in stile browser: ogni scheda e' larga <see cref="PreferredTabWidth"/> e, quando non ci stanno piu',
    /// si restringono tutte insieme fino a <see cref="MinTabWidth"/>
    /// </summary>
    public class TabStripPanel : Panel
    {
        public double PreferredTabWidth { get; set; } = 220;
        public double MinTabWidth { get; set; } = 110;
        public double Gap { get; set; } = 4;

        public double AddTabWidth { get; set; } = 40;

        private static bool IsAddTab(UIElement e) => e is ContentPresenter { Content: Lemora.View.MainTabItem { IsAdd: true } };
        // una scheda divisa (due sessioni affiancate) pesa quasi il doppio di una normale
        private static double Weight(UIElement e) => e is ContentPresenter { Content: Lemora.View.MainTabItem { IsSplit: true } } ? 1.9 : 1;

        private double TabWidth(double available, double weight)
        {
            if (weight <= 0) return PreferredTabWidth;
            if (double.IsInfinity(available)) return PreferredTabWidth;
            var fit = available / weight;
            return Math.Max(MinTabWidth, Math.Min(PreferredTabWidth, fit));
        }

        private (int fixedCount, double weight) Counts()
        {
            var k = 0; double w = 0;
            foreach (UIElement child in InternalChildren) { if (IsAddTab(child)) k++; else w += Weight(child); }
            return (k, w);
        }

        // le schede "+" hanno larghezza fissa: lo spazio rimasto si divide tra le altre (in proporzione al peso)
        private double Unit(double available)
        {
            var (k, weight) = Counts();
            var n = InternalChildren.Count;
            if (double.IsInfinity(available)) return PreferredTabWidth;
            var room = available - k * AddTabWidth - (n - 1) * Gap;
            return TabWidth(room, weight);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var n = InternalChildren.Count;
            var u = Unit(constraint.Width);
            double h = 0, total = 0;
            foreach (UIElement child in InternalChildren)
            {
                var cw = IsAddTab(child) ? AddTabWidth : u * Weight(child);
                child.Measure(new Size(cw, constraint.Height));
                h = Math.Max(h, child.DesiredSize.Height);
                total += cw;
            }
            return new Size(n == 0 ? 0 : total + (n - 1) * Gap, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var u = Unit(finalSize.Width);
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                var cw = IsAddTab(child) ? AddTabWidth : u * Weight(child);
                child.Arrange(new Rect(x, 0, cw, finalSize.Height));
                x += cw + Gap;
            }
            return finalSize;
        }
    }
}
