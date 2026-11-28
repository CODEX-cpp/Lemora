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

        // the touchpad sends many events with small deltas: WPF treats each one as a full mouse wheel notch.
        // Here we scroll in proportion to the delta (mouse: 120 = 60 px), so finger movement matches the distance.
        // wide screen: identity on the left, protocol form on the right; narrow: everything in one column
        private void ScrollViewerMain_OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            var wide = ScrollViewerMain.ActualWidth >= 1750;
            System.Windows.Controls.Grid.SetRow(PanelRight, wide ? 0 : 1);
            System.Windows.Controls.Grid.SetColumn(PanelRight, wide ? 1 : 0);
            PanelRight.Margin = wide ? new System.Windows.Thickness(32, 0, 0, 0) : new System.Windows.Thickness(0);
            // the form never exceeds the visible area: with narrow windows it shrinks instead of being cut off on the left
            var avail = Math.Max(420, ScrollViewerMain.ActualWidth - 70);
            PanelLeft.Width = Math.Min(wide ? 760 : 800, avail);
            PanelRight.Width = Math.Min(wide ? 760 : 800, avail);
        }

        private void ScrollViewerMain_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            // an open drop-down (e.g. Folder) or a list in the form takes the wheel: before, the whole page scrolled and the list stayed still
            if (Lemora.Utils.SmoothWheelScroll.InnerCanScroll(e.OriginalSource as System.Windows.DependencyObject, sv, e.Delta)) return;
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