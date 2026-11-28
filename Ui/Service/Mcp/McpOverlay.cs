using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lemora.View.Host.ProtocolHosts;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Over the remote screen of a session used by Claude: yellow border and "Claude" cursor where Claude moved the mouse.
    /// It is a separate window, transparent and click-through: RDP (FreeRDP) and VNC are native windows and would cover a WPF drawing.
    /// It belongs to the session window (stays above it only, disappears when it is minimized) and follows it every 100 ms.
    /// </summary>
    internal sealed class McpOverlay : Window
    {
        public static readonly Color Yellow = Color.FromRgb(0xF2, 0xC9, 0x4C);
        private static readonly Dictionary<string, McpOverlay> Open = new();

        private readonly HostBase _host;
        private readonly DispatcherTimer _timer;
        private readonly Canvas _cursor;
        private readonly TextBlock _labelText;
        private readonly DispatcherTimer _labelReset;
        private readonly Window _controls;
        private bool _dragging;
        private (int x, int y)? _remote;
        /// <summary>remote screen (real pixels of this PC's screen) at the last Follow: used to save the panel position relative to the session</summary>
        private (int x, int y, int w, int h, double dpi) _area;

        private static bool FindButton(DependencyObject d)
        {
            for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (x is Button) return true;
            return false;
        }

        /// <summary>distance from the session's right edge and top, in pixels at 100% (the same on screens with different zoom), in the encrypted database</summary>
        private void SavePosition()
        {
            if (!GetWindowRect(new WindowInteropHelper(_controls).Handle, out var r) || _area.w <= 0) return;
            var s = McpGuard.Settings;
            s.ControlsRight = Math.Max(0, (_area.x + _area.w - r.Right) / _area.dpi);
            s.ControlsTop = Math.Max(0, (r.Top - _area.y) / _area.dpi);
            McpGuard.Save();
        }

        /// <summary>
        /// panel with Pause and Stop at the top center of the remote screen: this window receives no clicks (they pass to the session), so it is a
        /// separate window, small and clickable, that never takes focus (focus stays with the session)
        /// </summary>
        private Window MakeControls()
        {
            var yellow = new SolidColorBrush(Yellow);
            // small and thin: text size 11, short buttons, thin border; the grip on the left shows it can be moved
            var grip = new TextBlock { Text = "⠿", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.6 };
            grip.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            var pause = new Button { Padding = new Thickness(7, 0, 7, 1), MinWidth = 0, MinHeight = 0, FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
            var stop = new Button { Padding = new Thickness(7, 0, 7, 1), MinWidth = 0, MinHeight = 0, FontSize = 11, Content = IoC.Translate("mcp_overlay_stop"),
                                    ToolTip = IoC.Translate("mcp_overlay_stop_tip") };
            void Refresh()
            {
                var paused = McpGuard.IsPaused(_host.ProtocolServer);
                // "Stop Claude" pressed: Claude can do nothing here, Pause means nothing until "Resume Claude"
                pause.Visibility = McpGuard.IsStopped ? Visibility.Collapsed : Visibility.Visible;
                if (McpGuard.IsStopped) { _labelReset.Stop(); _labelText.Text = "Claude"; }   // "Claude · Click" stayed next to the cursor
                text.Text = McpGuard.IsStopped ? IoC.Translate("mcp_banner_stopped") : paused ? McpGuard.TextPaused : McpGuard.TextActive;   // texts from Settings > Appearance
                pause.Content = IoC.Translate(paused ? "mcp_overlay_resume" : "Pause");
                pause.ToolTip = IoC.Translate(paused ? "mcp_overlay_resume_tip" : "mcp_overlay_pause_tip");
            }
            pause.Click += (_, _) => { McpGuard.SetPaused(_host.ProtocolServer, !McpGuard.IsPaused(_host.ProtocolServer)); Refresh(); };
            stop.Click += (_, _) => McpGuard.Block(_host.ProtocolServer);   // the session no longer belongs to Claude: Sync closes border and panel
            Refresh();
            Action onTexts = () => Dispatcher.BeginInvoke(new Action(Refresh));
            McpGuard.TextsChanged += onTexts;
            Closed += (_, _) => McpGuard.TextsChanged -= onTexts;
            var border = new Border
            {
                Padding = new Thickness(6, 3, 3, 3), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = yellow,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { grip, text, pause, stop } },
                Cursor = System.Windows.Input.Cursors.SizeAll, ToolTip = IoC.Translate("mcp_drag_to_move"),
            };
            border.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
            pause.Cursor = stop.Cursor = System.Windows.Input.Cursors.Arrow;
            // hand-made dragging (the window never takes focus); on release the distance from the right edge and the top is saved
            System.Windows.Point start = default;
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
                start = e.GetPosition(border);
                _dragging = border.CaptureMouse();
                e.Handled = true;
            };
            border.MouseMove += (_, e) =>
            {
                if (!_dragging) return;
                var p = border.PointToScreen(e.GetPosition(border));
                var dpi = VisualTreeHelper.GetDpi(border);
                SetWindowPos(new WindowInteropHelper(_controls).Handle, IntPtr.Zero, (int)(p.X - start.X * dpi.DpiScaleX), (int)(p.Y - start.Y * dpi.DpiScaleY), 0, 0, 0x0010 | 0x0004 | 0x0001);
            };
            border.MouseLeftButtonUp += (_, _) =>
            {
                if (!_dragging) return;
                _dragging = false;
                border.ReleaseMouseCapture();
                SavePosition();
            };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = border, Title = "Claude - controls",
            };
            w.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);   // NOACTIVATE + TOOLWINDOW: clicks yes, focus and Alt+Tab no
                // WM_MOUSEACTIVATE -> MA_NOACTIVATE: the click does not take focus from the session
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg != 0x21) return IntPtr.Zero;
                    handled = true;
                    return (IntPtr)3;
                });
            };
            return w;
        }

        /// <summary>one window for each Claude session; the others are closed (on the UI thread)</summary>
        public static void Sync(IReadOnlyCollection<HostBase> hosts)
        {
            if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") == "1") return;
            foreach (var id in Open.Keys.Where(k => hosts.All(h => h.ConnectionId != k)).ToList())
            {
                Open[id].Close();
                Open.Remove(id);
            }
            foreach (var h in hosts.Where(h => !Open.ContainsKey(h.ConnectionId)))
                Open[h.ConnectionId] = new McpOverlay(h);
        }

        /// <summary>Claude moved or pressed the mouse at x,y (remote screen pixels)</summary>
        public static void Pointer(HostBase host, int x, int y)
        {
            if (Open.TryGetValue(host.ConnectionId, out var o)) { o.StopGlide(); o._remote = (x, y); o.Follow(); }
        }

        /// <summary>
        /// what Claude is doing, next to the cursor ("Claude · Click"); it goes back to "Claude" a few seconds after the last action.
        /// From any thread. Shown only once the cursor has a position (before the first mouse action there is no cursor to put it next to)
        /// </summary>
        public static void Doing(string connectionId, string text)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Open.TryGetValue(connectionId, out var o)) return;
                o._labelText.Text = "Claude · " + text;
                o._labelReset.Stop(); o._labelReset.Start();
            }));
        }

        /// <summary>
        /// the cursor slides from where it is to x,y like a hand-moved mouse (ease in and out, 150-450 ms depending on the distance), instead of
        /// jumping; the task ends when it has arrived, so the real click comes after. From any thread; no overlay (headless, other tab) = done at once
        /// </summary>
        public static Task GlideTo(HostBase host, int x, int y)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return Task.CompletedTask;
            return app.Dispatcher.InvokeAsync(() =>
            {
                if (!Open.TryGetValue(host.ConnectionId, out var o) || !o.IsVisible || o._remote is not { } from || from == (x, y)) return Task.CompletedTask;
                return o.Glide(from, (x, y));
            }).Task.Unwrap();
        }

        // tests only (demo mcp-cursore): the overlay also in the hidden instance, where Sync does not open it
        internal static McpOverlay TestOpen(HostBase h) => Open.TryGetValue(h.ConnectionId, out var o) ? o : Open[h.ConnectionId] = new McpOverlay(h);
        internal (double x, double y) TestCursor => (Canvas.GetLeft(_cursor), Canvas.GetTop(_cursor));
        internal string TestLabel => _labelText.Text;
        internal FrameworkElement TestContent => (FrameworkElement)Content;

        private DispatcherTimer? _glideTimer;
        private TaskCompletionSource? _glideDone;

        private Task Glide((int x, int y) from, (int x, int y) to)
        {
            StopGlide();
            var dist = Math.Sqrt(Math.Pow(to.x - from.x, 2) + Math.Pow(to.y - from.y, 2));
            var ms = Math.Clamp(150 + dist / 4, 150, 450);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var done = _glideDone = new TaskCompletionSource();
            _glideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
            {
                var t = Math.Min(1, clock.ElapsedMilliseconds / ms);
                var e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;   // ease in-out: starts and stops gently, like a hand
                _remote = ((int)Math.Round(from.x + (to.x - from.x) * e), (int)Math.Round(from.y + (to.y - from.y) * e));
                PlaceCursor();
                if (t >= 1) StopGlide();
            }, Dispatcher);
            _glideTimer.Start();
            return done.Task;
        }

        private void StopGlide()
        {
            _glideTimer?.Stop(); _glideTimer = null;
            _glideDone?.TrySetResult(); _glideDone = null;
        }

        private McpOverlay(HostBase host)
        {
            _host = host;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; Focusable = false; IsHitTestVisible = false; ResizeMode = ResizeMode.NoResize;
            Title = "Claude - " + host.ProtocolServer.DisplayName;
            var yellow = new SolidColorBrush(Yellow);
            var dark = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            var frame = new Border { BorderBrush = yellow, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4) };
            // arrow like the Windows one, yellow with a dark outline (visible on light and dark backgrounds), and the "Claude" label
            var arrow = new Path
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.5,13 L7.5,20 L10.5,18.8 L7.6,12 L13,12 Z"),
                Fill = yellow, Stroke = dark, StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round,
            };
            _labelText = new TextBlock { Text = "Claude", Foreground = dark, FontWeight = FontWeights.SemiBold, FontSize = 12 };
            var label = new Border
            {
                Background = yellow, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 3), BorderBrush = dark, BorderThickness = new Thickness(0.8),
                Child = _labelText,
            };
            _labelReset = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => { _labelText.Text = "Claude"; _labelReset!.Stop(); }, Dispatcher);
            Canvas.SetLeft(label, 15); Canvas.SetTop(label, 18);
            _cursor = new Canvas { Visibility = Visibility.Collapsed, Children = { arrow, label } };
            Content = new Grid { Children = { frame, new Canvas { ClipToBounds = true, Children = { _cursor } } } };
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                // WS_EX_TRANSPARENT: the user's clicks pass to the session below; NOACTIVATE/TOOLWINDOW: never focus, never in Alt+Tab
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80000 | 0x08000000 | 0x80);
            };
            _controls = MakeControls();
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Follow(), Dispatcher);
            Closed += (_, _) => { _timer.Stop(); _labelReset.Stop(); StopGlide(); _controls.Close(); };
            _timer.Start();
            Follow();
        }

        /// <summary>same position and size as the remote screen; hidden if the session is not visible (another tab, minimized window)</summary>
        private void Follow()
        {
            try
            {
                var owner = _host.IsVisible ? GetWindow(_host) : null;
                if (owner == null || owner.WindowState == WindowState.Minimized || _host.ActualWidth < 20 || _host.ActualHeight < 20 || PresentationSource.FromVisual(_host) == null)
                {
                    if (IsVisible) Hide();
                    if (_controls.IsVisible) _controls.Hide();
                    return;
                }
                if (!ReferenceEquals(Owner, owner)) Owner = owner;   // the session was detached into another window (or came back)
                if (!ReferenceEquals(_controls.Owner, owner)) _controls.Owner = owner;
                var dpi = VisualTreeHelper.GetDpi(_host);
                var tl = _host.PointToScreen(new Point(0, 0));
                int w = (int)Math.Round(_host.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(_host.ActualHeight * dpi.DpiScaleY);
                if (!IsVisible) Show();
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, IntPtr.Zero, (int)tl.X, (int)tl.Y, w, h, 0x0010 | 0x0004);   // SWP_NOACTIVATE | SWP_NOZORDER: real pixels, no DPI math
                // the controls panel: where the user put it (default top right, 8 pixels from the edges), always inside the session
                if (!_controls.IsVisible) _controls.Show();
                var cd = VisualTreeHelper.GetDpi(_controls);
                _area = ((int)tl.X, (int)tl.Y, w, h, cd.DpiScaleX);
                if (!_dragging)
                {
                    var ch = new WindowInteropHelper(_controls).Handle;
                    int cw = (int)Math.Round(_controls.ActualWidth * cd.DpiScaleX), chh = (int)Math.Round(_controls.ActualHeight * cd.DpiScaleY);
                    var st = McpGuard.Settings;
                    int right = (int)((st.ControlsRight ?? 8) * cd.DpiScaleX), top = (int)((st.ControlsTop ?? 8) * cd.DpiScaleY);
                    int cx = Math.Clamp((int)tl.X + w - cw - right, (int)tl.X, Math.Max((int)tl.X, (int)tl.X + w - cw));
                    int cy = Math.Clamp((int)tl.Y + top, (int)tl.Y, Math.Max((int)tl.Y, (int)tl.Y + h - chh));
                    SetWindowPos(ch, IntPtr.Zero, cx, cy, 0, 0, 0x0010 | 0x0004 | 0x0001);   // + SWP_NOSIZE
                }
                PlaceCursor();
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Debug("MCP: overlay over the session not moved: " + e.Message); }
        }

        /// <summary>the cursor where Claude's mouse is (remote screen pixels -> this window)</summary>
        private void PlaceCursor()
        {
            try
            {
                // paused or stopped: the mouse is the user's, Claude's cursor and its label ("Claude · Click") stayed on screen
                if (McpGuard.IsStopped || McpGuard.IsPaused(_host.ProtocolServer)) { _cursor.Visibility = Visibility.Collapsed; return; }
                if (!IsVisible || _remote is not { } r || _host is not IMcpScreen s || s.McpToScreen(r.x, r.y) is not { } p) return;
                var tl = _host.PointToScreen(new Point(0, 0));
                var my = VisualTreeHelper.GetDpi(this);
                Canvas.SetLeft(_cursor, (p.X - tl.X) / my.DpiScaleX);
                Canvas.SetTop(_cursor, (p.Y - tl.Y) / my.DpiScaleY);
                _cursor.Visibility = Visibility.Visible;
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Debug("MCP: Claude's pointer not moved: " + e.Message); }
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
