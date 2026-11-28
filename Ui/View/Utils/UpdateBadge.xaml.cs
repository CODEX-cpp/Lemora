using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Lemora.Setup;

namespace Lemora.View.Utils
{
    /// <summary>update box above "Options": see UpdateBadge.xaml and UpdateController</summary>
    public partial class UpdateBadge : UserControl
    {
        private INotifyPropertyChanged? _page;

        public UpdateBadge()
        {
            InitializeComponent();
            Loaded += (_, _) => { UpdateController.State.PropertyChanged += OnState; HookPage(); Refresh(); };
            Unloaded += (_, _) => { UpdateController.State.PropertyChanged -= OnState; if (_page != null) _page.PropertyChanged -= OnPage; _page = null; };
            DataContextChanged += (_, _) => { HookPage(); Refresh(); };
        }

        private void OnState(object? s, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(Refresh));
        private void OnPage(object? s, PropertyChangedEventArgs e) { if (e.PropertyName is null or "" or "LayoutMode") Dispatcher.BeginInvoke(new Action(Refresh)); }

        /// <summary>the sidebar changes shape (wide / icon-only) from the page's LayoutMode property</summary>
        private void HookPage()
        {
            if (_page != null) _page.PropertyChanged -= OnPage;
            _page = DataContext as INotifyPropertyChanged;
            if (_page != null) _page.PropertyChanged += OnPage;
        }

        private bool IsNarrow => DataContext?.GetType().GetProperty("LayoutMode")?.GetValue(DataContext)?.ToString() == "Medium";

        private void Refresh()
        {
            var st = UpdateController.State;
            Btn.Visibility = st.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            var glyph = st.IsDownloading ? "" : "";   // down arrow while downloading, "update" when ready
            WideIcon.Text = glyph; NarrowIcon.Text = glyph;
            WideSpin.Visibility = st.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
            Btn.IsHitTestVisible = st.IsReady;   // nothing to click during the download
            Wide.Visibility = IsNarrow ? Visibility.Collapsed : Visibility.Visible;
            NarrowIcon.Visibility = IsNarrow ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
