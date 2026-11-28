using System;
using System.Windows;
using System.Windows.Controls;

namespace Lemora.Controls
{
    /// <summary>
    /// browser-style tab panel: each tab is <see cref="PreferredTabWidth"/> wide and, when they no longer fit,
    /// they all shrink together down to <see cref="MinTabWidth"/>
    /// </summary>
    public class TabStripPanel : Panel
    {
        public double PreferredTabWidth { get; set; } = 220;
        public double MinTabWidth { get; set; } = 110;
        public double Gap { get; set; } = 4;

        public double AddTabWidth { get; set; } = 40;

        private static bool IsAddTab(UIElement e) => e is ContentPresenter { Content: Lemora.View.MainTabItem { IsAdd: true } };
        // a split tab (two sessions side by side) weighs almost twice a normal one
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

        // "+" tabs have a fixed width: the remaining space is split among the others (in proportion to their weight)
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
