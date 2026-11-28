using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Lemora.Service;
using Shawn.Utils;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// «Terminal settings · xterm.js»: live preview at the top, categories on the left, as in the design.
    /// Every change is saved immediately (encrypted database) and applied to the open sessions.
    /// </summary>
    public class TerminalSettingsWindow : WindowBase
    {
        private static TerminalSettingsWindow? _single;

        public static void ShowSingle(Window? owner)
        {
            if (_single != null) { _single.Activate(); return; }
            _single = new TerminalSettingsWindow();
            if (owner is { IsVisible: true }) _single.Owner = owner;
            _single.Show();
        }

        private TerminalSettings _s = TerminalSettings.Current.Clone();
        private readonly WebView2 _preview = new();
        private readonly ContentControl _page = new();
        private readonly StackPanel _navPanel = new();
        private int _navIndex;
        private bool _building;

        private static readonly string[] Fonts = { "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New" };
        private static string[] Cats => new[] { IoC.Translate("term_cat_look"), IoC.Translate("term_cat_keys"), IoC.Translate("term_cat_behavior"), IoC.Translate("term_cat_blocks"), IoC.Translate("Security"), IoC.Translate("term_cat_history") };

        public TerminalSettingsWindow()
        {
            Title = IoC.Translate("term_title");
            Width = 1000; Height = 780; MinWidth = 780; MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "PrimaryTextBrush");

            var root = new Grid { Margin = new Thickness(8, 4, 8, 8) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // categories
            _navPanel.Margin = new Thickness(0, 8, 0, 8);
            var nav = Panel(_navPanel);
            nav.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(nav, 0);
            root.Children.Add(nav);

            // right column: preview, page, footer
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            _preview.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x10, 0x10);
            _preview.CreationProperties = TerminalHost.CreateProps();
            var prevGrid = new Grid(); prevGrid.Children.Add(_preview);
            var cap = new TextBlock { Text = IoC.Translate("Preview"), Margin = new Thickness(0, 8, 14, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, FontSize = 12, IsHitTestVisible = false };
            cap.SetResourceReference(TextBlock.ForegroundProperty, "IslandSecondaryTextBrush");
            prevGrid.Children.Add(cap);
            var prevPanel = Panel(prevGrid); prevPanel.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(prevPanel, 0);
            right.Children.Add(prevPanel);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page };
            var pagePanel = Panel(scroll); pagePanel.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(pagePanel, 1);
            right.Children.Add(pagePanel);

            var foot = new DockPanel { Margin = new Thickness(16, 10, 12, 10), LastChildFill = false };
            var close = Btn(IoC.Translate("Close"), true, () => Close());
            var reset = Btn(IoC.Translate("Reset"), false, () => { _s = TerminalSettings.Defaults(); Apply(); ShowPage(); });
            reset.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(reset, Dock.Right);
            var note = new TextBlock { Text = IoC.Translate("term_apply_note"), VerticalAlignment = VerticalAlignment.Center };
            note.SetResourceReference(TextBlock.ForegroundProperty, "IslandSecondaryTextBrush");
            foot.Children.Add(close); foot.Children.Add(reset); foot.Children.Add(note);
            var footPanel = Panel(foot);
            Grid.SetRow(footPanel, 2);
            right.Children.Add(footPanel);
            Content = root;

            BuildNav();
            ShowPage();
            Loaded += async (_, _) => await StartPreviewAsync();
            TerminalSettings.Changed += OnChanged;
            Closed += (_, _) => { TerminalSettings.Changed -= OnChanged; _single = null; try { _preview.Dispose(); } catch { } };
        }

        // ---------- preview
        private async System.Threading.Tasks.Task StartPreviewAsync()
        {
            try
            {
                await _preview.EnsureCoreWebView2Async();
                var st = _preview.CoreWebView2.Settings;
                st.AreDefaultContextMenusEnabled = false; st.AreDevToolsEnabled = false; st.IsStatusBarEnabled = false;
                st.IsZoomControlEnabled = false; st.AreBrowserAcceleratorKeysEnabled = false;
                _preview.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true; };
                _preview.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                _preview.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    if (doc.RootElement.GetProperty("t").GetString() == "ready") { _previewReady = true; SendPreview(); }
                };
                _preview.NavigateToString(TerminalHost.BuildHtml());
            }
            catch (Exception e) { SimpleLogHelper.Warning("Terminal preview: " + e.Message); }
        }

        private void Post(object msg)
        {
            try { _preview.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg, new JsonSerializerOptions { IncludeFields = true })); } catch { /* preview closed */ }
        }

        private void SendPreview()
        {
            Post(new { t = "preview" });
            Post(new { t = "settings", s = _s.ToPage() });
            Post(new { t = "out", d = Convert.ToBase64String(Encoding.UTF8.GetBytes(Sample)) });
        }

        private void OnChanged() => Post(new { t = "settings", s = TerminalSettings.Current.ToPage() });

        private const string Esc = "\u001b", Bel = "\u0007";
        private static string Prompt => $"{Esc}]133;A{Bel}{Esc}[32mroot@web-01{Esc}[0m:{Esc}[34m~/app{Esc}[0m# {Esc}]133;B{Bel}";
        // preview as in the design: a successful command with colors, and an input line with the cursor
        private static string Sample =>
            Prompt + "ls -la\r\n" + $"{Esc}]133;C{Bel}drwxr-xr-x 3 root root {Esc}[34mreleases{Esc}[0m\r\n-rwxr-xr-x 1 root root {Esc}[32mdeploy.sh{Esc}[0m\r\n{Esc}]133;D;0{Bel}" +
            $"\r\n{Esc}]133;A{Bel}{Esc}[32mroot@web-01{Esc}[0m:{Esc}[34m~/app{Esc}[0m# {Esc}]133;B{Bel}{Esc}[31merror:{Esc}[0m exit 1 {Esc}[33mwarning{Esc}[0m ";

        // ---------- window structure
        private Border Panel(UIElement child)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = child, ClipToBounds = true };
            b.SetResourceReference(Border.BackgroundProperty, "IslandSubCardBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
            return b;
        }

        private void BuildNav()
        {
            _navPanel.Children.Clear();
            for (int i = 0; i < Cats.Length; i++)
            {
                var idx = i;
                var t = new TextBlock { Text = Cats[i], FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0) };
                var bar = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Visibility = idx == _navIndex ? Visibility.Visible : Visibility.Collapsed };
                bar.SetResourceReference(Border.BackgroundProperty, "AccentMidBrush");
                var g = new Grid { Height = 40, Cursor = Cursors.Hand };
                g.Children.Add(t); g.Children.Add(bar);
                var item = new Border { Child = g };
                if (idx == _navIndex) { item.SetResourceReference(Border.BackgroundProperty, "PrimaryLightBrush"); t.FontWeight = FontWeights.SemiBold; t.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush"); }
                else { item.Background = Brushes.Transparent; t.SetResourceReference(TextBlock.ForegroundProperty, "IslandSecondaryTextBrush"); }
                item.MouseEnter += (_, _) => { if (idx != _navIndex) item.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)); };
                item.MouseLeave += (_, _) => { if (idx != _navIndex) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (_, _) => { _navIndex = idx; BuildNav(); ShowPage(); };
                _navPanel.Children.Add(item);
            }
        }

        // ---------- automatic test (DevDemo): changes page and theme, takes a screenshot of the preview
        private bool _previewReady;
        internal async System.Threading.Tasks.Task<string> TestAsync(string photoPath)
        {
            for (int i = 0; i < 100 && !_previewReady; i++) await System.Threading.Tasks.Task.Delay(100);
            var pages = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Cats.Length; i++) { _navIndex = i; BuildNav(); ShowPage(); pages.Add(((StackPanel)_page.Content).Children.Count.ToString()); }
            _navIndex = 0; BuildNav(); ShowPage();
            var old = _s.Theme; _s.Theme = "blunotte"; _s.FontSize = 15; _s.CursorStyle = "bar"; Apply();
            await System.Threading.Tasks.Task.Delay(1200);
            var cap = _preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
            string photo;
            if (await System.Threading.Tasks.Task.WhenAny(cap, System.Threading.Tasks.Task.Delay(8000)) == cap)
            {
                using var d = JsonDocument.Parse(await cap);
                System.IO.File.WriteAllBytes(photoPath, Convert.FromBase64String(d.RootElement.GetProperty("data").GetString()!));
                photo = "ok";
            }
            else photo = "timeout";
            _s.Theme = old; Apply(); ShowPage();
            foreach (var idx in new[] { 0, 3 })
            {
                _navIndex = idx; BuildNav(); ShowPage(); UpdateLayout(); await System.Threading.Tasks.Task.Delay(300);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
                rtb.Render(bg);
                rtb.Render((Visual)Content);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(photoPath.Replace(".png", $"_finestra{idx}.png")); enc.Save(fs);
            }
            return $"previewReady={_previewReady} itemsPerPage=[{string.Join(",", pages)}] photo={photo}";
        }

        // ---------- pages
        private void Apply() { TerminalSettings.Save(_s); }

        private void ShowPage()
        {
            _building = true;
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            switch (_navIndex)
            {
                case 0: BuildLook(p); break;
                case 1: BuildKeys(p); break;
                case 2: BuildBehavior(p); break;
                case 3: BuildBlocks(p); break;
                case 4: BuildSecurity(p); break;
                default: BuildHistory(p); break;
            }
            _page.Content = p;
            _building = false;
        }

        private void BuildLook(StackPanel p)
        {
            p.Children.Add(Section(IoC.Translate("Theme"), first: true));
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 6) };
            foreach (var t in TerminalSettings.Themes) grid.Children.Add(ThemeCard(t));
            p.Children.Add(grid);

            p.Children.Add(Section("Font"));
            var font = new ComboBox { Width = 260 };
            foreach (var f in Fonts.Concat(new[] { _s.FontFamily }).Distinct()) font.Items.Add(f);
            font.SelectedItem = _s.FontFamily;
            font.SelectionChanged += (_, _) => { if (_building || font.SelectedItem is not string f) return; _s.FontFamily = f; Apply(); };
            p.Children.Add(Row(IoC.Translate("Font"), null, font));
            p.Children.Add(Row(IoC.Translate("term_font_size"), null, SliderRow(9, 24, 0.5, _s.FontSize, v => { _s.FontSize = v; Apply(); }, v => v.ToString("0.#") + " px")));
            p.Children.Add(Row(IoC.Translate("term_line_height"), null, SliderRow(1.0, 1.6, 0.05, _s.LineHeight, v => { _s.LineHeight = v; Apply(); }, v => v.ToString("0.00"))));

            p.Children.Add(Section(IoC.Translate("term_cursor")));
            var cur = new ComboBox { Width = 200 };
            cur.Items.Add(IoC.Translate("term_cursor_block")); cur.Items.Add(IoC.Translate("term_cursor_bar")); cur.Items.Add(IoC.Translate("term_cursor_underline"));
            cur.SelectedIndex = _s.CursorStyle == "bar" ? 1 : _s.CursorStyle == "underline" ? 2 : 0;
            cur.SelectionChanged += (_, _) => { if (_building) return; _s.CursorStyle = cur.SelectedIndex == 1 ? "bar" : cur.SelectedIndex == 2 ? "underline" : "block"; Apply(); };
            p.Children.Add(Row(IoC.Translate("term_cursor_shape"), null, cur));
            p.Children.Add(Row(IoC.Translate("term_cursor_blink"), null, Toggle(_s.CursorBlink, v => { _s.CursorBlink = v; Apply(); })));
        }

        private void BuildKeys(StackPanel p)
        {
            p.Children.Add(Section(IoC.Translate("term_cat_keys"), first: true));
            p.Children.Add(Row(IoC.Translate("term_keys_bar"), IoC.Translate("term_keys_bar_hint"),
                Toggle(_s.SpecialKeysBar, v => { _s.SpecialKeysBar = v; Apply(); })));
            var on = _s.SpecialKeys.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            void Key(string id, string label)
            {
                p.Children.Add(Row(label, null, Toggle(on.Contains(id), v => { if (v) on.Add(id); else on.Remove(id); _s.SpecialKeys = string.Join(",", on); Apply(); })));
            }
            Key("ctrlc", "Ctrl C"); Key("ctrld", "Ctrl D"); Key("ctrlz", "Ctrl Z"); Key("esc", "Esc"); Key("tab", "Tab"); Key("arrows", IoC.Translate("term_key_arrows")); Key("fkeys", "F1 – F12"); Key("custom", IoC.Translate("term_key_custom"));
            var label = new TextBox { Width = 140, Text = _s.CustomKeyLabel, MaxLength = 12 };
            label.TextChanged += (_, _) => { if (_building) return; _s.CustomKeyLabel = label.Text; Apply(); };
            var text = new TextBox { Width = 260, Text = _s.CustomKeyText, MaxLength = 80 };
            text.TextChanged += (_, _) => { if (_building) return; _s.CustomKeyText = text.Text; Apply(); };
            p.Children.Add(Row(IoC.Translate("term_custom_label"), null, label));
            p.Children.Add(Row(IoC.Translate("term_custom_text"), string.Format(IoC.Translate("term_custom_text_hint"), "\\r", "\\t", "\\e"), text));
        }

        private void BuildBehavior(StackPanel p)
        {
            p.Children.Add(Section(IoC.Translate("term_cat_behavior"), first: true));
            var sb = new ComboBox { Width = 200 };
            foreach (var v in new[] { 1000, 5000, 10000, 50000, 100000 }.Concat(new[] { _s.Scrollback }).Distinct().OrderBy(x => x)) sb.Items.Add(v);
            sb.SelectedItem = _s.Scrollback;
            sb.SelectionChanged += (_, _) => { if (_building || sb.SelectedItem is not int v) return; _s.Scrollback = v; Apply(); };
            p.Children.Add(Row(IoC.Translate("term_scrollback"), IoC.Translate("term_scrollback_hint"), sb));

            var bell = new ComboBox { Width = 200 };
            bell.Items.Add(IoC.Translate("term_bell_none")); bell.Items.Add(IoC.Translate("term_bell_visual")); bell.Items.Add(IoC.Translate("term_bell_sound"));
            bell.SelectedIndex = _s.Bell == "none" ? 0 : _s.Bell == "sound" ? 2 : 1;
            bell.SelectionChanged += (_, _) => { if (_building) return; _s.Bell = bell.SelectedIndex == 0 ? "none" : bell.SelectedIndex == 2 ? "sound" : "visual"; Apply(); };
            p.Children.Add(Row(IoC.Translate("term_bell"), IoC.Translate("term_bell_hint"), bell));
            p.Children.Add(Row(IoC.Translate("term_copy_on_select"), IoC.Translate("term_copy_on_select_hint"), Toggle(_s.CopyOnSelect, v => { _s.CopyOnSelect = v; Apply(); })));
            p.Children.Add(Row(IoC.Translate("term_warn_paste"), IoC.Translate("term_warn_paste_hint"), Toggle(_s.WarnMultiLinePaste, v => { _s.WarnMultiLinePaste = v; Apply(); })));
        }

        private void BuildBlocks(StackPanel p)
        {
            p.Children.Add(Section(IoC.Translate("term_cat_blocks"), first: true));
            p.Children.Add(Row("Shell integration", IoC.Translate("term_shell_integration_hint"),
                Toggle(_s.ShellIntegration, v => { _s.ShellIntegration = v; Apply(); })));
            var show = new Button { Content = IoC.Translate("term_show"), Padding = new Thickness(18, 6, 18, 6), MinWidth = 90 };
            show.SetResourceReference(StyleProperty, "BtnSecondary");
            show.Click += (_, _) => Lemora.Utils.MessageBoxHelper.Info(TerminalHost.ShellIntegrationText.Trim(), IoC.Translate("term_sent_line_title"));
            p.Children.Add(Row(IoC.Translate("term_show_sent_line"), IoC.Translate("term_show_sent_line_hint"), show));

            var ac = new ComboBox { Width = 200 };
            var never = IoC.Translate("term_never");
            string Lines(int n) => n == 0 ? never : string.Format(IoC.Translate("term_lines_or_more"), n);
            foreach (var o in new[] { 0, 10, 20, 50, 100 }.Concat(new[] { _s.AutoCollapseLines }).Distinct().OrderBy(x => x)) ac.Items.Add(Lines(o));
            ac.SelectedItem = Lines(_s.AutoCollapseLines);
            ac.SelectionChanged += (_, _) =>
            {
                if (_building || ac.SelectedItem is not string t) return;
                _s.AutoCollapseLines = t == never ? 0 : int.Parse(t.Split(' ')[0]); Apply();   // the count is the first word of the text
            };
            p.Children.Add(Row(IoC.Translate("term_auto_collapse"), null, ac));
            p.Children.Add(Row(IoC.Translate("term_show_duration"), null, Toggle(_s.ShowDurationTime, v => { _s.ShowDurationTime = v; Apply(); })));
            p.Children.Add(Row(IoC.Translate("term_show_copy_save"), IoC.Translate("term_show_copy_save_hint"), Toggle(_s.ShowCopySaveButtons, v => { _s.ShowCopySaveButtons = v; Apply(); })));
        }

        private void BuildSecurity(StackPanel p)
        {
            p.Children.Add(Section(IoC.Translate("Security"), first: true));
            p.Children.Add(Row(IoC.Translate("term_verify_host_key"), IoC.Translate("term_verify_host_key_hint"),
                Toggle(_s.VerifyHostKey, v => { _s.VerifyHostKey = v; Apply(); })));
            p.Children.Add(Row(IoC.Translate("term_server_clipboard"), IoC.Translate("term_server_clipboard_hint"),
                Toggle(_s.AllowServerClipboard, v => { _s.AllowServerClipboard = v; Apply(); })));
            p.Children.Add(Row(IoC.Translate("term_confirm_links"), IoC.Translate("term_confirm_links_hint"), Toggle(_s.ConfirmLinks, v => { _s.ConfirmLinks = v; Apply(); })));
        }

        private void BuildHistory(StackPanel p)
        {
            p.Children.Add(Section(IoC.Translate("term_cat_history"), first: true));
            var lim = new ComboBox { Width = 200 };
            foreach (var v in new[] { 100, 500, 2000, 10000 }.Concat(new[] { _s.HistoryLimit }).Distinct().OrderBy(x => x)) lim.Items.Add(v);
            lim.SelectedItem = _s.HistoryLimit;
            lim.SelectionChanged += (_, _) => { if (_building || lim.SelectedItem is not int v) return; _s.HistoryLimit = v; Apply(); };
            p.Children.Add(Row(IoC.Translate("term_history_limit"), IoC.Translate("term_history_limit_hint"), lim));
        }

        // ---------- UI pieces
        private static UIElement Section(string t, bool first = false)
        {
            var b = new TextBlock { Text = t.ToUpperInvariant(), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 22, 0, 8) };
            b.SetResourceReference(TextBlock.ForegroundProperty, "AccentMidBrush");
            return b;
        }

        /// <summary>row as in the design: title (and explanation) on the left, control on the right, divider below</summary>
        private static UIElement Row(string label, string? hint, UIElement control)
        {
            var g = new Grid { Margin = new Thickness(0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 20, 12) };
            var l = new TextBlock { Text = label, FontSize = 15, TextWrapping = TextWrapping.Wrap };
            l.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            left.Children.Add(l);
            if (hint != null)
            {
                var h = new TextBlock { Text = hint, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "IslandSecondaryTextBrush");
                left.Children.Add(h);
            }
            g.Children.Add(left);
            if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.HorizontalAlignment = HorizontalAlignment.Right; }
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
            line.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            return line;
        }

        private CheckBox Toggle(bool on, Action<bool> set)
        {
            var c = new CheckBox { IsChecked = on };
            c.SetResourceReference(StyleProperty, "ToggleSwitch");
            c.Checked += (_, _) => { if (!_building) set(true); };
            c.Unchecked += (_, _) => { if (!_building) set(false); };
            return c;
        }

        private UIElement SliderRow(double min, double max, double step, double value, Action<double> set, Func<double, string> fmt)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var s = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = value, Width = 280, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = fmt(value), Width = 80, TextAlignment = TextAlignment.Right, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "AccentMidBrush");
            s.ValueChanged += (_, e) => { t.Text = fmt(e.NewValue); if (!_building) set(Math.Round(e.NewValue / step) * step); };
            sp.Children.Add(s); sp.Children.Add(t);
            return sp;
        }

        /// <summary>theme card: colored prompt, five color swatches and name, as in the design</summary>
        private Border ThemeCard(TerminalTheme t)
        {
            Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            var prompt = new TextBlock { FontFamily = new FontFamily("Cascadia Mono"), FontSize = 13, Margin = new Thickness(14, 12, 14, 6) };
            prompt.Inlines.Add(new System.Windows.Documents.Run("root@web") { Foreground = B(t.Ansi[2]) });
            prompt.Inlines.Add(new System.Windows.Documents.Run(":~# ls") { Foreground = B(t.Foreground) });
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 6) };
            foreach (var i in new[] { 1, 2, 3, 4, 5 }) sw.Children.Add(new Border { Width = 14, Height = 8, Margin = new Thickness(0, 0, 4, 0), Background = B(t.Ansi[i]) });
            var name = new TextBlock { Text = t.Name, Margin = new Thickness(14, 0, 14, 12), FontSize = 14.5, Foreground = B(t.Foreground), TextTrimming = TextTrimming.CharacterEllipsis };
            var inner = new StackPanel(); inner.Children.Add(prompt); inner.Children.Add(sw); inner.Children.Add(name);
            var card = new Border
            {
                Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, Child = inner,
                Background = B(t.Background), BorderThickness = new Thickness(2), ToolTip = t.Name,
            };
            if (_s.Theme == t.Key) card.SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush"); else card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            card.MouseLeftButtonUp += (_, _) => { _s.Theme = t.Key; Apply(); ShowPage(); };
            return card;
        }

        private Button Btn(string text, bool primary, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(20, 7, 20, 7), MinWidth = 100, FontSize = 14.5 };
            b.SetResourceReference(StyleProperty, primary ? "BtnPrimary" : "BtnSecondary");
            b.Click += (_, _) => click();
            return b;
        }
    }
}
