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
                    ActiveServerViewModel?.Attach();   // Release() sopra gli ha tolto gli eventi: la stessa pagina torna attiva
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
                SetAndNotifyIfChanged(ref _editorViewModel, value);
                RaisePropertyChanged(nameof(IsShownList));
                RefreshTabs();
            }
        }

        // schede della barra del titolo: Home sempre, poi Impostazioni / editor quando aperti
        private readonly System.Collections.ObjectModel.ObservableCollection<MainTabItem> _tabs = new();
        public System.Collections.ObjectModel.ObservableCollection<MainTabItem> Tabs
        {
            get
            {
                if (_tabs.Count == 0) RefreshTabsOnUiThread();
                return _tabs;
            }
        }

        // l'editor puo' essere aperto da un thread di lavoro: la lista delle schede si tocca solo dal thread dell'interfaccia
        private void RefreshTabs() => Execute.OnUIThread(RefreshTabsOnUiThread);

        // ---- sessioni ospitate nella finestra principale (una scheda ciascuna, in stile browser)
        private readonly System.Collections.ObjectModel.ObservableCollection<Lemora.View.Host.ProtocolHosts.HostBase> _sessionHosts = new();
        public System.Collections.ObjectModel.ObservableCollection<Lemora.View.Host.ProtocolHosts.HostBase> SessionHosts => _sessionHosts;
        public event Action? SessionsChanged;

        private Lemora.View.Host.ProtocolHosts.HostBase? _activeSession;
        /// <summary>sessione mostrata ora (null = si vede Home / Impostazioni / editor)</summary>
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
        /// <summary>barra con stato e pulsanti della sessione mostrata (null = nessuna sessione)</summary>
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

        // ---- sessioni staccate (finestra normale o Picture in picture)
        private readonly Dictionary<Lemora.View.Host.ProtocolHosts.HostBase, Lemora.View.Host.DetachedSessionWindow> _detached = new();
        public bool IsDetached(Lemora.View.Host.ProtocolHosts.HostBase host) => _detached.ContainsKey(host);

        public void DetachSession(Lemora.View.Host.ProtocolHosts.HostBase host, bool pip)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (!_sessionHosts.Contains(host)) return;
                if (IsSplit(host)) Unsplit(host);   // una sessione staccata in una finestra esce dalla vista divisa
                Rect? bounds = null;
                if (_detached.TryGetValue(host, out var old))
                {
                    bounds = new Rect(old.Left, old.Top, old.ActualWidth, old.ActualHeight);
                    _detached.Remove(host);
                    old.ReleaseHost();
                }
                // il client cambia genitore: va tolto dalla griglia delle schede prima di entrare nella nuova finestra
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
                    // PiP nell'angolo in basso a destra dello schermo di lavoro; la finestra normale vicino alla principale
                    var wa = SystemParameters.WorkArea;
                    if (pip)
                    {
                        // altezza secondo le proporzioni dello schermo remoto (la cornice del PiP e' di 6 unita' per lato)
                        var rs = host.RemoteDesktopSize;
                        if (!rs.IsEmpty && rs.Width > 0) win.Height = Math.Round((380 - 12) * rs.Height / rs.Width + 12);
                        win.Left = wa.Right - 380 - 24; win.Top = wa.Bottom - win.Height - 24;
                    }
                    else { win.Left = main.Left + 40; win.Top = main.Top + 40; win.Width = Math.Min(main.ActualWidth, 1100); win.Height = Math.Min(main.ActualHeight, 700); }
                }
                _detached[host] = win;
                win.Show();
                host.SetParentWindow(win);
                host.SetFitToWindow(pip);   // Picture in picture: si vede tutto lo schermo remoto, ridotto
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

        // pannelli "Impostazioni schermo / avanzate" di una sessione RDP: uno per sessione e tipo, non modali (si vede l'effetto subito)
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

        /// <summary>aggiunge la sessione come nuova scheda, la mostra e le da' il focus</summary>
        public void AddSessionTab(Lemora.View.Host.ProtocolHosts.HostBase host)
        {
            Execute.OnUIThreadSync(() =>
            {
                EnsureSessionTimer();
                // come in un browser: la scheda Home aperta con il "+" diventa la sessione, non resta una Home in piu'
                if (_activeHome != 0 && ActiveSession is null && _extraHomes.Remove(_activeHome)) _activeHome = 0;
                if (!_sessionHosts.Contains(host))
                {
                    _sessionHosts.Add(host);
                    // stato cambiato (collegata, scollegata, in riconnessione): pallini delle schede e barra si aggiornano subito, non al prossimo secondo
                    host.StatusChanged += () => Execute.OnUIThread(() =>
                    {
                        foreach (var t in _tabs) t.RefreshStatus();
                        SessionBar?.Refresh();
                    });
                }
                // "Dividi la vista": la sessione appena aperta si affianca a quella da cui si e' partiti, nella stessa scheda
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

        // ---- vista divisa: due sessioni affiancate nella stessa scheda
        private readonly Dictionary<Lemora.View.Host.ProtocolHosts.HostBase, Lemora.View.Host.ProtocolHosts.HostBase> _split = new();
        private Lemora.View.Host.ProtocolHosts.HostBase? _pendingSplitFrom;
        private DateTime _pendingSplitAt;

        public Lemora.View.Host.ProtocolHosts.HostBase? SplitPartner(Lemora.View.Host.ProtocolHosts.HostBase h) => _split.TryGetValue(h, out var p) ? p : null;
        public bool IsSplit(Lemora.View.Host.ProtocolHosts.HostBase h) => _split.ContainsKey(h);

        /// <summary>nome mostrato sulla testata del pannello: se le due sessioni hanno lo stesso nome la seconda e' "(2)"</summary>
        public string PaneTitle(Lemora.View.Host.ProtocolHosts.HostBase h)
        {
            var n = h.ProtocolServer.DisplayName;
            var p = SplitPartner(h);
            return p != null && p.ProtocolServer.DisplayName == n && _sessionHosts.IndexOf(h) > _sessionHosts.IndexOf(p) ? n + " (2)" : n;
        }

        private void LinkSplit(Lemora.View.Host.ProtocolHosts.HostBase a, Lemora.View.Host.ProtocolHosts.HostBase b)
        {
            _split[a] = b; _split[b] = a;
            // le sessioni staccate in una finestra non si affiancano: tornano nella scheda
            if (IsDetached(a)) ReattachSession(a);
            if (IsDetached(b)) ReattachSession(b);
        }

        /// <summary>trascinando una scheda su un'altra le due sessioni si affiancano (vale per qualsiasi protocollo)</summary>
        public void MergeSessions(Lemora.View.Host.ProtocolHosts.HostBase a, Lemora.View.Host.ProtocolHosts.HostBase b)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (ReferenceEquals(a, b) || IsSplit(a) || IsSplit(b) || !_sessionHosts.Contains(a) || !_sessionHosts.Contains(b)) return;
                LinkSplit(b, a);   // b e' la scheda su cui si e' lasciato: resta a sinistra
                RefreshTabs();
                ActivateSession(a);
                SessionBar?.Refresh();
            });
        }

        /// <summary>le due sessioni tornano schede separate</summary>
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

        /// <summary>"Dividi la vista": apre una seconda sessione dello stesso server e la affianca (se il protocollo ne ammette una sola, si spiega come fare)</summary>
        public void SplitCurrent()
        {
            var h = ActiveSession;
            if (h == null) return;
            if (IsSplit(h)) { Unsplit(h); return; }
            if (h.ProtocolServer.IsOnlyOneInstance())
            {
                Lemora.Service.ToastService.Show("Questo protocollo apre una sola sessione per server: trascina un'altra scheda su questa per affiancarle.");
                return;
            }
            _pendingSplitFrom = h; _pendingSplitAt = DateTime.Now;
            GlobalEventHelper.OnRequestServerConnect?.Invoke(h.ProtocolServer, fromView: "Split");
        }

        /// <summary>chiude la scheda divisa: chiede conferma e chiude entrambe le sessioni</summary>
        private void CloseSplitTab(Lemora.View.Host.ProtocolHosts.HostBase a, Lemora.View.Host.ProtocolHosts.HostBase b)
        {
            if (!App.ExitingFlag && !MessageBoxHelper.Confirm($"Questa scheda contiene due sessioni: {a.ProtocolServer.DisplayName} e {b.ProtocolServer.DisplayName}. Chiudendola vengono chiuse tutte e due e le connessioni terminate.",
                    "Chiudi entrambe", "Annulla", "Chiudere entrambe le sessioni?", ownerViewModel: this)) return;
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
                // il focus va al contenuto appena la scheda e' visibile
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
                if (partner != null) { _split.Remove(h); _split.Remove(partner); }   // chiusa una delle due: l'altra resta da sola
                if (ReferenceEquals(ActiveSession, h)) ActiveSession = partner != null && _sessionHosts.Contains(partner) ? partner : null;
                else RefreshTabs();
                SessionsChanged?.Invoke();
            });
        }

        /// <summary>primo piano: la finestra principale resta sopra alle altre</summary>
        public bool IsPinned => View is Window w && w.Topmost;

        public void TogglePin() => Execute.OnUIThread(() =>
        {
            if (View is Window w) w.Topmost = !w.Topmost;
            SessionBar?.Refresh();
        });

        // ---- scorciatoie da tastiera (le riceve Service/AppShortcuts)
        private readonly List<string> _tabHistory = new();   // chiavi delle schede, l'ultima usata per prima
        private int _cycleIndex = -1;                         // >= 0 mentre si tiene premuto Ctrl e si preme Tab

        private string CurrentTabKey() => ActiveSession != null ? "s:" + ActiveSession.ConnectionId : "h:" + _activeHome;

        private bool TabExists(string key) => key.StartsWith("s:") ? _sessionHosts.Any(x => "s:" + x.ConnectionId == key)
                                                                    : key == "h:0" || _extraHomes.Any(x => "h:" + x == key);

        /// <summary>la scheda mostrata ora diventa la piu' recente (non durante Ctrl+Tab: li' l'ordine si fissa solo al rilascio di Ctrl)</summary>
        private void NoteActiveTab()
        {
            if (_cycleIndex >= 0) return;
            var key = CurrentTabKey();
            _tabHistory.Remove(key);
            _tabHistory.Insert(0, key);
        }

        /// <summary>Ctrl+Tab: alla scheda usata piu' di recente; tenendo Ctrl e premendo ancora Tab si scende nell'elenco delle ultime usate</summary>
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

        /// <summary>Ctrl+W / Ctrl+F4: chiude la scheda mostrata come farebbe la sua ✕ (la Home principale non si chiude)</summary>
        public void CloseCurrentTab() => _tabs.FirstOrDefault(x => x.IsActive && x.CanClose)?.CloseCommand?.Execute();

        public void NewHomeTab()
        {
            QuickProtocol = QuickProtocols.FirstOrDefault(x => x.Protocol == Lemora.Model.Protocol.VNC.ProtocolName) ?? QuickProtocols.First();   // una nuova Home riparte da VNC
            AddHomeTab();
            FocusFilterSoon();
        }

        private void FocusFilterSoon() => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            MainFilterIsFocused = false;
            MainFilterIsFocused = true;
        }), System.Windows.Threading.DispatcherPriority.Background);

        /// <summary>Ctrl+F: cursore nella ricerca (da una sessione si torna alla Home)</summary>
        public bool FocusSearch()
        {
            if (ActiveSession != null) ShowHomeTab(_activeHome);
            else if (!IsShownList) return false;
            FocusFilterSoon();
            return true;
        }

        /// <summary>Ctrl+N: aggiungi server</summary>
        public bool ShortcutAddServer()
        {
            if (ActiveSession != null) ShowHomeTab(_activeHome);
            if (!IsShownList || ActiveServerViewModel == null) return false;
            ActiveServerViewModel.CmdAdd.Execute();
            return true;
        }

        /// <summary>Ctrl+I: impostazioni</summary>
        public bool ShortcutSettings()
        {
            if (ShowSetting) return true;
            if (ActiveSession != null) ActiveSession = null;
            if (!IsShownList) return false;
            CmdGoSysOptionsPage.Execute();
            return true;
        }

        /// <summary>Ctrl+D: preferito si/no per la riga selezionata della Home</summary>
        public bool ShortcutFavorite()
        {
            if (ActiveSession != null || !IsShownList) return false;
            return ActiveServerViewModel is Lemora.View.ServerView.ServerListPageViewModel list && list.ToggleFavoriteOfSelection();
        }

        /// <summary>Ctrl+1 in finestra (o di nuovo nelle schede), Ctrl+2 schermo intero, Ctrl+3 adatta larghezza, Ctrl+4 adatta altezza</summary>
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
                    // scheda divisa: un'unica scheda con le due sessioni affiancate
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
                    CanFullScreen = false, // il pulsante schermo intero sta nella barra sessione
                    FullScreenCommand = new RelayCommand(_ =>
                    {
                        if (h.CanFullScreen && h.CanResizeNow())
                            IoC.Get<SessionControlService>().MoveSessionToFullScreen(h.ConnectionId);
                    }),
                });
            }
            // altre schede Home aperte con il "+": mostrano la stessa Home (lista e ricerca condivise), si chiudono con la X
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
            // "+" in fondo alle schede: apre un'altra Home
            // (spostato dopo Impostazioni / editor, vedi sotto)
            // Impostazioni / editor restano aperti sotto le sessioni; un clic sulla scheda li rimostra
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
        }

        public Action? OnMainWindowViewLoaded = null;
        protected override void OnViewLoaded()
        {
            base.OnViewLoaded();
            // all'avvio la finestra si mostra con lo scheletro prima che i dati siano pronti: il resto parte quando entra il contenuto vero
            Lemora.Utils.StartupSplash.WhenReal(OnRealViewLoaded);
        }

        private void OnRealViewLoaded()
        {
            GlobalEventHelper.OnGoToServerAddPage += async void (tagNames, assignDataSource, preset) =>
            {
                try
                {
                    // il "+" di un gruppo passa la sua origine: e' la destinazione predefinita (prima veniva ignorata e la voce finiva in Locale)
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
                    // voce di un'origine in sola lettura (Teams senza scrittura): duplicarla copierebbe la password in una voce locale leggibile
                    if (server.DataSource?.IsWritable == false)
                    {
                        MessageBoxHelper.ErrorAlert(IoC.Translate("server_readonly"));
                        return;
                    }
                    // l'editor mostra le password: con la verifica attiva serve prima impronta, volto o PIN
                    if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;
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
                    // l'editor mostra le password: con la verifica attiva serve prima impronta, volto o PIN
                    if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;
                    EditorViewModel = ServerEditorPageViewModel.Edit(_appData, server);
                    ShowMe();
                }
            };

            GlobalEventHelper.OnRequestGoToServerMultipleEditPage += async void (servers, isInAnimationShow) =>
            {
                if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;
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
            Lemora.Utils.DevDemo.RunIfRequested(); // no-op senza LEMORA_HEADLESS=1 + LEMORA_DEMO

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


        public void ShowList(bool clearSelection)
        {
            if (this.View is not MainWindowView { IsClosing: false } window) return;
            EditorViewModel = null;
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
                    IoC.Get<GeneralSettingViewModel>().RefreshTeams(); // sezione Teams: abbinamento, permesso o interruttore possono essere cambiati
                }, o => IsShownList);
            }
        }

        // ---- schede Home aggiuntive (il "+"): ognuna e' una scheda che mostra la Home
        private readonly List<int> _extraHomes = new();
        private int _nextHomeId = 1;
        private int _activeHome = 0;   // 0 = la scheda Home principale
        private readonly Dictionary<int, string> _homeFilters = new();   // ogni scheda Home ricorda il suo testo di ricerca

        private void ShowHomeTab(int id)
        {
            if (id != _activeHome)
            {
                _homeFilters[_activeHome] = MainFilterString;              // salva il filtro della scheda che lascio
                MainFilterString = _homeFilters.TryGetValue(id, out var f) ? f : "";   // nuova scheda = vuoto
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
            if (_activeHome == id) { _activeHome = -1; ShowHomeTab(0); }   // -1: forza il ripristino del filtro della Home 0
            else RefreshTabs();
        }

        // scheda Home della barra del titolo: dalle impostazioni equivale a "Fatto"
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
        /// L'app va in background (X o Alt+F4 con "riduci nel vassoio"): se l'utente ha scelto di non tenere le sessioni attive, si chiudono tutte (VNC, RDP, SSH...).
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
                    // scrivendo nella ricerca si lascia la home e si vede l'elenco filtrato
                    if (!string.IsNullOrEmpty(value) && ActiveServerViewModel is ServerPageViewModelBase homePage && homePage.IsHomeMode
                        && ActiveServerViewModel is not ServerListPageViewModel)   // la lista ha la home filtrabile: si resta li'
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

        // ---- connessione rapida dalla barra di ricerca: IP (o IP:porta) + Invio, con il tipo scelto nel menu a tendina (predefinito VNC)
        public List<Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort> QuickProtocols => IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().Protocols;

        private Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort? _quickProtocol;
        public Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort QuickProtocol
        {
            get => _quickProtocol ??= QuickProtocols.FirstOrDefault(x => x.Protocol == Lemora.Model.Protocol.VNC.ProtocolName) ?? QuickProtocols.First();
            set
            {
                if (SetAndNotifyIfChanged(ref _quickProtocol, value))
                    RaisePropertyChanged(nameof(QuickProtocolName));
            }
        }
        public string QuickProtocolName => QuickProtocol.ProtocolDisplayName;

        /// <summary>indirizzo IP oppure nome macchina (pc17, server.cliente.local): lettere, cifre, trattini, punti</summary>
        private static bool IsQuickHost(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            if (System.Net.IPAddress.TryParse(address, out _)) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(address.Trim(),
                @"^(?=.{1,253}$)[A-Za-z0-9_]([A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?(\.[A-Za-z0-9_]([A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)*$");
        }

        /// <summary>
        /// Se nella ricerca c'e' solo un indirizzo IP o un nome macchina (con porta facoltativa) avvia la connessione rapida e restituisce true.
        /// </summary>
        public bool TryQuickConnectFromFilter(bool forceAskCredentials = false)
        {
            var keywords = TagAndKeywordEncodeHelper.DecodeKeyword(MainFilterString).KeyWords;
            if (keywords.Count != 1) return false;
            var host = keywords[0].Trim();
            var address = host;
            var i = host.LastIndexOf(':');
            if (i > 0 && host.IndexOf(':') == i)   // IPv4:porta (un solo due punti); IPv6 senza porta
            {
                if (!int.TryParse(host.Substring(i + 1), out _)) return false;
                address = host.Substring(0, i);
            }
            if (!IsQuickHost(address)) return false;
            // un nome (pc17) vale come indirizzo solo se nessun server salvato corrisponde alla ricerca: altrimenti Invio non deve partire per sbaglio
            if (!System.Net.IPAddress.TryParse(address, out _) && ActiveServerViewModel?.VmServerList.Any(x => x.IsVisible) == true) return false;
            // il testo resta nella ricerca (ultimo IP usato) e si torna alla home, dove la connessione compare nella cronologia
            if (ActiveServerViewModel is ServerPageViewModelBase page) page.IsHomeMode = true;
            IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().ConnectToHost(host, QuickProtocol, forceAskCredentials);
            return true;
        }

        /// <summary>
        /// Se nella ricerca c'e' una sola parola (un indirizzo IP, anche con porta) prepara la creazione di un server con quell'indirizzo
        /// e con il tipo scelto nel menu a tendina della ricerca. Altrimenti null: la creazione si apre vuota.
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
            // la cartella la cui rete contiene l'IP e' gia' scelta nel modulo (si toglie se non va bene)
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