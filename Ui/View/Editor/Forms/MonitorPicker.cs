using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lemora.Utils;

namespace Lemora.View.Editor.Forms
{
    /// <summary>
    /// Selettore dei monitor: li disegna in scala con la loro posizione reale, un clic li attiva o li disattiva; "Rileva" mostra il numero su ogni monitor.
    /// <see cref="Selection"/> e' l'elenco dei numeri scelti ("1,2"); vuoto = automatico.
    /// </summary>
    public class MonitorPicker : StackPanel
    {
        public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
            nameof(Selection), typeof(string), typeof(MonitorPicker),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MonitorPicker)d).Rebuild()));

        public string Selection
        {
            get => (string)GetValue(SelectionProperty);
            set => SetValue(SelectionProperty, value);
        }

        private const double PickerWidth = 440, PickerHeight = 110;
        private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        public MonitorPicker()
        {
            var detect = new Button { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 10, 0, 0) };
            detect.SetResourceReference(ContentControl.ContentProperty, "monitor_detect");
            detect.Click += (_, _) => ShowNumbers();
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "IslandSecondaryTextBrush");
            _hint.SetResourceReference(TextBlock.TextProperty, "monitor_picker_hint");
            Children.Add(_canvas);
            Children.Add(detect);
            Children.Add(_hint);
            Loaded += (_, _) => Rebuild();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Unloaded += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Rebuild));

        private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        private void Rebuild()
        {
            _canvas.Children.Clear();
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length == 0) return;
            var all = screens.Select(s => s.Bounds).Aggregate(System.Drawing.Rectangle.Union);
            var scale = Math.Min(PickerWidth / all.Width, PickerHeight / all.Height);
            _canvas.Width = all.Width * scale;
            _canvas.Height = all.Height * scale;
            var selected = PhysicalScreens.ParseSelection(Selection);
            for (var i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                var on = selected.Contains(i);
                var tile = new Border
                {
                    Width = b.Width * scale - 4,
                    Height = b.Height * scale - 4,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(on ? 2 : 1),
                    Background = on ? Res("AccentMidBrush", Brushes.SteelBlue) : Res("PrimaryDarkBrush", Brushes.DimGray),
                    BorderBrush = on ? Res("AccentMidBrush", Brushes.SteelBlue) : Res("IslandBorderBrush", Brushes.Gray),
                    Cursor = Cursors.Hand,
                    Tag = i,
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString() + (screens[i].Primary ? " ★" : ""),
                        FontSize = 20,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = on ? Brushes.Black : Res("PrimaryTextBrush", Brushes.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                tile.MouseLeftButtonUp += Tile_Click;
                Canvas.SetLeft(tile, (b.X - all.X) * scale + 2);
                Canvas.SetTop(tile, (b.Y - all.Y) * scale + 2);
                _canvas.Children.Add(tile);
            }
        }

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            var index = (int)((FrameworkElement)sender).Tag;
            var set = PhysicalScreens.ParseSelection(Selection);
            if (!set.Remove(index)) set.Add(index);
            Selection = string.Join(",", set.OrderBy(x => x).Select(x => x + 1));
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>numero grande al centro di ogni monitor per due secondi</summary>
        private void ShowNumbers()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var wins = screens.Select((s, i) =>
            {
                var number = new TextBlock { Text = (i + 1).ToString(), FontSize = 72, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var w = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                    Width = 140, Height = 140, AllowsTransparency = true, Background = Brushes.Transparent,
                    Content = new Border { CornerRadius = new CornerRadius(12), Background = Res("AccentMidBrush", Brushes.SteelBlue), Child = number },
                };
                w.Show();
                var hwnd = new WindowInteropHelper(w).Handle;
                var dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
                var side = (int)(140 * dpi);
                SetWindowPos(hwnd, IntPtr.Zero, s.Bounds.X + (s.Bounds.Width - side) / 2, s.Bounds.Y + (s.Bounds.Height - side) / 2, side, side, 0x0010 | 0x0004);   // NOACTIVATE | NOZORDER
                return w;
            }).ToList();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); foreach (var w in wins) w.Close(); };
            timer.Start();
        }
    }
}
