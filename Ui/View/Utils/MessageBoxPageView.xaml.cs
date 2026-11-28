using System;
using System.Windows;

namespace Lemora.View.Utils
{
    public partial class MessageBoxPageView
    {
        /// <summary>
        /// The box follows the size of the Lemora window it appears on (before: always 400-520 wide and as tall as the text, too big on a small
        /// window). Width about 38% of the window (340-600), height at most about 3/4 of it, then the text scrolls.
        /// The text size stays the one chosen in the settings: only the box adapts.
        /// </summary>
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            var owner = win is MessageBoxView { Owner: { } o } ? o : win;
            if (owner == null || owner.ActualWidth <= 0 || owner.ActualHeight <= 0) return;
            var w = Math.Clamp(owner.ActualWidth * 0.38, 340, 600);
            Frame.MinWidth = Math.Min(400, w);
            Frame.MaxWidth = w;
            BodyScroll.MaxHeight = Math.Max(140, owner.ActualHeight * 0.75 - 170);   // 170 = title bar, footer with buttons and margins
            if (win is MessageBoxView) win.MaxWidth = w + Frame.Margin.Left + Frame.Margin.Right;   // its own window had a fixed limit (580)
        }
    }
}
