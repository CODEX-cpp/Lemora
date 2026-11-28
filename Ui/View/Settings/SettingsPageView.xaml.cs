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

        // ---- settings search (General + Quick launcher) ----
        private void TbSearch_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            var q = TbSearch.Text.Trim();
            var on = q.Length > 0;
            NormalHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            SearchHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            // result rows are created after layout: filter once layout is done (and again shortly after, for the lists)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => ApplySearch(q)));
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (_, _) => { t.Stop(); if (TbSearch.Text.Trim() == q) ApplySearch(q); };
            t.Start();
        }

        // Esc clears the search
        private void ClearSearch_OnClick(object sender, RoutedEventArgs e)
        {
            TbSearch.Text = "";
            TbSearch.Focus();
        }

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
            var c = ShowPageLinks(q);
            LabelPages.Visibility = c > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Visibility = a + b + c == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Text = $"{TryFindResource("settings_search_none") ?? ""} «{q}»";
        }

        /// <summary>
        /// what the other settings pages contain (their rows are not filtered here): searching "extension" found nothing because only
        /// General and Quick launcher were searched. A match shows a link that opens the page
        /// </summary>
        internal static (EnumMainWindowPage Page, string Title, string What, string Words)[] OtherPages => new[]
        {
            (EnumMainWindowPage.SettingsCredentialVault, IoC.Translate("Credentials Vault"), IoC.Translate("settings_nav_vault_desc_long"), IoC.Translate("settings_search_words_vault")),
            (EnumMainWindowPage.SettingsKeyMap, IoC.Translate("keymap_title"), IoC.Translate("settings_nav_keymap_desc"), IoC.Translate("settings_search_words_keymap")),
            (EnumMainWindowPage.SettingsData, "Database", IoC.Translate("settings_nav_database_desc"), IoC.Translate("settings_search_words_database")),
            (EnumMainWindowPage.SettingsTheme, IoC.Translate("settings_nav_theme"), IoC.Translate("settings_nav_theme_desc"), IoC.Translate("settings_search_words_theme")),
            (EnumMainWindowPage.SettingsRunners, IoC.Translate("settings_nav_protocol"), IoC.Translate("settings_nav_protocol_desc"), IoC.Translate("settings_search_words_protocol")),
            (EnumMainWindowPage.SettingsMcp, IoC.Translate("settings_nav_mcp"), IoC.Translate("settings_nav_mcp_desc"), IoC.Translate("settings_search_words_mcp")),
        };

        private int ShowPageLinks(string q)
        {
            SearchPages.Children.Clear();
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var mcpAllowed = (DataContext as SettingsPageViewModel)?.McpAllowed ?? false;   // page turned off by the Teams administrator: not found by search either
            var hits = OtherPages.Where(p => (p.Page != EnumMainWindowPage.SettingsMcp || mcpAllowed) && words.All(w => Contains(p.Title + " " + p.What + " " + p.Words, w))).ToList();
            foreach (var p in hits)
            {
                var page = p.Page;
                var b = new Button
                {
                    Style = TryFindResource("BtnSecondary") as Style, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6),
                    Content = $"{p.Title}: {p.What}",
                };
                b.Click += (_, _) => { TbSearch.Text = ""; (DataContext as SettingsPageViewModel)?.CmdShowPage.Execute(page); };   // same path as the side menu
                SearchPages.Children.Add(b);
            }
            return hits.Count;
        }

        private static bool Contains(string? text, string q)
            => text != null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

        /// <summary>filters the setting rows of a page; returns how many stay visible</summary>
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

            // in the results page no own scrolling and no title: the container scrolls
            var sv = all.OfType<ScrollViewer>().FirstOrDefault();
            if (sv != null) sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // the page margins are given by the results container
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
                    // rows in a group hidden by state (e.g. Teams sections): they do not count as found
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