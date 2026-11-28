using Lemora.Model;
using Lemora.Service;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View.Editor;
using Lemora.View.ServerView;
using Lemora.View.Settings;
using Lemora.View.Settings.General;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ServerTreeViewModel = Lemora.View.ServerView.Tree.ServerTreeViewModel;
using SetSelfStartingHelper = Lemora.Utils.SetSelfStartingHelper;

namespace Lemora.View
{
    public enum EnumServerViewStatus
    {
        Card,
        List,
        Tree,
    }

    public enum EnumMainWindowPage
    {
        CardView,
        ListView,
        TreeView,
        About,
        SettingsGeneral,
        SettingsData,
        SettingsCredentialVault,
        SettingsLauncher,
        SettingsTheme,
        SettingsRunners,
        SettingsMcp,
        SettingsKeyMap,
    }
    public class MainWindowViewModel : MaskLayerContainerScreenBase
    {
        public DataSourceService SourceService { get; }
        public ConfigurationService ConfigurationService { get; }
        //public ServerListPageViewModel? ServerListViewModel { get; } = IoC.Get<ServerListPageViewModel>();
        //public ServerTreeViewModel ServerTreeViewModel { get; } = IoC.Get<ServerTreeViewModel>();
        public SettingsPageViewModel SettingViewModel { get; } = IoC.Get<SettingsPageViewModel>();
        public AboutPageViewModel AboutViewModel { get; } = IoC.Get<AboutPageViewModel>();
        private readonly GlobalData _appData;


        #region Properties

        private EnumServerViewStatus _currentView;
        public EnumServerViewStatus CurrentView
        {
            get => _currentView;
            set
            {
                if (SetAndNotifyIfChanged(ref _currentView, value) || ActiveServerViewModel == null)
                {
                    IoC.Get<ConfigurationService>().General.ServerViewStatus = value;
                    IoC.Get<ConfigurationService>().Save();
                    // When switching to TreeView, force rebuild the tree to ensure data is displayed
                    ActiveServerViewModel?.Release();
                    switch (value)
                    {
                        case EnumServerViewStatus.Card:
                        case EnumServerViewStatus.List:
                            {
                                var vm = IoC.Get<ServerListPageViewModel>();
                                ActiveServerViewModel = vm;
                                vm.CurrentViewInListPage = value;
                                break;
                            }
                        case EnumServerViewStatus.Tree:
                            {
                                ActiveServerViewModel = IoC.Get<ServerTreeViewModel>();
                                break;
                            }
                        default:
                            throw new ArgumentOutOfRangeException(nameof(value), value, null);
                    }
                    ActiveServerViewModel?.Attach();   // Release() above removed its events: the same page becomes active again
                    RaisePropertyChanged(nameof(ActiveServerViewModel));
                }
            }
        }

        public ServerPageViewModelBase? ActiveServerViewModel { get; set; }

        private ServerEditorPageViewModel? _editorViewModel = null;
        public ServerEditorPageViewModel? EditorViewModel
        {
            get => _editorViewModel;
            set
            {
                // a new or closed editor: passwords "typed by hand" in the previous editor become protected again (they can only be read back with Windows Hello or password)
                if (!ReferenceEquals(_editorViewModel, value)) Lemora.Controls.PasswordInput.ForgetTyped();
                SetAndNotifyIfChanged(ref _editorViewModel, value);
                RaisePropertyChanged(nameof(IsShownList));
                RefreshTabs();
            }
        }

        // title bar tabs: Home always, then Settings / editor when open
        private readonly System.Collections.ObjectModel.ObservableCollection<MainTabItem> _tabs = new();
        public System.Collections.ObjectModel.ObservableCollection<MainTabItem> Tabs
        {
            get
            {
                if (_tabs.Count == 0) RefreshTabsOnUiThread();
                return _tabs;
            }
        }

        // the editor can be opened from a worker thread: the tab list is only touched from the UI thread
        private void RefreshTabs() => Execute.OnUIThread(RefreshTabsOnUiThread);

        // ---- sessions hosted in the main window (one tab each, browser style)
        private readonly System.Collections.ObjectModel.ObservableCollection<Lemora.View.Host.ProtocolHosts.HostBase> _sessionHosts = new();
        public System.Collections.ObjectModel.ObservableCollection<Lemora.View.Host.ProtocolHosts.HostBase> SessionHosts => _sessionHosts;
        public event Action? SessionsChanged;

        private Lemora.View.Host.ProtocolHosts.HostBase? _activeSession;
        /// <summary>session shown now (null = Home / Settings / editor is shown)</summary>
        public Lemora.View.Host.ProtocolHosts.HostBase? ActiveSession
        {
            get => _activeSession;
            private set
            {
                if (SetAndNotifyIfChanged(ref _activeSession, value))
                {
                    SessionBar = value == null ? null : new SessionBarViewModel(value, this);
                    RefreshTabs();
                    SessionsChanged?.Invoke();
                }
            }
        }

        private SessionBarViewModel? _sessionBar;
        /// <summary>bar with status and buttons of the shown session (null = no session)</summary>
        public SessionBarViewModel? SessionBar
        {
            get => _sessionBar;
            private set => SetAndNotifyIfChanged(ref _sessionBar, value);
        }

        private System.Windows.Threading.DispatcherTimer? _sessionTimer;
        private void EnsureSessionTimer()
        {
            if (_sessionTimer != null) return;
            _sessionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += (s, e) =>
            {
                SessionBar?.Refresh();
                RaisePropertyChanged(nameof(SessionCountText));
            };
            _sessionTimer.Start();
        }

        public string SessionCountText => _sessionHosts.Count == 1 ? IoC.Translate("session_count_one") : string.Format(IoC.Translate("session_count") ?? "{0}", _sessionHosts.Count);

        // ---- detached sessions (normal window or Picture in picture)
        private readonly Dictionary<Lemora.View.Host.ProtocolHosts.HostBase, Lemora.View.Host.DetachedSessionWindow> _detached = new();
        public bool IsDetached(Lemora.View.Host.ProtocolHosts.HostBase host) => _detached.ContainsKey(host);

        public void DetachSession(Lemora.View.Host.ProtocolHosts.HostBase host, bool pip)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (!_sessionHosts.Contains(host)) return;
                if (IsSplit(host)) Unsplit(host);   // a session detached into a window leaves the split view
                Rect? bounds = null;
                if (_detached.TryGetValue(host, out var old))
                {
                    bounds = new Rect(old.Left, old.Top, old.ActualWidth, old.ActualHeight);
                    _detached.Remove(host);
                    old.ReleaseHost();
                }
                // the client changes parent: it must be removed from the tab grid before entering the new window
                if (host.Parent is System.Windows.Controls.Panel panel) panel.Children.Remove(host);
                var win = new Lemora.View.Host.DetachedSessionWindow(host, pip,
                    onReattach: () => ReattachSession(host),
                    onSwitchMode: p => DetachSession(host, p),
                    onCloseSession: () => CloseSessionFromTab(host));
                if (bounds is { } b)
                {
                    win.Left = b.Left; win.Top = b.Top;
                    if (!pip) { win.Width = Math.Max(b.Width, win.MinWidth); win.Height = Math.Max(b.Height, win.MinHeight); }
                }
                else if (View is Window main)
                {
                    // PiP in the bottom-right corner of the work area; the normal window near the main one
                    var wa = SystemParameters.WorkArea;
                    if (pip)
                    {
                        // height following the remote screen's aspect ratio (the PiP frame is 6 units per side)
                        var rs = host.RemoteDesktopSize;
                        if (!rs.IsEmpty && rs.Width > 0) win.Height = Math.Round((380 - 12) * rs.Height / rs.Width + 12);
                        win.Left = wa.Right - 380 - 24; win.Top = wa.Bottom - win.Height - 24;
                    }
                    else { win.Left = main.Left + 40; win.Top = main.Top + 40; win.Width = Math.Min(main.ActualWidth, 1100); win.Height = Math.Min(main.ActualHeight, 700); }
                }
                _detached[host] = win;
                win.Show();
                host.SetParentWindow(win);
                host.SetFitToWindow(pip);   // Picture in picture: the whole remote screen is shown, scaled down
                win.Activate();
                host.FocusOnMe();
                RefreshTabs();
                SessionsChanged?.Invoke();
                SessionBar?.Refresh();
            });
        }

        public void ReattachSession(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (_detached.TryGetValue(host, out var win))
                {
                    _detached.Remove(host);
                    win.ReleaseHost();
                }
                if (View is Shawn.Utils.WpfResources.Theme.Styles.WindowBase main) host.SetParentWindow(main);
                host.SetFitToWindow(false);
                SessionsChanged?.Invoke();
                ActivateSession(host);
                RefreshTabs();
            });
        }

        public void ShowDetachedWindow(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (!_detached.TryGetValue(host, out var win)) return;
                if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                win.Show();
                win.Activate();
                host.FocusOnMe();
            });
        }

        // "Display / advanced settings" panels of an RDP session: one per session and kind, modeless (the effect is visible right away)
        private readonly Dictionary<string, Lemora.View.Host.SessionRdpSettingsDialogViewModel> _rdpSettingsDialogs = new();

        public void ShowSessionRdpSettings(Lemora.View.Host.ProtocolHosts.HostBase host, Lemora.View.Host.SessionRdpSettingsKind kind)
        {
            Execute.OnUIThread(() =>
            {
                if (host.ProtocolServer is not Lemora.Model.Protocol.RDP) return;
                var key = host.ConnectionId + "|" + kind;
                if (_rdpSettingsDialogs.TryGetValue(key, out var open) && !open.IsClosed)
                {
                    (open.View as Window)?.Activate();
                    return;
                }
                var dlg = new Lemora.View.Host.SessionRdpSettingsDialogViewModel(host, kind);
                _rdpSettingsDialogs[key] = dlg;
                dlg.ShowWindow(this);
            });
        }

        public void FullScreenSession(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            if (IsDetached(host)) return;
            IoC.Get<SessionControlService>().MoveSessionToFullScreen(host.ConnectionId);
        }

        /// <summary>adds the session as a new tab, shows it and gives it focus</summary>
        public void AddSessionTab(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            Execute.OnUIThreadSync(() =>
            {
                EnsureSessionTimer();
                // as in a browser: the Home tab opened with "+" becomes the session, no extra Home is left behind
                if (_activeHome != 0 && ActiveSession is null && _extraHomes.Remove(_activeHome)) _activeHome = 0;
                if (!_sessionHosts.Contains(host))
                {
                    _sessionHosts.Add(host);
                    // status changed (connected, disconnected, reconnecting): tab dots and bar update right away, not on the next second
                    host.StatusChanged += () => Execute.OnUIThread(() =>
                    {
                        foreach (var t in _tabs) t.RefreshStatus();
                        SessionBar?.Refresh();
                    });
                }
                // "Split view": the session just opened is placed next to the one it was started from, in the same tab
                var from = _pendingSplitFrom;
                if (from != null && _sessionHosts.Contains(from) && !ReferenceEquals(from, host) && DateTime.Now - _pendingSplitAt < TimeSpan.FromSeconds(30))
                {
                    _pendingSplitFrom = null;
                    LinkSplit(from, host);
                }
                SessionsChanged?.Invoke();
                ActivateSession(host);
            });
        }

        // ---- split view: two sessions side by side in the same tab
        private readonly Dictionary<Lemora.View.Host.ProtocolHosts.HostBase, Lemora.View.Host.ProtocolHosts.HostBase> _split = new();
        private Lemora.View.Host.ProtocolHosts.HostBase? _pendingSplitFrom;
        private DateTime _pendingSplitAt;

        public Lemora.View.Host.ProtocolHosts.HostBase? SplitPartner(Lemora.View.Host.ProtocolHosts.HostBase h) => _split.TryGetValue(h, out var p) ? p : null;
        public bool IsSplit(Lemora.View.Host.ProtocolHosts.HostBase h) => _split.ContainsKey(h);

        /// <summary>name shown on the pane header: if both sessions have the same name the second one is "(2)"</summary>
        public string PaneTitle(Lemora.View.Host.ProtocolHosts.HostBase h)
        {
            var n = h.ProtocolServer.DisplayName;
            var p = SplitPartner(h);
            return p != null && p.ProtocolServer.DisplayName == n && _sessionHosts.IndexOf(h) > _sessionHosts.IndexOf(p) ? n + " (2)" : n;
        }

        private void LinkSplit(Lemora.View.Host.ProtocolHosts.HostBase a, Lemora.View.Host.ProtocolHosts.HostBase b)
        {
            _split[a] = b; _split[b] = a;
            // sessions detached into a window are not split: they go back into the tab
            if (IsDetached(a)) ReattachSession(a);
            if (IsDetached(b)) ReattachSession(b);
        }

        /// <summary>dragging a tab onto another places the two sessions side by side (works for any protocol)</summary>
        public void MergeSessions(Lemora.View.Host.ProtocolHosts.HostBase a, Lemora.View.Host.ProtocolHosts.HostBase b)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (ReferenceEquals(a, b) || IsSplit(a) || IsSplit(b) || !_sessionHosts.Contains(a) || !_sessionHosts.Contains(b)) return;
                LinkSplit(b, a);   // b is the tab it was dropped on: it stays on the left
                RefreshTabs();
                ActivateSession(a);
                SessionBar?.Refresh();
            });
        }

        /// <summary>the two sessions go back to separate tabs</summary>
        public void Unsplit(Lemora.View.Host.ProtocolHosts.HostBase h)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (!_split.TryGetValue(h, out var p)) return;
                _split.Remove(h); _split.Remove(p);
                RefreshTabs();
                SessionsChanged?.Invoke();
                SessionBar?.Refresh();
            });
        }

        /// <summary>"Split view": opens a second session of the same server and places it alongside (if the protocol allows only one, it explains what to do)</summary>
        public void SplitCurrent()
        {
            var h = ActiveSession;
            if (h == null) return;
            if (IsSplit(h)) { Unsplit(h); return; }
            if (h.ProtocolServer.IsOnlyOneInstance())
            {
                Lemora.Service.ToastService.Show(IoC.Translate("session_split_one_instance"));
                return;
            }
            _pendingSplitFrom = h; _pendingSplitAt = DateTime.Now;
            GlobalEventHelper.OnRequestServerConnect?.Invoke(h.ProtocolServer, fromView: "Split");
        }

        /// <summary>closes the split tab: asks for confirmation and closes both sessions</summary>
        private void CloseSplitTab(Lemora.View.Host.ProtocolHosts.HostBase a, Lemora.View.Host.ProtocolHosts.HostBase b)
        {
            if (!App.ExitingFlag && !MessageBoxHelper.Confirm(string.Format(IoC.Translate("session_split_close_text"), a.ProtocolServer.DisplayName, b.ProtocolServer.DisplayName),
                    IoC.Translate("session_split_close_both"), IoC.Translate("Cancel"), IoC.Translate("session_split_close_title"), ownerViewModel: this)) return;
            var svc = IoC.Get<SessionControlService>();
            svc.CloseProtocolHostAsync(a.ConnectionId);
            svc.CloseProtocolHostAsync(b.ConnectionId);
        }

        public void ActivateSession(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (!_sessionHosts.Contains(host)) return;
                ActiveSession = host;
                SessionsChanged?.Invoke();
                if (View is Window w)
                {
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                }
                // focus goes to the content as soon as the tab is visible
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => host.FocusOnMe()), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        public void RemoveSessionTab(string connectionId)
        {
            Execute.OnUIThreadSync(() =>
            {
                var h = _sessionHosts.FirstOrDefault(x => x.ConnectionId == connectionId);
                if (h == null) return;
                if (_detached.TryGetValue(h, out var detachedWin)) { _detached.Remove(h); detachedWin.ReleaseHost(); }
                _sessionHosts.Remove(h);
                var partner = SplitPartner(h);
                if (partner != null) { _split.Remove(h); _split.Remove(partner); }   // one of the two closed: the other stays on its own
                if (ReferenceEquals(ActiveSession, h)) ActiveSession = partner != null && _sessionHosts.Contains(partner) ? partner : null;
                else RefreshTabs();
                SessionsChanged?.Invoke();
            });
        }

        /// <summary>always on top: the main window stays above the others</summary>
        public bool IsPinned => View is Window w && w.Topmost;

        public void TogglePin() => Execute.OnUIThread(() =>
        {
            if (View is Window w) w.Topmost = !w.Topmost;
            SessionBar?.Refresh();
        });

        // ---- keyboard shortcuts (received by Service/AppShortcuts)
        private readonly List<string> _tabHistory = new();   // tab keys, most recently used first
        private int _cycleIndex = -1;                         // >= 0 while Ctrl is held and Tab is pressed

        private string CurrentTabKey() => ActiveSession != null ? "s:" + ActiveSession.ConnectionId : "h:" + _activeHome;

        private bool TabExists(string key) => key.StartsWith("s:") ? _sessionHosts.Any(x => "s:" + x.ConnectionId == key)
                                                                    : key == "h:0" || _extraHomes.Any(x => "h:" + x == key);

        /// <summary>the tab shown now becomes the most recent (not during Ctrl+Tab: there the order is fixed only when Ctrl is released)</summary>
        private void NoteActiveTab()
        {
            if (_cycleIndex >= 0) return;
            var key = CurrentTabKey();
            _tabHistory.Remove(key);
            _tabHistory.Insert(0, key);
        }

        /// <summary>Ctrl+Tab: to the most recently used tab; holding Ctrl and pressing Tab again moves down the list of recently used ones</summary>
        public void CycleTabsByRecent()
        {
            if (_cycleIndex < 0) NoteActiveTab();
            _tabHistory.RemoveAll(k => !TabExists(k));
            if (_tabHistory.Count < 2) return;
            _cycleIndex = _cycleIndex < 0 ? 1 : (_cycleIndex + 1) % _tabHistory.Count;
            var key = _tabHistory[_cycleIndex];
            if (key.StartsWith("s:"))
            {
                var h = _sessionHosts.FirstOrDefault(x => "s:" + x.ConnectionId == key);
                if (h != null) ActivateSession(h);
            }
            else ShowHomeTab(int.Parse(key.Substring(2)));
        }

        public void EndTabCycle()
        {
            if (_cycleIndex < 0) return;
            _cycleIndex = -1;
            NoteActiveTab();
        }

        /// <summary>Ctrl+W / Ctrl+F4: closes the shown tab as its ✕ would (the main Home cannot be closed)</summary>
        public void CloseCurrentTab() => _tabs.FirstOrDefault(x => x.IsActive && x.CanClose)?.CloseCommand?.Execute();

        public void NewHomeTab()
        {
            QuickProtocol = QuickProtocols.FirstOrDefault(x => x.Protocol == Lemora.Model.Protocol.VNC.ProtocolName) ?? QuickProtocols.First();   // a new Home starts again from VNC
            AddHomeTab();
            FocusFilterSoon();
        }

        private void FocusFilterSoon() => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            MainFilterIsFocused = false;
            MainFilterIsFocused = true;
        }), System.Windows.Threading.DispatcherPriority.Background);

        /// <summary>Ctrl+F: cursor in the search (from a session it goes back to Home)</summary>
        public bool FocusSearch()
        {
            if (ActiveSession != null) ShowHomeTab(_activeHome);
            else if (!IsShownList) return false;
            FocusFilterSoon();
            return true;
        }

        /// <summary>Ctrl+N: add server</summary>
        public bool ShortcutAddServer()
        {
            if (ActiveSession != null) ShowHomeTab(_activeHome);
            if (!IsShownList || ActiveServerViewModel == null) return false;
            ActiveServerViewModel.CmdAdd.Execute();
            return true;
        }

        /// <summary>Ctrl+I: settings</summary>
        public bool ShortcutSettings()
        {
            if (ShowSetting) return true;
            if (ActiveSession != null) ActiveSession = null;
            if (!IsShownList) return false;
            CmdGoSysOptionsPage.Execute();
            return true;
        }

        /// <summary>Ctrl+D: toggle favorite for the selected Home row</summary>
        public bool ShortcutFavorite()
        {
            if (ActiveSession != null || !IsShownList) return false;
            return ActiveServerViewModel is Lemora.View.ServerView.ServerListPageViewModel list && list.ToggleFavoriteOfSelection();
        }

        /// <summary>Ctrl+1 in a window (or back into the tabs), Ctrl+2 full screen, Ctrl+3 fit width, Ctrl+4 fit height</summary>
        public bool ShortcutSessionView(Lemora.View.Host.ProtocolHosts.HostBase host, int n)
        {
            switch (n)
            {
                case 1:
                    if (!_sessionHosts.Contains(host)) return false;
                    if (IsDetached(host)) ReattachSession(host); else DetachSession(host, false);
                    return true;
                case 2:
                    if (IsDetached(host) || !host.CanFullScreen || !host.CanResizeNow()) return false;
                    FullScreenSession(host);
                    return true;
                default:
                    return host.SetFitMode(n == 3);
            }
        }

        public void CloseSessionFromTab(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            if (ConfigurationService.General.ConfirmBeforeClosingSession == true
                && App.ExitingFlag == false
                && false == MessageBoxHelper.Confirm(IoC.Translate("Are you sure you want to close the connection?"), ownerViewModel: this))
                return;
            IoC.Get<SessionControlService>().CloseProtocolHostAsync(host.ConnectionId);
        }

        private void RefreshTabsOnUiThread()
        {
            NoteActiveTab();
            _tabs.Clear();
            var onHome = ActiveSession is null && EditorViewModel is null && !_showSetting;
            _tabs.Add(new MainTabItem { Title = "Home", IsActive = onHome && _activeHome == 0, Command = new RelayCommand(_ => ShowHomeTab(0)) });
            var emitted = new HashSet<Lemora.View.Host.ProtocolHosts.HostBase>();
            foreach (var host in _sessionHosts)
            {
                var h = host;
                if (!emitted.Add(h)) continue;
                var partner = SplitPartner(h);
                if (partner != null && _sessionHosts.Contains(partner))
                {
                    // split tab: a single tab with the two sessions side by side
                    emitted.Add(partner);
                    var p = partner;
                    string Name(Lemora.View.Host.ProtocolHosts.HostBase x, Lemora.View.Host.ProtocolHosts.HostBase y) => x.ProtocolServer.DisplayName + (x.ProtocolServer.DisplayName == y.ProtocolServer.DisplayName && _sessionHosts.IndexOf(x) > _sessionHosts.IndexOf(y) ? " (2)" : "");
                    _tabs.Add(new MainTabItem
                    {
                        Title = h.ProtocolServer.DisplayName + " | " + p.ProtocolServer.DisplayName,
                        IsActive = ReferenceEquals(h, ActiveSession) || ReferenceEquals(p, ActiveSession),
                        IsSession = true, IsSplit = true, CanClose = true, Host = h, Seg2Host = p,
                        Seg1Proto = (h.ProtocolServer.ProtocolDisplayName ?? "").ToUpperInvariant(), Seg1Title = Name(h, p), Seg1Active = ReferenceEquals(h, ActiveSession),
                        Seg2Proto = (p.ProtocolServer.ProtocolDisplayName ?? "").ToUpperInvariant(), Seg2Title = Name(p, h), Seg2Active = ReferenceEquals(p, ActiveSession),
                        Seg1Command = new RelayCommand(_ => ActivateSession(h)),
                        Seg2Command = new RelayCommand(_ => ActivateSession(p)),
                        Command = new RelayCommand(_ => ActivateSession(ReferenceEquals(p, ActiveSession) ? p : h)),
                        CloseCommand = new RelayCommand(_ => CloseSplitTab(h, p)),
                    });
                    continue;
                }
                _tabs.Add(new MainTabItem
                {
                    Host = h,
                    Title = (IsDetached(h) ? (_detached[h].IsPip ? "▣ " : "⧉ ") : "") + h.ProtocolServer.DisplayName,
                    IsActive = ReferenceEquals(h, ActiveSession),
                    IsSession = true,
                    CanClose = true,
                    Command = new RelayCommand(_ => ActivateSession(h)),
                    CloseCommand = new RelayCommand(_ => CloseSessionFromTab(h)),
                    CanFullScreen = false, // the full screen button lives in the session bar
                    FullScreenCommand = new RelayCommand(_ =>
                    {
                        if (h.CanFullScreen && h.CanResizeNow())
                            IoC.Get<SessionControlService>().MoveSessionToFullScreen(h.ConnectionId);
                    }),
                });
            }
            // other Home tabs opened with "+": they show the same Home (shared list and search), closed with the X
            foreach (var homeId in _extraHomes)
            {
                var id = homeId;
                _tabs.Add(new MainTabItem
                {
                    Title = "Home",
                    IsActive = onHome && _activeHome == id,
                    CanClose = true,
                    Command = new RelayCommand(_ => ShowHomeTab(id)),
                    CloseCommand = new RelayCommand(_ => CloseHomeTab(id)),
                });
            }
            // "+" at the end of the tabs: opens another Home
            // (moved after Settings / editor, see below)
            // Settings / editor stay open under the sessions; a click on the tab shows them again
            var showUnder = new RelayCommand(_ => { if (ActiveSession != null) ActiveSession = null; });
            if (_showSetting)
                _tabs.Add(new MainTabItem { Title = IoC.Translate("settings_title"), IsActive = ActiveSession is null, CanClose = true, Command = showUnder, CloseCommand = SettingViewModel.CmdSaveAndGoBack });
            if (EditorViewModel != null)
                _tabs.Add(new MainTabItem
                {
                    Title = IoC.Translate(EditorViewModel.IsAddMode ? "editor_title_new" : "Edit"),
                    IsActive = ActiveSession is null,
                    CanClose = true,
                    Command = showUnder,
                    CloseCommand = EditorViewModel.CmdCancel,
                });
            _tabs.Add(new MainTabItem { Title = "+", IsAdd = true, ToolTipText = IoC.Translate("tab_new"), Command = new RelayCommand(_ => AddHomeTab()) });
        }

        private bool _showSetting = false;
        public bool ShowSetting
        {
            get => _showSetting;
            set
            {
                if (SetAndNotifyIfChanged(ref _showSetting, value))
                {
                    RaisePropertyChanged(nameof(IsShownList));
                    RefreshTabs();
                    if (_showSetting == true)
                        _appData.StopTick();
                    else
                        _appData.StartTick();
                }
            }
        }

        public EnumServerOrderBy ServerOrderBy
        {
            get => CurrentView == EnumServerViewStatus.Tree ? LocalityTreeViewService.Settings.ServerOrderBy : LocalityListViewService.Settings.ServerOrderBy;
            set
            {
                if (CurrentView == EnumServerViewStatus.Tree)
                {
                    LocalityTreeViewService.ServerOrderBySet(value);
                }
                else
                {
                    LocalityListViewService.ServerOrderBySet(value);
                }
                RaisePropertyChanged();
            }
        }

        public void SetServerOrderBy(object? o)
        {
            var newOrderBy = EnumServerOrderBy.IdAsc;
            if (int.TryParse(o?.ToString(), out var ot)
                && ot is >= (int)EnumServerOrderBy.IdAsc and <= (int)EnumServerOrderBy.Custom)
            {
                newOrderBy = (EnumServerOrderBy)ot;
            }
            else if (o is EnumServerOrderBy x)
            {
                newOrderBy = x;
            }

            if (newOrderBy is EnumServerOrderBy.IdAsc or EnumServerOrderBy.Custom)
            {
                ServerOrderBy = newOrderBy;
            }
            else
            {
                try
                {
                    // cancel order
                    if (ServerOrderBy == newOrderBy + 1)
                    {
                        newOrderBy = EnumServerOrderBy.IdAsc;
                    }
                    else if (ServerOrderBy == newOrderBy)
                    {
                        newOrderBy += 1;
                    }
                }
                catch
                {
                    newOrderBy = EnumServerOrderBy.IdAsc;
                }
                finally
                {
                    ServerOrderBy = newOrderBy;
                }
            }
        }

        #endregion Properties

        public MainWindowViewModel(GlobalData appData, DataSourceService sourceService, ConfigurationService configurationService)
        {
            _appData = appData;
            SourceService = sourceService;
            ConfigurationService = configurationService;
            CurrentView = IoC.Get<ConfigurationService>().General.ServerViewStatus;
            // Claude takes or releases a session: yellow outline and "AI" mark on the tabs (the event already arrives on the UI thread)
            Lemora.Service.Mcp.McpGuard.ClaudeSessionsChanged += () => { foreach (var t in _tabs) t.RefreshStatus(); };
        }

        public Action? OnMainWindowViewLoaded = null;
        protected override void OnViewLoaded()
        {
            base.OnViewLoaded();
            // at startup the window shows the skeleton before the data is ready: the rest starts when the real content comes in
            Lemora.Utils.StartupSplash.WhenReal(OnRealViewLoaded);
        }

        private void OnRealViewLoaded()
        {
            GlobalEventHelper.OnGoToServerAddPage += async void (tagNames, assignDataSource, preset) =>
            {
                try
                {
                    // a group's "+" passes its source: it is the default destination (before, it was ignored and the entry ended up in Local)
                    var source = preset?.DataSource ?? assignDataSource ?? await DataSourceSelectorViewModel.SelectDataSourceAsync();
#if !DEBUG
                    // use this to test the error message
                    if (source == null)
                    {
                        return;
                    }
#endif
                    if (source?.IsWritable == true)
                    {
                        // when adding from inside a folder, the folder is already chosen in the form (it can be changed)
                        if (ActiveServerViewModel is ServerPageViewModelBase { SelectedFolderPath: { Length: > 0 } open } && open != FolderService.NoFolder
                            && FolderService.Exists(open) && (preset == null || preset.TreeNodes.Count == 0))
                        {
                            preset ??= new ServerEditorPageViewModel.ParamsServerAddPreset();
                            preset.TreeNodes = FolderService.ToNodes(open);
                        }
                        if (preset != null)
                        {
                            preset.TagNames = tagNames ?? new List<string>();
                        }
                        EditorViewModel = ServerEditorPageViewModel.Add(_appData, source, preset);
                        ShowMe();
                    }
                    else
                    {
                        MessageBoxHelper.ErrorAlert($"Debug: Can not add server to DataSource ({source?.DataSourceName ?? "null"}) since it is not writable.");
                    }
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                    UnifyTracing.Error(e, new Dictionary<string, string>()
                    {
                        {"Action", "MainWindowViewModel.OnGoToServerAddPage"}
                    });
                }
            };
            GlobalEventHelper.OnRequestGoToServerDuplicatePage += async void (server, isInAnimationShow) =>
            {
                try
                {
                    // entry from a read-only source (Teams without write): duplicating it would copy the password into a readable local entry
                    if (server.DataSource?.IsWritable == false)
                    {
                        MessageBoxHelper.ErrorAlert(IoC.Translate("server_readonly"));
                        return;
                    }
                    // duplicating does not ask for verification either: in the editor the saved passwords stay hidden behind the padlock
                    var source = await DataSourceSelectorViewModel.SelectDataSourceAsync();
                    if (source == null)
                    {
                        return;
                    }
                    if (source.IsWritable == true)
                    {
                        EditorViewModel = ServerEditorPageViewModel.Duplicate(_appData, source, server);
                        ShowMe();
                    }
                    else
                    {
                        MessageBoxHelper.ErrorAlert($"Can not add server to DataSource ({source?.DataSourceName ?? "null"}) since it is not writable.");
                    }
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                    UnifyTracing.Error(e, new Dictionary<string, string>()
                    {
                        {"Action", "MainWindowViewModel.OnRequestGoToServerDuplicatePage"}
                    });
                }
            };

            GlobalEventHelper.OnRequestGoToServerEditPage += async void (serverToEdit, isInAnimationShow) =>
            {
                var server = _appData.VmItemList.FirstOrDefault(x => x.Id == serverToEdit.Id && x.DataSource == serverToEdit.DataSource)?.Server;
                if (server is not { DataSource: { IsWritable: true } })
                {
                    MessageBoxHelper.ErrorAlert(IoC.Translate("server_readonly"));
                }
                else
                {
                    // opening the editor no longer asks for verification: saved passwords stay hidden and the padlock (eye) asks for it when you want to see them
                    EditorViewModel = ServerEditorPageViewModel.Edit(_appData, server);
                    ShowMe();
                }
            };

            GlobalEventHelper.OnRequestGoToServerMultipleEditPage += async void (servers, isInAnimationShow) =>
            {
                var serverBases = servers.Where(x => x.DataSource is { IsWritable: true, Status: EnumDatabaseStatus.OK }).ToArray();
                EditorViewModel = null;
                EditorViewModel = serverBases.Length switch
                {
                    > 1 => ServerEditorPageViewModel.BuckEdit(_appData, serverBases),
                    1 => ServerEditorPageViewModel.Edit(_appData, serverBases.First()),
                    _ => EditorViewModel
                };
                if (EditorViewModel == null)
                {
                    MessageBoxHelper.ErrorAlert("Can not edit multiple servers since they are all not writable.");
                    return;
                }
                ShowMe();
            };
#if FOR_MICROSOFT_STORE_ONLY
            SetSelfStartingHelper.SetSelfStartByStartupTask(Assert.APP_NAME, null);
#endif
            OnMainWindowViewLoaded?.Invoke();
#if LEMORA_DEVDEMO
            Lemora.Utils.DevDemo.RunIfRequested(); // no-op without LEMORA_HEADLESS=1 + LEMORA_DEMO
#endif

            //var vm = new Lemora.View.Utils.MessageBoxPageViewModel();
            //vm.Setup(messageBoxText: "content",
            //    caption: "title",s
            //    icon: MessageBoxImage.Warning,
            //    buttons: MessageBoxButton.OK,
            //    buttonLabels: new Dictionary<MessageBoxResult, string>()
            //    {
            //        {MessageBoxResult.None, IoC.Translate("OK")},
            //        {MessageBoxResult.Yes, IoC.Translate("OK")},
            //        {MessageBoxResult.OK, IoC.Translate("OK")},
            //    }, onButtonClicked: () =>
            //    {
            //        TopLevelViewModel = null;
            //    });
            //TopLevelViewModel = vm;
        }

        protected override void OnClose()
        {
            App.Close();
        }

        public override void OnShowProcessingRing(long layerId, Visibility visibility, string msg)
        {
            if (this.View is not MainWindowView { IsClosing: false } window) return;
            base.OnShowProcessingRing(layerId, visibility, msg);
        }


        /// <summary>
        /// whoever opens the server editor (e.g. the folder edit page) can ask to return there when the editor closes
        /// with Save or Cancel; with any other exit (Home tab, Settings) the request is discarded
        /// </summary>
        public Action? ReturnFromEditor { get; set; }

        public void ShowList(bool clearSelection, bool runReturn = false)
        {
            if (this.View is not MainWindowView { IsClosing: false } window) return;
            var back = ReturnFromEditor;
            ReturnFromEditor = null;
            EditorViewModel = null;
            if (runReturn && back != null) Execute.OnUIThread(back);
            ShowSetting = false;
            if (clearSelection && ActiveServerViewModel != null)
            {
                ActiveServerViewModel.ClearSelection();
                ActiveServerViewModel.CmdCancelSelected.Execute();
            }
        }


        public bool IsShownList => EditorViewModel is null && ShowSetting == false;


        #region CMD

        private RelayCommand? _cmdGoSysOptionsPage;
        public RelayCommand CmdGoSysOptionsPage
        {
            get
            {
                return _cmdGoSysOptionsPage ??= new RelayCommand((o) =>
                {
                    ShowSetting = true;
                    EditorViewModel = null;
                    IoC.Get<GeneralSettingViewModel>().AppStartAutomatically = SetSelfStartingHelper.IsSelfStart(Assert.APP_NAME);
                    IoC.Get<GeneralSettingViewModel>().RefreshTeams(); // Teams section: pairing, permission or switch may have changed
                }, o => IsShownList);
            }
        }

        // ---- extra Home tabs (the "+"): each one is a tab that shows Home
        private readonly List<int> _extraHomes = new();
        private int _nextHomeId = 1;
        private int _activeHome = 0;   // 0 = the main Home tab
        private readonly Dictionary<int, string> _homeFilters = new();   // each Home tab remembers its search text

        private void ShowHomeTab(int id)
        {
            if (id != _activeHome)
            {
                _homeFilters[_activeHome] = MainFilterString;              // save the filter of the tab being left
                MainFilterString = _homeFilters.TryGetValue(id, out var f) ? f : "";   // new tab = empty
                MainFilterCaretIndex = MainFilterString.Length;
            }
            _activeHome = id;
            CmdTabHome.Execute();
            RefreshTabs();
        }

        private void AddHomeTab()
        {
            var id = _nextHomeId++;
            _extraHomes.Add(id);
            ShowHomeTab(id);
        }

        private void CloseHomeTab(int id)
        {
            _extraHomes.Remove(id);
            _homeFilters.Remove(id);
            if (_activeHome == id) { _activeHome = -1; ShowHomeTab(0); }   // -1: forces restoring the filter of Home 0
            else RefreshTabs();
        }

        // title bar Home tab: from the settings it is the same as "Done"
        private RelayCommand? _cmdTabHome;
        public RelayCommand CmdTabHome
        {
            get
            {
                return _cmdTabHome ??= new RelayCommand((o) =>
                {
                    if (ActiveSession != null) ActiveSession = null;
                    if (ShowSetting)
                        SettingViewModel.CmdSaveAndGoBack.Execute();
                });
            }
        }

        private RelayCommand? _cmdGoAboutPage;
        public RelayCommand CmdGoAboutPage
        {
            get
            {
                return _cmdGoAboutPage ??= new RelayCommand((o) =>
                {
                    MaskLayerController.ShowWindowWithMask(AboutViewModel, this);
                });
            }
        }


        private RelayCommand? _cmdToggleCardView;
        public RelayCommand CmdToggleCardView
        {
            get
            {
                return _cmdToggleCardView ??= new RelayCommand((o) =>
                {
                    CurrentView = EnumServerViewStatus.Card;
                }, o => CurrentView != EnumServerViewStatus.Card);
            }
        }


        private RelayCommand? _cmdToggleListView;
        public RelayCommand CmdToggleListView
        {
            get
            {
                return _cmdToggleListView ??= new RelayCommand((o) =>
                {
                    CurrentView = EnumServerViewStatus.List;
                }, o => CurrentView != EnumServerViewStatus.List);
            }
        }

        private RelayCommand? _cmdToggleTreeView;
        public RelayCommand CmdToggleTreeView
        {
            get
            {
                return _cmdToggleTreeView ??= new RelayCommand((o) =>
                {
                    CurrentView = EnumServerViewStatus.Tree;
                }, o => CurrentView != EnumServerViewStatus.Tree);
            }
        }

        private RelayCommand? _cmdReOrder;
        public RelayCommand CmdReOrder
        {
            get
            {
                return _cmdReOrder ??= new RelayCommand((o) =>
                {
                    SetServerOrderBy(o);
                    ActiveServerViewModel?.ApplySort();
                });
            }
        }

        #endregion CMD




        public void ShowMe(bool isForceActivate = false, EnumMainWindowPage? goPage = null)
        {
            if (this.View is not MainWindowView)
            {
                Execute.OnUIThreadSync(() =>
                {
                    IoC.Get<IWindowManager>().ShowWindow(this);
                });
                return;
            }
            if (this.View is not MainWindowView { IsClosing: false } window) return;

            if (goPage != null)
            {
                switch (goPage)
                {
                    case EnumMainWindowPage.CardView:
                        CurrentView = EnumServerViewStatus.Card;
                        ShowList(false);
                        break;
                    case EnumMainWindowPage.ListView:
                        CurrentView = EnumServerViewStatus.List;
                        ShowList(false);
                        break;
                    case EnumMainWindowPage.TreeView:
                        CurrentView = EnumServerViewStatus.Tree;
                        ShowList(false);
                        break;
                    case EnumMainWindowPage.About:
                        CmdGoAboutPage?.Execute();
                        break;
                    case EnumMainWindowPage.SettingsGeneral:
                    case EnumMainWindowPage.SettingsData:
                    case EnumMainWindowPage.SettingsRunners:
                    case EnumMainWindowPage.SettingsMcp:
                    case EnumMainWindowPage.SettingsKeyMap:
                    case EnumMainWindowPage.SettingsLauncher:
                    case EnumMainWindowPage.SettingsTheme:
                        SettingViewModel.ShowPage((EnumMainWindowPage)goPage);
                        CmdGoSysOptionsPage?.Execute();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(goPage), goPage, null);
                }
            }

            Execute.OnUIThread(() =>
            {
                lock (window)
                {
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    if (window.IsVisible == false)
                    {
                        if (isForceActivate) HideMe();
                        window.Show();
                        window.ShowInTaskbar = true;
                    }
                    // Bring the window to the front even if it is already visible
                    // but covered by other windows (e.g. click on the tray icon,
                    // or pick an action from the launcher).
                    window.Topmost = true;
                    window.Activate();
                    window.Topmost = false;
                    window.Focus();
                }
            });
        }

        /// <summary>
        /// The app goes to the background (X or Alt+F4 with "minimize to tray"): if the user chose not to keep sessions alive, they all close (VNC, RDP, SSH...).
        /// </summary>
        public void CloseSessionsWhenGoingToBackground()
        {
            var general = IoC.Get<ConfigurationService>().General;
            if (general.CloseButtonBehavior == (int)GeneralConfig.EnumCloseButtonBehavior.Minimize && !general.KeepSessionsInBackground)
                IoC.Get<SessionControlService>().CloseAllSessions();
        }

        public void HideMe()
        {
            if (this.View is not MainWindowView { IsClosing: false } window) return;
            if (Shawn.Utils.ConsoleManager.HasConsole)
                Shawn.Utils.ConsoleManager.Hide();
            Execute.OnUIThread(() =>
            {
                window.ShowInTaskbar = false;
                window.Hide();
                window.Visibility = Visibility.Hidden;
                // After startup and initalizing our application and when closing our window and minimize the application to tray we free memory with the following line:
                System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
            });
        }

        private RelayCommand? _cmdExit;
        public RelayCommand CmdExit
        {
            get
            {
                return _cmdExit ??= new RelayCommand((o) =>
                {
                    App.Close();
                });
            }
        }



        #region MainFilter
        private bool _mainFilterIsFocused = false;
        public bool MainFilterIsFocused
        {
            get => _mainFilterIsFocused;
            set => SetAndNotifyIfChanged(ref _mainFilterIsFocused, value);
        }

        private int _mainFilterCaretIndex = 0;
        public int MainFilterCaretIndex
        {
            get => _mainFilterCaretIndex;
            set => SetAndNotifyIfChanged(ref _mainFilterCaretIndex, value);
        }


        private readonly DebounceDispatcher _debounceDispatcher = new();
        private string _mainFilterString = "";
        public string MainFilterString
        {
            get => _mainFilterString;
            set
            {
                // can only be called by the Ui
                if (SetAndNotifyIfChanged(ref _mainFilterString, value))
                {
                    // search changed: the previously highlighted row (click or arrows) no longer applies, Enter starts from what was typed
                    (ActiveServerViewModel as ServerListPageViewModel)?.ClearHomeSelection();
                    // typing in the search leaves Home and shows the filtered list
                    if (!string.IsNullOrEmpty(value) && ActiveServerViewModel is ServerPageViewModelBase homePage && homePage.IsHomeMode
                        && ActiveServerViewModel is not ServerListPageViewModel)   // the list has a filterable Home: stay there
                        homePage.IsHomeMode = false;
                    _debounceDispatcher.Debounce(IoC.Get<GlobalData>().VmItemList.Count > 100 ? 200 : 100, (obj) =>
                    {
                        if (_mainFilterString != MainFilterString) return;
                        // MainFilterString changed -> refresh view source -> calc visible in `ServerListItemSource_OnFilter`
                        ActiveServerViewModel?.CalcServerVisibleAndRefresh();
                    });
                }
            }
        }

        // ---- protocol filter of the server list: "" = all, "RDP", "VNC". Only lasts while the program is open: every start begins from "All"
        // (a forgotten filter hid connections of other types, e.g. SSH, without any clue why)
        private string _protocolFilter = "";
        public string ProtocolFilter
        {
            get => _protocolFilter;
            set
            {
                value = value is "RDP" or "VNC" ? value : "";
                if (string.Equals(_protocolFilter, value, StringComparison.Ordinal)) return;
                _protocolFilter = value;
                RaisePropertyChanged();
                ActiveServerViewModel?.CalcServerVisibleAndRefresh(true);
            }
        }

        private RelayCommand? _cmdSetProtocolFilter;
        public RelayCommand CmdSetProtocolFilter => _cmdSetProtocolFilter ??= new RelayCommand(o => ProtocolFilter = o as string ?? "");

        // ---- quick connection from the search bar: IP (or IP:port) + Enter, with the type chosen in the drop-down menu (VNC by default)
        public List<Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort> QuickProtocols => IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().Protocols;

        private Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort? _quickProtocol;
        public Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort QuickProtocol
        {
            get => _quickProtocol ??= QuickProtocols.FirstOrDefault(x => x.Protocol == Lemora.Model.Protocol.VNC.ProtocolName) ?? QuickProtocols.First();
            set
            {
                if (SetAndNotifyIfChanged(ref _quickProtocol, value))
                {
                    RaisePropertyChanged(nameof(QuickProtocolName));
                    // type chosen in the menu: Enter must use this, not the Home row left highlighted (otherwise with SSH chosen, the VNC from history opened)
                    (ActiveServerViewModel as ServerListPageViewModel)?.ClearHomeSelection();
                }
            }
        }
        public string QuickProtocolName => QuickProtocol.ProtocolDisplayName;

        /// <summary>IP address or machine name (pc17, server.acme.local): letters, digits, hyphens, dots</summary>
        private static bool IsQuickHost(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            if (System.Net.IPAddress.TryParse(address, out _)) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(address.Trim(),
                @"^(?=.{1,253}$)[A-Za-z0-9_]([A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(\.[A-Za-z0-9_]([A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*$");
        }

        /// <summary>
        /// If the search holds only an IP address or a machine name (with optional port), starts the quick connection and returns true.
        /// </summary>
        public bool TryQuickConnectFromFilter(bool forceAskCredentials = false)
        {
            var keywords = TagAndKeywordEncodeHelper.DecodeKeyword(MainFilterString).KeyWords;
            if (keywords.Count != 1) return false;
            var host = keywords[0].Trim();
            var address = host;
            var i = host.LastIndexOf(':');
            if (i > 0 && host.IndexOf(':') == i)   // IPv4:port (a single colon); IPv6 without port
            {
                if (!int.TryParse(host.Substring(i + 1), out _)) return false;
                address = host.Substring(0, i);
            }
            if (!IsQuickHost(address)) return false;
            // a name (pc17) counts as an address only if no saved server matches the search: otherwise Enter must not fire by mistake
            if (!System.Net.IPAddress.TryParse(address, out _) && ActiveServerViewModel?.VmServerList.Any(x => x.IsVisible) == true) return false;
            // the text stays in the search (last IP used) and we go back to Home, where the connection shows up in the history
            if (ActiveServerViewModel is ServerPageViewModelBase page) page.IsHomeMode = true;
            IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().ConnectToHost(host, QuickProtocol, forceAskCredentials);
            return true;
        }

        /// <summary>
        /// If the search holds a single word (an IP address, possibly with port), prepares the creation of a server with that address
        /// and the type chosen in the search drop-down menu. Otherwise null: the creation form opens empty.
        /// </summary>
        public ServerEditorPageViewModel.ParamsServerAddPreset? BuildAddPresetFromFilter()
        {
            var keywords = TagAndKeywordEncodeHelper.DecodeKeyword(MainFilterString).KeyWords;
            if (keywords.Count != 1) return null;
            var host = keywords[0].Trim();
            var address = host;
            var i = host.LastIndexOf(':');
            if (i > 0 && host.IndexOf(':') == i)
            {
                if (!int.TryParse(host.Substring(i + 1), out _)) return null;
                address = host.Substring(0, i);
            }
            if (!IsQuickHost(address)) return null;
            // the folder whose network contains the IP is already chosen in the form (it can be removed)
            var folder = QuickSessionFollowUp.FindFolderForAddress(address);
            return new ServerEditorPageViewModel.ParamsServerAddPreset
            {
                Host = host,
                Protocol = QuickProtocol.Protocol,
                TreeNodes = folder != null ? FolderService.ToNodes(folder) : new List<string>(),
            };
        }

        public void SetMainFilterString(List<TagFilter>? tags, List<string>? keywords)
        {
            if (tags?.Count == 1 && tags.First().TagName is ServerPageViewModelBase.TAB_TAGS_LIST_NAME)
            {
                _mainFilterString = ServerPageViewModelBase.TAB_TAGS_LIST_NAME;
                RaisePropertyChanged(nameof(MainFilterString));
            }
            else
            {
                MainFilterString = TagAndKeywordEncodeHelper.EncodeKeyword(tags, keywords);
                MainFilterCaretIndex = MainFilterString?.Length ?? 0;
            }
        }
        #endregion
    }
}