using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Lemora.Utils
{
    /// <summary>
    /// Startup "skeleton": the Home layout (tab, search, folders, history, details) with still-empty texts that
    /// pulse, drawn INSIDE the main window (a single window) while the app loads the data.
    /// It is drawn by its own thread, with its own Dispatcher, in a child window of the main window (not a second window): it keeps pulsing even when the UI
    /// thread is busy reading the database. The main window shows as soon as the database is open, with the
    /// skeleton in place of the real content; when the data is ready the real content goes in under the skeleton and, as soon as
    /// it is drawn, the skeleton is removed.
    /// Not used when started from the command line/minimized (no window in view) nor in hidden test instances
    /// (LEMORA_SKELETON=1 turns it on there too, for tests).
    /// </summary>
    public static class StartupSplash
    {
        private static Dispatcher? _dispatcher;
        private static FrameworkElement? _root;
        private static readonly ManualResetEventSlim Built = new(false);
        private static int _started;
        private static Grid? _wrap;
        private static UIElement? _real;
        private static readonly System.Collections.Generic.List<Action> Deferred = new();

        /// <summary>true from the moment the main window shows the skeleton until the real content comes in</summary>
        public static bool IsActive { get; private set; }

        /// <summary>prepares the skeleton on its own thread (built while the app reads configuration and database)</summary>
        public static void Start()
        {
            if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") == "1" && Lemora.Utils.DevOnly.Env("LEMORA_SKELETON") != "1") return;
            if (Environment.GetEnvironmentVariable("LEMORA_NO_SPLASH") == "1") return;
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try
            {
                var t = new Thread(Run) { IsBackground = true, Name = "StartupSplash" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                IsActive = true;
            }
            catch { /* without the skeleton the startup is just less pretty */ }
        }

        private static void Run()
        {
            try
            {
                StartDiag.Mark("Skeleton: thread started");
                _dispatcher = Dispatcher.CurrentDispatcher;
                StartDiag.Mark("Skeleton: dispatcher created");
                _root = Build();
                StartDiag.Mark("Skeleton: ready");
            }
            catch { _root = null; }
            finally { Built.Set(); }
            if (_root != null) Dispatcher.Run();
        }

        /// <summary>
        /// Called by the main window constructor (UI thread): the real content is set aside and the skeleton
        /// takes its place. Returns false if there is no skeleton (the window stays as it is).
        /// </summary>
        public static bool TakeOver(ContentControl window)
        {
            if (!IsActive || window.Content is not UIElement real) return false;
            if (!Built.Wait(3000) || _root == null || _dispatcher == null) { IsActive = false; return false; }
            _real = real;
            window.Content = null;
            _wrap = new Grid();
            // slot for the real content; above it an empty element that only marks where the skeleton's child window goes
            _wrap.Children.Add(new Border());
            var slot = new FrameworkElement { IsHitTestVisible = false };
            slot.SizeChanged += (_, _) => PlaceChild(slot);
            _wrap.Children.Add(slot);
            window.Content = _wrap;
            if (window is Window w)
            {
                void Create() { var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; if (h != IntPtr.Zero) CreateChild(h); }
                if (new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero) Create();
                else w.SourceInitialized += (_, _) => { StartDiag.Screen("main window without content (background color only)"); Create(); };
            }
            return true;
        }

        /// <summary>action to run when the real content comes in (immediately if there is no skeleton)</summary>
        public static void WhenReal(Action a)
        {
            if (_real == null) a();
            else Deferred.Add(a);
        }

        /// <summary>lets the window work (Windows messages, drawing) between loading steps: without it, Windows would mark it "Not responding"</summary>
        public static void Pump()
        {
            if (_real == null) return;
            try { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); } catch { }
        }

        private static int _showRealCalled;
        private static Action<string>? _reveal;   // set while the Home waits for HomeReady
        private static bool _homeReady;

        /// <summary>called by the Home list when it is loaded: from here the Home can replace the skeleton</summary>
        public static void HomeReady()
        {
            _homeReady = true;
            _reveal?.Invoke("Home ready");
        }

        /// <summary>
        /// The data is ready: the real content goes in under the skeleton; the skeleton is removed as soon as the real content is drawn.
        /// Can be called more than once (later calls do nothing).
        /// </summary>
        public static void ShowReal()
        {
            if (Lemora.Utils.DevOnly.Env("LEMORA_SKELETON_HOLD") == "1") return;   // dev only: the skeleton stays on screen to look at it
            // later calls (the Bootstrapper's safety net, right after the first one) do nothing: before, they closed the skeleton's
            // thread while the Home was still being prepared, and the window stayed empty until the Home was drawn
            if (Interlocked.Exchange(ref _showRealCalled, 1) == 1) return;
            var real = _real; var wrap = _wrap;
            _real = null;
            if (real != null && wrap != null)
            {
                ((Border)wrap.Children[0]).Child = real;
                if (Diag.Enabled)
                {
                    // diagnostics: frame times in the 4 s after the real content comes in (a long gap = UI thread busy)
                    var t0 = DateTime.UtcNow; var last = t0;
                    void Probe(object? s, EventArgs e)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - last).TotalMilliseconds > 100) StartDiag.Mark($"Skeleton: frame after a {(now - last).TotalMilliseconds:0} ms pause");
                        last = now;
                        if ((now - t0).TotalSeconds > 4) CompositionTarget.Rendering -= Probe;
                    }
                    CompositionTarget.Rendering += Probe;
                }
                // the Home is prepared and drawn UNDER the skeleton's child window (Windows always shows a child window above the content
                // of its window): nothing on screen changes until the Home says it is ready (HomeReady), then the child window is closed.
                // Before, the skeleton was a drawing hosted inside the window and Windows stopped showing it while the window was laying out
                // the Home: 1-2 s of empty window
                StartDiag.Mark("Skeleton: Home inserted under the skeleton");
                // safety only: if the Home never says it is ready (another page opened, an error), the skeleton does not stay forever
                var safety = new DispatcherTimer(DispatcherPriority.Normal, wrap.Dispatcher) { Interval = TimeSpan.FromSeconds(6) };
                var done = false;
                void Reveal(string why)
                {
                    if (done) return;
                    done = true;
                    _reveal = null;
                    safety.Stop();
                    StartDiag.Mark($"Skeleton: signal to remove it ({why})");
                    // two frames after the signal: the Home has been drawn at least once under the child window
                    var frames = 0;
                    void OnRendered(object? s, EventArgs e)
                    {
                        if (++frames < 2) return;
                        CompositionTarget.Rendering -= OnRendered;
                        if (wrap.Children.Count > 1) wrap.Children.RemoveAt(1);
                        StartDiag.Mark($"Skeleton: Home drawn underneath ({real.RenderSize.Width:0}x{real.RenderSize.Height:0}), closing the child window");
                        Shutdown($"Home ({why})");
                        wrap.Dispatcher.BeginInvoke(new Action(() => SaveTestShot(wrap)), DispatcherPriority.Background);
                    }
                    CompositionTarget.Rendering += OnRendered;
                }
                safety.Tick += (_, _) => Reveal("safety limit");
                safety.Start();
                _reveal = Reveal;
                if (_homeReady) Reveal("Home already ready");
            }
            else Shutdown(null);
            IsActive = false;
            var list = Deferred.ToArray(); Deferred.Clear();
            foreach (var a in list) a();
        }

        // tests only (LEMORA_SKELETON_SHOT=path): screenshot taken by the app at the moment the skeleton is removed (what is seen right after)
        // and later ones (_NNNms) when the app has no more queued work
        private static void SaveTestShot(FrameworkElement e)
        {
            var file = Lemora.Utils.DevOnly.Env("LEMORA_SKELETON_SHOT");
            if (string.IsNullOrEmpty(file)) return;
            void Save(string path)
            {
                try
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(e);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = System.IO.File.Create(path);
                    enc.Save(fs);
                }
                catch (Exception ex) { StartDiag.Mark("Skeleton: test screenshot failed: " + ex.Message); }
            }
            Save(file);
            foreach (var ms in new[] { 500, 1000, 1500 })
            {
                var t = new DispatcherTimer(DispatcherPriority.Normal, e.Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
                t.Tick += (_, _) => { t.Stop(); Save(System.IO.Path.ChangeExtension(file, null) + $"_{ms}ms.png"); StartDiag.Mark($"Skeleton: test screenshot at {ms} ms"); };
                t.Start();
            }
        }

        /// <summary>closes the skeleton's child window and its thread; screenNow: what the user sees after it (for avvio.log)</summary>
        private static void Shutdown(string? screenNow)
        {
            var d = _dispatcher;
            if (d == null) return;
            StartDiag.Mark($"Skeleton: closing requested (skeleton thread {(d.HasShutdownStarted ? "already closed" : "active")})");
            try
            {
                d.BeginInvoke(new Action(() =>
                {
                    StartDiag.Mark("Skeleton: closing started on its thread");
                    System.Windows.Interop.HwndSource? child;
                    lock (ChildLock) { child = _child; _child = null; _childHwnd = IntPtr.Zero; }
                    try { child?.Dispose(); } catch (Exception e) { StartDiag.Mark("Skeleton: error closing the child window: " + e.Message); }
                    StartDiag.Mark("Skeleton: child window closed");
                    if (screenNow != null) StartDiag.Screen(screenNow);
                    d.BeginInvokeShutdown(DispatcherPriority.Normal);
                }));
            }
            catch { /* thread already closed */ }
        }

        // skeleton parts that change with the window width, as in the real Home (see HomeLayout); touched only from the skeleton thread
        private static ColumnDefinition? _skSideCol, _skDetailCol;
        private static FrameworkElement? _skSearch, _skAdd, _skAddText, _skView, _skHamb, _skCenter;
        private static TextBlock? _skLoading;
        /// <summary>"Loading…" in the app language; call on the skeleton thread, once the language files are loaded (after IoC.Init)</summary>
        private static void SetLoadingText(string text) { if (_skLoading != null) _skLoading.Text = text; }
        private static readonly System.Collections.Generic.List<FrameworkElement> SkUserMenus = new();
        private static readonly System.Collections.Generic.List<FrameworkElement> SkSideTexts = new();   // folder names and counts: hidden in the icon bar

        /// <summary>
        /// Tests only ("responsive" demo): builds a new skeleton on its own thread and saves it to file at each size ("1280x800,640x420").
        /// Call it once the app has started: drawing to file while the main thread creates the application blocks the startup.
        /// </summary>
        public static void SaveSizeShots(string dir, string sizes)
        {
            var loadingText = IoC.Translate("startup_loading");
            var t = new Thread(() =>
            {
                try { var root = Build(); SetLoadingText(loadingText); SaveSizeShots(root, dir, sizes); }
                catch (Exception e) { StartDiag.Mark("Skeleton: test screenshots failed: " + e.Message); }
            }) { IsBackground = true, Name = "SkeletonShots" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(30000);
        }

        private static void SaveSizeShots(FrameworkElement root, string dir, string sizes)
        {
            foreach (var s in sizes.Split(','))
            {
                try
                {
                    var p = s.Split('x');
                    var size = new Size(double.Parse(p[0]), double.Parse(p[1]));
                    ApplyLayout(size.Width);
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"scheletro-{s.Trim()}.png"));
                    enc.Save(fs);
                }
                catch (Exception e) { StartDiag.Mark($"Skeleton: screenshot {s} failed: {e.Message}"); }
            }
        }

        private static void ApplyLayout(double width)
        {
            if (_skSideCol == null || _skDetailCol == null || _skSearch == null || _skAdd == null || _skAddText == null || _skView == null || _skHamb == null) return;
            var mode = Lemora.View.ServerView.HomeLayout.For(width);
            var wide = mode == Lemora.View.ServerView.HomeLayoutMode.Wide;
            var compact = mode is Lemora.View.ServerView.HomeLayoutMode.Small or Lemora.View.ServerView.HomeLayoutMode.Square;
            var square = mode == Lemora.View.ServerView.HomeLayoutMode.Square;
            // folders: full, icons only or hidden (flyout menu closed); details only with a wide window
            _skSideCol.Width = new GridLength(wide ? 230 : compact ? 0 : 64);
            _skDetailCol.Width = new GridLength(wide ? 320 : 0);
            if (_skCenter != null) _skCenter.Margin = new Thickness(compact ? 10 : 0, 0, wide ? 8 : 10, 8);
            _skHamb.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            _skView.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            _skAddText.Visibility = square ? Visibility.Collapsed : Visibility.Visible;
            _skAdd.Width = square ? 44 : wide ? 190 : 130;
            // search: fixed width, or all the free space between the folders button and "Add"
            _skSearch.Width = !compact ? Lemora.View.ServerView.HomeLayout.SearchWidth(mode)
                : Math.Max(120, width - 20 - 44 - 12 - _skAdd.Width - 4);
            foreach (var u in SkUserMenus) u.Visibility = square ? Visibility.Collapsed : Visibility.Visible;
            foreach (var t in SkSideTexts) t.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- the skeleton as a child window of the main window
        // A child window (not a second window: no taskbar, no Alt+Tab, it moves with the main window) with its own thread draws the
        // skeleton; Windows always shows it above the content of the main window, whatever the UI thread is doing underneath
        private static System.Windows.Interop.HwndSource? _child;
        private static IntPtr _childHwnd;
        private static Int32Rect _childRect;   // device pixels, relative to the main window's client area
        private static readonly object ChildLock = new();
        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_EX_NOPARENTNOTIFY = 0x4;
        private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>UI thread: where the child window goes (the slot over the window content), in device pixels; moves it if it exists</summary>
        private static void PlaceChild(FrameworkElement slot)
        {
            try
            {
                if (PresentationSource.FromVisual(slot) is not System.Windows.Interop.HwndSource src || src.RootVisual == null || src.CompositionTarget == null) return;
                var m = src.CompositionTarget.TransformToDevice;
                var tl = m.Transform(slot.TransformToAncestor(src.RootVisual).Transform(new Point(0, 0)));
                var size = m.Transform(new Vector(slot.ActualWidth, slot.ActualHeight));
                var r = new Int32Rect((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Ceiling(size.X), (int)Math.Ceiling(size.Y));
                IntPtr h;
                lock (ChildLock) { _childRect = r; h = _childHwnd; }
                // asynchronous: the UI thread never waits for the skeleton thread
                if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            }
            catch { /* the skeleton keeps its previous size */ }
        }

        /// <summary>UI thread: asks the skeleton thread to create its child window inside the main window</summary>
        private static void CreateChild(IntPtr parent)
        {
            var d = _dispatcher; var root = _root;
            if (d == null || root == null) return;
            StartDiag.Mark("Skeleton: main window created, creating the child window");
            var loadingText = IoC.Translate("startup_loading");   // UI thread, after IoC.Init: the language is loaded
            d.BeginInvoke(new Action(() =>
            {
                try
                {
                    SetLoadingText(loadingText);
                    Int32Rect r;
                    lock (ChildLock) r = _childRect;
                    var p = new System.Windows.Interop.HwndSourceParameters("LemoraSkeleton")
                    {
                        ParentWindow = parent, WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, ExtendedWindowStyle = WS_EX_NOPARENTNOTIFY,
                        PositionX = r.X, PositionY = r.Y, Width = Math.Max(1, r.Width), Height = Math.Max(1, r.Height),
                    };
                    var src = new System.Windows.Interop.HwndSource(p) { SizeToContent = SizeToContent.Manual };
                    if (src.CompositionTarget != null && _parts != null) src.CompositionTarget.BackgroundColor = _parts.Frame.Color;
                    root.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
                    var first = true;
                    src.ContentRendered += (_, _) => { if (first) { first = false; StartDiag.Screen("skeleton"); } };
                    src.RootVisual = root;
                    Int32Rect now;
                    lock (ChildLock) { _child = src; _childHwnd = src.Handle; now = _childRect; }
                    // the window was laid out while the child was being created: move it to the latest place
                    if (!now.Equals(r)) SetWindowPos(src.Handle, IntPtr.Zero, now.X, now.Y, now.Width, now.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                    StartDiag.Mark($"Skeleton: child window created ({now.Width}x{now.Height} px)");
                }
                catch (Exception e) { StartDiag.Mark("Skeleton: child window not created: " + e.Message); }
            }));
        }

        private static bool WindowsUsesLightTheme()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
            }
            catch { return false; }
        }

        private static FrameworkElement Build()
        {
            SkUserMenus.Clear(); SkSideTexts.Clear();   // also built for the test screenshots, on another thread: no pieces of the previous skeleton
            StartDiag.Mark("Skeleton: build 1/6 start");
            var light = WindowsUsesLightTheme();
            Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
            SolidColorBrush B(Color c) => new SolidColorBrush(c);
            var bg = C(light ? "#F3F3F3" : "#171717");
            var panel = C(light ? "#FFFFFF" : "#202020");
            var panel2 = C(light ? "#EDEDED" : "#2B2B2B");
            var bar = C(light ? "#E0E0E0" : "#363636");
            var hi = C(light ? "#F7F7F7" : "#4A4A4A");
            var border = C(light ? "#D6D6D6" : "#2E2E2E");
            var muted = C(light ? "#6B6B6B" : "#A0A0A0");
            var accent = C(light ? "#0F6CBD" : "#4CC2FF");
            var accentSoft = C(light ? "#DCEBF8" : "#2A3B4A");
            // one brush per part of the window: UseTheme recolors them with the colors of the last theme used
            var frameB = B(bg); var sideB = B(bg); var toolbarB = B(bg); var panelB = B(panel); var panel2B = B(panel2); var borderB = B(border);
            var mutedB = B(muted); var accentB = B(accent); var accentSoftB = B(accentSoft);
            StartDiag.Mark("Skeleton: build 2/6 colors ready");

            // a single brush with a moving shimmer: all bars pulse together
            var tt = new TranslateTransform();
            var shimmer = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = tt };
            shimmer.GradientStops.Add(new GradientStop(bar, 0.30));
            shimmer.GradientStops.Add(new GradientStop(hi, 0.50));
            shimmer.GradientStops.Add(new GradientStop(bar, 0.70));
            _parts = new Parts(frameB, sideB, toolbarB, panelB, panel2B, borderB, mutedB, accentB, accentSoftB, shimmer.GradientStops);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1.2, 1.2, TimeSpan.FromMilliseconds(1500)) { RepeatBehavior = RepeatBehavior.Forever });
            StartDiag.Mark("Skeleton: build 3/6 animation started");
            if (Lemora.Utils.DevOnly.Env("LEMORA_SKELETON") == "1")
            {
                // tests only: the shimmer must move even while the UI thread is busy
                var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                probe.Tick += (_, _) => StartDiag.Mark($"Skeleton: shimmer at {tt.X:0.00}");
                probe.Start();
            }

            Rectangle Bar(double width, double height, double radius = 4, Thickness? margin = null) => new Rectangle
            {
                Width = width, Height = height, RadiusX = radius, RadiusY = radius, Fill = shimmer,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0),
            };
            Border Card(Brush fill, double radius, UIElement? child = null) => new Border
            {
                Background = fill, CornerRadius = new CornerRadius(radius), BorderBrush = borderB, BorderThickness = new Thickness(1), Child = child,
            };

            // ---- top bar: logo, tab, "+", window buttons
            var top = new DockPanel { Height = 40, Margin = new Thickness(8, 0, 10, 0), LastChildFill = false };
            var logo = new Rectangle { Width = 26, Height = 26, RadiusX = 6, RadiusY = 6, Fill = shimmer, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(logo, Dock.Left); top.Children.Add(logo);
            var tab = new Border { Width = 176, Height = 34, Background = panel2B, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, Child = Bar(60, 11, 4, new Thickness(14, 0, 0, 0)) };
            DockPanel.SetDock(tab, Dock.Left); top.Children.Add(tab);
            var plus = Bar(12, 12, 3, new Thickness(18, 0, 0, 0)); DockPanel.SetDock(plus, Dock.Left); top.Children.Add(plus);
            var winBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 3; i++) winBtns.Children.Add(Bar(11, 11, 2, new Thickness(i == 0 ? 0 : 34, 0, i == 2 ? 6 : 0, 0)));
            DockPanel.SetDock(winBtns, Dock.Right); top.Children.Add(winBtns);

            // ---- toolbar: search (with type selector), "Add", view switch
            var search = new Grid { Width = 440, Height = 34, HorizontalAlignment = HorizontalAlignment.Left };
            var searchBox = Card(panelB, 4); search.Children.Add(searchBox);
            var searchText = Bar(120, 9, 4, new Thickness(10, 0, 0, 0)); search.Children.Add(searchText);
            // protocol chip of the quick connection: accent colored, as in the Home
            var onAccent = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            var pill = new Border { Width = 74, Height = 24, CornerRadius = new CornerRadius(3), Background = accentB, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0),
                Child = new Rectangle { Width = 34, Height = 9, RadiusX = 4, RadiusY = 4, Fill = onAccent } };
            search.Children.Add(pill);
            var addText = new Rectangle { Width = 90, Height = 10, RadiusX = 4, RadiusY = 4, Fill = onAccent };
            // "Add" is a split button: text, thin divider, arrow on the right
            var addInner = new Grid();
            addInner.ColumnDefinitions.Add(new ColumnDefinition());
            addInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            addInner.Children.Add(addText);
            var addArrow = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), BorderThickness = new Thickness(1, 0, 0, 0),
                Child = new Rectangle { Width = 8, Height = 5, RadiusX = 1, RadiusY = 1, Fill = onAccent } };
            Grid.SetColumn(addArrow, 1); addInner.Children.Add(addArrow);
            var add = new Border { Width = 190, Height = 34, CornerRadius = new CornerRadius(4), Background = accentB, Margin = new Thickness(12, 0, 0, 0), Child = addInner };
            // on the right two segmented switches (All/RDP/VNC and List/Card/Tree), first segment selected
            var view = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (segs, gap) in new[] { (new[] { 64.0, 59, 60 }, 10.0), (new[] { 54.0, 58, 60 }, 0.0) })
            {
                var group = new StackPanel { Orientation = Orientation.Horizontal };
                for (var i = 0; i < segs.Length; i++)
                    group.Children.Add(new Border { Width = segs[i], Height = 34, Background = i == 0 ? accentSoftB : Brushes.Transparent, CornerRadius = new CornerRadius(3),
                        Child = new Rectangle { Width = segs[i] - 30, Height = 9, RadiusX = 4, RadiusY = 4, Fill = shimmer } });
                var g = Card(Brushes.Transparent, 4, group); g.Margin = new Thickness(0, 0, gap, 0); g.VerticalAlignment = VerticalAlignment.Center;
                view.Children.Add(g);
            }
            // narrow window: folders button to the left of the search
            var hamb = Card(panelB, 4, Bar(14, 10, 2, new Thickness(9, 0, 0, 0))); hamb.Width = 34; hamb.Height = 34; hamb.Margin = new Thickness(0, 0, 10, 0); hamb.Visibility = Visibility.Collapsed;
            var toolbar = new DockPanel { Height = 56, Margin = new Thickness(10, 0, 10, 0), LastChildFill = false };
            DockPanel.SetDock(view, Dock.Right); toolbar.Children.Add(view);
            DockPanel.SetDock(hamb, Dock.Left); toolbar.Children.Add(hamb);
            _skSearch = search; _skAdd = add; _skAddText = addText; _skView = view; _skHamb = hamb;
            DockPanel.SetDock(search, Dock.Left); toolbar.Children.Add(search);
            DockPanel.SetDock(add, Dock.Left); toolbar.Children.Add(add);

            StartDiag.Mark("Skeleton: build 4/6 top bars ready");
            // ---- left column: "Home" selected, folders (square + name + number), "New folder", "Options" at the bottom
            var side = new Grid { Margin = new Thickness(10, 0, 0, 10) };
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition());
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sideList = new StackPanel { Margin = new Thickness(4, 8, 4, 0) };
            // "Home" and "No folder" (with its number) are plain lines without a card; nothing selected yet
            var homeText = Bar(46, 11, 4, new Thickness(18, 0, 0, 0)); SkSideTexts.Add(homeText);
            sideList.Children.Add(new Border { Height = 46, Child = homeText });
            var noFolder = new Grid { Height = 46, Margin = new Thickness(0, 0, 0, 6) };
            var noFolderText = Bar(100, 11, 4, new Thickness(18, 0, 0, 0)); noFolder.Children.Add(noFolderText); SkSideTexts.Add(noFolderText);
            var noFolderCount = Bar(8, 10, 3, new Thickness(0, 0, 16, 0)); noFolderCount.HorizontalAlignment = HorizontalAlignment.Right; noFolder.Children.Add(noFolderCount); SkSideTexts.Add(noFolderCount);
            sideList.Children.Add(noFolder);
            var folderWidths = new[] { 68.0, 88, 96, 72, 70, 60, 84, 64 };
            foreach (var fw in folderWidths)
            {
                var row = new Grid { Height = 46, Margin = new Thickness(0, 0, 0, 4) };
                var rowBg = Card(panelB, 4); row.Children.Add(rowBg);
                var inner = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                inner.Children.Add(new Rectangle { Width = 22, Height = 22, RadiusX = 4, RadiusY = 4, Fill = shimmer, Margin = new Thickness(0, 0, 12, 0) });
                var name = Bar(fw, 11); inner.Children.Add(name); SkSideTexts.Add(name);
                row.Children.Add(inner);
                var count = Bar(10, 10, 3, new Thickness(0, 0, 16, 0)); count.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(count); SkSideTexts.Add(count);
                sideList.Children.Add(row);
            }
            // "+ New folder" in the accent color, as the link in the Home
            var newFolder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 16, 0, 0) };
            newFolder.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = accentB, Margin = new Thickness(0, 0, 10, 0) });
            newFolder.Children.Add(new Rectangle { Width = 88, Height = 9, RadiusX = 4, RadiusY = 4, Fill = accentB, Opacity = 0.6 });
            sideList.Children.Add(newFolder); SkSideTexts.Add(newFolder);
            sideList.ClipToBounds = true;
            side.Children.Add(sideList);
            Grid.SetRowSpan(sideList, 2);
            // "Options" with its shortcut, under a divider
            var options = new Grid { Height = 52, Background = sideB };
            options.Children.Add(new Border { Height = 1, Background = borderB, VerticalAlignment = VerticalAlignment.Top });
            var optText = Bar(50, 10, 4, new Thickness(18, 0, 0, 0)); options.Children.Add(optText); SkSideTexts.Add(optText);
            var optKey = Bar(30, 9, 4, new Thickness(0, 0, 18, 0)); optKey.HorizontalAlignment = HorizontalAlignment.Right; options.Children.Add(optKey); SkSideTexts.Add(optKey);
            Grid.SetRow(options, 2); side.Children.Add(options);
            var sideCard = Card(sideB, 6, side); sideCard.Margin = new Thickness(8, 0, 8, 8);   // 8 px gap to the list, as in the Home sideCard.BorderBrush = borderB;

            // ---- center area: "history" title and rows (star, type, two texts, user menu, arrow)
            var rows = new StackPanel { ClipToBounds = true, Margin = new Thickness(0, 0, 0, 0) };
            // rows start at the top of the card, as in the Home (no title above them): star outline, protocol, name and address,
            // on the right a user menu (or a plain user name, or nothing), a thin divider and the arrow
            var titleW = new[] { 70.0, 130, 110, 100, 80, 90, 70, 130, 120, 90, 110, 100, 70, 120, 100, 90, 110, 80 };
            var starGeo = Geometry.Parse("M8,0.8 L10.2,5.4 15.2,6 11.5,9.4 12.5,14.4 8,11.9 3.5,14.4 4.5,9.4 0.8,6 5.8,5.4 Z");
            for (var i = 0; i < titleW.Length; i++)
            {
                var row = new Grid { Height = 53 };
                var star = new System.Windows.Shapes.Path { Data = starGeo, Width = 17, Height = 17, Stretch = Stretch.Uniform, Stroke = shimmer, StrokeThickness = 1.6,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
                var proto = Bar(28, 10, 4, new Thickness(41, 0, 0, 0));
                var lines = new StackPanel { Margin = new Thickness(111, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var l1 = Bar(titleW[i], 13, 4, new Thickness(0, 0, 0, 9)); lines.Children.Add(l1); lines.Children.Add(Bar(100 + (i * 7) % 20, 8));
                row.Children.Add(star); row.Children.Add(proto); row.Children.Add(lines);
                var kind = i % 4;   // 0-1: user menu, 2: plain user name, 3: nothing
                if (kind < 2)
                {
                    var menu = new Grid();
                    menu.Children.Add(Bar(70 + kind * 20, 9, 4, new Thickness(12, 0, 0, 0)));
                    var arrow = Bar(7, 4, 1, new Thickness(0, 0, 11, 0)); arrow.HorizontalAlignment = HorizontalAlignment.Right; menu.Children.Add(arrow);
                    var userMenu = Card(Brushes.Transparent, 4, menu);
                    userMenu.Width = 118 + kind * 20; userMenu.Height = 28; userMenu.HorizontalAlignment = HorizontalAlignment.Right; userMenu.Margin = new Thickness(0, 0, 79, 0);
                    row.Children.Add(userMenu);
                    SkUserMenus.Add(userMenu);
                }
                else if (kind == 2)
                {
                    var user = Bar(84, 9, 4, new Thickness(0, 0, 85, 0)); user.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(user);
                    SkUserMenus.Add(user);
                }
                row.Children.Add(new Rectangle { Width = 1, Height = 28, Fill = borderB, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 61, 0) });
                var chevron = Bar(5, 9, 1, new Thickness(0, 0, 33, 0)); chevron.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(chevron);
                rows.Children.Add(row);
                rows.Children.Add(new Border { Height = 1, Background = borderB, Opacity = 0.55 });   // row dividers are lighter than the borders
            }
            var centerCard = Card(panelB, 6, rows); centerCard.Margin = new Thickness(0, 0, 8, 8); centerCard.ClipToBounds = true;

            // ---- details panel on the right: icon and title, big button, two rows of buttons, table
            var detail = new StackPanel { Margin = new Thickness(20, 30, 18, 0) };
            // protocol square and name: the name sits on the top line of the square, as in the Home
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var protoBox = Card(frameB, 4, Bar(26, 9, 4, new Thickness(10, 0, 0, 0))); protoBox.Width = 47; protoBox.Height = 47;
            head.Children.Add(protoBox);
            var titleBar = Bar(104, 20, 5, new Thickness(17, 3, 0, 0)); titleBar.VerticalAlignment = VerticalAlignment.Top;
            head.Children.Add(titleBar);
            detail.Children.Add(head);
            detail.Children.Add(new Border { Height = 30 });
            // "Connect" with its menu button beside it
            var connect = new Grid { Height = 30 };
            connect.ColumnDefinitions.Add(new ColumnDefinition());
            connect.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            connect.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = accentB, Child = new Rectangle { Width = 60, Height = 9, RadiusX = 4, RadiusY = 4, Fill = onAccent } });
            var connectMenu = Card(Brushes.Transparent, 3, Bar(7, 4, 1, new Thickness(12, 0, 0, 0))); connectMenu.Width = 32; connectMenu.Height = 32; connectMenu.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(connectMenu, 1); connect.Children.Add(connectMenu);
            detail.Children.Add(connect);
            detail.Children.Add(new Border { Height = 11 });
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            // buttons: Edit, Duplicate, Copy; Export, Delete (outlined, as in the Home)
            foreach (var bw in new[] { 83.0, 75, 80 }) btnRow.Children.Add(Card(Brushes.Transparent, 3, Bar(bw - 36, 9, 4, new Thickness(18, 0, 0, 0))) is Border bb ? WithSize(bb, bw, 32, 7) : null!);
            detail.Children.Add(btnRow);
            detail.Children.Add(new Border { Height = 8 });
            var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var bw in new[] { 91.0, 73 }) btnRow2.Children.Add(Card(Brushes.Transparent, 3, Bar(bw - 36, 9, 4, new Thickness(18, 0, 0, 0))) is Border bb ? WithSize(bb, bw, 32, 7) : null!);
            detail.Children.Add(btnRow2);
            detail.Children.Add(new Border { Height = 16 });
            // table: label on the left, value from the same column (kept inside the card)
            var table = new StackPanel();
            var valueW = new[] { 110.0, 0, 30, 90 };
            for (var i = 0; i < 4; i++)
            {
                var tr = new Grid { Height = 51, ClipToBounds = true };
                tr.Children.Add(Bar(i == 3 ? 88 : 72, 10, 4, new Thickness(16, 0, 0, 0)));
                if (valueW[i] > 0) tr.Children.Add(Bar(valueW[i], 10, 4, new Thickness(128, 0, 0, 0)));
                table.Children.Add(tr);
                if (i < 3) table.Children.Add(new Border { Height = 1, Background = borderB });
            }
            detail.Children.Add(Card(panelB, 4, table));
            var detailCard = Card(panelB, 6, detail); detailCard.Margin = new Thickness(0, 0, 10, 8);

            var body = new Grid();
            _skSideCol = new ColumnDefinition { Width = new GridLength(230) };
            _skDetailCol = new ColumnDefinition { Width = new GridLength(320) };
            body.ColumnDefinitions.Add(_skSideCol);
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(_skDetailCol);
            _skCenter = centerCard;
            body.Children.Add(sideCard);
            Grid.SetColumn(centerCard, 1); body.Children.Add(centerCard);
            Grid.SetColumn(detailCard, 2); body.Children.Add(detailCard);

            StartDiag.Mark("Skeleton: build 5/6 columns ready");
            // the text comes later (SetLoadingText): the skeleton is built before the language files are loaded
            var loading = _skLoading = new TextBlock { Foreground = mutedB, FontSize = 12, Margin = new Thickness(14, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            var progress = new ProgressBar { IsIndeterminate = true, Height = 3, Foreground = accentB, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            StartDiag.Mark("Skeleton: build 6/6 text and progress bar ready");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(top);
            // the toolbar on its own band, as in the app (until the theme arrives it has the frame color: not visible)
            toolbar.Margin = new Thickness(10, 0, 10, 0); toolbar.Height = 54;
            var toolbarCard = new Border { Background = toolbarB, BorderBrush = borderB, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 10, 6), Child = toolbar };
            Grid.SetRow(toolbarCard, 1); root.Children.Add(toolbarCard);
            Grid.SetRow(body, 2); root.Children.Add(body);
            Grid.SetRow(loading, 3); root.Children.Add(loading);
            Grid.SetRow(progress, 4); root.Children.Add(progress);

            // fills the whole main window (same position and size: it is the same window)
            var page = new Border { Background = frameB, Child = root };
            TextElement.SetFontFamily(page, new FontFamily("Segoe UI Variable Text, Segoe UI"));
            lock (ThemeLock) { if (_themeColors is { } early) Paint(early); }   // the theme arrived while the skeleton was being built
            return page;
        }

        // ---------------------------------------------------------------- colors of the last theme used
        private record Parts(SolidColorBrush Frame, SolidColorBrush Side, SolidColorBrush Toolbar, SolidColorBrush Panel, SolidColorBrush Panel2,
            SolidColorBrush Border, SolidColorBrush Muted, SolidColorBrush Accent, SolidColorBrush AccentSoft, GradientStopCollection Shimmer);
        private static Parts? _parts;
        private static Color[]? _themeColors;   // arrived before the skeleton was ready: applied at the end of Build
        private static readonly object ThemeLock = new();

        /// <summary>
        /// the theme saved at the last closing (read with the configuration, before the window appears): the skeleton takes its colors,
        /// so it looks like the Home that comes after it. Called from the UI thread, applied on the skeleton's own thread.
        /// </summary>
        public static void UseTheme(Lemora.Service.ThemeConfig theme)
        {
            if (!IsActive) return;
            var colors = ColorsFor(theme);
            lock (ThemeLock)
            {
                if (_parts == null || _dispatcher == null) { _themeColors = colors; return; }
            }
            _dispatcher.BeginInvoke(new Action(() => Paint(colors)));
        }

        /// <summary>tests only ("skeleton-themes" demo): the skeleton in the colors of each theme, saved as scheletro-tema-NAME.png (1280x800)</summary>
        public static void SaveThemeShots(string dir, System.Collections.Generic.IEnumerable<(string Name, Lemora.Service.ThemeConfig Theme)> themes)
        {
            var loadingText = IoC.Translate("startup_loading");
            var t = new Thread(() =>
            {
                foreach (var (name, theme) in themes)
                {
                    try
                    {
                        var root = Build(); SetLoadingText(loadingText); Paint(ColorsFor(theme));
                        var size = new Size(1280, 800);
                        ApplyLayout(size.Width); root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(root);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"scheletro-tema-{name}.png"));
                        enc.Save(fs);
                    }
                    catch (Exception e) { StartDiag.Mark($"Skeleton: screenshot of theme {name} failed: {e.Message}"); }
                }
            }) { IsBackground = true, Name = "SkeletonThemeShots" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(60000);
        }

        private static Color[] ColorsFor(Lemora.Service.ThemeConfig theme)
        {
            var k = Lemora.Service.ThemeService.TokensOf(theme);
            var dark = Lemora.Service.ThemeService.IsDark(theme);
            Color C(string hex) => Shawn.Utils.Wpf.ColorAndBrushHelper.HexColorToMediaColor(hex.Length == 7 ? "#FF" + hex[1..] : hex);
            // colors with their own transparency (dark themes) become solid over the surface they sit on
            Color On(string hex, Color under) { var c = C(hex); double a = c.A / 255.0; byte M(byte x, byte y) => (byte)Math.Round(x * a + y * (1 - a)); return Color.FromRgb(M(c.R, under.R), M(c.G, under.G), M(c.B, under.B)); }
            Color Mix(Color fg, Color bg, double a) { byte M(byte x, byte y) => (byte)Math.Round(x * a + y * (1 - a)); return Color.FromRgb(M(fg.R, bg.R), M(fg.G, bg.G), M(fg.B, bg.B)); }
            var content = C(k.Content); var nav = C(k.Nav); var text = C(k.Text);
            return new[]
            {
                C(k.Frame), nav, C(k.Bar), content, On(k.Hover, content), On(k.Border, content), On(k.Text2, content), C(k.Accent), On(k.Selected, nav),
                Mix(text, content, 0.12), Mix(text, content, dark ? 0.22 : 0.04),   // pulsing bars: base and highlight
            };
        }

        private static void Paint(Color[] c)
        {
            var p = _parts; if (p == null) return;
            p.Frame.Color = c[0]; p.Side.Color = c[1]; p.Toolbar.Color = c[2]; p.Panel.Color = c[3]; p.Panel2.Color = c[4];
            p.Border.Color = c[5]; p.Muted.Color = c[6]; p.Accent.Color = c[7]; p.AccentSoft.Color = c[8];
            p.Shimmer[0].Color = c[9]; p.Shimmer[1].Color = c[10]; p.Shimmer[2].Color = c[9];
            StartDiag.Mark("Skeleton: theme colors applied");
        }

        private static Border WithSize(Border b, double width, double height, double rightMargin)
        {
            b.Width = width; b.Height = height; b.Margin = new Thickness(0, 0, rightMargin, 0);
            return b;
        }
    }
}
