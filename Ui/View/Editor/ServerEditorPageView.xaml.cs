using System;
using System.Windows.Controls;
using System.Windows.Input;
using Lemora.Resources.Icons;

namespace Lemora.View.Editor
{
    public partial class ServerEditorPageView : UserControl
    {
        public ServerEditorPageView()
        {
            this.Loaded += (sender, args) =>
            {
                if (this.DataContext is ServerEditorPageViewModel vm)
                {
                    // add mode
                    if (vm.Server.IsTmpSession())
                    {
                        ButtonSave.Content = IoC.Translate("Add");
                        if (vm.Server.IconImg == null
                            && ServerIcons.Instance.IconsBase64.Count > 0)
                        {
                            var r = new Random(DateTime.Now.Millisecond);
                            vm.Server.IconBase64 = ServerIcons.Instance.IconsBase64[r.Next(0, ServerIcons.Instance.IconsBase64.Count)];
                        }
                    }
                }
            };
        }
        ~ServerEditorPageView()
        {
            Console.WriteLine($"Release {this.GetType().Name}({this.GetHashCode()})");
        }

        private void TextBoxMarkdown_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
        }

        // il touchpad invia molti eventi con delta piccoli: WPF li tratta tutti come una tacca intera della rotellina.
        // Qui si scorre in proporzione al delta (mouse: 120 = 60 px), cosi' il movimento delle dita corrisponde alla distanza.
        // schermo largo: identita' a sinistra, form del protocollo a destra; stretto: tutto in una colonna
        private void ScrollViewerMain_OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            var wide = ScrollViewerMain.ActualWidth >= 1750;
            System.Windows.Controls.Grid.SetRow(PanelRight, wide ? 0 : 1);
            System.Windows.Controls.Grid.SetColumn(PanelRight, wide ? 1 : 0);
            PanelRight.Margin = wide ? new System.Windows.Thickness(32, 0, 0, 0) : new System.Windows.Thickness(0);
            // il modulo non supera l'area visibile: con finestre strette si restringe invece di tagliarsi a sinistra
            var avail = Math.Max(420, ScrollViewerMain.ActualWidth - 70);
            PanelLeft.Width = Math.Min(wide ? 760 : 800, avail);
            PanelRight.Width = Math.Min(wide ? 760 : 800, avail);
        }

        private void ScrollViewerMain_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.5);
            e.Handled = true;
        }

        private void ButtonShowNote_OnMouseEnter(object sender, MouseEventArgs e)
        {
            PopupNote.IsOpen = false;
            PopupNote.IsOpen = true;
        }
    }
}