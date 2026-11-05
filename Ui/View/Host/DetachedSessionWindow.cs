using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils.Wpf;
using Shawn.Utils.WpfResources.Theme.Styles;
using Stylet;

namespace Lemora.View.Host
{
    /// <summary>
    /// Sessione staccata dalla finestra principale: finestra Windows normale oppure Picture in picture
    /// (piccola, sempre sopra, senza barra del titolo). Le opzioni stanno su una barra a scomparsa sul bordo alto.
    /// La barra vive in una finestra trasparente separata sopra lo schermo remoto: il client VNC/RDP e' una finestra nativa
    /// e un normale elemento WPF finirebbe sotto di lei; cosi' lo schermo non cambia mai dimensione.
    /// </summary>
    public class DetachedSessionWindow : WindowBase
    {
        public HostBase Host { get; }
        public bool IsPip { get; }

        private readonly Grid _content = new Grid();
        private readonly OverlayWindow? _overlay;   // assente nel Picture in picture: li' la barra e' fissa sopra lo schermo
        private const double PIP_BAR_HEIGHT = 26;
        private readonly DispatcherTimer _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private DateTime _leftAt = DateTime.MinValue;
        private bool _closedByCode;
        private readonly Action _onCloseSession;

        private const double OPEN_ZONE = 56;   // il mouse apre la barra entro questa distanza dal bordo alto
        private const double KEEP_ZONE = 110;  // la barra resta aperta finche' il mouse e' entro questa distanza
        private const double CLOSE_DELAY_SEC = 1.2;
        private const double BAR_HEIGHT = 40;

        public DetachedSessionWindow(HostBase host, bool pip, Action onReattach, Action<bool> onSwitchMode, Action onCloseSession)
        {
            Host = host;
            IsPip = pip;
            _onCloseSession = onCloseSession;
            Title = host.ProtocolServer.DisplayName + " - " + Assert.APP_DISPLAY_NAME;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
            UseLayoutRounding = true;

            if (pip)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResize;
                Topmost = true;
                ShowInTaskbar = false;
                Width = 380; Height = 238; MinWidth = 320; MinHeight = 170;
                // il client VNC/RDP e' una finestra nativa e copre tutto cio' che sta sotto: per poter ridimensionare serve una cornice
                // libera attorno allo schermo (6 px di presa), con la linea accent da 1 px sul suo bordo interno
                WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
            }
            else
            {
                Width = 1100; Height = 700; MinWidth = 480; MinHeight = 320;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;

            // lo schermo remoto occupa sempre tutta la finestra
            var root = new Grid();
            _content.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
            if (pip)
            {
                var frame = new Border { Margin = new Thickness(5), BorderThickness = new Thickness(1), Child = _content };
                frame.SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush");
                root.Children.Add(frame);
            }
            else
            {
                root.Children.Add(_content);
            }
            _content.Children.Add(host);
            Content = root;

            var bar = BuildBar(onReattach, onSwitchMode);
            if (pip)
            {
                // barra sottile e fissa sopra lo schermo remoto (non lo copre): niente barra che compare da sola quando si va in alto con il mouse
                _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(host, 1);
                bar.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(bar, 0);
                _content.Children.Add(bar);
            }
            else
            {
                _overlay = new OverlayWindow(this, BuildHandle(), bar);
            }

            Closing += (s, e) =>
            {
                if (_closedByCode) return;
                e.Cancel = true;            // chiudere la finestra chiude la sessione, con la stessa conferma della scheda
                _onCloseSession();
            };
            if (_overlay != null)
            {
                Loaded += (s, e) => { _overlay.Owner = this; _overlay.Show(); _overlay.Reposition(); _hoverTimer.Start(); };   // l'Owner si imposta solo a finestra gia' mostrata
                LocationChanged += (s, e) => _overlay.Reposition();
                SizeChanged += (s, e) => _overlay.Reposition();
                StateChanged += (s, e) => { if (WindowState != WindowState.Minimized) _overlay.Reposition(); };
                Closed += (s, e) => { _hoverTimer.Stop(); _overlay.CloseByCode(); };
                _hoverTimer.Tick += (s, e) => HoverTick();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (IsPip && System.Windows.PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
                src.AddHook(SizingHook);
        }

        // Nel Picture in picture lo schermo remoto si riduce per stare tutto nella finestra: se la finestra cambiasse proporzioni
        // l'immagine si schiaccerebbe. Durante il ridimensionamento si tengono le proporzioni dello schermo remoto (WM_SIZING).
        private IntPtr SizingHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SIZING = 0x0214;
            if (msg != WM_SIZING) return IntPtr.Zero;
            var remote = Host.RemoteDesktopSize;
            if (remote.IsEmpty || remote.Width < 1 || remote.Height < 1) return IntPtr.Zero;
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            var frame = 12 * scale;   // cornice: 5 px di presa + 1 px di linea, per lato
            var frameV = frame + PIP_BAR_HEIGHT * scale;   // in verticale c'e' anche la barra sottile sopra lo schermo
            var rc = System.Runtime.InteropServices.Marshal.PtrToStructure<SizingRect>(lParam);
            var aspect = remote.Width / remote.Height;
            var edge = wParam.ToInt32();   // 1 sinistra, 2 destra, 3 sopra, 6 sotto, 4/5 angoli in alto, 7/8 angoli in basso
            var cw = Math.Max(60, rc.Right - rc.Left - frame);
            var ch = Math.Max(40, rc.Bottom - rc.Top - frameV);
            if (edge is 3 or 6)
                cw = ch * aspect;            // si e' mosso il bordo alto o basso: la larghezza segue
            else
                ch = cw / aspect;            // altrimenti l'altezza segue la larghezza
            var newW = (int)Math.Round(cw + frame);
            var newH = (int)Math.Round(ch + frameV);
            // il lato fermo resta dov'e': si muove quello opposto a quello trascinato
            if (edge is 1 or 4 or 7) rc.Left = rc.Right - newW; else rc.Right = rc.Left + newW;
            if (edge is 3 or 4 or 5) rc.Top = rc.Bottom - newH; else rc.Bottom = rc.Top + newH;
            System.Runtime.InteropServices.Marshal.StructureToPtr(rc, lParam, false);
            handled = true;
            return new IntPtr(1);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SizingRect { public int Left, Top, Right, Bottom; }

        /// <summary>apre la barra e restituisce la sua finestra (serve alla mappatura dell'interfaccia: screenshot e misure)</summary>
        public Window? OpenBarForMapping()
        {
            _overlay?.SetOpen(true);
            return _overlay;
        }

        /// <summary>chiude la finestra senza chiudere la sessione (il client resta vivo e va riposizionato)</summary>
        public void ReleaseHost()
        {
            _hoverTimer.Stop();
            _content.Children.Remove(Host);
            _closedByCode = true;
            _overlay?.CloseByCode();
            Close();
        }

        /// <summary>origine e larghezza dell'area client, in unita' del programma (DIP)</summary>
        private Rect ClientRect()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var p = _content.PointToScreen(new Point(0, 0));
            return new Rect(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY, _content.ActualWidth, _content.ActualHeight);
        }

        private void HoverTick()
        {
            if (_overlay == null) return;
            try
            {
                if (WindowState == WindowState.Minimized) { _overlay.SetOpen(false); return; }
                var screenPos = System.Windows.Forms.Cursor.Position;
                var p = PointFromScreen(new Point(screenPos.X, screenPos.Y));
                var inside = p.X >= 0 && p.X <= ActualWidth && p.Y >= 0;
                if (!_overlay.IsOpen)
                {
                    if (inside && p.Y <= OPEN_ZONE) _overlay.SetOpen(true);
                }
                else if (inside && p.Y <= KEEP_ZONE)
                {
                    _leftAt = DateTime.MinValue;
                }
                else
                {
                    if (_leftAt == DateTime.MinValue) _leftAt = DateTime.Now;
                    else if ((DateTime.Now - _leftAt).TotalSeconds >= CLOSE_DELAY_SEC) { _overlay.SetOpen(false); _leftAt = DateTime.MinValue; }
                }
            }
            catch { /* finestra in chiusura: il timer si ferma da solo */ }
        }

        // ---- finestra trasparente con linguetta e barra
        private sealed class OverlayWindow : Window
        {
            private readonly DetachedSessionWindow _owner;
            private readonly FrameworkElement _handle;
            private readonly FrameworkElement _bar;
            private bool _closedByCode;
            public bool IsOpen => _bar.Visibility == Visibility.Visible;

            public OverlayWindow(DetachedSessionWindow owner, FrameworkElement handle, FrameworkElement bar)
            {
                _owner = owner;
                _handle = handle;
                _bar = bar;
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = null;               // i pixel trasparenti non catturano il mouse: lo schermo remoto resta cliccabile
                ShowInTaskbar = false;
                ShowActivated = false;
                ResizeMode = ResizeMode.NoResize;
                Height = BAR_HEIGHT;
                Topmost = owner.Topmost;
                var grid = new Grid();
                handle.VerticalAlignment = VerticalAlignment.Top;
                handle.HorizontalAlignment = HorizontalAlignment.Center;
                bar.VerticalAlignment = VerticalAlignment.Top;
                bar.Visibility = Visibility.Collapsed;
                grid.Children.Add(handle);
                grid.Children.Add(bar);
                Content = grid;
                Closing += (s, e) => { if (!_closedByCode) e.Cancel = true; };
            }

            public void SetOpen(bool open)
            {
                _bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                _handle.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            }

            public void Reposition()
            {
                if (!_owner.IsLoaded || _owner.WindowState == WindowState.Minimized) return;
                var r = _owner.ClientRect();
                Left = r.X; Top = r.Y; Width = Math.Max(10, r.Width);
            }

            public void CloseByCode()
            {
                _closedByCode = true;
                try { Close(); } catch { /* gia' chiusa */ }
            }
        }

        // ---- elementi della barra
        /// <summary>✕ come quella delle schede: simbolo centrato, fondo chiaro al passaggio del mouse (BtnNav lasciava troppo poco spazio al simbolo)</summary>
        private static ControlTemplate CloseButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border)) { Name = "Bd" };
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "✕");
            glyph.SetBinding(TextBlock.FontSizeProperty, Lemora.Utils.FontPx.CreateBinding(12.0));
            glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            border.AppendChild(glyph);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), "Bd"));
            template.Triggers.Add(hover);
            return template;
        }

        private static Button MakeButton(string resourceKey, Action click, bool primary = false, string? fallback = null, double height = 28)
        {
            var b = new Button { Height = height, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 0, 12, 0), Focusable = false };
            b.SetResourceReference(StyleProperty, primary ? "BtnPrimary" : "BtnSecondary");
            b.Height = height; b.MinHeight = 0;
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, }; Lemora.Utils.FontPx.Apply(tb, TextBlock.FontSizeProperty, 12.5);
            if (fallback != null) tb.Text = fallback; else tb.SetResourceReference(TextBlock.TextProperty, resourceKey);
            b.Content = tb;
            b.Click += (s, e) => click();
            return b;
        }

        private FrameworkElement BuildHandle()
        {
            if (IsPip)
            {
                // lineetta accent: apre la barra e, trascinandola, sposta la finestra
                var line = new Border { Width = 44, Height = 6, CornerRadius = new CornerRadius(0, 0, 3, 3), Cursor = Cursors.SizeAll };
                line.SetResourceReference(Border.BackgroundProperty, "AccentMidBrush");
                line.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
                return line;
            }
            var t = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Lemora.Utils.FontPx.Apply(t, TextBlock.FontSizeProperty, 11.5);
            t.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            t.SetResourceReference(TextBlock.TextProperty, "session_options");
            var tab = new Border { Height = 22, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(0, 0, 4, 4), Cursor = Cursors.Hand, Child = t, Opacity = 0.92 };
            tab.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
            tab.MouseLeftButtonUp += (s, e) => _overlay?.SetOpen(true);
            return tab;
        }

        private FrameworkElement BuildBar(Action onReattach, Action<bool> onSwitchMode)
        {
            var bar = new Border { Height = IsPip ? PIP_BAR_HEIGHT : BAR_HEIGHT, BorderThickness = new Thickness(0, 0, 0, 1), Opacity = 0.97 };
            if (IsPip) bar.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };   // tutta la barra sposta la finestra (i pulsanti prendono il clic prima)
            bar.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
            bar.SetResourceReference(Border.BorderBrushProperty, "IslandDividerBrush");
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(IsPip ? 8 : 12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

            // a sinistra: sigla + nome (in PiP e' anche la maniglia di trascinamento)
            var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 14, 0) };
            if (IsPip)
            {
                var grip = new TextBlock { Text = "⋮⋮", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; Lemora.Utils.FontPx.Apply(grip, TextBlock.FontSizeProperty, 13);
                grip.SetResourceReference(TextBlock.ForegroundProperty, "IslandSecondaryTextBrush");
                info.Children.Add(grip);
                info.Cursor = Cursors.SizeAll;
                info.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            }
            var proto = new TextBlock { Text = (Host.ProtocolServer.ProtocolDisplayName ?? "").ToUpperInvariant(), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; Lemora.Utils.FontPx.Apply(proto, TextBlock.FontSizeProperty, 12);
            proto.SetResourceReference(TextBlock.ForegroundProperty, "AccentMidBrush");
            var name = new TextBlock { Text = Host.ProtocolServer.DisplayName, FontWeight = FontWeights.SemiBold, MaxWidth = IsPip ? 90 : 420, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Lemora.Utils.FontPx.Apply(name, TextBlock.FontSizeProperty, 13);
            name.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            info.Children.Add(proto);
            info.Children.Add(name);
            DockPanel.SetDock(info, Dock.Left);
            row.Children.Add(info);

            // stessi pulsanti della barra della sessione nella finestra principale: icone con suggerimento, a gruppi separati da filetti
            void Add(FrameworkElement e) { DockPanel.SetDock(e, Dock.Right); row.Children.Add(e); }   // i pulsanti si aggiungono da destra verso sinistra
            Border Sep() { var s = new Border(); s.SetResourceReference(StyleProperty, "SessionBarSeparator"); return s; }
            Button Icon(string geometryKey, string tipKey, Action click)
            {
                var b = new Button { Focusable = false };
                b.SetResourceReference(StyleProperty, "SessionIconButton");
                if (IsPip) { b.Height = 22; b.MinHeight = 0; b.Padding = new Thickness(4, 0, 4, 0); }
                b.SetResourceReference(FrameworkElement.ToolTipProperty, tipKey);
                var p = new System.Windows.Shapes.Path();
                p.SetResourceReference(StyleProperty, "SessionIcon");
                p.SetResourceReference(System.Windows.Shapes.Path.DataProperty, geometryKey);
                b.Content = p;
                b.Click += (s, e) => click();
                return b;
            }

            // ✕ chiude la sessione: aggiunta per prima, quindi la piu' a destra
            var close = new Button { Width = IsPip ? 26 : 30, Height = IsPip ? 22 : 28, Padding = new Thickness(0), Focusable = false, Cursor = Cursors.Hand, Template = CloseButtonTemplate() };
            close.Click += (s, e) => _onCloseSession();
            Add(close);

            if (IsPip)
            {
                Add(MakeButton("session_reattach_short", onReattach, primary: true, height: 22));
                Add(Icon("IconDetach", "session_pip_window", () => onSwitchMode(false)));
            }
            else
            {
                // connessione: riconnetti
                Add(Icon("IconRefresh", "session_reconnect", () => Host.ReConn()));
                Add(Sep());
                // finestra: Picture in picture e riattacca (l'azione principale)
                Add(MakeButton("session_reattach", onReattach, primary: true));
                Add(Icon("IconPip", "session_pip", () => onSwitchMode(true)));
                var sepScreen = Sep(); Add(sepScreen);
                // tasti
                Button? keys = null;
                if (Host.MenuItems.OfType<MenuItem>().FirstOrDefault(x => x.Header as string == "Ctrl + Alt + Del") is { } cad)
                {
                    keys = new Button { Content = "Ctrl Alt Canc", Focusable = false, ToolTip = "Ctrl + Alt + Canc" };
                    keys.SetResourceReference(StyleProperty, "SessionKeysButton");
                    keys.Click += (s, e) => cad.Command?.Execute(null);
                    Add(keys);
                }
                var sepKeys = Sep(); Add(sepKeys);
                // VNC con piu' monitor uniti: "Adatta · zoom" (solo quando serve)
                var fitLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                var fitIcon = new System.Windows.Shapes.Path { Width = 16, Height = 16 };
                fitIcon.SetResourceReference(StyleProperty, "SessionIcon");
                fitIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "IconFullscreen");
                var chevron = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 3.5,3.5 L 7,0"), StrokeThickness = 1.5, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                chevron.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
                var fitContent = new StackPanel { Orientation = Orientation.Horizontal };
                fitContent.Children.Add(fitIcon); fitContent.Children.Add(fitLabel); fitContent.Children.Add(chevron);
                Button? fit = null;
                fit = new Button { Content = fitContent, Focusable = false };
                fit.SetResourceReference(StyleProperty, "SessionTextButton");
                fit.Click += (s, e) => Host.ShowFitMenu(fit!);
                Add(fit);
                void RefreshViewControls()
                {
                    var has = Host.HasViewControls;
                    fit!.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                    fitLabel.Text = $"{IoC.Translate("vnc_fit")} · {Host.ZoomText}";
                    sepKeys.Visibility = has && keys != null ? Visibility.Visible : Visibility.Collapsed;
                    sepScreen.Visibility = has || keys != null ? Visibility.Visible : Visibility.Collapsed;
                }
                Host.ViewControlsChanged += () => Dispatcher.BeginInvoke(new Action(RefreshViewControls));
                RefreshViewControls();
            }
            bar.Child = row;
            return bar;
        }

        private string AddressText()
            => Host.ProtocolServer is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort a
                ? (string.IsNullOrEmpty(a.Port) ? a.Address : $"{a.Address}:{a.Port}")
                : Host.ProtocolServer.SubTitle;
    }
}
