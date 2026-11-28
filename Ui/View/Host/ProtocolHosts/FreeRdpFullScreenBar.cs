using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// FreeRDP in full screen: FreeRDP lacks the connection bar of the Microsoft component, so Lemora shows its own,
    /// small, at the top center, only when the mouse reaches the top edge. It is a separate window (a WPF element would not be visible
    /// above the FreeRDP window); the mouse position is polled at intervals because FreeRDP receives its movements.
    /// </summary>
    internal sealed class FreeRdpFullScreenBar : Window
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private System.Drawing.Rectangle _screenPx;
        private DateTime _leftAt = DateTime.MaxValue;

        public FreeRdpFullScreenBar(string title, Action exitFullScreen, Action close)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            // not "always on top": the full screen window is its owner (Arm), so the bar stays above the session but an app of this PC
            // put over the session on the same monitor covers it too (before, the bar stayed visible above that app)
            Topmost = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            // same icon buttons as the session bar (SessionIconButton/SessionIcon in FluentIslands.xaml)
            Button Btn(string tip, string geometry, Action a, bool hide = true)
            {
                var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry) };
                icon.SetResourceReference(StyleProperty, "SessionIcon");
                var b = new Button { Content = icon, ToolTip = tip, Focusable = false };
                b.SetResourceReference(StyleProperty, "SessionIconButton");
                b.Click += (_, _) => { if (hide) Hide(); a(); };
                return b;
            }

            var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "GlobalFontFamily");
            var sep = new Border();
            sep.SetResourceReference(StyleProperty, "SessionBarSeparator");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            // pin, like the Microsoft connection bar: pinned = always visible; unpinned = it hides and comes back at the top edge
            _pin = Btn("", PinGeometry, () => { _pinned = !_pinned; UpdatePin(); }, hide: false);
            UpdatePin();
            row.Children.Add(_pin);
            row.Children.Add(label);
            row.Children.Add(sep);
            // exit full screen: primary (blue) text button
            var back = new Button { Content = IoC.Translate("session_back_to_window"), Height = 26, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(2, 0, 4, 0), Focusable = false };
            back.SetResourceReference(StyleProperty, "BtnPrimary");
            back.Click += (_, _) => { Hide(); exitFullScreen(); };
            row.Children.Add(back);
            // minimize: the full screen window goes to the taskbar and comes back as it was (the session stays connected)
            row.Children.Add(Btn(IoC.Translate("session_minimize"), "M6,17 L18,17", () => { if (Owner != null) Owner.WindowState = WindowState.Minimized; }));
            row.Children.Add(Btn(IoC.Translate("session_close_connection"), "M6,6 L18,18 M18,6 L6,18", close));
            var box = new Border
            {
                Child = row,
                Padding = new Thickness(6, 5, 6, 5),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                BorderThickness = new Thickness(1, 0, 1, 1),
            };
            // solid background, in the tab bar color: above the remote desktop it must be readable over any image
            box.SetResourceReference(Border.BackgroundProperty, "PrimaryMidBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "IslandWindowBorderBrush");
            Content = box;
            _timer.Tick += (_, _) => CheckMouse();

            // draggable along the top edge, also onto another monitor, like the Microsoft connection bar: grabbed anywhere outside the buttons
            // (before only by the name, a small target that was easy to miss). The buttons handle their own click, so it does not get here
            label.Cursor = System.Windows.Input.Cursors.SizeWE;
            box.Cursor = System.Windows.Input.Cursors.SizeWE;
            label.ToolTip = IoC.Translate("session_fsbar_drag");
            box.MouseLeftButtonDown += (_, e) =>
            {
                _dragCursorX = System.Windows.Forms.Cursor.Position.X;
                _dragCenterX = CenterXPx;
                box.CaptureMouse();
                e.Handled = true;
            };
            box.MouseMove += (_, _) => { if (box.IsMouseCaptured) DragTo(_dragCenterX + System.Windows.Forms.Cursor.Position.X - _dragCursorX); };
            box.MouseLeftButtonUp += (_, _) => box.ReleaseMouseCapture();
        }

        private readonly Button _pin;
        private const string PinGeometry = "M9,4 L15,4 M10,4 L10,11 L7,14 L17,14 L14,11 L14,4 M12,14 L12,20";

        private void UpdatePin()
        {
            _pin.ToolTip = IoC.Translate(_pinned ? "session_fsbar_pinned" : "session_fsbar_pin");
            _pin.Opacity = _pinned ? 1 : 0.55;
        }

        private bool OwnerMinimized => Owner?.WindowState == WindowState.Minimized;

        /// <summary>where the user dragged the bar (center, screen pixels); null = center of the monitor</summary>
        private int? _centerXPx;
        private int _dragCursorX, _dragCenterX;

        private int CenterXPx => _centerXPx ?? _screenPx.Left + _screenPx.Width / 2;

        private void DragTo(int centerX)
        {
            // the monitor under the new position becomes the bar's monitor (its top edge shows it again)
            var monitors = Lemora.Utils.PhysicalScreens.All().Select(m => m.Bounds).ToList();
            var mon = monitors.FirstOrDefault(b => centerX >= b.Left && centerX < b.Right);
            if (mon.IsEmpty) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var halfPx = (int)(ActualWidth * dpi.DpiScaleX / 2);
            centerX = Math.Max(mon.Left + halfPx, Math.Min(mon.Right - halfPx, centerX));
            _screenPx = mon;
            _centerXPx = centerX;
            Place();
        }

        // server options, like the Microsoft component's connection bar (no feature lost with FreeRDP)
        private bool _enabled = true;   // "Show the connection bar": off = no bar, exit with Ctrl+Alt+Pause
        private bool _pinned;           // "Pin the bar": always visible at the top, does not hide
        private Action? _exitFullScreen;
        private bool _keyLatched;

        /// <summary>enables the bar for full screen on this rectangle (screen pixels)</summary>
        public void Arm(Window owner, System.Drawing.Rectangle screenPx, bool enabled = true, bool pinned = false, Action? exitByKeys = null)
        {
            Owner = owner;
            _screenPx = screenPx;
            _centerXPx = null;   // a new full screen: back to the center of the monitor
            _enabled = enabled;
            _pinned = enabled && pinned;
            _exitFullScreen = exitByKeys;
            _keyLatched = false;
            UpdatePin();
            _timer.Start();
            if (_pinned) ShowAtTop();
            else if (IsVisible) Hide();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>Ctrl+Alt+Pause exits full screen, as in the Microsoft component (with Ctrl held, Pause also arrives as VK_CANCEL)</summary>
        private bool ExitKeysPressed() => Down(0x11) && Down(0x12) && (Down(0x13) || Down(0x03));

        public void Disarm()
        {
            _timer.Stop();
            Hide();
        }

        private void CheckMouse()
        {
            if (_exitFullScreen != null)
            {
                var keys = ExitKeysPressed();
                if (keys && !_keyLatched) { _keyLatched = true; Hide(); _exitFullScreen(); return; }
                if (!keys) _keyLatched = false;
            }
            if (!_enabled) return;
            // session minimized: no bar above the other windows until it comes back
            if (OwnerMinimized) { if (IsVisible) Hide(); return; }
            if (_pinned) { if (!IsVisible) ShowAtTop(); return; }
            if (System.Windows.Input.Mouse.Captured != null && IsVisible) { _leftAt = DateTime.MaxValue; return; }   // being dragged
            var p = System.Windows.Forms.Cursor.Position;   // real screen pixels
            var inside = p.X >= _screenPx.Left && p.X < _screenPx.Right;
            if (!IsVisible)
            {
                if (inside && p.Y >= _screenPx.Top && p.Y <= _screenPx.Top + 2) ShowAtTop();
                return;
            }
            // disappears when the mouse moves away from the bar (with a brief tolerance)
            var dpi = VisualTreeHelper.GetDpi(this);
            var bottomPx = _screenPx.Top + ActualHeight * dpi.DpiScaleY + 40;
            if (inside && p.Y <= bottomPx) { _leftAt = DateTime.MaxValue; return; }
            if (_leftAt == DateTime.MaxValue) _leftAt = DateTime.Now;
            else if (DateTime.Now - _leftAt > TimeSpan.FromMilliseconds(600)) { Hide(); _leftAt = DateTime.MaxValue; }
        }

        private void ShowAtTop()
        {
            Show();
            UpdateLayout();
            Place();
        }

        private void Place()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = CenterXPx / dpi.DpiScaleX - ActualWidth / 2;
            Top = _screenPx.Top / dpi.DpiScaleY;
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}
