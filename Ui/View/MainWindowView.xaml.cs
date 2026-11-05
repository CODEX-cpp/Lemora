using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Lemora.Service;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.View.ServerView;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.WpfResources.Theme.Styles;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Lemora.View
{
    public partial class MainWindowView : WindowChromeBase
    {
        public MainWindowViewModel Vm { get; }
        private readonly ConfigurationService _configurationService;

        private void RefreshTeamsBar()
        {
            TeamsBar.Visibility = Visibility.Visible;
            var link = Lemora.Setup.TeamsLink.Load();
            // l'invio delle connessioni salvate al server si dichiara sempre in modo visibile (TEAMS-DESIGN.md sezione 3)
            TxtTeamsBar.Text = link.StatusText + (link.State == "approved" ? Lemora.Setup.TeamsSync.LastSyncText + Lemora.Setup.TeamsSync.SharingText(link) : "");
            BtnTeamsSync.Visibility = link.State == "approved" ? Visibility.Visible : Visibility.Collapsed;
            if (BtnTeamsSync.IsEnabled) BtnTeamsSync.Content = Lemora.Setup.SetupText.T("tm_sync_now");
        }

        private async void BtnTeamsSync_OnClick(object sender, RoutedEventArgs e)
        {
            BtnTeamsSync.IsEnabled = false;
            BtnTeamsSync.Content = Lemora.Setup.SetupText.T("tm_syncing");
            try { await Lemora.Setup.TeamsLinkService.PollAsync(); await Lemora.Setup.TeamsSync.RunAsync(); }
            catch (Exception ex) { SimpleLogHelper.Warning("Teams: sincronizzazione manuale non riuscita: " + ex.Message); }
            finally { BtnTeamsSync.IsEnabled = true; RefreshTeamsBar(); }
        }

        internal void OpenTeamsConnect()
        {
            var w = new Lemora.Setup.SetupWindow(teams: true) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.ShowDialog();
            // PC installato Locale (o portatile) appena abbinato da Opzioni: parte qui quello che un PC Teams fa all'avvio, senza riavviare l'app
            if (!_teamsStarted) { if (Lemora.Setup.TeamsLink.IsTeams) StartTeams(); }
            else RefreshTeamsBar();
        }

        private bool _teamsStarted;

        private void StartTeams()
        {
            if (_teamsStarted) return;
            _teamsStarted = true;
            RefreshTeamsBar();
            Lemora.Setup.TeamsSync.ShareChanged += () => Dispatcher.BeginInvoke(new Action(RefreshTeamsBar));
            var link = Lemora.Setup.TeamsLink.Load();
            if (link.State == "none" && !link.Skipped) Dispatcher.BeginInvoke(new Action(OpenTeamsConnect), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // controllo del server: ogni 10 secondi finche' la richiesta e' in attesa, poi ogni 5 minuti (permesso cambiato o PC scollegato)
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            bool busy = false;
            var lastSync = DateTime.MinValue;
            Lemora.Setup.TeamsSync.ShowSaved(); // voci condivise: subito la copia salvata, anche senza rete
            async void Check()
            {
                if (busy) return;
                busy = true;
                try
                {
                    if (await Lemora.Setup.TeamsLinkService.PollAsync()) RefreshTeamsBar();
                    // voci condivise: ogni 20 minuti; se il PC non e' piu' collegato la copia si cancella
                    if (Lemora.Setup.TeamsLink.Load().State != "approved" || DateTime.Now - lastSync >= Lemora.Setup.TeamsSync.Period)
                    {
                        await Lemora.Setup.TeamsSync.RunAsync();
                        lastSync = DateTime.Now;
                        RefreshTeamsBar();
                    }
                    timer.Interval = Lemora.Setup.TeamsLink.Load().State == "pending" ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5);
                }
                catch (Exception ex) { SimpleLogHelper.Warning("Teams: controllo del server non riuscito: " + ex.Message); }
                finally { busy = false; }
            }
            timer.Tick += (_, _) => Check();
            timer.Start();
            Check();
        }

        private void TeamsBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenTeamsConnect();


        public MainWindowView(MainWindowViewModel vm, LocalityService localityService, ConfigurationService configurationService)
        {
            Lemora.Utils.StartDiag.Mark("MainWindowView: inizio costruzione");
            InitializeComponent();
            Lemora.Utils.StartDiag.Mark("MainWindowView: XAML caricato");
            ContentRendered += (_, _) => Lemora.Utils.StartDiag.Mark("MainWindowView: primo disegno della finestra");
            // logo dell'interfaccia e icona della finestra: due versioni (fondo scuro / fondo chiaro), si cambiano col tema dell'app e con quello di Windows
            ImgTitleLogo.Source = AppIcons.TitleTile();
            void RefreshTitleLogo() => Dispatcher.BeginInvoke(new Action(() => ImgTitleLogo.Source = AppIcons.TitleTile()));
            ThemeService.ThemeApplied += RefreshTitleLogo;
#if !DEBUG
            Icon = AppIcons.WindowIcon();
            void RefreshWindowIcon(object? s, Microsoft.Win32.UserPreferenceChangedEventArgs e) { if (e.Category == Microsoft.Win32.UserPreferenceCategory.General) Dispatcher.BeginInvoke(new Action(() => Icon = AppIcons.WindowIcon())); }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += RefreshWindowIcon;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= RefreshWindowIcon;
#endif
            Closed += (_, _) => ThemeService.ThemeApplied -= RefreshTitleLogo;
            // finestra stretta: la barra sessione toglie prima l'indirizzo e le scritte dei pulsanti, poi lo stato, invece di andare a capo su due righe
            SessionBarBorder.SizeChanged += (_, e) => SessionBarBorder.Tag = e.NewSize.Width < 860 ? "2" : e.NewSize.Width < 1080 ? "1" : "0";
            Vm = vm;
            InitSplitUi();
            Lemora.Service.AppShortcuts.Install();
            Lemora.Utils.VncKeyForwarder.Install();
            _configurationService = configurationService;
            this.DataContext = Vm;
            Title = Assert.APP_DISPLAY_NAME;
            // restore the window size from 
            this.Width = localityService.MainWindowWidth;
            this.Height = localityService.MainWindowHeight;
            this.WindowState = localityService.MainWindowState;

            this.SizeChanged += (sender, args) =>
            {
                if (this.WindowState == WindowState.Normal)
                {
                    localityService.MainWindowState = this.WindowState;
                    localityService.MainWindowHeight = this.Height;
                    localityService.MainWindowWidth = this.Width;
                    SimpleLogHelper.Debug($"Main window resize to: w = {this.Width}, h = {this.Height}");
                }
            };

            this.LocationChanged += (sender, args) =>
            {
                localityService.MainWindowTop = this.Top;
                localityService.MainWindowLeft = this.Left;
                SimpleLogHelper.Debug($"Main window move to: top = {this.Top}, left = {this.Left}");
            };

            this.StateChanged += (sender, args) =>
            {
                localityService.MainWindowState = this.WindowState;
            };

            WinTitleBar.PreviewMouseDown += WinTitleBar_OnPreviewMouseDown;
            WinTitleBar.PreviewMouseMove += WinTitleBar_OnPreviewMouseMove;

            // PC Teams (installato cosi' o gia' abbinato): barra di stato, sincronizzazione e, al primo avvio, proposta di collegare il server
            // (il controllo si fa al Loaded: IsTeams legge l'abbinamento dal database, come gia' ShowSaved)
            Loaded += (_, _) => { if (!_teamsStarted && Lemora.Setup.TeamsLink.IsTeams) StartTeams(); };

            // Restore or reset window location
            if (double.IsNaN(localityService.MainWindowTop) || double.IsNaN(localityService.MainWindowLeft)
                || localityService.MainWindowTop < SystemParameters.VirtualScreenTop
                || localityService.MainWindowTop + this.Height > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight
                || localityService.MainWindowLeft < SystemParameters.VirtualScreenLeft
                || localityService.MainWindowLeft + this.Width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
            {
                ResetLocation();
            }
            else
            {
                this.Top = localityService.MainWindowTop;
                this.Left = localityService.MainWindowLeft;
            }

            // la X chiude o nasconde la finestra; l'invito a donare/recensire del progetto originale (link all'autore originale) e' stato tolto
            BtnClose.Click += (sender, args) =>
            {
                vm.HideMe();
#if DEBUG
                App.Close();
#else
                switch (IoC.Get<ConfigurationService>().General.CloseButtonBehavior)
                {
                    case (int)GeneralConfig.EnumCloseButtonBehavior.Exit:
                        App.Close();
                        break;
                    case (int)GeneralConfig.EnumCloseButtonBehavior.Minimize:
                        // Minimize to system tray - just hide (le sessioni restano o si chiudono secondo l'impostazione)
                    default:
                        vm.CloseSessionsWhenGoingToBackground();
                        break;
                }
#endif
            };

            // sessioni nelle schede: la vista segue la lista del ViewModel
            Vm.SessionsChanged += SyncSessionLayer;
            // il lampeggio del pulsante nella barra di Windows (lasciato acceso dal client RDP) si spegne quando la finestra torna attiva o una sessione si chiude
            Activated += (_, _) => Lemora.Utils.TaskbarFlash.Stop(this);
            Vm.SessionsChanged += () => Dispatcher.BeginInvoke(() => Lemora.Utils.TaskbarFlash.Stop(this));
            // popup del trasferimento file: allineato al bordo destro della barra sessione, subito sotto
            FtPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width - 4, targetSize.Height - 4), System.Windows.Controls.Primitives.PopupPrimaryAxis.None) };

            BtnMaximize.Click += (sender, args) => this.WindowState = (this.WindowState == WindowState.Normal) ? WindowState.Maximized : WindowState.Normal;
            BtnMinimize.Click += (sender, args) => { this.WindowState = WindowState.Minimized; };

            // avvio: finche' i dati non sono pronti la finestra mostra lo scheletro al posto del contenuto vero (vedi StartupSplash)
            Lemora.Utils.StartupSplash.TakeOver(this);
        }

        /// <summary>
        /// Ricerca e menu del tipo di connessione rapida stanno qui ma si vedono dentro la barra della pagina della lista:
        /// la pagina li sposta a destra di <paramref name="extraLeft"/> e li allarga a <paramref name="width"/> secondo la finestra (Home responsive).
        /// </summary>
        public void PlaceSearch(double extraLeft, double width)
        {
            TbMainFilter.Width = width;
            // casella stretta: suggerimento corto, quello lungo finirebbe sotto il menu del tipo
            TbMainFilter.SetResourceReference(TagProperty, width < 300 ? "search_short" : "Connect/Search");
            TbMainFilter.Margin = new Thickness(18 + extraLeft, 10, 0, 0);
            // il menu del tipo sta dentro la casella, a destra (80 = sua larghezza 74 + margine)
            CbQuickProtocol.Margin = new Thickness(18 + extraLeft + width - 80, 15, 0, 0);
        }

        /// <summary>dimensione dell'area dove si mostrano le sessioni (serve a RDP per partire con la risoluzione giusta)</summary>
        // misura vera dell'area sotto la barra sessione; prima che la sessione sia a video si toglie l'altezza minima della barra (37)
        public Size SessionAreaSize => SessionArea.ActualHeight > 0
            ? new Size(SessionArea.ActualWidth, SessionArea.ActualHeight)
            : new Size(PagingArea.ActualWidth, Math.Max(0, PagingArea.ActualHeight - SessionBarBorder.MinHeight));

        private Lemora.View.FileTransfer.FileTransferViewModel? _ftHooked;

        /// <summary>
        /// Il navigatore dei file prende il posto dello schermo: lo schermo VNC e' un controllo nativo e va nascosto, non basta coprirlo.
        /// Segue la sessione mostrata e il suo modello (si apre dal popup, si chiude con "Torna allo schermo").
        /// </summary>
        private void SyncFileNavigator()
        {
            var ft = Vm.SessionBar?.FileTransfer;
            if (!ReferenceEquals(ft, _ftHooked))
            {
                if (_ftHooked != null) _ftHooked.NavigatorOpenChanged -= OnNavigatorOpenChanged;
                _ftHooked = ft;
                if (ft != null) ft.NavigatorOpenChanged += OnNavigatorOpenChanged;
            }
            var open = ft?.IsNavigatorOpen == true && Vm.ActiveSession != null && !Vm.IsDetached(Vm.ActiveSession);
            FtNavigator.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SessionGrid.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnNavigatorOpenChanged(bool _) => Dispatcher.BeginInvoke(SyncFileNavigator);

        private void SyncSessionLayer()
        {
            var hosts = Vm.SessionHosts;
            foreach (var stale in SessionGrid.Children.OfType<Lemora.View.Host.ProtocolHosts.HostBase>().Where(x => !hosts.Contains(x)).ToList())
                SessionGrid.Children.Remove(stale);
            // le sessioni staccate vivono nella loro finestra, non nella griglia
            foreach (var h in hosts)
                if (!Vm.IsDetached(h) && !SessionGrid.Children.Contains(h))
                {
                    // una sessione passata da schermo intero e scheda puo' risultare ancora figlia di un altro contenitore (log: crash "gia' figlio logico di un altro elemento")
                    switch (h.Parent)
                    {
                        case System.Windows.Controls.Panel p: p.Children.Remove(h); break;
                        case System.Windows.Controls.ContentControl c: c.Content = null; break;
                        case System.Windows.Controls.ContentPresenter cp: cp.Content = null; break;
                        case System.Windows.Controls.Decorator d: d.Child = null; break;
                    }
                    SessionGrid.Children.Add(h);
                }
            LayoutSessions();   // posizioni (singola o divisa), testate e divisore
            // si vede la sessione attiva e, se la scheda e' divisa, anche l'altra
            var shown = Vm.ActiveSession;
            var shownPartner = shown != null ? Vm.SplitPartner(shown) : null;
            foreach (var h in hosts)
                if (!Vm.IsDetached(h)) h.Visibility = ReferenceEquals(h, shown) || (shownPartner != null && ReferenceEquals(h, shownPartner) && !Vm.IsDetached(shown!)) ? Visibility.Visible : Visibility.Collapsed;
            if (_paneAnimating) foreach (var h in new[] { _paneL, _paneR }) if (h != null) h.Visibility = Visibility.Visible;   // separazione in corso: le due sessioni restano visibili fino alla fine
            DetachedPlaceholder.Visibility = Vm.ActiveSession != null && Vm.IsDetached(Vm.ActiveSession) ? Visibility.Visible : Visibility.Collapsed;
            SessionLayer.Visibility = Vm.ActiveSession == null ? Visibility.Collapsed : Visibility.Visible;
            SyncFileNavigator();
        }

        public void ResetLocation()
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var screenEx = ScreenInfoEx.GetCurrentScreenBySystemPosition(ScreenInfoEx.GetMouseSystemPosition());
            // Check the current screen size
            this.WindowState = WindowState.Normal;
            if (this.Width > screenEx.VirtualWorkingArea.Width)
                this.Width = Math.Min(screenEx.VirtualWorkingArea.Width * 0.8, this.Width * 0.8);
            if (this.Height > screenEx.VirtualWorkingArea.Height)
                this.Height = Math.Min(screenEx.VirtualWorkingArea.Height * 0.8, this.Height * 0.8);
            // Place the window in the center of the current screen
            this.Top = screenEx.VirtualWorkingAreaCenter.Y - this.Height / 2;
            this.Left = screenEx.VirtualWorkingAreaCenter.X - this.Width / 2;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (this.ShowInTaskbar)
            {
#if !DEBUG
                // Honor the user's 'close button behavior = Exit' choice for Alt+F4 and the
                // taskbar 'Close window' command too, mirroring BtnClose.Click's Exit branch
                // instead of always hiding to the tray (which leaves a background process
                // with live sessions that the user believes is gone).
                if (IoC.Get<ConfigurationService>().General.CloseButtonBehavior == (int)GeneralConfig.EnumCloseButtonBehavior.Exit)
                {
                    Vm.HideMe(); // clears ShowInTaskbar so App.Close's internal close is not re-intercepted here
                    App.Close();
                    return;
                }
#endif
                Vm.HideMe();
                Vm.CloseSessionsWhenGoingToBackground();
                e.Cancel = true;
            }
            else
            {
                base.OnClosing(e);
            }
        }


        private void CommandFocusFilter_OnExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            SimpleLogHelper.Debug($"CommandFocusFilter_OnExecuted");
            if (Vm.ActiveSession != null) return;   // dentro una sessione Ctrl+F e' del computer remoto
            if (Vm.IsShownList)
            {
                if (Vm.ActiveServerViewModel?.TagListViewModel == null)
                {
                    Vm.MainFilterIsFocused = true;
                }
                else
                {
                    Vm.ActiveServerViewModel.TagsPanelViewModel.FilterIsFocused = true;
                }
            }
        }



        private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (this.DataContext is MainWindowViewModel vm)
            {
                if (Keyboard.FocusedElement is TextBox)
                {
                    //SimpleLogHelper.Debug($"Current FocusedElement is " + textBox.Name);
                }
                else if (e.Key == Key.Escape && vm.IsShownList == false)
                {
                    vm.ShowList(false);
                }
                else if (e.Key != Key.LeftCtrl && e.Key != Key.RightCtrl && vm.IsShownList)
                {
                    if (Vm.ActiveServerViewModel?.TagListViewModel == null)
                    {
                        Vm.MainFilterIsFocused = true;
                        Vm.MainFilterIsFocused = true;
                    }
                    else
                    {
                        Vm.ActiveServerViewModel.TagsPanelViewModel.FilterIsFocused = true;
                        Vm.ActiveServerViewModel.TagsPanelViewModel.FilterIsFocused = true;
                    }
                }
            }
        }

        private void ProcessingRing_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 等待动画实现拖拽
            if (e.ClickCount >= 2)
                return;
            WinTitleBar_OnPreviewMouseDown(sender, e);
        }


        private void QuickProtocolItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort p })
            {
                Vm.QuickProtocol = p;
                CbQuickProtocol.IsChecked = false;
                TbMainFilter.Focus();
            }
        }

        private ServerListPageViewModel? HomePage => Vm.ActiveServerViewModel is ServerListPageViewModel { IsHomeMode: true } p ? p : null;

        /// <summary>
        /// Tasti della ricerca. Frecce su/giu': evidenziano le voci della home (il testo resta scrivibile).
        /// Invio ed Esc agiscono alla PRESSIONE e non al rilascio: la finestra della password si chiude con l'Invio premuto, il fuoco torna qui
        /// e il rilascio di quello stesso Invio riavviava la connessione (finestra password in ciclo, una scheda nuova a ogni giro).
        /// </summary>
        private void MainFilter_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                var page = HomePage;
                if (page == null) return;
                page.MoveHomeSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }
            if (e.IsRepeat || sender is not TextBox) return;   // tenere premuto il tasto non deve ripetere l'azione

            // con una voce evidenziata (frecce) Invio collega quella; Esc toglie prima l'evidenziazione
            if (HomePage is { HasHomeSelection: true } sel)
            {
                if (e.Key == Key.Enter) { sel.ConnectHomeSelection(); e.Handled = true; return; }
                if (e.Key == Key.Escape) { sel.ClearHomeSelection(); return; }
            }
            // Invio con un IP nella ricerca: connessione rapida con il tipo scelto nel menu a tendina
            if (e.Key == Key.Enter)
            {
                Vm.TryQuickConnectFromFilter(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                return;
            }
            // When press Esc, clear all the search keywords, but keep selected tags;
            if (e.Key != Key.Escape) return;
            var s = TagAndKeywordEncodeHelper.DecodeKeyword(Vm.MainFilterString);
            Vm.SetMainFilterString(s.KeyWords.Count == 0 ? null : s.TagFilterList, null);
        }


        public override void WinTitleBar_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm.TopLevelViewModel != null)
                return;
            // sopra una scheda o un pulsante il trascinamento serve a loro (affiancare le sessioni), non a spostare la finestra
            if (e.OriginalSource is DependencyObject src && MyVisualTreeHelper.VisualUpwardSearch<System.Windows.Controls.Primitives.ButtonBase>(src) != null)
            {
                _isDragging = false;
                _mousePosition = new Point(-1, -1);
                return;
            }
            base.WinTitleBar_OnPreviewMouseDown(sender, e);
        }
    }
}