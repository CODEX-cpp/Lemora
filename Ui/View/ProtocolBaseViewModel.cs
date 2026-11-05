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

        /// <summary>voce condivisa dal server Teams: si mostra insieme alle locali (stesso gruppo, stesse cartelle) con l'icona della nuvola</summary>
        public bool IsTeams => DataSource is TeamsSource;

        /// <summary>orologio "in attesa dell'amministratore": voce Teams con una richiesta (permesso "richieste") non ancora decisa,
        /// oppure voce locale inviata come proposta (connessioni personali, sola lettura o richieste) non ancora decisa</summary>
        public bool IsTeamsPending => IsTeams ? Lemora.Setup.TeamsSync.IsPending(Id) : Lemora.Setup.TeamsSync.IsPersonalPending(Id);

        /// <summary>origine con cui la voce si raggruppa in elenco e albero: le voci Teams stanno con quelle locali</summary>
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

        // testi per le righe della lista e per il pannello dettaglio (restyle "Fluent Islands")
        // ---- preferiti (salvati nel database, per id del server)
        private const string FavoritesKey = "Favorites";
        private const string FavoritesDbKey = "favorites";
        private static HashSet<string>? _favoritesCache;
        private static readonly object FavoritesLock = new object();

        /// <summary>
        /// Legge i preferiti dal database. Se il database non e' ancora pronto restituisce un insieme vuoto senza metterlo in cache.
        /// Al primo avvio importa i preferiti che stavano nelle impostazioni locali e li cancella da li' (dopo la scrittura nel database).
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
                if (_favoritesCache == null) return;   // database non pronto: non si scrive nulla
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

        /// <summary>azioni del server (le stesse del vecchio menu della riga), mostrate nel pannello dettaglio</summary>
        private DetailActionSet _detailActionSet = new DetailActionSet(new List<ProtocolAction>());
        public DetailActionSet DetailActionSet
        {
            get => _detailActionSet;
            private set => SetAndNotifyIfChanged(ref _detailActionSet, value);
        }
        /// <summary>da chiamare quando il server viene mostrato nel pannello dettaglio</summary>
        private bool _detailActionsBuilt;
        public void RefreshDetailActions()
        {
            _detailActionsBuilt = true;
            DetailActionSet = new DetailActionSet(this.GetActions());
        }

        /// <summary>
        /// il permesso dell'origine e' cambiato (Teams: scrittura <-> sola lettura): riletti IsEditable, azioni del pannello e menu,
        /// senza aspettare un cambio di selezione o di vista
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

        // ---- utente: con piu' utenti (propri o delle credenziali della cartella) la riga mostra un menu a tendina per sceglierlo (come nella Home)
        /// <summary>letto dai binding: usa solo la copia in memoria delle credenziali di cartella, mai il database</summary>
        public List<Lemora.Service.UserResolver.UserChoice> UserChoices => Server is ProtocolBaseWithAddressPortUserPwd u
            ? Lemora.Service.UserResolver.GetChoices(u, Lemora.Service.FolderCredentialService.Cached)
            : new List<Lemora.Service.UserResolver.UserChoice>();
        public bool HasUserChoices => UserChoices.Count > 1;
        /// <summary>"Chiedi sempre" se il server chiede l'utente a ogni collegamento, altrimenti l'utente predefinito (o il primo)</summary>
        public string UserLabel
        {
            get
            {
                var choices = UserChoices;
                if (choices.Count < 2 || Server is not ProtocolBaseWithAddressPortUserPwd u) return UserText;
                return Lemora.Service.UserResolver.RowLabel(u, choices);
            }
        }
        /// <summary>dopo un cambio di utenti o di credenziali di cartella: la riga rilegge le scelte</summary>
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
                    // dopo una modifica il server e' un altro oggetto: le azioni del pannello dettaglio (Duplica, Elimina...) devono usare quello nuovo
                    if (_detailActionsBuilt) RefreshDetailActions();
                }
                RaisePropertyChanged();
            }
        }

        public ProtocolBaseViewModel(ProtocolBase psb)
        {
            Server = psb;
            _server = psb;
            
            // 初始化 CustomOrder
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
                // origine in sola lettura (es. Teams senza permesso di scrittura): il pulsante Modifica resta spento invece di dare errore dopo
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

    /// <summary>azioni di un server raggruppate per il pannello dettaglio: Connetti con.., Duplica, Copia, Altro, Elimina</summary>
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
        /// <summary>esporta file RDP (solo RDP) ed esporta collegamento (.mora): il pulsante "Esporta" del pannello dettaglio</summary>
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
