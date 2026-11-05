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
    /// Gestore password della cartella: linguetta sul bordo dello schermo di una sessione RDP o VNC salvata, che apre un popup con le password
    /// dei dispositivi della stessa cartella (NAS, switch, DVR...) da copiare al volo.
    /// Lo schermo remoto e' una finestra nativa e copre qualsiasi elemento WPF della stessa finestra: linguetta e popup stanno quindi in una finestra
    /// propria, piccola, trasparente, "posseduta" dalla finestra della sessione e sempre posizionata sul bordo dello schermo remoto.
    /// Mock di Claude Design: blueprint/password/GESTORE-PASSWORD-BLUEPRINT.md.
    /// </summary>
    internal sealed class DevicePasswordDrawer
    {
        private static readonly Dictionary<HostBase, DevicePasswordDrawer> Map = new();

        private const double TabShort = 34, TabLong = 124, PopupWidth = 290, PopupMaxHeight = 430, EdgeGap = 6, KeepIn = 4, ShadowPad = 16;

        // ---- uso da fuori
        /// <summary>chiamata quando la sessione e' nella finestra: crea (una sola volta) il gestore se la sessione ne ha diritto</summary>
        public static void Attach(HostBase host)
        {
            if (!DevicePasswordService.Enabled || Map.ContainsKey(host) || Closed.TryGetValue(host, out _) || !IsEligible(host)) return;
            Map[host] = new DevicePasswordDrawer(host);
        }

        /// <summary>sessioni gia' chiuse: chiudendo la scheda l'host puo' ricevere ancora un Loaded, e non deve far rinascere la linguetta</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HostBase, object> Closed = new();

        /// <summary>la sessione si chiude: via linguetta e popup</summary>
        public static void Detach(HostBase host)
        {
            Closed.AddOrUpdate(host, new object());
            if (Map.Remove(host, out var d)) d.Dispose();
        }

        /// <summary>Ctrl+Alt+P: apre o chiude il popup della sessione. false = questa sessione non ha il gestore.</summary>
        public static bool Toggle(HostBase? host)
        {
            if (host == null || !Map.TryGetValue(host, out var d)) return false;
            d.ToggleOpen();
            return true;
        }

        // ---- solo per le prove in background (LEMORA_DEMO=devpw)
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
                case "drag":   // trascinamento fino a un punto dello schermo (x,y in unita' del programma): la linguetta deve restare lo stesso elemento anche cambiando bordo
                {
                    var before = d._win.Content; var oldEdge = d._place.Edge;
                    var xy = arg.Split(',');
                    d.DragTo(new Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture)));
                    DemoLast = $"trascinata {oldEdge} -> {d._place.Edge} (t={d._place.T:0.00}), stesso elemento={ReferenceEquals(before, d._win.Content)}";
                    break;
                }
            }
        }

        /// <summary>solo connessioni SALVATE di tipo RDP e VNC (le connessioni rapide non hanno una cartella)</summary>
        private static bool IsEligible(HostBase host) => host is AxMsRdpClient09Host or VncHost;   // 05/11: Ivan la vuole sempre visibile, anche nelle connessioni rapide (senza cartella valgono solo le voci fuori da ogni cartella)

        // ---- stato
        private readonly HostBase _host;
        private readonly DispatcherTimer _timer;
        private DrawerWindow? _win;
        private Window? _owner;
        private int _unloadedTicks;
        private DrawerPlace _place = DevicePasswordService.GetPlace();
        private enum Mode { Tab, List, Form }
        private Mode _mode = Mode.Tab;
        private string _query = "";
        private DeviceEntry? _editing;     // null in Form = voce nuova

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
        /// Linguetta e popup seguono la finestra subito: mentre la si trascina il timer da solo arrivava con circa un secondo di ritardo
        /// (il ciclo di spostamento di Windows lascia poco spazio ai timer). Gli eventi di posizione e dimensione arrivano a ogni passo.
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
            catch (Exception ex) { SimpleLogHelper.Debug("Gestore password: riposizionamento non riuscito: " + ex.Message); }
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

        // ---- posizione e visibilita' (sempre agganciata allo schermo remoto)
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
                // sessione chiusa senza passare da Close() (la scheda sparisce e l'host non e' piu' nella finestra): dopo ~6 s la linguetta se ne va; se l'host torna (scheda cambiata) riparte da sola
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

        /// <summary>mette la finestra al suo posto sul bordo: linguetta se chiuso, popup (centrato sulla linguetta ma dentro lo schermo) se aperto</summary>
        private void Layout()
        {
            if (_win == null) return;
            var r = HostRect();
            if (r.Width < 10 || r.Height < 10) return;
            bool vertical = _place.Edge is "left" or "right";
            double along = vertical
                ? r.Top + TabLong / 2 + _place.T * Math.Max(0, r.Height - TabLong)
                : r.Left + TabLong / 2 + _place.T * Math.Max(0, r.Width - TabLong);
            double w, h, x, y;
            if (_mode == Mode.Tab)
            {
                w = vertical ? TabShort : TabLong; h = vertical ? TabLong : TabShort;
                x = _place.Edge switch { "left" => r.Left, "right" => r.Right - TabShort, _ => along - TabLong / 2 };
                y = _place.Edge switch { "top" => r.Top, "bottom" => r.Bottom - TabShort, _ => along - TabLong / 2 };
            }
            else
            {
                w = Math.Min(PopupWidth, r.Width - 2 * KeepIn);
                h = Math.Min(PopupMaxHeight, r.Height - 2 * KeepIn);
                x = _place.Edge switch { "left" => r.Left + EdgeGap, "right" => r.Right - w - EdgeGap, _ => along - w / 2 };
                y = _place.Edge switch { "top" => r.Top + EdgeGap, "bottom" => r.Bottom - h - EdgeGap, _ => along - h / 2 };
                x = Math.Clamp(x, r.Left + KeepIn, Math.Max(r.Left + KeepIn, r.Right - w - KeepIn));
                y = Math.Clamp(y, r.Top + KeepIn, Math.Max(r.Top + KeepIn, r.Bottom - h - KeepIn));
            }
            // il popup ha attorno un margine trasparente per l'ombra (come nel mock): la finestra e' piu' grande del popup, il popup resta dov'e' calcolato
            double pad = _mode == Mode.Tab ? 0 : ShadowPad;
            x -= pad; y -= pad; w += 2 * pad; h += 2 * pad;
            if (Math.Abs(_win.Left - x) > 0.5) _win.Left = x;
            if (Math.Abs(_win.Top - y) > 0.5) _win.Top = y;
            if (Math.Abs(_win.Width - w) > 0.5) _win.Width = w;
            if (Math.Abs(_win.Height - h) > 0.5) _win.Height = h;
        }

        /// <summary>
        /// Tema scuro: i colori del mock (popup #1e1e1e, barra e linguetta #262626, campi e piede #171717, riga in hover #2a2a2a, filetti #2e2e2e, bordi #4a4a4a, testo secondario #c0c0c0)
        /// al posto di quelli del tema, che davano grigi azzurrati. Si mettono come risorse della finestra: vincono su quelle dell'app solo qui.
        /// Con un tema chiaro restano i colori del tema (il mock dice "stessi ruoli dello STYLE-BLUEPRINT").
        /// </summary>
        private static void ApplyPalette(Window w)
        {
            if (Application.Current?.TryFindResource("PrimaryMidBrush") is not SolidColorBrush mid) return;
            var c = mid.Color;
            if ((0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5) return;   // tema chiaro
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

        /// <summary>cornice, margine e ombra del popup (0 8px 24px nero al 50% come nel mock)</summary>
        private static void PopupChrome(Border root)
        {
            root.Margin = new Thickness(ShadowPad);
            root.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        }

        /// <summary>trascinamento: la linguetta si aggancia al bordo piu' vicino e scorre lungo di esso, anche intorno agli angoli</summary>
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
            double span = Math.Max(1, (vertical ? r.Height : r.Width) - TabLong);
            double t = vertical ? (screenDip.Y - r.Top - TabLong / 2) / span : (screenDip.X - r.Left - TabLong / 2) / span;
            var oldEdge = _place.Edge;
            _place = new DrawerPlace { Edge = edge, T = Math.Clamp(t, 0, 1) };
            if (oldEdge != edge && _mode == Mode.Tab && _win?.Content is Border tab) FillTab(tab);   // la linguetta cambia verso (verticale/orizzontale) restando lo stesso elemento
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
            _host.FocusOnMe();   // il computer remoto riprende i tasti
        }

        // ---- disegno
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

        private static void Font(TextBlock tb, double size, FontWeight? weight = null)
        {
            tb.FontSize = size;   // misure fisse come nel mock: con la dimensione dei testi del programma il popup (largo 290) andrebbe fuori proporzione
            if (weight != null) tb.FontWeight = weight.Value;
        }

        private static TextBlock Text(string text, double size, string brush = "PrimaryTextBrush", FontWeight? weight = null)
        {
            var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            Font(tb, size, weight);
            tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return tb;
        }

        /// <summary>chiave disegnata a mano (nessun font di simboli da cui dipendere)</summary>
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
            tab.SetResourceReference(Border.BackgroundProperty, "PrimaryLightBrush");   // mock: linguetta #262626 con bordo #4a4a4a
            tab.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
            FillTab(tab);
            MakeDraggable(tab, Open);   // clic senza spostarsi (meno di 4 px) = apre
            return tab;
        }

        /// <summary>
        /// Contenuto e angoli della linguetta per il bordo attuale (verticale a sinistra/destra, orizzontale sopra/sotto).
        /// Si aggiorna lo STESSO elemento quando il bordo cambia durante il trascinamento: sostituirlo faceva perdere al mouse la presa (la linguetta restava indietro).
        /// </summary>
        private void FillTab(Border tab)
        {
            bool vertical = _place.Edge is "left" or "right";
            var label = Text(T("dp_tab"), 12.5, weight: FontWeights.SemiBold);
            var panel = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = KeyIcon();
            if (vertical) { icon.Margin = new Thickness(0, 0, 0, 6); label.LayoutTransform = new RotateTransform(90); }   // stesso verso da entrambi i lati, mai specchiato
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
            root.ClipToBounds = true;   // gli angoli arrotondati ritagliano barra e piede
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

            // campo piu' scuro della barra, con il filo accent di 2 px sotto (mock: barra #262626, campo #171717); il TextBox e' senza bordo e fondo propri per non farsi cambiare l'aspetto dal tema dei campi
            var box = new TextBox { Text = _query, BorderThickness = new Thickness(0), Padding = new Thickness(26, 6, 6, 6), VerticalContentAlignment = VerticalAlignment.Center, Height = 32, Background = Brushes.Transparent };
            box.Template = BareTextBoxTemplate();
            box.FontSize = 12.5;
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
            var boxHost = new Border { Child = boxInner, BorderThickness = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(3, 3, 0, 0), Height = 34 };
            boxHost.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");
            boxHost.SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush");
            _win!.SearchBox = box;

            var close = new Border { Width = 30, Height = 30, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = "btn", ToolTip = T("dp_close"), Child = Text("✕", 13) };
            ((TextBlock)close.Child).HorizontalAlignment = HorizontalAlignment.Center;
            close.MouseEnter += (_, _) => close.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
            close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
            close.MouseLeftButtonUp += (_, _) => CloseToTab();
            Grid.SetColumn(boxHost, 0); Grid.SetColumn(close, 1);
            row.Children.Add(boxHost); row.Children.Add(close);
            bar.Child = row;
            MakeDraggable(bar, null);   // si sposta il popup dalla barra di ricerca (fuori dal campo)
            return bar;
        }

        /// <summary>TextBox senza i bordi, il fondo e i colori di focus del tema: il fondo e il filo li mette la cornice che lo contiene</summary>
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
            foot.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");   // piu' scuro dell'elenco (mock: piede #171717)
            foot.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            var btn = SoftButton(T("dp_new"), false, () => OpenForm(null), 28);
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Visibility = TeamsShared.CanEditDevices ? Visibility.Visible : Visibility.Collapsed;
            foot.Child = btn;
            if (!TeamsShared.CanEditDevices) foot.Padding = new Thickness(0);
            MakeDraggable(foot, null);
            return foot;
        }

        // ---- elenco
        private ScrollViewer? _listScroll;
        private StackPanel? _listPanel;

        private FrameworkElement BuildListBody()
        {
            _listPanel = new StackPanel();
            _listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _listPanel, Background = Brushes.Transparent };
            MakeDraggable(_listScroll, null);   // anche dagli spazi vuoti
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
            var one = shown.Count == 1 && _query.Trim().Length > 0;   // un solo risultato di una ricerca: la riga e' gia' in hover
            foreach (var e in shown) _listPanel.Children.Add(BuildRow(e, one));
        }

        private FrameworkElement BuildRow(DeviceEntry e, bool hoverNow)
        {
            // filetto sotto la riga; la barra accent a sinistra e' un elemento a parte (prima il bordo accent copriva anche il filetto sotto)
            var rowBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(11, 6, 8, 6), Background = Brushes.Transparent };
            rowBorder.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // il nome prende tutto lo spazio che resta (matita a destra, IP dopo) e si taglia con "..." solo se non basta davvero
            var nameRow = new DockPanel { LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Left };
            var name = Text(e.Name.Length > 0 ? e.Name : e.Ip, 13.5, weight: FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            if (TeamsShared.CanEditDevices)   // col server Teams in sola lettura (o "con approvazione") le voci non si modificano da qui
            {
                // matita in diagonale, centrata nel quadrato 15x15 (x e y da 2 a 13); alzata di 1 px: a occhio il testo in grassetto sta piu' in alto del centro della riga
                var pencil = IconButton("M 2.5,12.5 L 3.2,9.6 L 9.8,3 Q 10.5,2.3 11.2,3 L 12,3.8 Q 12.7,4.5 12,5.2 L 5.4,11.8 Z M 8.6,4.2 L 10.8,6.4", T("dp_edit"), () => OpenForm(e));
                pencil.Margin = new Thickness(6, 0, 0, 0);
                pencil.RenderTransform = new TranslateTransform(0, -1);
                DockPanel.SetDock(pencil, Dock.Right);
                nameRow.Children.Add(pencil);
            }
            nameRow.Children.Add(name);   // ultimo: riempie lo spazio rimasto
            var ip = Text(e.Ip, 11.5);
            ip.FontFamily = new FontFamily("Consolas");
            ip.RenderTransform = new TranslateTransform(0, -2);   // le cifre a spaziatura fissa, piu' piccole del nome, restavano un paio di px sotto la sua linea di base
            var ipBox = new Border { Padding = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Tag = "btn", Background = Brushes.Transparent, Child = ip, Visibility = e.Ip.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
            ipBox.MouseEnter += (_, _) => ipBox.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
            ipBox.MouseLeave += (_, _) => ipBox.Background = Brushes.Transparent;
            ipBox.MouseLeftButtonUp += (_, _) => CopyText(e.Ip, string.Format(T("dp_copied_ip"), e.Ip), sensitive: false, "ip", e.Name, "");
            Grid.SetColumn(nameRow, 0); Grid.SetColumn(ipBox, 1);
            head.Children.Add(nameRow); head.Children.Add(ipBox);
            stack.Children.Add(head);

            var buttons = new List<UIElement>();
            foreach (var u in e.Users)
            {
                var line = new Grid { Height = 24 };
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
                // mock: sfondo della riga + barra accent di 3 px a sinistra, il filetto sotto resta com'e'
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

        // ---- pulsanti fatti a mano (il tema dei pulsanti normali e' troppo alto per righe da 24 px)
        private static Border SoftButton(string text, bool primary, Action click, double height)
        {
            var label = Text(text, 12, primary ? "AccentTextBrush" : "PrimaryTextBrush", FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { Height = height, MinWidth = 52, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Tag = "btn", Child = label };
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
            var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand, Tag = "btn", ToolTip = tip, Background = Brushes.Transparent, Child = p, VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "IslandHoverBrush");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
            return b;
        }

        // ---- modifica e nuova voce
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
            var ipBox = Field(fields, T("dp_ip"), e?.Ip ?? prefix, "", true);
            var warn = Text("", 11.5, "FtWarnBrush"); warn.TextWrapping = TextWrapping.Wrap; warn.Margin = new Thickness(0, 4, 0, 0); warn.Visibility = Visibility.Collapsed;
            fields.Children.Add(warn);
            if (e == null)
            {
                void CheckSame()
                {
                    var ipText = ipBox.Text.Trim();
                    var same = ipText.Length == 0 ? null : DevicePasswordService.All().FirstOrDefault(x => string.Equals(x.Folder, folder, StringComparison.CurrentCultureIgnoreCase) && string.Equals(x.Ip, ipText, StringComparison.OrdinalIgnoreCase));
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
                var ub = Field(block, T("dp_username"), u?.User ?? "", T("dp_username_hint"), false);
                var pb = Field(block, T("dp_password"), u?.Password ?? "", "", true);
                var entry = (ub, pb);
                rows.Add(entry);
                var x = IconButton("M 3,3 L 12,12 M 12,3 L 3,12", T("dp_delete"), () =>
                {
                    if (rows.Count <= 1) return;   // si puo' togliere una credenziale solo se ce n'e' piu' di una
                    rows.Remove(entry); usersHost.Children.Remove(block); Renumber();
                });
                x.Visibility = Visibility.Collapsed;
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
                        ((TextBlock)h.Children[0]).Text = usersHost.Children.Count == 1 ? T("dp_cred") : string.Format(T("dp_user_n"), i);   // una sola: "Credenziale" (mock); piu' di una: "Utente 1", "Utente 2"
                        if (h.Children.Count > 1) h.Children[1].Visibility = usersHost.Children.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
            foreach (var u in (e?.Users.Count > 0 ? e.Users : new List<DeviceUser> { new() })) AddUserBlock(u);

            // pulsante tratteggiato accent (mock): un rettangolo con il bordo a trattini dietro al testo
            var dash = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false };
            dash.SetResourceReference(Shape.StrokeProperty, "AccentMidBrush");
            var addLabel = Text(T("dp_add_user"), 12, "AccentMidBrush", FontWeights.SemiBold); addLabel.HorizontalAlignment = HorizontalAlignment.Center;
            var addInner = new Grid(); addInner.Children.Add(dash); addInner.Children.Add(addLabel);
            var add = new Border { Height = 30, Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand, Tag = "btn", Background = Brushes.Transparent, Child = addInner };
            add.MouseEnter += (_, _) => add.Opacity = 0.8;
            add.MouseLeave += (_, _) => add.Opacity = 1;
            add.MouseLeftButtonUp += (_, ev) => { AddUserBlock(null); ev.Handled = true; };
            fields.Children.Add(add);

            if (e != null && TeamsShared.CanDeleteDevices)   // la sola lettura crea e modifica ma non elimina
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

        /// <summary>campo di testo del modulo: fondo scuro, filo grigio, filo accent col fuoco (il tema dei campi lo disegnava piu' alto e con il blu di sistema)</summary>
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
            var box = new TextBox { Text = value, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            box.Template = FieldTemplate();
            box.FontSize = 13;
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
            var name = nameBox.Text.Trim(); var ip = ipBox.Text.Trim();
            // il prefisso proposto da solo ("10.0.4.") non e' un indirizzo
            if (old == null && ip == DevicePasswordService.IpPrefix((_host.ProtocolServer as ProtocolBaseWithAddressPort)?.Address)) ip = "";
            if (name.Length == 0 && ip.Length == 0) { ShowToast(T("dp_need_name"), true); return; }
            var entry = old != null ? old.Clone() : new DeviceEntry { Folder = folder };
            entry.Name = name.Length > 0 ? name : ip;
            entry.Ip = ip;
            entry.Users = rows.Select(r => new DeviceUser { User = r.user.Text, Password = r.pass.Text }).ToList();
            var prepared = DevicePasswordService.Prepare(entry, old == null, out _);   // stesso IP nella stessa cartella: le credenziali si aggiungono al dispositivo che c'e'
            if (prepared == null) { ShowToast(T("dp_save_fail"), true); return; }
            DeviceEntry? saved = prepared;
            if (TeamsShared.Linked)
            {
                // col server Teams la voce si scrive prima sul server (serve il permesso "scrittura"); una voce solo di questo PC si crea sul server con lo stesso id
                var err = TeamsShared.WriteDevice(prepared, create: !prepared.OnServer, out saved);
                if (err != null || saved == null) { ShowToast(err ?? T("dp_save_fail"), true); return; }
            }
            else if (!DevicePasswordService.Save(prepared)) { ShowToast(T("dp_save_fail"), true); return; }
            ShowToast(string.Format(T("dp_saved"), saved.Name), false);
            BackToList();
        }

        // ---- trascinamento
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

        /// <summary>pulsanti, campi di testo e barre di scorrimento non trascinano</summary>
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

        // ---- copia
        private void CopyPassword(DeviceEntry e, DeviceUser u)
        {
            try
            {
                // decisione di Ivan (05/11): copiare una password dal gestore non chiede MAI password, impronta o altre verifiche, per nessuno
                CopyText(u.Password, string.Format(T("dp_copied_pass"), e.Name, u.User, DevicePasswordService.ClipboardClearSeconds), sensitive: true, "password", e.Name, u.User);
            }
            catch (Exception ex) { SimpleLogHelper.Warning("Gestore password: " + ex.Message); }
        }

        /// <summary>copia negli appunti; col server Teams la copia finisce nel suo registro ("credenziale copiata"), mai con la password</summary>
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

        // ---- finestra della linguetta / del popup
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
                Background = null;               // i pixel trasparenti non catturano il mouse: lo schermo remoto resta cliccabile
                ShowInTaskbar = false;
                ShowActivated = false;           // la linguetta non ruba il focus allo schermo remoto
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
                try { Close(); } catch { /* gia' chiusa */ }
            }
        }

        /// <summary>avviso in basso al centro dello schermo remoto (il toast normale del programma resterebbe sotto lo schermo nativo)</summary>
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
                try { Close(); } catch { /* gia' chiusa */ }
            }
        }
    }

    /// <summary>
    /// Appunti per i segreti: la password si cancella dagli appunti dopo N secondi (solo se negli appunti c'e' ancora lei) e non finisce
    /// nella cronologia degli appunti di Windows ne' negli appunti sincronizzati col cloud.
    /// </summary>
    internal static class SecretClipboard
    {
        private static DispatcherTimer? _timer;
        private static string? _pending;

        public static bool Set(string text, int clearAfterSeconds)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text);
                if (clearAfterSeconds > 0)
                {
                    foreach (var fmt in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                        data.SetData(fmt, new System.IO.MemoryStream(new byte[4]));
                }
                for (var i = 0; ; i++)
                {
                    try { Clipboard.SetDataObject(data, true); break; }
                    catch (System.Runtime.InteropServices.COMException) when (i < 5) { System.Threading.Thread.Sleep(40); }   // gli appunti sono occupati da un altro programma
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
            catch { /* appunti occupati: al prossimo giro non c'e' piu' nulla da cancellare */ }
        }
    }
}
