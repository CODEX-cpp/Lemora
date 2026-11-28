using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Setup;
using Lemora.Utils;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;

namespace Lemora.View.Host
{
    /// <summary>
    /// Folder password manager: a handle on the edge of the screen of a saved RDP or VNC session, which opens a popup with the passwords
    /// of the devices in the same folder (NAS, switches, DVRs...) to copy on the fly.
    /// The remote screen is a native window and covers any WPF element of the same window: so the handle and popup live in their own
    /// small, transparent window, "owned" by the session window and always placed on the edge of the remote screen.
    /// </summary>
    internal sealed class DevicePasswordDrawer
    {
        private static readonly Dictionary<HostBase, DevicePasswordDrawer> Map = new();

        private const double TabShort = 34, TabLong = 124, PopupWidth = 290, PopupMaxHeight = 430, EdgeGap = 6, KeepIn = 4, ShadowPad = 16;

        // ---- external use
        /// <summary>called when the session is in the window: creates the manager (only once) if the session is eligible</summary>
        public static void Attach(HostBase host)
        {
            if (!DevicePasswordService.Enabled || Map.ContainsKey(host) || Closed.TryGetValue(host, out _) || !IsEligible(host)) return;
            Map[host] = new DevicePasswordDrawer(host);
        }

        /// <summary>sessions already closed: when the tab is closed the host can still receive a Loaded, and it must not bring the handle back</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HostBase, object> Closed = new();

        /// <summary>the session closes: remove handle and popup</summary>
        public static void Detach(HostBase host)
        {
            Closed.AddOrUpdate(host, new object());
            if (Map.Remove(host, out var d)) d.Dispose();
        }

        /// <summary>Ctrl+Alt+P: opens or closes the session popup. false = this session has no manager.</summary>
        public static bool Toggle(HostBase? host)
        {
            if (host == null || !Map.TryGetValue(host, out var d)) return false;
            d.ToggleOpen();
            return true;
        }

        // ---- only for background tests (LEMORA_DEMO=devpw)
        internal static IReadOnlyList<Window> DemoWindows() => Map.Values.Where(d => d._win != null).Select(d => (Window)d._win!).ToList();

        internal static string DemoLast = "";

        internal static void DemoDo(HostBase host, string what, string arg = "")
        {
            if (!Map.TryGetValue(host, out var d) || d._win == null) return;
            switch (what)
            {
                case "search": if (d._win.SearchBox != null) d._win.SearchBox.Text = arg; break;
                case "place": { var p = arg.Split(','); d._place = new DrawerPlace { Edge = p[0], T = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) }; d.Render(); d.Layout(); break; }
                case "new": d.OpenForm(null); break;
                case "edit": d.OpenForm(DevicePasswordService.ForServer(host.ProtocolServer.TreeNodes).FirstOrDefault()); break;
                case "list": d.BackToList(); break;
                case "layout": d.Layout(); break;
                case "drag":   // drag to a point on the screen (x,y in program units): the handle must stay the same element even when changing edge
                {
                    var before = d._win.Content; var oldEdge = d._place.Edge;
                    var xy = arg.Split(',');
                    d.DragTo(new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)));
                    DemoLast = $"dragged {oldEdge} -> {d._place.Edge} (t={d._place.T:0.00}), same element={ReferenceEquals(before, d._win.Content)}";
                    break;
                }
            }
        }

        /// <summary>only SAVED RDP and VNC connections (quick connections have no folder)</summary>
        private static bool IsEligible(HostBase host) => host is AxMsRdpClient09Host or VncHost or IntegrateHost { IsFreeRdp: true } && !host.ProtocolServer.IsTmpSession();   // RDP with FreeRDP too; only for devices registered in the database, not for quick connections

        // ---- state
        private readonly HostBase _host;
        private readonly DispatcherTimer _timer;
        private DrawerWindow? _win;
        private Window? _owner;
        private int _unloadedTicks;
        private DrawerPlace _place = DevicePasswordService.GetPlace();
        private enum Mode { Tab, List, Form }
        private Mode _mode = Mode.Tab;
        private string _query = "";
        private DeviceEntry? _editing;     // null in Form = new entry

        private DevicePasswordDrawer(HostBase host)
        {
            _host = host;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            host.SizeChanged += OnMoved;
            DevicePasswordService.Changed += OnDataChanged;
            Tick();
        }

        private void Dispose()
        {
            _timer.Stop();
            DevicePasswordService.Changed -= OnDataChanged;
            WatchOwner(null);
            _host.SizeChanged -= OnMoved;
            CloseWindow();
        }

        /// <summary>
        /// Handle and popup follow the window immediately: while dragging it, the timer alone lagged about a second behind
        /// (the Windows move loop leaves little room for timers). Position and size events arrive at every step.
        /// </summary>
        private void WatchOwner(Window? owner)
        {
            if (_owner != null) { _owner.LocationChanged -= OnMoved; _owner.SizeChanged -= OnMoved; _owner.StateChanged -= OnMoved; }
            _owner = owner;
            if (_owner != null) { _owner.LocationChanged += OnMoved; _owner.SizeChanged += OnMoved; _owner.StateChanged += OnMoved; }
        }

        private void OnMoved(object? sender, EventArgs e)
        {
            try { if (_win != null && _win.IsVisible && HostShown()) Layout(); }
            catch (Exception ex) { SimpleLogHelper.Debug("Password manager: repositioning failed: " + ex.Message); }
        }

        private void CloseWindow()
        {
            if (_win == null) return;
            var w = _win; _win = null;
            w.CloseByCode();
        }

        private void OnDataChanged() => _host.Dispatcher.BeginInvoke(new Action(() => { if (_win != null && _mode == Mode.List) Render(); }));

        private string FolderPath => FolderService.ToPath(_host.ProtocolServer.TreeNodes);

        private bool HasEntries => DevicePasswordService.ForServer(_host.ProtocolServer.TreeNodes).Count > 0;

        // ---- position and visibility (always attached to the remote screen)
        private bool HostShown()
        {
            if (!_host.IsLoaded || !_host.IsVisible || _host.ActualWidth < 200 || _host.ActualHeight < 160) return false;
            var w = Window.GetWindow(_host);
            return w is { IsVisible: true, WindowState: not WindowState.Minimized };
        }

        private Rect HostRect()
        {
            var dpi = VisualTreeHelper.GetDpi(_host);
            var p = _host.PointToScreen(new Point(0, 0));
            return new Rect(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY, _host.ActualWidth, _host.ActualHeight);
        }

        private void Tick()
        {
            try
            {
                // session closed without going through Close() (the tab disappears and the host is no longer in the window): after ~6 s the handle goes away; if the host comes back (tab switched) it restarts on its own
                if (!_host.IsLoaded) { if (++_unloadedTicks > 40) { Map.Remove(_host); Dispose(); return; } }
                else _unloadedTicks = 0;
                if (!HostShown() || (DevicePasswordService.ShowOnlyIfFolderHasEntries && _mode == Mode.Tab && !HasEntries))
                {
                    if (_win != null && _win.IsVisible) _win.Hide();
                    return;
                }
                var owner = Window.GetWindow(_host);
                if (_win == null || _owner != owner)
                {
                    CloseWindow();
                    WatchOwner(owner);
                    _win = new DrawerWindow(owner!, this);
                    Render();
                }
                if (!_win.IsVisible) _win.Show();
                Layout();
            }
            catch (Exception ex) { SimpleLogHelper.Warning("Gestore password: " + ex.Message); }
        }

        /// <summary>puts the window in place on the edge: handle when closed, popup (centered on the handle but inside the screen) when open</summary>
        private void Layout()
        {
            if (_win == null) return;
            var r = HostRect();
            if (r.Width < 10 || r.Height < 10) return;
            bool vertical = _place.Edge is "left" or "right";
            double along = vertical
                ? r.Top + S(TabLong) / 2 + _place.T * Math.Max(0, r.Height - S(TabLong))
                : r.Left + S(TabLong) / 2 + _place.T * Math.Max(0, r.Width - S(TabLong));
            double w, h, x, y;
            if (_mode == Mode.Tab)
            {
                w = vertical ? S(TabShort) : S(TabLong); h = vertical ? S(TabLong) : S(TabShort);
                x = _place.Edge switch { "left" => r.Left, "right" => r.Right - S(TabShort), _ => along - S(TabLong) / 2 };
                y = _place.Edge switch { "top" => r.Top, "bottom" => r.Bottom - S(TabShort), _ => along - S(TabLong) / 2 };
            }
            else
            {
                w = Math.Min(S(PopupWidth), r.Width - 2 * KeepIn);
                h = Math.Min(S(PopupMaxHeight), r.Height - 2 * KeepIn);
                x = _place.Edge switch { "left" => r.Left + EdgeGap, "right" => r.Right - w - EdgeGap, _ => along - w / 2 };
                y = _place.Edge switch { "top" => r.Top + EdgeGap, "bottom" => r.Bottom - h - EdgeGap, _ => along - h / 2 };
                x = Math.Clamp(x, r.Left + KeepIn, Math.Max(r.Left + KeepIn, r.Right - w - KeepIn));
                y = Math.Clamp(y, r.Top + KeepIn, Math.Max(r.Top + KeepIn, r.Bottom - h - KeepIn));
            }
            // the popup has a transparent margin around it for the shadow (as in the mock): the window is larger than the popup, the popup stays where it was computed
            double pad = _mode == Mode.Tab ? 0 : ShadowPad;
            x -= pad; y -= pad; w += 2 * pad; h += 2 * pad;
            if (Math.Abs(_win.Left - x) > 0.5) _win.Left = x;
            if (Math.Abs(_win.Top - y) > 0.5) _win.Top = y;
            if (Math.Abs(_win.Width - w) > 0.5) _win.Width = w;
            if (Math.Abs(_win.Height - h) > 0.5) _win.Height = h;
        }

        /// <summary>
        /// Dark theme: the mock colors (popup #1e1e1e, bar and handle #262626, fields and footer #171717, hovered row #2a2a2a, dividers #2e2e2e, borders #4a4a4a, secondary text #c0c0c0)
        /// instead of the theme ones, which gave bluish greys. They are set as window resources: they win over the app ones only here.
        /// With a light theme the theme colors stay (same roles as in the app style).
        /// </summary>
        private static void ApplyPalette(Window w)
        {
            if (Application.Current?.TryFindResource("PrimaryMidBrush") is not SolidColorBrush mid) return;
            var c = mid.Color;
            if ((0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5) return;   // light theme
            SolidColorBrush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
            w.Resources["PrimaryMidBrush"] = B("#1e1e1e");
            w.Resources["PrimaryLightBrush"] = B("#262626");
            w.Resources["PrimaryDarkBrush"] = B("#171717");
            w.Resources["IslandBorderBrush"] = B("#4a4a4a");
            w.Resources["IslandButtonBorderBrush"] = B("#4a4a4a");
            w.Resources["IslandDividerBrush"] = B("#2e2e2e");
            w.Resources["IslandHoverBrush"] = B("#2a2a2a");
            w.Resources["IslandSecondaryTextBrush"] = B("#c0c0c0");
        }

        /// <summary>popup frame, margin and shadow (0 8px 24px black at 50% as in the mock)</summary>
        private static void PopupChrome(Border root)
        {
            root.Margin = new Thickness(ShadowPad);
            root.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        }

        /// <summary>dragging: the handle snaps to the nearest edge and slides along it, even around the corners</summary>
        private void DragTo(Point screenDip)
        {
            var r = HostRect();
            var d = new (string edge, double dist)[]
            {
                ("left", Math.Abs(screenDip.X - r.Left)), ("right", Math.Abs(screenDip.X - r.Right)),
                ("top", Math.Abs(screenDip.Y - r.Top)), ("bottom", Math.Abs(screenDip.Y - r.Bottom)),
            };
            var edge = d.OrderBy(x => x.dist).First().edge;
            bool vertical = edge is "left" or "right";
            double span = Math.Max(1, (vertical ? r.Height : r.Width) - S(TabLong));
            double t = vertical ? (screenDip.Y - r.Top - S(TabLong) / 2) / span : (screenDip.X - r.Left - S(TabLong) / 2) / span;
            var oldEdge = _place.Edge;
            _place = new DrawerPlace { Edge = edge, T = Math.Clamp(t, 0, 1) };
            if (oldEdge != edge && _mode == Mode.Tab && _win?.Content is Border tab) FillTab(tab);   // the handle changes orientation (vertical/horizontal) while staying the same element
            Layout();
        }

        private void ToggleOpen()
        {
            if (_win == null) { Tick(); if (_win == null) return; }
            if (_mode == Mode.Tab) Open(); else CloseToTab();
        }

        private void Open()
        {
            _mode = Mode.List; _query = ""; _editing = null;
            Render(); Layout();
            _win?.FocusSearch();
        }

        private void CloseToTab()
        {
            _mode = Mode.Tab; _editing = null;
            Render(); Layout();
            _host.FocusOnMe();   // the remote computer takes the keys back
        }

        // ---- drawing
        private void Render()
        {
            if (_win == null) return;
            _win.SearchBox = null; _win.KeyDownHandler = null;
            switch (_mode)
            {
                case Mode.Tab: _win.SetContent(BuildTab()); break;
                case Mode.List: _win.SetContent(BuildPopup(BuildListBody(), BuildListFooter())); break;
                case Mode.Form: _win.SetContent(BuildForm()); break;
            }
        }

        private static string T(string key) => SetupText.T(key);

        /// <summary>mock measurement (drawn for size M) scaled to the size chosen in the settings: heights and widths follow the texts</summary>
        private static double S(double v) => Math.Round(v * Lemora.Utils.FontScale.Instance.Scale);

        private static void Font(TextBlock tb, double size, FontWeight? weight = null)
        {
            FontPx.Apply(tb, TextBlock.FontSizeProperty, size);   // mock measurements (drawn for size M) that grow and shrink with the one chosen in the settings
            if (weight != null) tb.FontWeight = weight.Value;
        }

        private static TextBlock Text(string text, double size, string brush = "PrimaryTextBrush", FontWeight? weight = null)
        {
            var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            Font(tb, size, weight);
            tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return tb;
        }

        /// <summary>hand-drawn key (no symbol font to depend on)</summary>
        private static FrameworkElement KeyIcon()
        {
            var p = new Path
            {
                Data = Geometry.Parse("M 6,2.5 A 3.5,3.5 0 1 0 6,9.5 A 3.5,3.5 0 1 0 6,2.5 M 8.6,8.6 L 16,16 M 13,13 L 15.2,10.8 M 15,15 L 17.2,12.8"),
                StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = 18, Height = 18, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center,
            };
            p.SetResourceReference(Shape.StrokeProperty, "AccentMidBrush");
            return p;
        }

        private FrameworkElement BuildTab()
        {
            var tab = new Border { BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "Ctrl + Alt + P" };
            tab.SetResourceReference(Border.BackgroundProperty, "PrimaryLightBrush");   // mock: handle #262626 with border #4a4a4a
            tab.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
            FillTab(tab);
            MakeDraggable(tab, Open);   // click without moving (less than 4 px) = open
            return tab;
        }

        /// <summary>
        /// Content and corners of the handle for the current edge (vertical on left/right, horizontal on top/bottom).
        /// The SAME element is updated when the edge changes during dragging: replacing it made the mouse lose its grip (the handle lagged behind).
        /// </summary>
        private void FillTab(Border tab)
        {
            bool vertical = _place.Edge is "left" or "right";
            var label = Text(T("dp_tab"), 12.5, weight: FontWeights.SemiBold);
            var panel = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = KeyIcon();
            if (vertical) { icon.Margin = new Thickness(0, 0, 0, 6); label.LayoutTransform = new RotateTransform(90); }   // same direction on both sides, never mirrored
            else icon.Margin = new Thickness(0, 0, 7, 0);
            panel.Children.Add(icon); panel.Children.Add(label);
            tab.Child = panel;
            tab.CornerRadius = _place.Edge switch { "left" => new CornerRadius(0, 4, 4, 0), "right" => new CornerRadius(4, 0, 0, 4), "top" => new CornerRadius(0, 0, 4, 4), _ => new CornerRadius(4, 4, 0, 0) };
        }

        private FrameworkElement BuildPopup(FrameworkElement body, FrameworkElement footer)
        {
            var root = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
            root.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
            root.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = BuildSearchBar();
            Grid.SetRow(header, 0); Grid.SetRow(body, 1); Grid.SetRow(footer, 2);
            grid.Children.Add(header); grid.Children.Add(body); grid.Children.Add(footer);
            root.Child = grid;
            root.ClipToBounds = true;   // the rounded corners clip the bar and footer
            PopupChrome(root);
            return root;
        }

        private FrameworkElement BuildSearchBar()
        {
            var bar = new Border { Padding = new Thickness(8) };
            bar.SetResourceReference(Border.BackgroundProperty, "PrimaryLightBrush");
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // field darker than the bar, with the 2 px accent line below (mock: bar #262626, field #171717); the TextBox has no border or background of its own so the field theme does not change its look
            var box = new TextBox { Text = _query, BorderThickness = new Thickness(0), Padding = new Thickness(26, 6, 6, 6), VerticalContentAlignment = VerticalAlignment.Center, Height = S(32), Background = Brushes.Transparent };
            box.Template = BareTextBoxTemplate();
            FontPx.Apply(box, Control.FontSizeProperty, 12.5);
            box.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            box.SetResourceReference(TextBoxBase.CaretBrushProperty, "PrimaryTextBrush");
            var hint = Text(T("dp_search"), 12.5, "IslandSecondaryTextBrush");
            hint.IsHitTestVisible = false; hint.Margin = new Thickness(28, 0, 0, 2);
            hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var lens = new Path
            {
                Data = Geometry.Parse("M 5,1.5 A 3.7,3.7 0 1 0 5,8.9 A 3.7,3.7 0 1 0 5,1.5 M 7.8,7.8 L 11,11"), StrokeThickness = 1.4, Width = 13, Height = 13, Stretch = Stretch.None,
                Margin = new Thickness(7, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };
            lens.SetResourceReference(Shape.StrokeProperty, "IslandSecondaryTextBrush");
            box.TextChanged += (_, _) => { _query = box.Text; hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshList(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseToTab(); e.Handled = true; } };
            var boxInner = new Grid(); boxInner.Children.Add(box); boxInner.Children.Add(hint); boxInner.Children.Add(lens);
            var boxHost = new Border { Child = boxInner, BorderThickness = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(3, 3, 0, 0), Height = S(34) };
            boxHost.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");
            boxHost.SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush");
            _win!.SearchBox = box;

            var close = new Border { Width = S(30), Height = S(30), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = "btn", ToolTip = T("dp_close"), Child = Text("✕", 13) };
            ((TextBlock)close.Child).HorizontalAlignment = HorizontalAlignment.Center;
            close.MouseEnter += (_, _) => close.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
            close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
            close.MouseLeftButtonUp += (_, _) => CloseToTab();
            Grid.SetColumn(boxHost, 0); Grid.SetColumn(close, 1);
            row.Children.Add(boxHost); row.Children.Add(close);
            bar.Child = row;
            MakeDraggable(bar, null);   // the popup is moved from the search bar (outside the field)
            return bar;
        }

        /// <summary>TextBox without the theme borders, background and focus colors: the containing frame provides the background and line</summary>
        private static ControlTemplate BareTextBoxTemplate()
        {
            var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            host.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            host.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            host.SetValue(Control.FocusVisualStyleProperty, null);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        }

        private FrameworkElement BuildListFooter()
        {
            var foot = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(0, 1, 0, 0) };
            foot.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");   // darker than the list (mock: footer #171717)
            foot.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            var btn = SoftButton(T("dp_new"), false, () => OpenForm(null), 28);
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Visibility = TeamsShared.CanEditDevices ? Visibility.Visible : Visibility.Collapsed;
            foot.Child = btn;
            if (!TeamsShared.CanEditDevices) foot.Padding = new Thickness(0);
            MakeDraggable(foot, null);
            return foot;
        }

        // ---- list
        private ScrollViewer? _listScroll;
        private StackPanel? _listPanel;

        private FrameworkElement BuildListBody()
        {
            _listPanel = new StackPanel();
            _listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _listPanel, Background = Brushes.Transparent };
            MakeDraggable(_listScroll, null);   // from the empty spaces too
            RefreshList();
            return _listScroll;
        }

        private void RefreshList()
        {
            if (_listPanel == null) return;
            _listPanel.Children.Clear();
            var all = DevicePasswordService.ForServer(_host.ProtocolServer.TreeNodes);
            var shown = all.Where(x => DevicePasswordService.Matches(x, _query)).ToList();
            if (shown.Count == 0)
            {
                var msg = Text(all.Count == 0 ? T("dp_empty") : string.Format(T("dp_none"), _query.Trim()), 12.5, "IslandSecondaryTextBrush");
                msg.TextWrapping = TextWrapping.Wrap; msg.Margin = new Thickness(14, 16, 14, 16);
                _listPanel.Children.Add(msg);
                return;
            }
            var one = shown.Count == 1 && _query.Trim().Length > 0;   // a single search result: the row is already hovered
            foreach (var e in shown) _listPanel.Children.Add(BuildRow(e, one));
        }

        private FrameworkElement BuildRow(DeviceEntry e, bool hoverNow)
        {
            // divider below the row; the accent bar on the left is a separate element (before, the accent border also covered the divider below)
            var rowBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(11, 6, 8, 6), Background = Brushes.Transparent };
            rowBorder.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // the name takes all the remaining space (pencil on the right, IP after) and is cut with "..." only if it really does not fit
            var nameRow = new DockPanel { LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Left };
            var name = Text(e.Name.Length > 0 ? e.Name : e.Ip, 13.5, weight: FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            if (TeamsShared.CanEditDevices)   // with the Teams server read-only (or "with approval") entries cannot be edited from here
            {
                // diagonal pencil, centered in the 15x15 square (x and y from 2 to 13); raised by 1 px: visually the bold text sits higher than the row center
                var pencil = IconButton("M 2.5,12.5 L 3.2,9.6 L 9.8,3 Q 10.5,2.3 11.2,3 L 12,3.8 Q 12.7,4.5 12,5.2 L 5.4,11.8 Z M 8.6,4.2 L 10.8,6.4", T("dp_edit"), () => OpenForm(e));
                pencil.Margin = new Thickness(6, 0, 0, 0);
                pencil.RenderTransform = new TranslateTransform(0, -1);
                DockPanel.SetDock(pencil, Dock.Right);
                nameRow.Children.Add(pencil);
            }
            nameRow.Children.Add(name);   // last: fills the remaining space
            // one box per address (a NAS with two network cards has two): each copies its own
            var ipsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var addr in e.AllIps())
            {
                var ip = Text(addr, 11.5);
                ip.FontFamily = new FontFamily("Consolas");
                ip.RenderTransform = new TranslateTransform(0, -2);   // the monospaced digits, smaller than the name, sat a couple of px below its baseline
                var ipBox = new Border { Padding = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Tag = "btn", Background = Brushes.Transparent, Child = ip };
                ipBox.MouseEnter += (_, _) => ipBox.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
                ipBox.MouseLeave += (_, _) => ipBox.Background = Brushes.Transparent;
                ipBox.MouseLeftButtonUp += (_, _) => CopyText(addr, string.Format(T("dp_copied_ip"), addr), sensitive: false, "ip", e.Name, "");
                ipsPanel.Children.Add(ipBox);
            }
            Grid.SetColumn(nameRow, 0); Grid.SetColumn(ipsPanel, 1);
            head.Children.Add(nameRow); head.Children.Add(ipsPanel);
            stack.Children.Add(head);

            var buttons = new List<UIElement>();
            foreach (var u in e.Users)
            {
                var line = new Grid { Height = S(24) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var un = Text(u.User, 12, "IslandSecondaryTextBrush"); un.TextTrimming = TextTrimming.CharacterEllipsis;
                var bs = new StackPanel { Orientation = Orientation.Horizontal, Visibility = hoverNow ? Visibility.Visible : Visibility.Collapsed };
                var user = u; var entry = e;
                bs.Children.Add(SoftButton(T("dp_user"), false, () => CopyText(user.User, string.Format(T("dp_copied_user"), entry.Name, user.User), sensitive: false, "user", entry.Name, user.User), 22));
                var pw = SoftButton(T("dp_pass"), true, () => CopyPassword(entry, user), 22);
                pw.Margin = new Thickness(6, 0, 0, 0);
                bs.Children.Add(pw);
                buttons.Add(bs);
                Grid.SetColumn(un, 0); Grid.SetColumn(bs, 1);
                line.Children.Add(un); line.Children.Add(bs);
                stack.Children.Add(line);
            }
            rowBorder.Child = stack;

            var bar = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentMidBrush");
            var cell = new Grid(); cell.Children.Add(rowBorder); cell.Children.Add(bar);

            void Hover(bool on)
            {
                // mock: row background + 3 px accent bar on the left, the divider below stays as is
                if (on) rowBorder.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
                else rowBorder.Background = Brushes.Transparent;
                bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                foreach (var b in buttons) b.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            rowBorder.MouseEnter += (_, _) => Hover(true);
            rowBorder.MouseLeave += (_, _) => Hover(false);
            if (hoverNow) Hover(true);
            return cell;
        }

        // ---- hand-made buttons (the normal button theme is too tall for 24 px rows)
        private static Border SoftButton(string text, bool primary, Action click, double height)
        {
            var label = Text(text, 12, primary ? "AccentTextBrush" : "PrimaryTextBrush", FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { Height = S(height), MinWidth = S(52), Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Tag = "btn", Child = label };
            if (primary) { b.SetResourceReference(Border.BackgroundProperty, "AccentMidBrush"); b.SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush"); }
            else { b.Background = Brushes.Transparent; b.SetResourceReference(Border.BorderBrushProperty, "IslandButtonBorderBrush"); }
            b.MouseEnter += (_, _) => b.Opacity = 0.85;
            b.MouseLeave += (_, _) => b.Opacity = 1;
            b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
            return b;
        }

        private static Border IconButton(string pathData, string tip, Action click)
        {
            var p = new Path { Data = Geometry.Parse(pathData), StrokeThickness = 1.2, Width = 15, Height = 15, Stretch = Stretch.None, StrokeLineJoin = PenLineJoin.Round };
            p.SetResourceReference(Shape.StrokeProperty, "PrimaryTextBrush");
            var b = new Border { Width = S(22), Height = S(22), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Tag = "btn", ToolTip = tip, Background = Brushes.Transparent, Child = p, VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
            return b;
        }

        // ---- edit and new entry
        private void OpenForm(DeviceEntry? entry)
        {
            _mode = Mode.Form; _editing = entry;
            Render(); Layout();
        }

        private void BackToList()
        {
            _mode = Mode.List; _editing = null;
            Render(); Layout();
            _win?.FocusSearch();
        }

        private FrameworkElement BuildForm()
        {
            var e = _editing?.Clone();
            string folder = e?.Folder ?? FolderPath;
            string prefix = DevicePasswordService.IpPrefix((_host.ProtocolServer as ProtocolBaseWithAddressPort)?.Address);

            var root = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
            root.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
            root.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var fields = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            var title = Text(e == null ? T("dp_new_title") : string.Format(T("dp_edit_title"), e.Name), 12, "AccentMidBrush", FontWeights.Bold);
            title.Margin = new Thickness(0, 0, 0, 8);
            fields.Children.Add(title);

            var nameBox = Field(fields, T("dp_display_name"), e?.Name ?? "", T("dp_name_hint"), false);
            var ipBox = Field(fields, T("dp_ip"), e != null ? string.Join(", ", e.AllIps()) : prefix, "", true);
            // entering the IP field moves the caret to the end (the suggested prefix, e.g. "192.0.2.", stays as is and you keep typing)
            ipBox.GotKeyboardFocus += (_, _) => ipBox.Dispatcher.BeginInvoke(new Action(() => { ipBox.SelectionLength = 0; ipBox.CaretIndex = ipBox.Text.Length; }), DispatcherPriority.Input);
            var known = KnownUserNames();
            var warn = Text("", 11.5, "FtWarnBrush"); warn.TextWrapping = TextWrapping.Wrap; warn.Margin = new Thickness(0, 4, 0, 0); warn.Visibility = Visibility.Collapsed;
            fields.Children.Add(warn);
            if (e == null)
            {
                void CheckSame()
                {
                    var typed = DevicePasswordService.SplitIps(ipBox.Text);
                    var same = typed.Count == 0 ? null : DevicePasswordService.All().FirstOrDefault(x => string.Equals(x.Folder, folder, StringComparison.CurrentCultureIgnoreCase) && x.AllIps().Intersect(typed, StringComparer.OrdinalIgnoreCase).Any());
                    warn.Text = same == null ? "" : string.Format(T("dp_exists"), same.Name);
                    warn.Visibility = same == null ? Visibility.Collapsed : Visibility.Visible;
                }
                ipBox.TextChanged += (_, _) => CheckSame();
            }

            var usersHost = new StackPanel();
            fields.Children.Add(usersHost);
            var rows = new List<(TextBox user, TextBox pass)>();
            void AddUserBlock(DeviceUser? u)
            {
                var n = rows.Count + 1;
                var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) };
                sep.SetResourceReference(Border.BackgroundProperty, "IslandDividerBrush");
                block.Children.Add(sep);
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = Text(string.Format(T("dp_user_n"), n), 11.5, "IslandSecondaryTextBrush");
                Grid.SetColumn(lbl, 0); head.Children.Add(lbl);
                block.Children.Add(head);
                var ub = Field(block, T("dp_username"), u?.User ?? "", "", false);   // no "Administrator" placeholder: the already saved user names are suggested
                AttachUserSuggest(ub, known);
                var pb = Field(block, T("dp_password"), u?.Password ?? "", "", true);
                var entry = (ub, pb);
                rows.Add(entry);
                // user already shared on the Teams server: read-only cannot remove it (it would be a deletion, the server rejects it)
                bool locked = u != null && e?.OnServer == true && !TeamsShared.CanDeleteDevices;
                var x = IconButton("M 3,3 L 12,12 M 12,3 L 3,12", T("dp_delete"), () =>
                {
                    if (rows.Count <= 1 || locked) return;   // a credential can be removed only if there is more than one
                    rows.Remove(entry); usersHost.Children.Remove(block); Renumber();
                });
                x.Visibility = Visibility.Collapsed;
                x.Tag = locked;
                Grid.SetColumn(x, 1); head.Children.Add(x);
                usersHost.Children.Add(block);
                Renumber();

                void Renumber()
                {
                    int i = 0;
                    foreach (var b in usersHost.Children.OfType<StackPanel>())
                    {
                        i++;
                        var h = b.Children.OfType<Grid>().First();
                        ((TextBlock)h.Children[0]).Text = usersHost.Children.Count == 1 ? T("dp_cred") : string.Format(T("dp_user_n"), i);   // only one: "Credential" (mock); more than one: "User 1", "User 2"
                        if (h.Children.Count > 1) h.Children[1].Visibility = usersHost.Children.Count > 1 && (h.Children[1] as FrameworkElement)?.Tag is not true ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
            foreach (var u in (e?.Users.Count > 0 ? e.Users : new List<DeviceUser> { new() })) AddUserBlock(u);

            // dashed accent button (mock): a rectangle with a dashed border behind the text
            var dash = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false };
            dash.SetResourceReference(Shape.StrokeProperty, "AccentMidBrush");
            var addLabel = Text(T("dp_add_user"), 12, "AccentMidBrush", FontWeights.SemiBold); addLabel.HorizontalAlignment = HorizontalAlignment.Center;
            var addInner = new Grid(); addInner.Children.Add(dash); addInner.Children.Add(addLabel);
            var add = new Border { Height = S(30), Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand, Tag = "btn", Background = Brushes.Transparent, Child = addInner };
            add.MouseEnter += (_, _) => add.Opacity = 0.8;
            add.MouseLeave += (_, _) => add.Opacity = 1;
            add.MouseLeftButtonUp += (_, ev) => { AddUserBlock(null); ev.Handled = true; };
            fields.Children.Add(add);

            if (e != null && TeamsShared.CanDeleteDevices)   // read-only creates and edits but does not delete
            {
                var del = Text(T("dp_delete"), 12, "FtErrBrush");
                var delBtn = new Border { Cursor = Cursors.Hand, Tag = "btn", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Child = del };
                delBtn.MouseLeftButtonUp += (_, ev) =>
                {
                    ev.Handled = true;
                    if (MessageBox.Show(_win, string.Format(T("dp_delete_ask"), e.Name), T("dp_delete"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                    var err = e.OnServer && TeamsShared.Linked ? TeamsShared.DeleteDevice(e.Id) : (DevicePasswordService.Delete(e.Id) ? null : T("dp_save_fail"));
                    if (err != null) { ShowToast(err, true); return; }
                    BackToList();
                };
                fields.Children.Add(delBtn);
            }

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = fields, Background = Brushes.Transparent };
            MakeDraggable(scroll, null);

            var foot = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(0, 1, 0, 0) };
            foot.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");
            foot.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(SoftButton(T("dp_cancel"), false, BackToList, 32));
            var save = SoftButton(T("dp_save"), true, () => SaveForm(e, folder, nameBox, ipBox, rows), 32);
            save.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(save);
            foot.Child = buttons;
            MakeDraggable(foot, null);

            Grid.SetRow(scroll, 0); Grid.SetRow(foot, 1);
            grid.Children.Add(scroll); grid.Children.Add(foot);
            root.Child = grid;
            root.ClipToBounds = true;
            PopupChrome(root);
            _win!.SearchBox = null;
            _win.KeyDownHandler = ev => { if (ev.Key == Key.Escape) { BackToList(); ev.Handled = true; } };
            return root;
        }

        /// <summary>all user names already saved in the program (shared passwords, folder credentials, connection users), without duplicates</summary>
        private static List<string> KnownUserNames()
        {
            var set = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            void Add(string? n)
            {
                n = (n ?? "").Trim();
                if (n.Length == 0) return;
                if (n.Contains('\\')) n = n.Substring(n.LastIndexOf('\\') + 1);   // "DOMAIN\user": only the user is suggested here
                if (n.Length == 0) return;
                set[n] = set.TryGetValue(n, out var c) ? c + 1 : 1;
            }
            try { foreach (var d in DevicePasswordService.All()) foreach (var u in d.Users) Add(u.User); } catch (Exception ex) { SimpleLogHelper.Debug("Password manager: known users: " + ex.Message); }
            try { foreach (var c in FolderCredentialService.Cached) Add(c.UserName); } catch { /* list not ready: suggest the others */ }
            try
            {
                foreach (var vm in IoC.Get<GlobalData>().VmItemList.ToList())
                    if (vm.Server is ProtocolBaseWithAddressPortUserPwd s) { Add(s.UserName); foreach (var u in s.Users) Add(u.UserName); }
            }
            catch { /* same */ }
            return set.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Length).ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Key).ToList();
        }

        /// <summary>
        /// Inline completion of the user name: typing "adm" shows, selected, the rest of the shortest name starting that way ("admin");
        /// Tab or Enter accept it, typing on replaces it, Backspace removes it.
        /// </summary>
        private static void AttachUserSuggest(TextBox box, List<string> known)
        {
            bool deleting = false, busy = false;
            box.PreviewKeyDown += (_, e) =>
            {
                deleting = e.Key is Key.Back or Key.Delete;
                if (e.Key is Key.Tab or Key.Enter && box.SelectionLength > 0 && box.SelectionStart + box.SelectionLength == box.Text.Length)
                {
                    box.CaretIndex = box.Text.Length; box.SelectionLength = 0;   // accept the completion (stays in the field)
                    e.Handled = true;
                }
            };
            box.TextChanged += (_, _) =>
            {
                if (busy || deleting) { deleting = false; return; }
                var typed = box.Text;
                if (typed.Length == 0 || box.CaretIndex != typed.Length) return;
                // most used first, then the shortest: "adm" -> "admin", "admini" -> "administrator"
                var match = known.Where(k => k.Length > typed.Length && k.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase))
                                 .OrderBy(k => k.Length).ThenBy(k => known.IndexOf(k)).FirstOrDefault();
                if (match == null) return;
                busy = true;
                try { box.Text = typed + match.Substring(typed.Length); box.Select(typed.Length, match.Length - typed.Length); }
                finally { busy = false; }
            };
        }

        /// <summary>form text field: dark background, grey line, accent line on focus (the field theme drew it taller and with the system blue)</summary>
        private static ControlTemplate FieldTemplate()
        {
            var bd = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            host.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            host.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            host.SetValue(Control.PaddingProperty, new Thickness(8, 0, 8, 0));
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(host);
            var t = new ControlTemplate(typeof(TextBox)) { VisualTree = bd };
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentMidBrush"), "bd"));
            t.Triggers.Add(focus);
            return t;
        }

        private static TextBox Field(Panel parent, string label, string value, string hint, bool mono)
        {
            var l = Text(label, 12, "IslandSecondaryTextBrush"); l.Margin = new Thickness(0, 6, 0, 3);
            parent.Children.Add(l);
            var box = new TextBox { Text = value, Height = S(32), VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            box.Template = FieldTemplate();
            FontPx.Apply(box, Control.FontSizeProperty, 13);
            if (mono) box.FontFamily = new FontFamily("Consolas");
            box.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            box.SetResourceReference(Control.BackgroundProperty, "PrimaryDarkBrush");
            box.SetResourceReference(Control.BorderBrushProperty, "IslandButtonBorderBrush");
            box.SetResourceReference(TextBoxBase.CaretBrushProperty, "PrimaryTextBrush");
            if (hint.Length == 0) { parent.Children.Add(box); return box; }
            var ph = Text(hint, 13, "IslandSecondaryTextBrush"); ph.IsHitTestVisible = false; ph.Margin = new Thickness(10, 0, 0, 0);
            ph.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (_, _) => ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var g = new Grid(); g.Children.Add(box); g.Children.Add(ph);
            parent.Children.Add(g);
            return box;
        }

        private void SaveForm(DeviceEntry? old, string folder, TextBox nameBox, TextBox ipBox, List<(TextBox user, TextBox pass)> rows)
        {
            var name = nameBox.Text.Trim();
            // several addresses separated by commas; the suggested prefix alone ("10.0.4.") is not an address
            var prefix = DevicePasswordService.IpPrefix((_host.ProtocolServer as ProtocolBaseWithAddressPort)?.Address);
            var ips = DevicePasswordService.SplitIps(ipBox.Text).Where(x => old != null || prefix.Length == 0 || x != prefix).ToList();
            if (name.Length == 0 && ips.Count == 0) { ShowToast(T("dp_need_name"), true); return; }
            var entry = old != null ? old.Clone() : new DeviceEntry { Folder = folder };
            entry.Name = name.Length > 0 ? name : ips[0];
            DevicePasswordService.SetIps(entry, ips);
            entry.Users = rows.Select(r => new DeviceUser { User = r.user.Text, Password = r.pass.Text }).ToList();
            var prepared = DevicePasswordService.Prepare(entry, old == null, out _);   // same IP in the same folder: the credentials are added to the existing device
            if (prepared == null) { ShowToast(T("dp_save_fail"), true); return; }
            DeviceEntry? saved = prepared;
            if (TeamsShared.Linked)
            {
                // with the Teams server the entry is written to the server first ("write" permission needed); an entry only on this PC is created on the server with the same id
                var err = TeamsShared.WriteDevice(prepared, create: !prepared.OnServer, out saved);
                if (err != null) { ShowToast(err, true); return; }
                if (saved == null) { BackToList(); return; }   // "requests": request sent (notice already shown), the entry changes after approval
            }
            else if (!DevicePasswordService.Save(prepared)) { ShowToast(T("dp_save_fail"), true); return; }
            ShowToast(string.Format(T("dp_saved"), saved.Name), false);
            BackToList();
        }

        // ---- dragging
        private void MakeDraggable(UIElement el, Action? onClick)
        {
            Point down = default; bool dragging = false, pressed = false;
            el.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsInteractive(e.OriginalSource as DependencyObject, el)) return;
                pressed = true; dragging = false; down = ScreenDip(e);
                el.CaptureMouse();
            };
            el.PreviewMouseMove += (_, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                var cur = ScreenDip(e);
                if (!dragging && (cur - down).Length >= 4) dragging = true;
                if (dragging) DragTo(cur);
            };
            el.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (!pressed) return;
                pressed = false; el.ReleaseMouseCapture();
                if (dragging) { DevicePasswordService.SetPlace(_place); e.Handled = true; }
                else if (onClick != null) { onClick(); e.Handled = true; }
            };
        }

        /// <summary>buttons, text fields and scroll bars do not drag</summary>
        private static bool IsInteractive(DependencyObject? d, UIElement stopAt)
        {
            while (d != null && !ReferenceEquals(d, stopAt))
            {
                if (d is TextBoxBase or ButtonBase or ScrollBar or Thumb || (d is FrameworkElement { Tag: "btn" })) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private Point ScreenDip(MouseEventArgs e)
        {
            var w = _win!;
            var dpi = VisualTreeHelper.GetDpi(w);
            var p = w.PointToScreen(e.GetPosition(w));
            return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
        }

        // ---- copy
        private void CopyPassword(DeviceEntry e, DeviceUser u)
        {
            try
            {
                // by design: copying a password from the manager NEVER asks for a password, fingerprint or other checks, for anyone
                CopyText(u.Password, string.Format(T("dp_copied_pass"), e.Name, u.User, DevicePasswordService.ClipboardClearSeconds), sensitive: true, "password", e.Name, u.User);
            }
            catch (Exception ex) { SimpleLogHelper.Warning("Gestore password: " + ex.Message); }
        }

        /// <summary>copies to the clipboard; with the Teams server the copy ends up in its log ("credential copied"), never with the password</summary>
        private void CopyText(string text, string toast, bool sensitive, string what, string device, string user)
        {
            if (SecretClipboard.Set(text, sensitive ? DevicePasswordService.ClipboardClearSeconds : 0)) { ShowToast(toast, false); TeamsShared.AuditCopy(what, device, user); }
            else ShowToast(T("dp_save_fail"), true);
        }

        private void ShowToast(string text, bool error)
        {
            if (_win == null) return;
            ToastWindow.Display(_win, HostRect(), text, error);
        }

        // ---- window of the handle / popup
        private sealed class DrawerWindow : Window
        {
            private readonly DevicePasswordDrawer _drawer;
            private bool _closedByCode;
            public TextBox? SearchBox { get; set; }
            public Action<KeyEventArgs>? KeyDownHandler { get; set; }

            public DrawerWindow(Window owner, DevicePasswordDrawer drawer)
            {
                _drawer = drawer;
                Owner = owner;
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = null;               // transparent pixels do not capture the mouse: the remote screen stays clickable
                ShowInTaskbar = false;
                ShowActivated = false;           // the handle does not steal focus from the remote screen
                ResizeMode = ResizeMode.NoResize;
                Topmost = owner.Topmost;
                ApplyPalette(this);
                Focusable = true;
                Closing += (_, e) => { if (!_closedByCode) e.Cancel = true; };
                PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape && KeyDownHandler != null) { KeyDownHandler(e); return; }
                    if (e.Key == Key.Escape) { _drawer.CloseToTab(); e.Handled = true; }
                    else if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { _drawer.CloseToTab(); e.Handled = true; }
                };
            }

            public void SetContent(FrameworkElement content) => Content = content;

            public void FocusSearch()
            {
                Activate();
                Dispatcher.BeginInvoke(new Action(() => { SearchBox?.Focus(); Keyboard.Focus(SearchBox); }), DispatcherPriority.Input);
            }

            public void CloseByCode()
            {
                _closedByCode = true;
                try { Close(); } catch { /* already closed */ }
            }
        }

        /// <summary>notice at the bottom center of the remote screen (the program's normal toast would stay beneath the native screen)</summary>
        private sealed class ToastWindow : Window
        {
            private static ToastWindow? _current;
            private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };

            public static void Display(Window owner, Rect host, string text, bool error)
            {
                _current?.CloseNow();
                var w = new ToastWindow(owner, host, text, error);
                _current = w;
                w.Show();
            }

            private ToastWindow(Window owner, Rect host, string text, bool error)
            {
                Owner = owner;
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null;
                ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Topmost = owner.Topmost;
                ApplyPalette(this);
                IsHitTestVisible = false;
                SizeToContent = SizeToContent.WidthAndHeight;
                var tb = Text(text, 13); tb.TextWrapping = TextWrapping.Wrap; tb.MaxWidth = Math.Max(200, Math.Min(620, host.Width - 60));
                var bar = new Border { Width = 3 };
                bar.SetResourceReference(Border.BackgroundProperty, error ? "FtErrBrush" : "FtOkBrush");
                var row = new DockPanel();
                DockPanel.SetDock(bar, Dock.Left);
                row.Children.Add(bar);
                tb.Margin = new Thickness(14, 10, 16, 10);
                row.Children.Add(tb);
                var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = row };
                card.SetResourceReference(Border.BackgroundProperty, "PrimaryLightBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
                Content = card;
                SourceInitialized += (_, _) => { };
                ContentRendered += (_, _) =>
                {
                    Left = host.Left + (host.Width - ActualWidth) / 2;
                    Top = host.Bottom - ActualHeight - 28;
                };
                _timer.Tick += (_, _) => CloseNow();
                _timer.Start();
            }

            private void CloseNow()
            {
                _timer.Stop();
                if (_current == this) _current = null;
                try { Close(); } catch { /* already closed */ }
            }
        }
    }

    /// <summary>
    /// Clipboard for secrets: the password is cleared from the clipboard after N seconds (only if it is still there) and does not end up
    /// in the Windows clipboard history or in the cloud-synced clipboard.
    /// </summary>
    internal static class SecretClipboard
    {
        private static DispatcherTimer? _timer;
        private static string? _pending;

        /// <summary>
        /// Windows markers: this content does not go into the clipboard history (Win+V), nor the cloud clipboard, nor to programs that monitor it.
        /// <paramref name="monitors"/>=false drops the last marker: "ExcludeClipboardContentFromMonitorProcessing" asks clipboard listeners to ignore
        /// the content, and FreeRDP listens to the clipboard to pass it to the remote PC (Claude via RDP). For Win+V and cloud the other two are enough
        /// </summary>
        public static DataObject NoHistory(DataObject data, bool monitors = true)
        {
            foreach (var fmt in monitors
                         ? new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" }
                         : new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                data.SetData(fmt, new System.IO.MemoryStream(new byte[4]));
            return data;
        }

        public static bool Set(string text, int clearAfterSeconds)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text);
                if (clearAfterSeconds > 0) NoHistory(data);
                for (var i = 0; ; i++)
                {
                    try { Clipboard.SetDataObject(data, true); break; }
                    catch (System.Runtime.InteropServices.COMException) when (i < 5) { System.Threading.Thread.Sleep(40); }   // the clipboard is busy with another program
                }
            }
            catch (Exception ex) { SimpleLogHelper.Warning("Appunti: " + ex.Message); return false; }

            _timer?.Stop();
            _pending = null;
            if (clearAfterSeconds > 0)
            {
                _pending = text;
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clearAfterSeconds) };
                _timer.Tick += (_, _) => ClearIfStillThere();
                _timer.Start();
                if (Application.Current != null) { Application.Current.Exit -= OnExit; Application.Current.Exit += OnExit; }
            }
            return true;
        }

        private static void OnExit(object sender, ExitEventArgs e) => ClearIfStillThere();

        private static void ClearIfStillThere()
        {
            _timer?.Stop();
            var pending = _pending; _pending = null;
            if (pending == null) return;
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == pending) Clipboard.Clear(); }
            catch { /* clipboard busy: on the next round there is nothing left to clear */ }
        }
    }
}
