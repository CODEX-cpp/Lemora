using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lemora.Utils
{
    /// <summary>
    /// Il touchpad invia molti eventi con delta piccoli e WPF li tratta tutti come una tacca intera della rotellina:
    /// la pagina scorre troppo in fretta. Qui ogni ScrollViewer verticale scorre in proporzione al delta
    /// (mouse: una tacca = 120 = 60 px), cosi' la distanza segue il movimento delle dita.
    /// Si registra una volta sola all'avvio (class handler: vale per tutte le pagine, impostazioni comprese).
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
            if (Keyboard.Modifiers != ModifierKeys.None) return;                                   // Ctrl + rotellina: zoom e simili
            if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return;
            if (sv.ScrollableHeight <= 0) return;
            // liste che scorrono a elementi (non a pixel): l'offset e' in righe, il calcolo in pixel non vale
            if (sv.CanContentScroll
                && !(sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel)) return;
            // ai bordi si lascia salire l'evento: scorre il contenitore esterno
            if (e.Delta > 0 && sv.VerticalOffset <= 0) return;
            if (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * PixelsPerDelta);
            e.Handled = true;
        }
    }
}
