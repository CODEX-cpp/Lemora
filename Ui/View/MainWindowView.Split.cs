using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lemora.View.Host.ProtocolHosts;

namespace Lemora.View
{
    /// <summary>vista divisa: due sessioni affiancate nella stessa scheda, con testata per pannello e divisore trascinabile</summary>
    public partial class MainWindowView
    {
        private readonly Border _frameL = new(), _frameR = new(), _headL = new(), _headR = new();
        private readonly GridSplitter _splitter = new();
        private readonly DispatcherTimer _paneTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private HostBase? _paneL, _paneR;
        private bool _mouseWasDown;

        private void InitSplitUi()
        {
            SessionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SessionGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            SessionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            SessionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            SessionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });

            foreach (var (frame, head, col) in new[] { (_frameL, _headL, 0), (_frameR, _headR, 2) })
            {
                Grid.SetRow(frame, 0); Grid.SetRowSpan(frame, 2); Grid.SetColumn(frame, col);
                frame.BorderThickness = new Thickness(1); frame.CornerRadius = new CornerRadius(4); frame.Visibility = Visibility.Collapsed;
                frame.SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
                Panel.SetZIndex(frame, -1);
                Grid.SetRow(head, 0); Grid.SetColumn(head, col);
                head.Margin = new Thickness(1, 1, 1, 0); head.Height = 30; head.Visibility = Visibility.Collapsed; head.ClipToBounds = true;
                head.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
                SessionGrid.Children.Add(frame); SessionGrid.Children.Add(head);
            }
            // divisore: si trascina per cambiare la larghezza dei due pannelli
            Grid.SetRow(_splitter, 0); Grid.SetRowSpan(_splitter, 2); Grid.SetColumn(_splitter, 1);
            _splitter.HorizontalAlignment = HorizontalAlignment.Stretch; _splitter.VerticalAlignment = VerticalAlignment.Stretch;
            _splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext; _splitter.ResizeDirection = GridResizeDirection.Columns;
            _splitter.Cursor = Cursors.SizeWE; _splitter.Background = Brushes.Transparent; _splitter.Visibility = Visibility.Collapsed; _splitter.ShowsPreview = false;
            _splitter.MouseEnter += (_, _) => _splitter.SetResourceReference(Control.BackgroundProperty, "AccentMidBrush");
            _splitter.MouseLeave += (_, _) => _splitter.Background = Brushes.Transparent;
            SessionGrid.Children.Add(_splitter);

            _paneTimer.Tick += (_, _) => PaneTick();
        }

        private UIElement BuildPaneHeader(HostBase h, bool active)
        {
            var proto = new TextBlock { Text = (h.ProtocolServer.ProtocolDisplayName ?? "").ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC9, 0x95)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            var name = new TextBlock { Text = Vm.PaneTitle(h), FontSize = 13.5, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Fill = h.Status == ProtocolHostStatus.Connected ? new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)) : Brushes.Gray };
            var close = new Button { Content = "✕", Width = 26, Height = 22, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand, Focusable = false, ToolTip = "Chiudi questa sessione", Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            close.SetResourceReference(Control.ForegroundProperty, "IslandSecondaryTextBrush");
            close.Click += (_, e) => { e.Handled = true; Vm.CloseSessionFromTab(h); };
            var g = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 1); Grid.SetColumn(dot, 2); Grid.SetColumn(close, 3);
            g.Children.Add(proto); g.Children.Add(name); g.Children.Add(dot); g.Children.Add(close);
            g.MouseLeftButtonDown += (_, _) => Vm.ActivateSession(h);
            return g;
        }

        /// <summary>mette sessioni, testate e divisore al loro posto secondo la scheda mostrata</summary>
        private void LayoutSessions()
        {
            var hosts = Vm.SessionHosts;
            var active = Vm.ActiveSession;
            var partner = active != null ? Vm.SplitPartner(active) : null;
            HostBase? left = null, right = null;
            if (active != null && partner != null && hosts.Contains(partner) && !Vm.IsDetached(active) && !Vm.IsDetached(partner))
            {
                left = hosts.IndexOf(active) < hosts.IndexOf(partner) ? active : partner;
                right = ReferenceEquals(left, active) ? partner : active;
            }
            if (_paneAnimating) CancelPaneAnim();
            var wasSplit = _paneL != null && _paneR != null;
            // si separa: la sessione rimasta si allarga fino a riempire l'area, poi si applica la disposizione finale
            if ((left == null || right == null) && wasSplit && active != null && hosts.Contains(_paneL!) && hosts.Contains(_paneR!)
                && !Vm.IsDetached(_paneL!) && !Vm.IsDetached(_paneR!) && (ReferenceEquals(active, _paneL) || ReferenceEquals(active, _paneR)))
            {
                var toLeft = ReferenceEquals(active, _paneL);
                AnimateColumns(1, 1, toLeft ? 4 : 1, toLeft ? 1 : 4, () => { _paneL = _paneR = null; SyncSessionLayer(); });
                return;
            }
            foreach (var h in hosts)
            {
                if (Vm.IsDetached(h)) continue;
                if (ReferenceEquals(h, left)) { Grid.SetRow(h, 1); Grid.SetRowSpan(h, 1); Grid.SetColumn(h, 0); Grid.SetColumnSpan(h, 1); h.Margin = new Thickness(1, 0, 1, 1); }
                else if (ReferenceEquals(h, right)) { Grid.SetRow(h, 1); Grid.SetRowSpan(h, 1); Grid.SetColumn(h, 2); Grid.SetColumnSpan(h, 1); h.Margin = new Thickness(1, 0, 1, 1); }
                else { Grid.SetRow(h, 0); Grid.SetRowSpan(h, 2); Grid.SetColumn(h, 0); Grid.SetColumnSpan(h, 3); h.Margin = new Thickness(0); }
            }
            var split = left != null && right != null;
            _paneL = left; _paneR = right;
            foreach (var e in new UIElement[] { _frameL, _frameR, _headL, _headR, _splitter }) e.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            if (split)
            {
                _headL.Child = BuildPaneHeader(left!, ReferenceEquals(left, active));
                _headR.Child = BuildPaneHeader(right!, ReferenceEquals(right, active));
                // pannello attivo: bordo accent
                (ReferenceEquals(left, active) ? _frameL : _frameR).SetResourceReference(Border.BorderBrushProperty, "AccentMidBrush");
                (ReferenceEquals(left, active) ? _frameR : _frameL).SetResourceReference(Border.BorderBrushProperty, "IslandBorderBrush");
                if (!_paneTimer.IsEnabled) _paneTimer.Start();
                // si affianca: la sessione che era sola si restringe e l'altra entra dal suo lato; riquadri e testate sfumano
                if (!wasSplit && _lastSingle != null && (ReferenceEquals(_lastSingle, left) || ReferenceEquals(_lastSingle, right)))
                {
                    var fromLeft = ReferenceEquals(_lastSingle, left);
                    AnimateColumns(fromLeft ? 4 : 1, fromLeft ? 1 : 4, 1, 1, null);
                    foreach (var e in new UIElement[] { _frameL, _frameR, _headL, _headR, _splitter })
                        e.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                }
            }
            else { _paneTimer.Stop(); _headL.Child = null; _headR.Child = null; _lastSingle = active; }
        }

        // ---- animazione dei due pannelli: si muovono i pesi delle colonne (le sessioni sono finestre native, una trasformazione non le sposterebbe)
        private bool _paneAnimating;
        private int _animGen;
        private HostBase? _lastSingle;

        private void CancelPaneAnim()
        {
            _animGen++;
            _paneAnimating = false;
            ResetPaneColumns();
        }

        private void ResetPaneColumns()
        {
            SessionGrid.ColumnDefinitions[0].MinWidth = 160; SessionGrid.ColumnDefinitions[2].MinWidth = 160;
            SessionGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SessionGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }

        private void AnimateColumns(double fromL, double fromR, double toL, double toR, Action? done)
        {
            var gen = ++_animGen;
            _paneAnimating = done != null;   // solo la separazione tiene le due sessioni visibili fino alla fine
            var c0 = SessionGrid.ColumnDefinitions[0]; var c2 = SessionGrid.ColumnDefinitions[2];
            // le colonne non scendono sotto la larghezza minima (160): un terminale nativo a larghezza quasi zero non va ridimensionato
            c0.Width = new GridLength(fromL, GridUnitType.Star); c2.Width = new GridLength(fromR, GridUnitType.Star);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Step(object? s, EventArgs e)
            {
                if (gen != _animGen) { CompositionTarget.Rendering -= Step; return; }
                var t = Math.Min(1, sw.Elapsed.TotalMilliseconds / 220);
                var k = 1 - Math.Pow(1 - t, 3);   // rallenta verso la fine
                c0.Width = new GridLength(Math.Max(0.0001, fromL + (toL - fromL) * k), GridUnitType.Star);
                c2.Width = new GridLength(Math.Max(0.0001, fromR + (toR - fromR) * k), GridUnitType.Star);
                if (t < 1) return;
                CompositionTarget.Rendering -= Step;
                _paneAnimating = false;
                ResetPaneColumns();
                done?.Invoke();
            }
            CompositionTarget.Rendering += Step;
        }

        // ---- clic dentro un pannello: i client RDP/VNC/terminale sono finestre native e non avvisano WPF, quindi si guarda il mouse
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private void PaneTick()
        {
            var down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            var pressed = down && !_mouseWasDown;
            _mouseWasDown = down;
            if (!pressed || _paneL == null || _paneR == null) return;
            if (GetForegroundWindow() != new WindowInteropHelper(this).Handle) return;
            GetCursorPos(out var p);
            var pt = new Point(p.X, p.Y);
            foreach (var h in new[] { _paneL, _paneR })
            {
                if (!h.IsVisible || ReferenceEquals(h, Vm.ActiveSession)) continue;
                var tl = h.PointToScreen(new Point(0, 0));
                var rect = new Rect(tl, new Size(h.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX, h.ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY));
                if (rect.Contains(pt)) { Vm.ActivateSession(h); break; }
            }
        }

        // ---- trascinare una scheda su un'altra affianca le due sessioni
        private Point _tabDragStart;
        private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _tabDragStart = e.GetPosition(null);

        private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || sender is not Button { DataContext: MainTabItem { Host: { } host, IsSplit: false } }) return;
            var d = e.GetPosition(null) - _tabDragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var src = (Button)sender;
            StartGhost(src, ((MainTabItem)src.DataContext).Title);
            try { DragDrop.DoDragDrop(src, new DataObject("lemora.session", host.ConnectionId), DragDropEffects.Move); }
            finally { ClearDropHint(); StopGhost(); }   // anche se il rilascio e' stato annullato
        }

        // ---- strato sopra tutta la finestra per fantasma e scritta del trascinamento (elementi normali: nessuna finestra in piu', e funziona anche nella barra del titolo)
        private Canvas? _overlay;

        private Canvas Overlay
        {
            get
            {
                if (_overlay == null && Content is Grid root)
                {
                    _overlay = new Canvas { IsHitTestVisible = false };
                    Panel.SetZIndex(_overlay, 9999);
                    Grid.SetRowSpan(_overlay, 99); Grid.SetColumnSpan(_overlay, 99);
                    root.Children.Add(_overlay);
                }
                return _overlay!;
            }
        }

        private static Border Chip(string text, double size, double opacity)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            var b = new Border { Child = t, Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(6), Opacity = opacity, IsHitTestVisible = false };
            b.SetResourceReference(Border.BackgroundProperty, "AccentMidBrush");
            return b;
        }

        // ---- scheda "fantasma" che segue il cursore durante il trascinamento
        private Border? _ghost;
        private Button? _ghostSrc;

        private void StartGhost(Button src, string title)
        {
            StopGhost();
            var ov = Overlay;
            if (ov == null) return;
            _ghost = Chip(title, 13, 0.9);
            Canvas.SetLeft(_ghost, -1000); Canvas.SetTop(_ghost, -1000);
            ov.Children.Add(_ghost);
            // DragOver arriva solo sopra gli elementi che accettano il rilascio (le schede): GiveFeedback invece parte a ogni movimento, ovunque
            _ghostSrc = src; src.GiveFeedback += Ghost_GiveFeedback;
            MoveGhostToCursor();
        }

        private void Ghost_GiveFeedback(object sender, GiveFeedbackEventArgs e) => MoveGhostToCursor();

        private void MoveGhostToCursor()
        {
            if (_ghost == null || _overlay == null || !GetCursorPos(out var p)) return;
            try
            {
                var pt = _overlay.PointFromScreen(new Point(p.X, p.Y));
                Canvas.SetLeft(_ghost, pt.X + 14); Canvas.SetTop(_ghost, pt.Y + 14);
            }
            catch (InvalidOperationException) { }
        }

        private void StopGhost()
        {
            if (_ghostSrc != null) { _ghostSrc.GiveFeedback -= Ghost_GiveFeedback; _ghostSrc = null; }
            if (_ghost != null) { _overlay?.Children.Remove(_ghost); _ghost = null; }
        }

        // ---- scheda di destinazione: alone azzurro e scritta "Affianca" mentre ci si passa sopra
        private Border? _dropHint;
        private Button? _dropHintTarget;

        private void ShowDropHint(Button target)
        {
            if (ReferenceEquals(_dropHintTarget, target)) return;
            ClearDropHint();
            _dropHintTarget = target;
            var glow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0, Color = (Color)FindResource("AccentMidColor") };
            target.Effect = glow;
            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(150)));
            var chip = Chip("Affianca", 12, 0);
            Overlay.Children.Add(chip);
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            try
            {
                var at = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight), Overlay);
                Canvas.SetLeft(chip, at.X - chip.DesiredSize.Width / 2); Canvas.SetTop(chip, at.Y + 4);
            }
            catch (InvalidOperationException) { }
            chip.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _dropHint = chip;
        }

        private void ClearDropHint()
        {
            if (_dropHint != null) { _overlay?.Children.Remove(_dropHint); _dropHint = null; }
            if (_dropHintTarget != null) { _dropHintTarget.Effect = null; _dropHintTarget = null; }
        }

        private void Tab_DragLeave(object sender, DragEventArgs e)
        {
            if (ReferenceEquals(sender, _dropHintTarget)) ClearDropHint();
        }

        private HostBase? DraggedHost(DragEventArgs e)
            => e.Data.GetDataPresent("lemora.session") ? Vm.SessionHosts.FirstOrDefault(x => x.ConnectionId == (string)e.Data.GetData("lemora.session")) : null;

        private void Tab_DragOver(object sender, DragEventArgs e)
        {
            var src = DraggedHost(e);
            var ok = src != null && sender is Button { DataContext: MainTabItem { Host: { } target, IsSplit: false } } && !ReferenceEquals(src, target);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            if (ok) ShowDropHint((Button)sender); else if (ReferenceEquals(sender, _dropHintTarget)) ClearDropHint();
            e.Handled = true;
        }

        private void Tab_Drop(object sender, DragEventArgs e)
        {
            ClearDropHint();
            var src = DraggedHost(e);
            if (src != null && sender is Button { DataContext: MainTabItem { Host: { } target, IsSplit: false } } && !ReferenceEquals(src, target))
                Vm.MergeSessions(src, target);
            e.Handled = true;
        }
    }
}
