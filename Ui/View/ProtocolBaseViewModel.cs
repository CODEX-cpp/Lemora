using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Lemora.Controls.NoteDisplay;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.Model;
using Lemora.Service.Locality;
using Lemora.View.Launcher;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace Lemora.View
{
    public class ProtocolBaseViewModel : NotifyPropertyChangedBase
    {
        public DataSourceBase? DataSource => Server.DataSource;
        public string DataSourceName => DataSource?.DataSourceName ?? "";
        private string _dataSourceNameForLauncher = "";
        public string DataSourceNameForLauncher
        {
            get => _dataSourceNameForLauncher;
            set => SetAndNotifyIfChanged(ref _dataSourceNameForLauncher, value);
        }

        /// <summary>
        /// Order in Main window list view
        /// </summary>
        private int _customOrder = 0;
        public int CustomOrder
        {
            get => _customOrder;
            set => SetAndNotifyIfChanged(ref _customOrder, value);
        }

        public double KeywordMark = double.MinValue;

        /// <summary>entry shared by the Teams server: shown together with the local ones (same group, same folders) with the cloud icon</summary>
        public bool IsTeams => DataSource is TeamsSource;

        /// <summary>"waiting for the administrator" clock: a Teams entry with a request ("requests" permission) not yet decided,
        /// or a local entry sent as a proposal (personal connections, read-only or requests) not yet decided</summary>
        public bool IsTeamsPending => IsTeams ? Lemora.Setup.TeamsSync.IsPending(Id) : Lemora.Setup.TeamsSync.IsPersonalPending(Id);

        /// <summary>source the entry is grouped under in the list and tree: Teams entries sit with the local ones</summary>
        public DataSourceBase? GroupDataSource => IsTeams ? IoC.TryGet<DataSourceService>()?.LocalDataSource ?? DataSource : DataSource;

        #region Grouped
        public string GroupedOrder
        {
            get
            {
                var ds = GroupDataSource;
                var i = LocalityListViewService.GroupedOrderGet(dataSourceName: ds?.DataSourceName ?? "");
                var mark = IoC.Get<DataSourceService>().LocalDataSource == ds ? '!' : '#'; // ! for local, # for remote to make local first when i is same.
                return $"{i}_{mark}_{ds}";
            }
        }


        private bool? _groupedIsExpanded = null;
        public bool GroupedIsExpanded
        {
            set
            {
                if (IoC.TryGet<LocalityService>() != null
                    && SetAndNotifyIfChanged(ref _groupedIsExpanded, value))
                {
                    LocalityListViewService.GroupedIsExpandedSet(GroupDataSource?.DataSourceName ?? "", value);
                }
            }
            get
            {
                var ret = LocalityListViewService.GroupedIsExpandedGet(GroupDataSource?.DataSourceName ?? "");
                _groupedIsExpanded = ret;
                return ret;
            }
        }
        #endregion

        public bool IsEditable { get; private set; } = false;
        public bool IsViewable { get; private set; } = false;

        public string Id => Server.Id;

        public string DisplayName => Server.DisplayName;
        public string SubTitle => Server.SubTitle;
        public string ProtocolDisplayNameInShort => Server.ProtocolDisplayName;

        // texts for the list rows and the detail panel ("Fluent Islands" restyle)
        // ---- favorites (saved in the database, by server id)
        private const string FavoritesKey = "Favorites";
        private const string FavoritesDbKey = "favorites";
        private static HashSet<string>? _favoritesCache;
        private static readonly object FavoritesLock = new object();

        /// <summary>
        /// Reads the favorites from the database. If the database is not ready yet it returns an empty set without caching it.
        /// On first start it imports the favorites that were in the local settings and deletes them from there (after writing to the database).
        /// </summary>
        private static HashSet<string> Favorites
        {
            get
            {
                lock (FavoritesLock)
                {
                    if (_favoritesCache != null) return _favoritesCache;
                    if (!AppDbStore.TryGet(FavoritesDbKey, out var json)) return new HashSet<string>();
                    HashSet<string> set;
                    if (!string.IsNullOrEmpty(json))
                    {
                        set = new HashSet<string>(Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>());
                    }
                    else
                    {
                        var old = IoC.Get<LocalityService>().GetMisc<string>(FavoritesKey, "") ?? "";
                        set = new HashSet<string>(old.Split('|', StringSplitOptions.RemoveEmptyEntries));
                        if (set.Count > 0 && AppDbStore.Set(FavoritesDbKey, Newtonsoft.Json.JsonConvert.SerializeObject(set)))
                            IoC.Get<LocalityService>().SetMisc(FavoritesKey, "");
                    }
                    _favoritesCache = set;
                    return set;
                }
            }
        }
        private static void SaveFavorites()
        {
            lock (FavoritesLock)
            {
                if (_favoritesCache == null) return;   // database not ready: nothing is written
                AppDbStore.Set(FavoritesDbKey, Newtonsoft.Json.JsonConvert.SerializeObject(_favoritesCache));
            }
        }
        public static bool IsFavoriteId(string id) => Favorites.Contains(id);
        public static void SetFavoriteId(string id, bool value)
        {
            var favorites = Favorites;
            if (value == favorites.Contains(id)) return;
            if (value) favorites.Add(id); else favorites.Remove(id);
            SaveFavorites();
            LocalityConnectRecorder.RaiseChanged();
        }
        public bool IsFavorite
        {
            get => Favorites.Contains(Id);
            set
            {
                var favorites = Favorites;
                if (value == favorites.Contains(Id)) return;
                if (value) favorites.Add(Id); else favorites.Remove(Id);
                SaveFavorites();
                RaisePropertyChanged();
                LocalityConnectRecorder.RaiseChanged();
            }
        }

        /// <summary>server actions (the same as the old row menu), shown in the detail panel</summary>
        private DetailActionSet _detailActionSet = new DetailActionSet(new List<ProtocolAction>());
        public DetailActionSet DetailActionSet
        {
            get => _detailActionSet;
            private set => SetAndNotifyIfChanged(ref _detailActionSet, value);
        }
        /// <summary>to be called when the server is shown in the detail panel</summary>
        private bool _detailActionsBuilt;
        public void RefreshDetailActions()
        {
            _detailActionsBuilt = true;
            DetailActionSet = new DetailActionSet(this.GetActions());
        }

        /// <summary>
        /// the source permission changed (Teams: write <-> read-only): IsEditable, panel actions and menu are reloaded,
        /// without waiting for a selection or view change
        /// </summary>
        public void RefreshWritable()
        {
            IsViewable = IsEditable = _server.DataSource?.IsWritable == true;
            RaisePropertyChanged(nameof(IsViewable));
            RaisePropertyChanged(nameof(IsEditable));
            if (_detailActionsBuilt) RefreshDetailActions();
            if (Actions != null) BuildActions();
        }

        public string ProtocolUpper => (Server.ProtocolDisplayName ?? "").ToUpperInvariant();
        public string LastConnectText => LastConnectTime == DateTime.MinValue ? "-" : LastConnectTime.ToString("ddd dd/MM HH:mm", System.Globalization.CultureInfo.CurrentUICulture);
        public string HostText => Server is ProtocolBaseWithAddressPort a ? (string.IsNullOrEmpty(a.Port) ? a.Address : $"{a.Address}:{a.Port}") : Server.SubTitle;
        public string UserText => Server is ProtocolBaseWithAddressPortUserPwd u ? u.UserName : "";

        // ---- user: with several users (its own or from the folder credentials) the row shows a drop-down menu to pick one (as in Home)
        /// <summary>read by bindings: uses only the in-memory copy of the folder credentials, never the database</summary>
        public List<Lemora.Service.UserResolver.UserChoice> UserChoices => Server is ProtocolBaseWithAddressPortUserPwd u
            ? Lemora.Service.UserResolver.Usable(u, Lemora.Service.UserResolver.GetChoices(u, Lemora.Service.FolderCredentialService.Cached))
            : new List<Lemora.Service.UserResolver.UserChoice>();
        public bool HasUserChoices => UserChoices.Count > 1;
        /// <summary>"Always ask" if the server asks for the user on every connection, otherwise the default user (or the first one)</summary>
        public string UserLabel
        {
            get
            {
                var choices = UserChoices;
                if (choices.Count < 2 || Server is not ProtocolBaseWithAddressPortUserPwd u) return UserText;
                return Lemora.Service.UserResolver.RowLabel(u, choices);
            }
        }
        /// <summary>after a change of users or folder credentials: the row reloads the choices</summary>
        public void RefreshUserChoices()
        {
            RaisePropertyChanged(nameof(UserChoices)); RaisePropertyChanged(nameof(HasUserChoices)); RaisePropertyChanged(nameof(UserLabel));
        }
        public string ExtraUsersText => Server is ProtocolBaseWithAddressPort { AlternateCredentials.Count: > 0 } a ? $"+{a.AlternateCredentials.Count}" : "";
        public string AllUsersText => Server is ProtocolBaseWithAddressPortUserPwd u
            ? string.Join(", ", new[] { u.UserName }.Concat(u.AlternateCredentials.Select(x => x.UserName)).Where(x => !string.IsNullOrEmpty(x)))
            : "";

        /// <summary>
        /// like: "#work #asd", display in launcher page.
        /// </summary>
        public string TagString { get; private set; } = "";

        public List<Tag> Tags { get; private set; } = new List<Tag>();

        public void ReLoadTags()
        {
            Tags = IoC.TryGet<GlobalData>()?.TagList.Where(x => _server.Tags.Contains(x.Name)).OrderBy(x => x.CustomOrder).ThenBy(x => x.Name).ToList() ?? new List<Tag>();
            RaisePropertyChanged(nameof(Tags));
        }

        private ProtocolBase _server;
        public ProtocolBase Server
        {
            get => _server;
            set
            {
                if (_server != value)
                {
                    _server = value;
                    _server.Tags = _server.Tags.Select(x => x.ToLower()).ToList();

                    if (ConverterNoteToVisibility.IsVisible(_server.Note))
                    {
                        Execute.OnUIThreadSync(() =>
                        {
                            HoverNoteDisplayControl = new NoteIcon(_server);
                        });
                    }
                    LastConnectTime = LocalityConnectRecorder.ConnectTimeGet(_server);
                    TagString = string.Join(" ", _server.Tags.Select(x => "#" + x));
                    RaisePropertyChanged(nameof(TagString));
                    ReLoadTags();
                    RaisePropertyChanged(nameof(Id));
                    RaisePropertyChanged(nameof(DisplayName));
                    RaisePropertyChanged(nameof(SubTitle));
                    RaisePropertyChanged(nameof(ProtocolDisplayNameInShort));
                    IsViewable = IsEditable = _server.DataSource?.IsWritable == true;
                    RaisePropertyChanged(nameof(DataSource));
                    RaisePropertyChanged(nameof(IsViewable));
                    RaisePropertyChanged(nameof(IsEditable));
                    LauncherMainTitleViewModel = null;
                    LauncherSubTitleViewModel = null;
                    // after an edit the server is a different object: the detail panel actions (Duplicate, Delete...) must use the new one
                    if (_detailActionsBuilt) RefreshDetailActions();
                }
                RaisePropertyChanged();
            }
        }

        public ProtocolBaseViewModel(ProtocolBase psb)
        {
            Server = psb;
            _server = psb;
            
            // initialize CustomOrder
            CustomOrder = LocalityListViewService.Settings.ServerCustomOrder.GetValueOrDefault(psb.Id, 0);

            if (ConverterNoteToVisibility.IsVisible(Server.Note))
            {
                Execute.OnUIThreadSync(() =>
                {
                    HoverNoteDisplayControl = new NoteIcon(this.Server);
                });
            }
        }

        private ServerTitleViewModel? _launcherMainTitleViewModel;
        public ServerTitleViewModel? LauncherMainTitleViewModel
        {
            get => _launcherMainTitleViewModel ??= new ServerTitleViewModel(Server.DisplayName);
            private set => SetAndNotifyIfChanged(ref _launcherMainTitleViewModel, value);
        }


        private ServerTitleViewModel? _launcherSubTitleViewModel = null;
        public ServerTitleViewModel? LauncherSubTitleViewModel
        {
            get => _launcherSubTitleViewModel ??= new ServerTitleViewModel(Server.SubTitle);
            private set => SetAndNotifyIfChanged(ref _launcherSubTitleViewModel, value);
        }

        private NoteIcon? _hoverNoteDisplayControl = null;
        public NoteIcon? HoverNoteDisplayControl
        {
            get => _hoverNoteDisplayControl;
            set => SetAndNotifyIfChanged(ref _hoverNoteDisplayControl, value);
        }

        private bool _isSelected = false;
        /// <summary>
        /// is selected in list of MainWindow?
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetAndNotifyIfChanged(ref _isSelected, value);
        }


        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            private set => SetAndNotifyIfChanged(ref _isVisible, value);
        }

        public virtual void SetIsVisible(bool isVisible)
        {
            IsVisible = isVisible;
        }

        private DateTime _lastConnectTime = DateTime.MinValue;
        public DateTime LastConnectTime
        {
            get => _lastConnectTime;
            set
            {
                if (SetAndNotifyIfChanged(ref _lastConnectTime, value))
                    RaisePropertyChanged(nameof(LastConnectText));
            }
        }

        private List<ProtocolAction>? _actions;

        public List<ProtocolAction>? Actions
        {
            get => _actions;
            set => SetAndNotifyIfChanged(ref _actions, value);
        }

        public void ClearActions()
        {
            Actions = null;
        }
        public void BuildActions()
        {
            Actions = this.GetActions();
        }


        #region CMD

        private RelayCommand? _cmdConnServer;
        public RelayCommand? CmdConnServer
        {
            get
            {
                return _cmdConnServer ??= new RelayCommand(o =>
                {
                    GlobalEventHelper.OnRequestServerConnect?.Invoke(Server, fromView: nameof(MainWindowView));
                });
            }
        }

        private RelayCommand? _cmdEditServer;
        public RelayCommand CmdEditServer
        {
            get
            {
                // read-only source (e.g. Teams without write permission): the Edit button stays disabled instead of failing later
                return _cmdEditServer ??= new RelayCommand((o) =>
                {
                    GlobalEventHelper.OnRequestGoToServerEditPage?.Invoke(server: Server, showAnimation: true);
                }, o => Server.DataSource?.IsWritable != false);
            }
        }

        #endregion CMD
    }

    public class ProtocolBaseViewModelDummy : ProtocolBaseViewModel
    {
        public ProtocolBaseViewModelDummy(DataSourceBase source) : base(new Dummy() { DataSource = source })
        {
            base.SetIsVisible(false);
        }

        public override void SetIsVisible(bool isVisible)
        {
            base.SetIsVisible(false);
        }
    }

    /// <summary>a server's actions grouped for the detail panel: Connect with.., Duplicate, Copy, Other, Delete</summary>
    public class DetailActionSet
    {
        public DetailActionSet(List<ProtocolAction> all)
        {
            ConnectActions = all.Where(x => x.Kind == ActionKind.Connect).ToList();
            CopyActions = all.Where(x => x.Kind == ActionKind.Copy).ToList();
            OtherActions = all.Where(x => x.Kind == ActionKind.Other).ToList();
            ExportActions = all.Where(x => x.Kind == ActionKind.Export).ToList();
            Duplicate = all.FirstOrDefault(x => x.Kind == ActionKind.Duplicate);
            Delete = all.FirstOrDefault(x => x.Kind == ActionKind.Delete);
        }
        public List<ProtocolAction> ConnectActions { get; }
        public List<ProtocolAction> CopyActions { get; }
        public List<ProtocolAction> OtherActions { get; }
        /// <summary>export RDP file (RDP only) and export shortcut (.mora): the "Export" button of the detail panel</summary>
        public List<ProtocolAction> ExportActions { get; }
        public bool HasExportActions => ExportActions.Count > 0;
        public string ExportLabel => Lemora.Setup.SetupText.T("export_btn");
        public ProtocolAction? Duplicate { get; }
        public ProtocolAction? Delete { get; }
        public bool HasConnectActions => ConnectActions.Count > 0;
        public bool HasCopyActions => CopyActions.Count > 0;
        public bool HasOtherActions => OtherActions.Count > 0;
        public bool HasDuplicate => Duplicate != null;
        public bool HasDelete => Delete != null;
    }
}
