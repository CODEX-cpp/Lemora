using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.DataSource.Model;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace Lemora.View.ServerView
{
    public partial class ServerListPageViewModel : ServerPageViewModelBase
    {
        #region properties
        private EnumServerViewStatus _currentViewInListPage;
        public EnumServerViewStatus CurrentViewInListPage
        {
            get => _currentViewInListPage;
            set => SetAndNotifyIfChanged(ref _currentViewInListPage, value);
        }
        
        /// <summary>Home layout according to the window width (set by the view, see HomeLayout)</summary>
        private HomeLayoutMode _layoutMode = HomeLayoutMode.Wide;
        public HomeLayoutMode LayoutMode
        {
            get => _layoutMode;
            set => SetAndNotifyIfChanged(ref _layoutMode, value);
        }

        /// <summary>narrow window: folders drawer menu open</summary>
        private bool _navDrawerOpen;
        public bool NavDrawerOpen
        {
            get => _navDrawerOpen;
            set => SetAndNotifyIfChanged(ref _navDrawerOpen, value);
        }

        /// <summary>window not wide: detail open (over the list or in its place); with a wide window it is always visible</summary>
        private bool _detailOpen;
        public bool DetailOpen
        {
            get => _detailOpen;
            set => SetAndNotifyIfChanged(ref _detailOpen, value);
        }

        /// <summary>server shown in the detail panel: the last one clicked</summary>
        private ProtocolBaseViewModel? _detailServerViewModel;
        public ProtocolBaseViewModel? DetailServerViewModel
        {
            get => _detailServerViewModel;
            set
            {
                value?.RefreshDetailActions();
                SetAndNotifyIfChanged(ref _detailServerViewModel, value);
                if (value != null) { DetailQuick = null; FolderSummary = null; }   // one panel at a time
            }
        }

        protected override void OnFolderSummaryChanged(FolderSummaryViewModel? summary)
        {
            if (summary == null) return;
            DetailServerViewModel = null;
            DetailQuick = null;
        }

        protected override void OnNoFolderOpened()
        {
            DetailServerViewModel = null;
            DetailQuick = null;
        }

        /// <summary>quick connection (not saved) shown in the detail panel instead of a server</summary>
        private HomeItem? _detailQuick;
        public HomeItem? DetailQuick
        {
            get => _detailQuick;
            set
            {
                SetAndNotifyIfChanged(ref _detailQuick, value);
                if (value != null) FolderSummary = null;   // one panel at a time
            }
        }

        /// <summary>&gt; arrow of the Home row: details in the right panel, without connecting. The server is reloaded from the list: the row may hold a stale object</summary>
        public void ShowHomeDetails(HomeItem item)
        {
            if (item.Server != null)
            {
                DetailServerViewModel = AppData.GetItemById(item.Server.Server.DataSource?.DataSourceName ?? "", item.Server.Id) ?? item.Server;
            }
            else if (item.Quick != null)
            {
                DetailServerViewModel = null;
                DetailQuick = item;
            }
        }

        // the detail panel follows the selected row with the keyboard arrows too (before, it only updated on click)
        protected override void OnSelectedServerChanged(ProtocolBaseViewModel? server)
        {
            if (server != null) DetailServerViewModel = server;
        }

        // ---- home: favorites and history (saved servers + quick connections)
        public System.Collections.ObjectModel.ObservableCollection<HomeItem> FavoriteItems { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<HomeItem> HistoryItems { get; } = new();
        /// <summary>with an active search: saved servers that match and are not already among favorites/history</summary>
        public System.Collections.ObjectModel.ObservableCollection<HomeItem> OtherItems { get; } = new();

        private bool _homeIsFiltering;
        /// <summary>true if the search has text: Home shows only the matching entries</summary>
        public bool HomeIsFiltering { get => _homeIsFiltering; private set => SetAndNotifyIfChanged(ref _homeIsFiltering, value); }

        private string? _homeSelectedKey;
        public event Action<HomeItem>? HomeSelectionChanged;

        private static bool HomeMatch(HomeItem h, List<string> keywords)
        {
            var text = $"{h.Title} {h.HostText} {h.UserText} {h.ProtocolUpper} {h.Folder}";
            return keywords.All(k => text.Contains(k, StringComparison.CurrentCultureIgnoreCase));
        }
        private bool _homeEventHooked;

        protected override void OnHomeModeChanged()
        {
            base.OnHomeModeChanged();
            if (IsHomeMode) RefreshHome();
        }

        public void EnsureHomeHooked()
        {
            if (_homeEventHooked) return;
            _homeEventHooked = true;
            Lemora.Service.Locality.LocalityConnectRecorder.Changed += RefreshHome;
        }

        public void RefreshHome()
        {
            // a single read of the folder credentials, here (not while drawing the row): needed by every row's user choices
            var folderCredentials = Lemora.Service.FolderCredentialService.Snapshot();
            // only saved servers have a folder; a typed, unsaved IP has none (identical networks like 192.168.1.x belong to different customers)
            string FolderOf(ProtocolBaseViewModel x) => x.Server.TreeNodes.LastOrDefault() ?? "";
            Execute.OnUIThread(() =>
            {
                var swHome = System.Diagnostics.Stopwatch.StartNew();
                var favorites = AppData.VmItemList.Where(x => x.IsFavorite).OrderBy(x => x.DisplayName)
                    .Select(x => new HomeItem { Server = x, ProtocolUpper = x.ProtocolUpper, Title = x.DisplayName, HostText = x.HostText, UserText = x.UserText, Folder = FolderOf(x) }).ToList();
                var protocols = IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().Protocols;
                HomeItem ToHome(Lemora.View.Launcher.QuickConnectionItem q, DateTime time)
                {
                    var (user, _, _) = q.GetUserPassword();
                    return new HomeItem
                    {
                        Quick = q,
                        ProtocolUpper = (protocols.FirstOrDefault(x => x.Protocol == q.Protocol)?.ProtocolDisplayName ?? q.Protocol).ToUpperInvariant(),
                        Title = q.Host,
                        HostText = q.Host,
                        UserText = user,
                        Time = time,
                    };
                }
                var history = AppData.VmItemList.Where(x => x.LastConnectTime > DateTime.MinValue)
                    .Select(x => new HomeItem { Server = x, ProtocolUpper = x.ProtocolUpper, Title = x.DisplayName, HostText = x.HostText, UserText = x.UserText, Folder = FolderOf(x), Time = x.LastConnectTime })
                    .ToList();
                var all = Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryGetAll();
                // a quick connection to an address later saved as a server is shown as that server (saved name and data, not the typed IP)
                ProtocolBaseViewModel? SavedFor(Lemora.View.Launcher.QuickConnectionItem q)
                {
                    var host = q.Host.Trim(); var port = "";
                    var c = host.LastIndexOf(':');
                    if (c > 0 && host.IndexOf(':') == c) { port = host.Substring(c + 1); host = host.Substring(0, c); }
                    return AppData.VmItemList.FirstOrDefault(x => x.Server is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort ap
                        && string.Equals(ap.Protocol, q.Protocol, StringComparison.OrdinalIgnoreCase)
                        && string.Equals((ap.Address ?? "").Trim(), host, StringComparison.OrdinalIgnoreCase)
                        && (port.Length == 0 || string.IsNullOrEmpty(ap.Port) || ap.Port == port));
                }
                for (var i = 0; i < all.Count; i++)
                {
                    var q = all[i];
                    // old entries without a date stay at the end, in history order
                    var time = q.LastTime > DateTime.MinValue ? q.LastTime : DateTime.MinValue.AddSeconds(all.Count - i);
                    var saved = SavedFor(q);
                    if (saved != null)
                    {
                        HomeItem ToSaved() => new HomeItem { Server = saved, ProtocolUpper = saved.ProtocolUpper, Title = saved.DisplayName, HostText = saved.HostText, UserText = saved.UserText, Folder = FolderOf(saved), Time = time };
                        var existing = history.FirstOrDefault(x => x.Server == saved);
                        if (existing == null) history.Add(ToSaved());
                        if (q.IsFavorite && !favorites.Any(x => x.Server == saved)) favorites.Add(ToSaved());
                        continue;
                    }
                    history.Add(ToHome(q, time));
                    if (q.IsFavorite) favorites.Add(ToHome(q, time));   // a favorite quick connection also sits among the favorites
                }
                var tHist = swHome.ElapsedMilliseconds;
                // the search filters Home (quick connections too, which are not in the server database)
                var keywords = TagAndKeywordEncodeHelper.DecodeKeyword(IoC.Get<MainWindowViewModel>().MainFilterString).KeyWords
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                HomeIsFiltering = keywords.Count > 0;
                if (HomeIsFiltering)
                {
                    favorites = favorites.Where(x => HomeMatch(x, keywords)).ToList();
                    history = history.Where(x => HomeMatch(x, keywords)).ToList();
                }
                favorites = favorites.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                var historyShown = history.OrderByDescending(x => x.Time).ToList();   // all connections made, most recent first
                var others = new List<HomeItem>();
                if (!HomeIsFiltering && favorites.Count == 0 && historyShown.Count == 0)
                {
                    // no favorites or history but saved servers exist (even only those shared by the Teams server): show them instead of the "No connections yet" prompt
                    others = AppData.VmItemList
                        .Select(x => new HomeItem { Server = x, ProtocolUpper = x.ProtocolUpper, Title = x.DisplayName, HostText = x.HostText, UserText = x.UserText, Folder = FolderOf(x) })
                        .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).Take(30).ToList();
                }
                else if (HomeIsFiltering)
                {
                    var shown = new HashSet<string>(favorites.Concat(historyShown).Select(x => x.Key));
                    others = AppData.VmItemList
                        .Select(x => new HomeItem { Server = x, ProtocolUpper = x.ProtocolUpper, Title = x.DisplayName, HostText = x.HostText, UserText = x.UserText, Folder = FolderOf(x) })
                        .Where(x => !shown.Contains(x.Key) && HomeMatch(x, keywords))
                        .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).Take(30).ToList();
                }
                var tOrder = swHome.ElapsedMilliseconds;
                foreach (var h in favorites.Concat(historyShown).Concat(others)) h.SetChoices(folderCredentials);
                var tChoices = swHome.ElapsedMilliseconds;
                foreach (var v in AppData.VmItemList.ToList()) v.RefreshUserChoices();   // folder rows: the user menu refreshes with the data
                var tRefresh = swHome.ElapsedMilliseconds;
                Replace(FavoriteItems, favorites);
                Replace(HistoryItems, historyShown);
                Replace(OtherItems, others);
                // the highlighted entry stays highlighted if it is still in the list
                var navItems = HomeNavItems();
                if (_homeSelectedKey != null && !navItems.Any(x => x.Key == _homeSelectedKey)) _homeSelectedKey = null;
                ApplyHomeSelection();
                if (swHome.ElapsedMilliseconds > 100) Lemora.Utils.Diag.Log($"START-DIAG:   RefreshHome {swHome.ElapsedMilliseconds} ms (history {tHist}, order {tOrder - tHist}, user choices {tChoices - tOrder}, server refresh {tRefresh - tChoices}, rest {swHome.ElapsedMilliseconds - tRefresh}; entries {historyShown.Count})");
            });
        }

        /// <summary>entries reachable with the arrows, in the order shown, without duplicates (a server both in favorites and history counts once)</summary>
        private List<HomeItem> HomeNavItems()
            => FavoriteItems.Concat(HistoryItems).Concat(OtherItems).GroupBy(x => x.Key).Select(g => g.First()).ToList();

        private void ApplyHomeSelection()
        {
            foreach (var i in FavoriteItems.Concat(HistoryItems).Concat(OtherItems)) i.IsSelected = _homeSelectedKey != null && i.Key == _homeSelectedKey;
        }

        public bool HasHomeSelection => _homeSelectedKey != null;

        /// <summary>down arrow (+1) / up (-1): highlights the next/previous entry; from the first one, "up" clears the highlight</summary>
        public void MoveHomeSelection(int delta)
        {
            var all = HomeNavItems();
            if (all.Count == 0) return;
            var idx = _homeSelectedKey == null ? -1 : all.FindIndex(x => x.Key == _homeSelectedKey);
            idx += delta;
            if (idx < 0) { ClearHomeSelection(); return; }
            if (idx >= all.Count) idx = all.Count - 1;
            _homeSelectedKey = all[idx].Key;
            ApplyHomeSelection();
            HomeSelectionChanged?.Invoke(all[idx]);
        }

        /// <summary>single click on a Home row: highlights it (as with the arrows) and shows its details, without connecting</summary>
        public void SelectHomeItem(HomeItem item)
        {
            _homeSelectedKey = item.Key;
            ApplyHomeSelection();
            ShowHomeDetails(item);
        }

        public void ClearHomeSelection()
        {
            _homeSelectedKey = null;
            ApplyHomeSelection();
        }

        /// <summary>Ctrl+D: toggle favorite for the highlighted row (arrows) or, if none, for the last server opened in the detail panel</summary>
        public bool ToggleFavoriteOfSelection()
        {
            var item = HomeNavItems().FirstOrDefault(x => x.Key == _homeSelectedKey);
            if (item != null)
            {
                item.IsFavorite = !item.IsFavorite;
                RefreshHome();
                return true;
            }
            var d = DetailServerViewModel;
            if (d == null) return false;
            d.IsFavorite = !d.IsFavorite;
            return true;
        }

        /// <summary>connects the highlighted entry (true) or does nothing if there is none (false)</summary>
        public bool ConnectHomeSelection()
        {
            var item = HomeNavItems().FirstOrDefault(x => x.Key == _homeSelectedKey);
            if (item == null) return false;
            ConnectHomeItem(item);
            return true;
        }

        public void ConnectHomeItem(HomeItem item)
        {
            var main = IoC.Get<MainWindowViewModel>();
            DetailServerViewModel = item.Server ?? DetailServerViewModel;
            if (item.Server != null)
            {
                item.Server.CmdConnServer?.Execute();
            }
            else if (item.Quick != null)
            {
                var proto = main.QuickProtocols.FirstOrDefault(x => x.Protocol == item.Quick.Protocol);
                // the entry type applies to this connection, it does not change the one chosen in the search menu (VNC by default)
                IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().ConnectToHost(item.Quick.Host, proto ?? main.QuickProtocol);
            }
        }

        private static void Replace(System.Collections.ObjectModel.ObservableCollection<HomeItem> target, System.Collections.Generic.List<HomeItem> items)
        {
            target.Clear();
            foreach (var i in items) target.Add(i);
        }

        private bool _isAddToolTipShow = false;
        public bool IsAddToolTipShow
        {
            get => _isAddToolTipShow;
            set => SetAndNotifyIfChanged(ref _isAddToolTipShow, value);
        }



        private Visibility _briefNoteVisibility;
        public Visibility BriefNoteVisibility
        {
            get => _briefNoteVisibility;
            set
            {
                if (SetAndNotifyIfChanged(ref this._briefNoteVisibility, value))
                {
                    UpdateNote();
                }
            }
        }

        private void UpdateNote()
        {
            Execute.OnUIThread(() =>
            {
                foreach (var item in VmServerList.Where(x => x.HoverNoteDisplayControl != null))
                {
                    if (item.HoverNoteDisplayControl != null)
                    {
                        item.HoverNoteDisplayControl.IsBriefNoteShown = BriefNoteVisibility == Visibility.Visible;
                    }
                }
            });
        }

        #endregion

        public ServerListPageViewModel(DataSourceService sourceService, GlobalData appData) : base(sourceService, appData)
        {
            // Make sure the update do triggered the first time assign a value 
            BriefNoteVisibility = IoC.Get<ConfigurationService>().General.ShowNoteFieldInListView ? Visibility.Visible : Visibility.Collapsed;
            
            // Register CollectionChanged handler once to update selection-related properties
            VmServerList.CollectionChanged += (s, e) =>
            {
                RaisePropertyChanged(nameof(IsAnySelected));
                RaisePropertyChanged(nameof(IsSelectedAll));
                RaisePropertyChanged(nameof(SelectedCount));
            };
        }

        public double NameWidth
        {
            get => LocalityListViewService.Settings.ServerListNameWidth;
            set
            {
                LocalityListViewService.ServerListNameWidthSet(value);
                RaisePropertyChanged();
            }
        }

        public double NoteWidth
        {
            get => LocalityListViewService.Settings.ServerListNoteWidth;
            set
            {
                LocalityListViewService.ServerListNoteWidthSet(value);
                RaisePropertyChanged();
            }
        }

        protected override void OnViewLoaded()
        {
            ApplySort();
            base.OnViewLoaded();
        }

        public void DeleteServer(string id)
        {
            var viewModel = VmServerList.FirstOrDefault(x => x.Id == id);
            if (viewModel != null)
            {
                DeleteServer(viewModel);
            }
        }
        public void DeleteServer(ProtocolBaseViewModel viewModel)
        {
            Execute.OnUIThreadSync(() =>
            {
                viewModel.PropertyChanged -= VmServerPropertyChanged;
                if (VmServerList.Contains(viewModel))
                {
                    VmServerList.Remove(viewModel);
                    SimpleLogHelper.Debug($"Remote server {viewModel.DisplayName} of `{viewModel.DataSourceName}` removed from list");
                }
                else
                {
                    SimpleLogHelper.Debug($"Remote server {viewModel.DisplayName} of `{viewModel.DataSourceName}` removed from list, but not found in list");
                }
                VmServerListDummyNode();
            });
        }

        public void VmServerListDummyNode()
        {
            // Optimize by reducing UI thread operations and batching changes
            var dummiesNeedToAdd = new List<ProtocolBaseViewModelDummy>();
            var dummiesNeedToRemove = new List<ProtocolBaseViewModelDummy>();
            bool shouldShowTooltip = false;

            // Collect all changes first before executing on UI thread
            if (SourceService.LocalDataSource != null)
            {
                if (VmServerList.All(x => x.DataSource != SourceService.LocalDataSource))
                {
                    dummiesNeedToAdd.Add(new ProtocolBaseViewModelDummy(SourceService.LocalDataSource!));
                    SimpleLogHelper.Debug($"Add dummy server for `{SourceService.LocalDataSource.DataSourceName}`");
                }
                else if (VmServerList.Any(x => x.DataSource == SourceService.LocalDataSource && x is not ProtocolBaseViewModelDummy)
                         && VmServerList.FirstOrDefault(x => x.DataSource == SourceService.LocalDataSource && x is ProtocolBaseViewModelDummy) is ProtocolBaseViewModelDummy dummy)
                {
                    dummiesNeedToRemove.Add(dummy);
                    SimpleLogHelper.Debug($"Remove dummy server for `{SourceService.LocalDataSource.DataSourceName}`");
                }
            }

            // Teams entries sit in the local group: no separate empty group for them
            foreach (var source in SourceService.AdditionalSources.Where(x => x.Value is not TeamsSource))
            {
                if (VmServerList.All(x => x.DataSource != source.Value))
                {
                    dummiesNeedToAdd.Add(new ProtocolBaseViewModelDummy(source.Value));
                    SimpleLogHelper.Debug($"Add dummy server for `{source.Value.DataSourceName}`");
                }
                else if (VmServerList.Any(x => x.DataSource == source.Value && x is not ProtocolBaseViewModelDummy)
                         && VmServerList.FirstOrDefault(x => x.DataSource == source.Value && x is ProtocolBaseViewModelDummy) is ProtocolBaseViewModelDummy dummy)
                {
                    dummiesNeedToRemove.Add(dummy);
                    SimpleLogHelper.Debug($"Remove dummy server for `{source.Value.DataSourceName}`");
                }
            }

            shouldShowTooltip = !VmServerList.Any(x => x is not ProtocolBaseViewModelDummy) 
                               && !IoC.Get<ConfigurationService>().AdditionalDataSource.Any();

            // Apply all changes in a single UI thread operation
            if (dummiesNeedToAdd.Any() || dummiesNeedToRemove.Any() || IsAddToolTipShow != shouldShowTooltip)
            {
                Execute.OnUIThreadSync(() =>
                {
                    foreach (var dummy in dummiesNeedToRemove)
                    {
                        VmServerList.Remove(dummy);
                    }
                    foreach (var dummy in dummiesNeedToAdd)
                    {
                        VmServerList.Add(dummy);
                    }
                    IsAddToolTipShow = shouldShowTooltip;
                });
            }
            
            ApplySort();
        }

        public sealed override void BuildView()
        {
            lock (this)
            {
                var list = AppData.VmItemList.ToList();
                Execute.OnUIThread(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();   // list rebuild timings (slow startup diagnosis)
                    // Unsubscribe from existing items
                    foreach (var vs in VmServerList)
                    {
                        vs.PropertyChanged -= VmServerPropertyChanged;
                    }

                    // Prepare new list with proper ordering
                    var newList = list.OrderBy(x => x.CustomOrder).ThenBy(x => x.Id).ToList();

                    // Clear and repopulate the existing collection instead of replacing it
                    // This prevents race conditions with VirtualizingWrapPanel during layout operations
                    VmServerList.Clear();
                    foreach (var item in newList)
                    {
                        VmServerList.Add(item);
                    }
                    var tFill = sw.ElapsedMilliseconds;

                    SelectedServerViewModel = null;
                    foreach (var vs in VmServerList)
                    {
                        vs.IsSelected = false;
                        vs.PropertyChanged += VmServerPropertyChanged;
                    }

                    RaisePropertyChanged(nameof(IsAnySelected));
                    RaisePropertyChanged(nameof(IsSelectedAll));
                    RaisePropertyChanged(nameof(SelectedCount));
                    var tSel = sw.ElapsedMilliseconds;
                    UpdateNote();
                    var tNote = sw.ElapsedMilliseconds;

                    VmServerListDummyNode();
                    var tDummy = sw.ElapsedMilliseconds;
                    ApplySort();
                    var tSort = sw.ElapsedMilliseconds;
                    CalcServerVisibleAndRefresh(true);
                    var tVis = sw.ElapsedMilliseconds;

                    SimpleLogHelper.Debug($"[{this.GetHashCode()}] ListView rebuilt with {AppData.VmItemList.Count} servers");
                    if (tVis > 500) Lemora.Utils.Diag.Log($"START-DIAG: server list rebuilt in {tVis} ms (fill {tFill}, selection {tSel - tFill}, notes {tNote - tSel}, folders {tDummy - tNote}, sort {tSort - tDummy}, filter {tVis - tSort})");

                    // the detail panel pointed to the old object: after an edit or a delete it must be re-attached to the new one (or cleared)
                    var oldDetail = DetailServerViewModel;
                    if (oldDetail != null)
                        DetailServerViewModel = AppData.GetItemById(oldDetail.Server.DataSource?.DataSourceName ?? "", oldDetail.Id);

                    RefreshHome();
                });
            }
        }

        public override void ClearSelection()
        {
            foreach (var item in VmServerList)
            {
                item.IsSelected = false;
            }

            if (this.View is ServerListPageView view)
            {
                view.RefreshHeaderCheckBox();
            }
        }


        private EnumServerOrderBy _serverOrderBy;
        public EnumServerOrderBy ServerOrderBy
        {
            get => _serverOrderBy;
            set => SetAndNotifyIfChanged(ref this._serverOrderBy, value);
        }

        public override void ApplySort()
        {
            var orderBy = IoC.Get<MainWindowViewModel>().ServerOrderBy;
            ServerOrderBy = orderBy;
            if (this.View is ServerListPageView v)
            {
                Execute.OnUIThreadSync(() =>
                {
                    if (CollectionViewSource.GetDefaultView(v.LvServerCards.ItemsSource) is not ListCollectionView cvs) return;

                    string propertyName = "";
                    var direction = ListSortDirection.Ascending;
                    switch (orderBy)
                    {
                        case EnumServerOrderBy.IdAsc:
                            propertyName = nameof(ProtocolBaseViewModel.Id);
                            direction = ListSortDirection.Ascending;
                            break;
                        case EnumServerOrderBy.Custom:
                            propertyName = nameof(ProtocolBaseViewModel.CustomOrder);
                            direction = ListSortDirection.Ascending;
                            break;
                        case EnumServerOrderBy.ProtocolAsc:
                            propertyName = nameof(ProtocolBaseViewModel.ProtocolDisplayNameInShort);
                            direction = ListSortDirection.Ascending;
                            break;
                        case EnumServerOrderBy.ProtocolDesc:
                            propertyName = nameof(ProtocolBaseViewModel.ProtocolDisplayNameInShort);
                            direction = ListSortDirection.Descending;
                            break;
                        case EnumServerOrderBy.NameAsc:
                            propertyName = nameof(ProtocolBaseViewModel.DisplayName);
                            direction = ListSortDirection.Ascending;
                            break;
                        case EnumServerOrderBy.NameDesc:
                            propertyName = nameof(ProtocolBaseViewModel.DisplayName);
                            direction = ListSortDirection.Descending;
                            break;
                        case EnumServerOrderBy.AddressAsc:
                            propertyName = nameof(ProtocolBaseViewModel.SubTitle);
                            direction = ListSortDirection.Ascending;
                            break;
                        case EnumServerOrderBy.AddressDesc:
                            propertyName = nameof(ProtocolBaseViewModel.SubTitle);
                            direction = ListSortDirection.Descending;
                            break;
                        default:
                            SimpleLogHelper.Error($"ApplySort: type {orderBy} is not supported");
                            UnifyTracing.Error(new NotImplementedException($"ApplySort: type {orderBy} is not supported"));
                            break;
                    }

                    bool needRefresh = true;
                    if (cvs.SortDescriptions.Count == 2 && cvs.SortDescriptions[1] is var sd)
                    {
                        needRefresh = sd.PropertyName != propertyName || sd.Direction != direction;
                    }

                    if (needRefresh)
                    {
                        cvs.SortDescriptions.Clear();
                        if (propertyName == nameof(ProtocolBaseViewModel.SubTitle))
                        {
                            cvs.CustomSort = new SubTitleSortByNaturalIp(direction == ListSortDirection.Ascending);
                        }
                        else
                        {
                            cvs.SortDescriptions.Add(new SortDescription(nameof(ProtocolBaseViewModel.GroupedOrder), ListSortDirection.Ascending));
                            cvs.SortDescriptions.Add(new SortDescription(propertyName, direction));
                        }
                    }
                    //cvs.Refresh();


                    // groups only with other real sources: Teams entries are grouped with the local ones (GroupDataSource)
                    var grouped = SourceService.AdditionalSources.Values.Any(x => x is not TeamsSource);
                    if (cvs.GroupDescriptions.Count == 0 && grouped)
                    {
                        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProtocolBaseViewModel.GroupDataSource)));
                        SimpleLogHelper.Debug("GroupDescriptions = ProtocolBaseViewModel.GroupDataSource");
                    }
                    if (cvs.GroupDescriptions.Count > 0 && !grouped)
                    {
                        cvs.GroupDescriptions.Clear();
                        SimpleLogHelper.Debug("GroupDescriptions = null");
                    }
                });
            }
        }

        private bool _isRefreshScheduled = false;
        private readonly object _refreshLock = new object();

        public sealed override void CalcServerVisibleAndRefresh(bool force = false, bool matchSubTitle = true)
        {
            var swCalc = System.Diagnostics.Stopwatch.StartNew();
            base.CalcServerVisibleAndRefresh(force, matchSubTitle);
            var tBase = swCalc.ElapsedMilliseconds;
            if (IsHomeMode) RefreshHome();   // the search filters Home too
            if (swCalc.ElapsedMilliseconds > 200) Lemora.Utils.Diag.Log($"START-DIAG:   filtro: base {tBase} ms, home {swCalc.ElapsedMilliseconds - tBase} ms");

            // Debounce UI refresh to avoid excessive CollectionView.Refresh() calls
            lock (_refreshLock)
            {
                if (_isRefreshScheduled) return;
                _isRefreshScheduled = true;
            }

            Execute.OnUIThread(() =>
            {
                try
                {
                    if (this.View is ServerListPageView view && view.LvServerCards.ItemsSource != null)
                        CollectionViewSource.GetDefaultView(view.LvServerCards.ItemsSource).Refresh();
                }
                finally
                {
                    lock (_refreshLock)
                    {
                        _isRefreshScheduled = false;
                    }
                }
            });
        }


        #region Commands







        private DateTime _lastCmdReOrder = DateTime.MinValue;
        private RelayCommand? _cmdReOrder;
        public RelayCommand CmdReOrder
        {
            get
            {
                return _cmdReOrder ??= new RelayCommand((o) =>
                {
                    if ((DateTime.Now - _lastCmdReOrder).TotalMilliseconds < 200)
                        return;
                    _lastCmdReOrder = DateTime.Now;
                    IoC.Get<MainWindowViewModel>().SetServerOrderBy(o);
                    ApplySort();
                });
            }
        }


        #endregion


        #region NoteField

        private RelayCommand? _cmdHideNoteField;
        public RelayCommand CmdHideNoteField
        {
            get
            {
                return _cmdHideNoteField ??= new RelayCommand((o) =>
                {
                    IoC.Get<ConfigurationService>().General.ShowNoteFieldInListView = false;
                    IoC.Get<ConfigurationService>().Save();
                    BriefNoteVisibility = Visibility.Collapsed;
                });
            }
        }

        private RelayCommand? _cmdShowNoteField;

        public RelayCommand CmdShowNoteField
        {
            get
            {
                return _cmdShowNoteField ??= new RelayCommand((o) =>
                {
                    IoC.Get<ConfigurationService>().General.ShowNoteFieldInListView = true;
                    IoC.Get<ConfigurationService>().Save();
                    BriefNoteVisibility = Visibility.Visible;
                });
            }
        }

        #endregion


        private RelayCommand? _cmdRefreshDataSource;
        public RelayCommand CmdRefreshDataSource
        {
            get
            {
                return _cmdRefreshDataSource ??= new RelayCommand((o) =>
                {
                    if (o is DataSourceBase dataSource)
                    {
                        if (dataSource.Status != EnumDatabaseStatus.OK)
                        {
                            dataSource.ReconnectTime = DateTime.MinValue;
                        }
                        else
                        {
                            AppData.CheckUpdateTime = DateTime.MinValue;
                        }
                    }
                });
            }
        }
    }
}