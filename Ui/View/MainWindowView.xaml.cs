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

        private bool _teamsBarWanted;

        /// <summary>the Teams bar exists only with the Teams install and is hidden while a session is shown (the remote screen takes its space)</summary>
        private void ApplyTeamsBarVisibility() =>
            TeamsBar.Visibility = _teamsBarWanted && Vm.ActiveSession == null ? Visibility.Visible : Visibility.Collapsed;

        private void RefreshTeamsBar()
        {
            _teamsBarWanted = true;
            ApplyTeamsBarVisibility();
            var link = Lemora.Setup.TeamsLink.Load();
            // sending the saved connections to the server is always declared visibly (TEAMS-DESIGN.md section 3)
            TxtTeamsBar.Text = link.StatusText + (link.State == "approved" ? Lemora.Setup.TeamsSync.LastSyncText : "");   // no more " · proposes the saved connections to the administrator" (removed on request)
            BtnTeamsSync.Visibility = link.State == "approved" ? Visibility.Visible : Visibility.Collapsed;
            if (BtnTeamsSync.IsEnabled) BtnTeamsSync.Content = Lemora.Setup.SetupText.T("tm_sync_now");
        }

        private async void BtnTeamsSync_OnClick(object sender, RoutedEventArgs e)
        {
            BtnTeamsSync.IsEnabled = false;
            BtnTeamsSync.Content = Lemora.Setup.SetupText.T("tm_syncing");
            try { await Lemora.Setup.TeamsLinkService.PollAsync(); await Lemora.Setup.TeamsSync.RunAsync(); }
            catch (Exception ex) { SimpleLogHelper.Warning("Teams: manual sync failed: " + ex.Message); }
            finally { BtnTeamsSync.IsEnabled = true; RefreshTeamsBar(); }
        }

        internal void OpenTeamsConnect()
        {
            var w = new Lemora.Setup.SetupWindow(teams: true) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.ShowDialog();
            // PC installed as Local (or portable) just paired from Options: start here what a Teams PC does at startup, without restarting the app
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

            // server check: every 10 seconds while the request is pending, then every 5 minutes (permission changed or PC unpaired)
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            bool busy = false;
            var lastSync = DateTime.MinValue;
            Lemora.Setup.TeamsSync.ShowSaved(); // shared entries: show the saved copy right away, even without network
            async void Check()
            {
                if (busy) return;
                busy = true;
                try
                {
                    if (await Lemora.Setup.TeamsLinkService.PollAsync()) RefreshTeamsBar();
                    // shared entries: every 20 minutes; if the PC is no longer paired the copy is deleted
                    if (Lemora.Setup.TeamsLink.Load().State != "approved" || DateTime.Now - lastSync >= Lemora.Setup.TeamsSync.Period)
                    {
                        await Lemora.Setup.TeamsSync.RunAsync();
                        lastSync = DateTime.Now;
                        RefreshTeamsBar();
                    }
                    timer.Interval = Lemora.Setup.TeamsLink.Load().State == "pending" ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5);
                }
                catch (Exception ex) { SimpleLogHelper.Warning("Teams: server check failed: " + ex.Message); }
                finally { busy = false; }
            }
            timer.Tick += (_, _) => Check();
            timer.Start();
            Check();
        }

        private void TeamsBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // the Sync now button, disabled while syncing, passes the click to the bar: it used to open the status window
            if (BtnTeamsSync.IsVisible && new Rect(BtnTeamsSync.RenderSize).Contains(e.GetPosition(BtnTeamsSync))) return;
            OpenTeamsConnect();
        }


        public MainWindowView(MainWindowViewModel vm, LocalityService localityService, ConfigurationService configurationService)
        {
            Lemora.Utils.StartDiag.Mark("MainWindowView: construction starts");
            InitializeComponent();
            Lemora.Utils.StartDiag.Mark("MainWindowView: XAML loaded");
            ContentRendered += (_, _) => Lemora.Utils.StartDiag.Mark("MainWindowView: first render of the window");
            // auto update: look for a new version every time the UI is shown (from the tray to full screen), not when the app starts hidden
            SourceInitialized += (_, _) => Lemora.Setup.TaskbarIdentity.ApplyToWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, Lemora.Setup.SetupEngine.StableExePath);
            IsVisibleChanged += (_, e) => { if (e.NewValue is true) Lemora.Setup.UpdateController.OnInterfaceShown(); };
            // UI logo and window icon: two versions (dark background / light background), switched with the app theme and the Windows theme
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
            // narrow window: the session bar first drops the address and the button labels, then the status, instead of wrapping onto two rows
            SessionBarBorder.SizeChanged += (_, e) => SessionBarBorder.Tag = e.NewSize.Width < 860 ? "2" : e.NewSize.Width < 1080 ? "1" : "0";
            Vm = vm;
            InitSplitUi();
            Lemora.Service.AppShortcuts.Install();
            Lemora.Utils.VncKeyForwarder.Install();
            _configurationService = configurationService;
            this.DataContext = Vm;
            Vm.SessionsChanged += () => Dispatcher.BeginInvoke(new Action(ApplyTeamsBarVisibility));   // going from a session back to Home, the Teams bar reappears
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

            // HideMe (tray) turns the taskbar button off; whatever shows the window again, it must come back
            // (seen: window visible and maximized but missing from the taskbar, owned by WPF's "Hidden Window")
            // deferred: HideMe turns it off while the window is still visible and hides it right after; checked then, it stays off
            void BackToTaskbar() => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && !ShowInTaskbar && !IsClosing) ShowInTaskbar = true; }), System.Windows.Threading.DispatcherPriority.Background);
            IsVisibleChanged += (_, e) => { if (e.NewValue is true) BackToTaskbar(); };
            Activated += (_, _) => BackToTaskbar();

            WinTitleBar.PreviewMouseDown += WinTitleBar_OnPreviewMouseDown;
            WinTitleBar.PreviewMouseMove += WinTitleBar_OnPreviewMouseMove;

            // Teams PC (installed that way or already paired): status bar, sync and, on first start, an offer to connect the server
            // (the check runs on Loaded: IsTeams reads the pairing from the database, as ShowSaved already does)
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

            // the X closes or hides the window; the original project's donate/review prompt (link to the original author) was removed
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
                        // Minimize to system tray - just hide (sessions stay open or close depending on the setting)
                    default:
                        vm.CloseSessionsWhenGoingToBackground();
                        break;
                }
#endif
            };

            // sessions in tabs: the view follows the ViewModel list
            Vm.SessionsChanged += SyncSessionLayer;
            // the taskbar button flashing (left on by the RDP client) stops when the window is activated again or a session closes
            Activated += (_, _) => Lemora.Utils.TaskbarFlash.Stop(this);
            Vm.SessionsChanged += () => Dispatcher.BeginInvoke(() => Lemora.Utils.TaskbarFlash.Stop(this));
            // file transfer popup: aligned to the right edge of the session bar, just below it
            FtPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width - 4, targetSize.Height - 4), System.Windows.Controls.Primitives.PopupPrimaryAxis.None) };

            BtnMaximize.Click += (sender, args) => this.WindowState = (this.WindowState == WindowState.Normal) ? WindowState.Maximized : WindowState.Normal;
            BtnMinimize.Click += (sender, args) => { this.WindowState = WindowState.Minimized; };

            // startup: until the data is ready the window shows the skeleton instead of the real content (see StartupSplash)
            Lemora.Utils.StartupSplash.TakeOver(this);
        }

        /// <summary>
        /// The search box and the quick connection type menu live here but are shown inside the list page bar:
        /// the page moves them right by <paramref name="extraLeft"/> and widens them to <paramref name="width"/> to fit the window (responsive Home).
        /// </summary>
        public void PlaceSearch(double extraLeft, double width)
        {
            TbMainFilter.Width = width;
            // narrow box: short hint, the long one would end up under the type menu
            TbMainFilter.SetResourceReference(TagProperty, width < 300 ? "search_short" : "Connect/Search");
            TbMainFilter.Margin = new Thickness(18 + extraLeft, 10, 0, 0);
            // the type menu sits inside the box, on the right (80 = its width 74 + margin)
            CbQuickProtocol.Margin = new Thickness(18 + extraLeft + width - 80, 15, 0, 0);
        }

        /// <summary>size of the area where sessions are shown (RDP needs it to start with the right resolution)</summary>
        // real size of the area below the session bar; before the session is on screen, the bar's minimum height (37) is subtracted
        public Size SessionAreaSize => SessionArea.ActualHeight > 0
            ? new Size(SessionArea.ActualWidth, SessionArea.ActualHeight)
            : new Size(PagingArea.ActualWidth, Math.Max(0, PagingArea.ActualHeight - SessionBarBorder.MinHeight));

        private Lemora.View.FileTransfer.FileTransferViewModel? _ftHooked;

        /// <summary>
        /// The file navigator takes the place of the screen: the VNC screen is a native control and must be hidden, covering it is not enough.
        /// It follows the shown session and its model (opened from the popup, closed with "Back to screen").
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
            // detached sessions live in their own window, not in the grid
            foreach (var h in hosts)
                if (!Vm.IsDetached(h) && !SessionGrid.Children.Contains(h))
                {
                    // a session moved between full screen and tab can still be a child of another container (log: crash "already the logical child of another element")
                    switch (h.Parent)
                    {
                        case System.Windows.Controls.Panel p: p.Children.Remove(h); break;
                        case System.Windows.Controls.ContentControl c: c.Content = null; break;
                        case System.Windows.Controls.ContentPresenter cp: cp.Content = null; break;
                        case System.Windows.Controls.Decorator d: d.Child = null; break;
                    }
                    SessionGrid.Children.Add(h);
                }
            LayoutSessions();   // positions (single or split), headers and divider
            // the active session is shown and, if the tab is split, the other one too
            var shown = Vm.ActiveSession;
            var shownPartner = shown != null ? Vm.SplitPartner(shown) : null;
            foreach (var h in hosts)
                if (!Vm.IsDetached(h)) h.Visibility = ReferenceEquals(h, shown) || (shownPartner != null && ReferenceEquals(h, shownPartner) && !Vm.IsDetached(shown!)) ? Visibility.Visible : Visibility.Collapsed;
            if (_paneAnimating) foreach (var h in new[] { _paneL, _paneR }) if (h != null) h.Visibility = Visibility.Visible;   // unsplit in progress: both sessions stay visible until the end
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
            if (Vm.ActiveSession != null) return;   // inside a session Ctrl+F belongs to the remote computer
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



        private DateTime _lastKeyDiag;

        private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (this.DataContext is MainWindowViewModel vm)
            {
                // with a session on screen the keys belong to the remote computer: they used to end up in the (hidden) Home search
                if (vm.ActiveSession != null)
                {
                    // if they arrive here, the session does NOT have the keyboard: log it (at most one line every 2 s)
                    if (DateTime.Now - _lastKeyDiag > TimeSpan.FromSeconds(2))
                    {
                        _lastKeyDiag = DateTime.Now;
                        SimpleLogHelper.Debug($"FOCUS-DIAG({vm.ActiveSession.ProtocolServer.DisplayName}): key {e.Key} reached Lemora instead of the session; {Lemora.FocusDiag.Describe()}");
                    }
                    return;
                }
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
            // wait for the animation to implement dragging
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
        /// Search keys. Up/down arrows: highlight the Home entries (the text stays editable).
        /// Enter and Esc act on key PRESS, not release: the password window closes on the Enter press, focus comes back here
        /// and the release of that same Enter restarted the connection (password window in a loop, a new tab every round).
        /// </summary>
        private void ClearMainFilter_OnClick(object sender, RoutedEventArgs e)
        {
            Vm.MainFilterString = "";
            TbMainFilter.Focus();
        }

        private void MainFilter_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // the search is hidden behind the session but still has focus: the key must not land in it, the keyboard goes back to the session
            if (Vm.ActiveSession is { } session)
            {
                SimpleLogHelper.Debug($"FOCUS-DIAG({session.ProtocolServer.DisplayName}): key {e.Key} reached the hidden search, focus sent back to the session; {Lemora.FocusDiag.Describe()}");
                e.Handled = true;
                session.FocusOnMe();
                return;
            }
            if (e.Key is Key.Down or Key.Up)
            {
                var page = HomePage;
                if (page == null) return;
                page.MoveHomeSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }
            if (e.IsRepeat || sender is not TextBox) return;   // holding the key down must not repeat the action

            // with an entry highlighted (arrows) Enter connects it; Esc clears the highlight first
            if (HomePage is { HasHomeSelection: true } sel)
            {
                if (e.Key == Key.Enter) { sel.ConnectHomeSelection(); e.Handled = true; return; }
                if (e.Key == Key.Escape) { sel.ClearHomeSelection(); return; }
            }
            // Enter with an IP in the search: quick connection with the type chosen in the drop-down menu
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
            // over a tab or a button the drag belongs to them (placing sessions side by side), not to moving the window
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