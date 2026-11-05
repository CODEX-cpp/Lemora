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
    /// "Scheletro" dell'avvio: la struttura della Home (scheda, ricerca, cartelle, cronologia, dettagli) con i testi ancora vuoti che
    /// pulsano, disegnata DENTRO la finestra principale (una sola finestra) mentre l'app carica i dati.
    /// Lo disegna un thread suo, con un suo Dispatcher, attraverso un HostVisual: continua a pulsare anche quando il thread
    /// dell'interfaccia e' occupato a leggere il database. La finestra principale si mostra appena il database e' aperto, con lo
    /// scheletro al posto del contenuto vero; quando i dati sono pronti il contenuto vero entra sotto lo scheletro e, appena
    /// disegnato, lo scheletro si toglie.
    /// Non si usa in avvio da riga di comando/minimizzato (nessuna finestra in vista) ne' nelle istanze di prova nascoste
    /// (LEMORA_SKELETON=1 lo accende anche li', per le prove).
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

        /// <summary>true dal momento in cui la finestra principale mostra lo scheletro fino a quando entra il contenuto vero</summary>
        public static bool IsActive { get; private set; }

        /// <summary>prepara lo scheletro su un thread suo (si costruisce mentre l'app legge configurazione e database)</summary>
        public static void Start()
        {
            if (Environment.GetEnvironmentVariable("LEMORA_HEADLESS") == "1" && Environment.GetEnvironmentVariable("LEMORA_SKELETON") != "1") return;
            if (Environment.GetEnvironmentVariable("LEMORA_NO_SPLASH") == "1") return;
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try
            {
                var t = new Thread(Run) { IsBackground = true, Name = "StartupSplash" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                IsActive = true;
            }
            catch { /* senza scheletro l'avvio e' solo meno bello */ }
        }

        private static void Run()
        {
            try
            {
                StartDiag.Mark("Scheletro: thread partito");
                _dispatcher = Dispatcher.CurrentDispatcher;
                StartDiag.Mark("Scheletro: dispatcher creato");
                _root = Build();
                StartDiag.Mark("Scheletro: pronto");
            }
            catch { _root = null; }
            finally { Built.Set(); }
            if (_root != null) Dispatcher.Run();
        }

        /// <summary>
        /// Chiamata dal costruttore della finestra principale (thread dell'interfaccia): il contenuto vero si mette da parte e al suo
        /// posto va lo scheletro. Restituisce false se lo scheletro non c'e' (la finestra resta com'e').
        /// </summary>
        public static bool TakeOver(ContentControl window)
        {
            if (!IsActive || window.Content is not UIElement real) return false;
            if (!Built.Wait(3000) || _root == null || _dispatcher == null) { IsActive = false; return false; }
            var host = new SkeletonHost();
            var hv = host.Visual;
            var root = _root;
            _dispatcher.BeginInvoke(new Action(() => { try { new TargetSource(hv).RootVisual = root; } catch { } }));
            _real = real;
            window.Content = null;
            _wrap = new Grid();
            // posto per il contenuto vero gia' pronto sotto lo scheletro: inserirlo dopo spostando lo scheletro lo staccherebbe dallo schermo
            // finche' l'interfaccia non ha finito di disporre la Home (il "vuoto" di 0,5-1,5 s tra scheletro e Home)
            _wrap.Children.Add(new Border());
            _wrap.Children.Add(host);
            window.Content = _wrap;
            return true;
        }

        /// <summary>azione da fare quando entra il contenuto vero (se lo scheletro non c'e', subito)</summary>
        public static void WhenReal(Action a)
        {
            if (_real == null) a();
            else Deferred.Add(a);
        }

        /// <summary>lascia lavorare la finestra (messaggi di Windows, disegno) tra un passo e l'altro del caricamento: senza, Windows la segnerebbe "Non risponde"</summary>
        public static void Pump()
        {
            if (_real == null) return;
            try { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); } catch { }
        }

        /// <summary>
        /// I dati sono pronti: il contenuto vero entra sotto lo scheletro; lo scheletro si toglie appena il contenuto vero e' disegnato.
        /// Si puo' chiamare piu' volte (le successive non fanno niente).
        /// </summary>
        public static void ShowReal()
        {
            var real = _real; var wrap = _wrap;
            _real = null;
            if (real != null && wrap != null)
            {
                ((Border)wrap.Children[0]).Child = real;
                if (Diag.Enabled)
                {
                    // diagnosi: tempi dei fotogrammi nei 4 s dopo l'ingresso del contenuto vero (un vuoto lungo = thread dell'interfaccia occupato)
                    var t0 = DateTime.UtcNow; var last = t0;
                    void Probe(object? s, EventArgs e)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - last).TotalMilliseconds > 100) StartDiag.Mark($"Scheletro: fotogramma dopo {(now - last).TotalMilliseconds:0} ms di pausa");
                        last = now;
                        if ((now - t0).TotalSeconds > 4) CompositionTarget.Rendering -= Probe;
                    }
                    CompositionTarget.Rendering += Probe;
                }
                // il contenuto vero entra trasparente: mentre WPF lo dispone (anche 1 s) a schermo resta lo scheletro;
                // diventa visibile al primo fotogramma dopo la disposizione, e lo scheletro si toglie due fotogrammi dopo
                real.Opacity = 0;
                var frames = 0;
                void OnRendered(object? s, EventArgs e)
                {
                    // Rendering arriva dopo la disposizione degli elementi, prima del disegno
                    frames++;
                    if (frames == 1) { real.Opacity = 1; return; }
                    if (frames < 3) return;
                    CompositionTarget.Rendering -= OnRendered;
                    wrap.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (wrap.Children.Count > 1) wrap.Children.RemoveAt(1);
                        StartDiag.Mark($"Scheletro: tolto, contenuto vero a schermo ({real.RenderSize.Width:0}x{real.RenderSize.Height:0}, visibile={real.IsVisible})");
                        SaveTestShot(wrap);
                        Shutdown();
                    }), DispatcherPriority.Background);
                }
                CompositionTarget.Rendering += OnRendered;
            }
            else Shutdown();
            IsActive = false;
            var list = Deferred.ToArray(); Deferred.Clear();
            foreach (var a in list) a();
        }

        // solo prove (LEMORA_SKELETON_SHOT=percorso): foto fatta dall'app nell'istante in cui lo scheletro si toglie (cio' che si vede subito dopo)
        // e una seconda (_poi) quando l'app non ha piu' lavoro in coda
        private static void SaveTestShot(FrameworkElement e)
        {
            var file = Environment.GetEnvironmentVariable("LEMORA_SKELETON_SHOT");
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
                catch (Exception ex) { StartDiag.Mark("Scheletro: foto di prova non riuscita: " + ex.Message); }
            }
            Save(file);
            foreach (var ms in new[] { 500, 1000, 1500 })
            {
                var t = new DispatcherTimer(DispatcherPriority.Normal, e.Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
                t.Tick += (_, _) => { t.Stop(); Save(System.IO.Path.ChangeExtension(file, null) + $"_{ms}ms.png"); StartDiag.Mark($"Scheletro: foto di prova a {ms} ms"); };
                t.Start();
            }
        }

        private static void Shutdown()
        {
            try { _dispatcher?.InvokeShutdown(); } catch { /* thread gia' chiuso */ }
        }

        private static void Resize(Size size)
        {
            var root = _root;
            if (root == null || size.Width <= 0 || size.Height <= 0) return;
            try { _dispatcher?.BeginInvoke(new Action(() => { ApplyLayout(size.Width); root.Measure(size); root.Arrange(new Rect(size)); })); } catch { }
        }

        // parti dello scheletro che cambiano con la larghezza della finestra, come nella Home vera (vedi HomeLayout); si toccano solo dal thread dello scheletro
        private static ColumnDefinition? _skSideCol, _skDetailCol;
        private static FrameworkElement? _skSearch, _skAdd, _skAddText, _skView, _skHamb, _skCenter;
        private static readonly System.Collections.Generic.List<FrameworkElement> SkUserMenus = new();
        private static readonly System.Collections.Generic.List<FrameworkElement> SkSideTexts = new();   // nomi e conteggi delle cartelle: via nella barra a icone

        /// <summary>
        /// Solo prove (demo "responsive"): costruisce uno scheletro nuovo su un thread suo e lo salva su file a ogni grandezza ("1280x800,640x420").
        /// Va chiamata ad app avviata: disegnare su file mentre il thread principale crea l'applicazione blocca l'avvio.
        /// </summary>
        public static void SaveSizeShots(string dir, string sizes)
        {
            var t = new Thread(() =>
            {
                try { SaveSizeShots(Build(), dir, sizes); }
                catch (Exception e) { StartDiag.Mark("Scheletro: foto di prova non riuscite: " + e.Message); }
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
                catch (Exception e) { StartDiag.Mark($"Scheletro: foto {s} non riuscita: {e.Message}"); }
            }
        }

        private static void ApplyLayout(double width)
        {
            if (_skSideCol == null || _skDetailCol == null || _skSearch == null || _skAdd == null || _skAddText == null || _skView == null || _skHamb == null) return;
            var mode = Lemora.View.ServerView.HomeLayout.For(width);
            var wide = mode == Lemora.View.ServerView.HomeLayoutMode.Wide;
            var compact = mode is Lemora.View.ServerView.HomeLayoutMode.Small or Lemora.View.ServerView.HomeLayoutMode.Square;
            var square = mode == Lemora.View.ServerView.HomeLayoutMode.Square;
            // cartelle: piene, a sole icone o nascoste (menu a scomparsa chiuso); dettaglio solo con la finestra larga
            _skSideCol.Width = new GridLength(wide ? 230 : compact ? 0 : 64);
            _skDetailCol.Width = new GridLength(wide ? 320 : 0);
            if (_skCenter != null) _skCenter.Margin = new Thickness(compact ? 10 : 0, 0, wide ? 8 : 10, 8);
            _skHamb.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            _skView.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            _skAddText.Visibility = square ? Visibility.Collapsed : Visibility.Visible;
            _skAdd.Width = square ? 44 : wide ? 190 : 130;
            // ricerca: larghezza fissa, oppure tutto lo spazio libero tra pulsante delle cartelle e "Aggiungi"
            _skSearch.Width = !compact ? Lemora.View.ServerView.HomeLayout.SearchWidth(mode)
                : Math.Max(120, width - 20 - 44 - 12 - _skAdd.Width - 4);
            foreach (var u in SkUserMenus) u.Visibility = square ? Visibility.Collapsed : Visibility.Visible;
            foreach (var t in SkSideTexts) t.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>elemento della finestra principale che contiene il disegno fatto dall'altro thread</summary>
        private sealed class SkeletonHost : FrameworkElement
        {
            public readonly HostVisual Visual = new();
            public SkeletonHost() { AddVisualChild(Visual); IsHitTestVisible = false; }
            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => Visual;
            protected override Size ArrangeOverride(Size finalSize) { Resize(finalSize); return finalSize; }
        }

        /// <summary>sorgente di presentazione sul thread dello scheletro: serve perche' animazioni e caricamento degli elementi funzionino</summary>
        private sealed class TargetSource : PresentationSource
        {
            private readonly VisualTarget _target;
            public TargetSource(HostVisual host) { _target = new VisualTarget(host); AddSource(); }
            public override Visual RootVisual
            {
                get => _target.RootVisual;
                set { var old = _target.RootVisual; _target.RootVisual = value; RootChanged(old, value); }
            }
            public override bool IsDisposed => false;
            protected override CompositionTarget GetCompositionTargetCore() => _target;
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
            SkUserMenus.Clear(); SkSideTexts.Clear();   // si costruisce anche per le foto di prova, su un altro thread: niente pezzi dello scheletro precedente
            StartDiag.Mark("Scheletro: costruzione 1/6 inizio");
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
            StartDiag.Mark("Scheletro: costruzione 2/6 colori pronti");

            // un solo pennello con riflesso che scorre: tutte le barre pulsano insieme
            var tt = new TranslateTransform();
            var shimmer = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = tt };
            shimmer.GradientStops.Add(new GradientStop(bar, 0.30));
            shimmer.GradientStops.Add(new GradientStop(hi, 0.50));
            shimmer.GradientStops.Add(new GradientStop(bar, 0.70));
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1.2, 1.2, TimeSpan.FromMilliseconds(1500)) { RepeatBehavior = RepeatBehavior.Forever });
            StartDiag.Mark("Scheletro: costruzione 3/6 animazione avviata");
            if (Environment.GetEnvironmentVariable("LEMORA_SKELETON") == "1")
            {
                // solo prove: il riflesso deve muoversi anche mentre il thread dell'interfaccia e' occupato
                var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                probe.Tick += (_, _) => StartDiag.Mark($"Scheletro: riflesso a {tt.X:0.00}");
                probe.Start();
            }

            Rectangle Bar(double width, double height, double radius = 4, Thickness? margin = null) => new Rectangle
            {
                Width = width, Height = height, RadiusX = radius, RadiusY = radius, Fill = shimmer,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0),
            };
            Border Card(Color fill, double radius, UIElement? child = null) => new Border
            {
                Background = B(fill), CornerRadius = new CornerRadius(radius), BorderBrush = B(border), BorderThickness = new Thickness(1), Child = child,
            };

            // ---- barra in alto: logo, scheda, "+", pulsanti della finestra
            var top = new DockPanel { Height = 40, Margin = new Thickness(8, 0, 10, 0), LastChildFill = false };
            var logo = new Ellipse { Width = 24, Height = 24, Fill = shimmer, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(logo, Dock.Left); top.Children.Add(logo);
            var tab = new Border { Width = 176, Height = 34, Background = B(panel2), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, Child = Bar(60, 11, 4, new Thickness(14, 0, 0, 0)) };
            DockPanel.SetDock(tab, Dock.Left); top.Children.Add(tab);
            var plus = Bar(12, 12, 3, new Thickness(18, 0, 0, 0)); DockPanel.SetDock(plus, Dock.Left); top.Children.Add(plus);
            var winBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 3; i++) winBtns.Children.Add(Bar(12, 12, 3, new Thickness(i == 0 ? 0 : 26, 0, 0, 0)));
            DockPanel.SetDock(winBtns, Dock.Right); top.Children.Add(winBtns);

            // ---- barra degli strumenti: ricerca (con selettore del tipo), "Aggiungi", cambio vista
            var search = new Grid { Width = 440, Height = 34, HorizontalAlignment = HorizontalAlignment.Left };
            var searchBox = Card(panel, 4); search.Children.Add(searchBox);
            var searchText = Bar(120, 9, 4, new Thickness(10, 0, 0, 0)); search.Children.Add(searchText);
            var pill = Card(panel2, 4, Bar(40, 9, 4, new Thickness(12, 0, 0, 0))); pill.Width = 74; pill.Height = 26; pill.HorizontalAlignment = HorizontalAlignment.Right; pill.Margin = new Thickness(0, 0, 4, 0);
            search.Children.Add(pill);
            var addText = new Rectangle { Width = 90, Height = 10, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) };
            var add = new Border { Width = 190, Height = 34, CornerRadius = new CornerRadius(4), Background = B(accent), Margin = new Thickness(12, 0, 0, 0), Child = addText };
            var view = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 3; i++) view.Children.Add(Card(i == 0 ? accentSoft : panel, 3, Bar(i == 1 ? 26 : 18, 9, 4, new Thickness(i == 1 ? 14 : 20, 0, 14, 0))));
            // finestra stretta: pulsante delle cartelle a sinistra della ricerca
            var hamb = Card(panel, 4, Bar(14, 10, 2, new Thickness(9, 0, 0, 0))); hamb.Width = 34; hamb.Height = 34; hamb.Margin = new Thickness(0, 0, 10, 0); hamb.Visibility = Visibility.Collapsed;
            var toolbar = new DockPanel { Height = 56, Margin = new Thickness(10, 0, 10, 0), LastChildFill = false };
            DockPanel.SetDock(view, Dock.Right); toolbar.Children.Add(view);
            DockPanel.SetDock(hamb, Dock.Left); toolbar.Children.Add(hamb);
            _skSearch = search; _skAdd = add; _skAddText = addText; _skView = view; _skHamb = hamb;
            DockPanel.SetDock(search, Dock.Left); toolbar.Children.Add(search);
            DockPanel.SetDock(add, Dock.Left); toolbar.Children.Add(add);

            StartDiag.Mark("Scheletro: costruzione 4/6 barre in alto pronte");
            // ---- colonna sinistra: "Home" selezionata, cartelle (quadrato + nome + numero), "Nuova cartella", "Opzioni" in fondo
            var side = new Grid { Margin = new Thickness(10, 0, 0, 10) };
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition());
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sideList = new StackPanel { Margin = new Thickness(4, 8, 4, 0) };
            var homeText = Bar(60, 11, 4, new Thickness(18, 0, 0, 0)); SkSideTexts.Add(homeText);
            sideList.Children.Add(new Border { Height = 38, CornerRadius = new CornerRadius(4), Background = B(accentSoft), Margin = new Thickness(0, 0, 0, 8), Child = homeText });
            var folderWidths = new[] { 84.0, 96, 78, 70, 56, 40 };
            foreach (var fw in folderWidths)
            {
                var row = new Grid { Height = 46, Margin = new Thickness(0, 0, 0, 4) };
                var rowBg = Card(panel, 4); row.Children.Add(rowBg);
                var inner = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                inner.Children.Add(new Rectangle { Width = 22, Height = 22, RadiusX = 4, RadiusY = 4, Fill = shimmer, Margin = new Thickness(0, 0, 12, 0) });
                var name = Bar(fw, 11); inner.Children.Add(name); SkSideTexts.Add(name);
                row.Children.Add(inner);
                var count = Bar(10, 10, 3, new Thickness(0, 0, 16, 0)); count.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(count); SkSideTexts.Add(count);
                sideList.Children.Add(row);
            }
            var newFolder = Bar(110, 10, 4, new Thickness(14, 14, 0, 0)); sideList.Children.Add(newFolder); SkSideTexts.Add(newFolder);
            side.Children.Add(sideList);
            var options = Bar(60, 10, 4, new Thickness(22, 0, 0, 10)); Grid.SetRow(options, 2); side.Children.Add(options); SkSideTexts.Add(options);
            var sideCard = Card(bg, 6, side); sideCard.Margin = new Thickness(8, 0, 0, 8); sideCard.BorderBrush = B(border);

            // ---- zona centrale: titolo "cronologia" e righe (stella, tipo, due testi, menu utente, freccia)
            var rows = new StackPanel { ClipToBounds = true, Margin = new Thickness(0, 0, 0, 0) };
            rows.Children.Add(Bar(80, 9, 4, new Thickness(18, 18, 0, 14)));
            var titleW = new[] { 140.0, 120, 100, 90, 110, 70, 100, 100, 130, 150, 120, 80, 90, 130, 110, 100, 90, 120 };
            for (var i = 0; i < titleW.Length; i++)
            {
                var row = new Grid { Height = 54 };
                var dotStar = new Ellipse { Width = 16, Height = 16, Fill = shimmer, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 0, 0) };
                var proto = Bar(28, 10, 4, new Thickness(46, 0, 0, 0));
                var lines = new StackPanel { Margin = new Thickness(100, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var l1 = Bar(titleW[i], 12, 4, new Thickness(0, 0, 0, 7)); lines.Children.Add(l1); lines.Children.Add(Bar(titleW[i] * 0.7, 9));
                row.Children.Add(dotStar); row.Children.Add(proto); row.Children.Add(lines);
                if (i % 3 != 2)
                {
                    var userMenu = Card(panel2, 4, Bar(70, 9, 4, new Thickness(12, 0, 0, 0)));
                    userMenu.Width = 128; userMenu.Height = 26; userMenu.HorizontalAlignment = HorizontalAlignment.Right; userMenu.Margin = new Thickness(0, 0, 56, 0);
                    row.Children.Add(userMenu);
                    SkUserMenus.Add(userMenu);
                }
                var chevron = Bar(8, 12, 3, new Thickness(0, 0, 22, 0)); chevron.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(chevron);
                rows.Children.Add(row);
                rows.Children.Add(new Border { Height = 1, Background = B(border), Margin = new Thickness(0, 0, 0, 0) });
            }
            var centerCard = Card(panel, 6, rows); centerCard.Margin = new Thickness(0, 0, 8, 8); centerCard.ClipToBounds = true;

            // ---- pannello dettagli a destra: icona e titolo, pulsante grande, due file di pulsanti, tabella
            var detail = new StackPanel { Margin = new Thickness(20, 20, 20, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Card(panel2, 6, null)); ((Border)head.Children[0]).Width = 48; ((Border)head.Children[0]).Height = 48;
            head.Children.Add(Bar(130, 22, 5, new Thickness(18, 0, 0, 0)));
            detail.Children.Add(head);
            detail.Children.Add(new Border { Height = 18 });
            detail.Children.Add(new Border { Height = 30, CornerRadius = new CornerRadius(3), Background = B(accent), Child = new Rectangle { Width = 60, Height = 9, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) } });
            detail.Children.Add(new Border { Height = 10 });
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var bw in new[] { 82.0, 76, 78 }) btnRow.Children.Add(Card(panel2, 3, Bar(bw - 36, 9, 4, new Thickness(18, 0, 0, 0))) is Border bb ? WithSize(bb, bw, 32, 8) : null!);
            detail.Children.Add(btnRow);
            detail.Children.Add(new Border { Height = 8 });
            var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var bw in new[] { 90.0, 74 }) btnRow2.Children.Add(Card(panel2, 3, Bar(bw - 36, 9, 4, new Thickness(18, 0, 0, 0))) is Border bb ? WithSize(bb, bw, 32, 8) : null!);
            detail.Children.Add(btnRow2);
            detail.Children.Add(new Border { Height = 16 });
            var table = new StackPanel();
            for (var i = 0; i < 4; i++)
            {
                var tr = new Grid { Height = 51 };
                tr.Children.Add(Bar(70, 10, 4, new Thickness(16, 0, 0, 0)));
                tr.Children.Add(Bar(i == 0 ? 120 : 80, 10, 4, new Thickness(150, 0, 0, 0)));
                table.Children.Add(tr);
                if (i < 3) table.Children.Add(new Border { Height = 1, Background = B(border) });
            }
            detail.Children.Add(Card(panel, 4, table));
            var detailCard = Card(panel, 6, detail); detailCard.Margin = new Thickness(0, 0, 10, 8);

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

            StartDiag.Mark("Scheletro: costruzione 5/6 colonne pronte");
            var loading = new TextBlock { Text = "Caricamento…", Foreground = B(muted), FontSize = 12, Margin = new Thickness(18, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
            var progress = new ProgressBar { IsIndeterminate = true, Height = 3, Foreground = B(accent), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            StartDiag.Mark("Scheletro: costruzione 6/6 testo e barra di avanzamento pronti");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(top);
            Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
            Grid.SetRow(body, 2); root.Children.Add(body);
            Grid.SetRow(loading, 3); root.Children.Add(loading);
            Grid.SetRow(progress, 4); root.Children.Add(progress);

            // occupa tutta la finestra principale (stessa posizione e grandezza: e' la stessa finestra)
            var page = new Border { Background = B(bg), Child = root };
            TextElement.SetFontFamily(page, new FontFamily("Segoe UI Variable Text, Segoe UI"));
            return page;
        }

        private static Border WithSize(Border b, double width, double height, double rightMargin)
        {
            b.Width = width; b.Height = height; b.Margin = new Thickness(0, 0, rightMargin, 0);
            return b;
        }
    }
}
