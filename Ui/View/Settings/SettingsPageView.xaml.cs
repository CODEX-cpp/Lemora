using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lemora.Model;
using Lemora.Service;
using Lemora.Utils;
using Shawn.Utils;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.FileSystem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Lemora.View.Settings
{
    public partial class SettingsPageView : UserControl
    {
        public SettingsPageView()
        {
            InitializeComponent();
        }

        // ---- ricerca nelle impostazioni (Generale + Avvio rapido) ----
        private void TbSearch_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            var q = TbSearch.Text.Trim();
            var on = q.Length > 0;
            NormalHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            SearchHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            // le righe dei risultati nascono dopo il layout: si filtra a layout finito (e di nuovo poco dopo, per le liste)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => ApplySearch(q)));
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (_, _) => { t.Stop(); if (TbSearch.Text.Trim() == q) ApplySearch(q); };
            t.Start();
        }

        // Esc svuota la ricerca
        private void TbSearch_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && TbSearch.Text.Length > 0)
            {
                TbSearch.Text = "";
                e.Handled = true;
            }
        }

        private void ApplySearch(string q)
        {
            var a = FilterRows(SearchGeneral, q);
            var b = FilterRows(SearchLauncher, q);
            LabelGeneral.Visibility = SearchGeneral.Visibility = a > 0 ? Visibility.Visible : Visibility.Collapsed;
            LabelLauncher.Visibility = SearchLauncher.Visibility = b > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Visibility = a + b == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Text = $"{TryFindResource("settings_search_none") ?? ""} «{q}»";
        }

        private static bool Contains(string? text, string q)
            => text != null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

        /// <summary>filtra le righe impostazione di una pagina; ritorna quante restano visibili</summary>
        private int FilterRows(DependencyObject root, string q)
        {
            var rowStyle = Application.Current.TryFindResource("SettingRow") as Style;
            var sectionStyle = Application.Current.TryFindResource("SettingSection") as Style;
            var titleStyle = Application.Current.TryFindResource("SettingPageTitle") as Style;

            var all = new List<DependencyObject>();
            void Walk(DependencyObject d)
            {
                var n = VisualTreeHelper.GetChildrenCount(d);
                for (var i = 0; i < n; i++)
                {
                    var ch = VisualTreeHelper.GetChild(d, i);
                    all.Add(ch);
                    Walk(ch);
                }
            }
            Walk(root);

            // nella pagina dei risultati niente scorrimento proprio e niente titolo: scorre il contenitore
            var sv = all.OfType<ScrollViewer>().FirstOrDefault();
            if (sv != null) sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // i margini della pagina li da' il contenitore dei risultati
            var inner = all.OfType<StackPanel>().FirstOrDefault(x => x.MaxWidth < 1000);
            if (inner != null) inner.Margin = new Thickness(0);

            var visible = 0;
            string? section = null;
            var sectionRows = new List<(TextBlock Title, List<HeaderedContentControl> Rows)>();
            foreach (var d in all)
            {
                if (d is TextBlock tb && titleStyle != null && ReferenceEquals(tb.Style, titleStyle)) tb.Visibility = Visibility.Collapsed;
                else if (d is TextBlock tl && tl.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()) tl.Visibility = Visibility.Collapsed;
                else if (d is TextBlock st && sectionStyle != null && ReferenceEquals(st.Style, sectionStyle))
                {
                    section = st.Text;
                    sectionRows.Add((st, new List<HeaderedContentControl>()));
                }
                else if (d is HeaderedContentControl row && rowStyle != null && ReferenceEquals(row.Style, rowStyle))
                {
                    // righe in un gruppo nascosto dallo stato (es. sezioni Teams): non contano come trovate
                    var match = VisualTreeHelper.GetParent(row) is not UIElement { Visibility: not Visibility.Visible }
                                && (Contains(row.Header?.ToString(), q) || Contains(row.Tag?.ToString(), q) || Contains(section, q));
                    row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) visible++;
                    if (sectionRows.Count > 0 && match) sectionRows[^1].Rows.Add(row);
                }
            }
            foreach (var (title, rows) in sectionRows)
                title.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return visible;
        }
    }
}