using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lemora.Utils
{
    /// <summary>
    /// The touchpad sends many events with small deltas and WPF treats each one as a full wheel notch:
    /// the page scrolls too fast. Here every vertical ScrollViewer scrolls in proportion to the delta
    /// (mouse: one notch = 120 = 60 px), so the distance follows the finger movement.
    /// Registered only once at startup (class handler: applies to all pages, settings included).
    /// </summary>
    public static class SmoothWheelScroll
    {
        private const double PixelsPerDelta = 0.5;
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not ScrollViewer sv) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;                                   // Ctrl + wheel: zoom and the like
            if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return;
            if (sv.ScrollableHeight <= 0) return;
            // lists that scroll by item (not by pixel): the offset is in rows, the pixel math does not apply
            if (sv.CanContentScroll
                && !(sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel)) return;
            // at the edges the event is let through: the outer container scrolls
            if (e.Delta > 0 && sv.VerticalOffset <= 0) return;
            if (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight) return;
            // the wheel tunnels from the outermost ScrollViewer: a list inside it (an open drop-down, a list in a page)
            // that can still move gets the wheel, otherwise the page scrolled and the list under the mouse stayed still
            if (InnerCanScroll(e.OriginalSource as DependencyObject, sv, e.Delta)) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * PixelsPerDelta);
            e.Handled = true;
        }

        /// <summary>true if between the element under the mouse and <paramref name="outer"/> there is a ScrollViewer that can
        /// still scroll in the wheel direction. Climbs through popups too (a drop-down is a separate window: visual parent null,
        /// logical parent = the Popup inside the page).</summary>
        internal static bool InnerCanScroll(DependencyObject? from, ScrollViewer outer, int delta)
        {
            for (var d = from; d != null && d != outer; d = ParentOf(d))
            {
                // inside an open drop-down the page never moves, not even when the list is at its end (it moved under the open menu)
                if (d is System.Windows.Controls.Primitives.Popup) return true;
                if (d is not ScrollViewer inner || inner.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
                if (delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight) return true;
            }
            return false;
        }

        private static DependencyObject? ParentOf(DependencyObject d)
        {
            // the drop-down content: its visual parent is the popup window root (a dead end), its logical parent is the Popup
            if (LogicalTreeHelper.GetParent(d) is System.Windows.Controls.Primitives.Popup popup) return popup;
            if (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var p = System.Windows.Media.VisualTreeHelper.GetParent(d);
                if (p != null) return p;
            }
            return LogicalTreeHelper.GetParent(d);
        }
    }
}
