using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.DataSource.Model;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View.Editor.Forms;
using Lemora.View.Editor.Forms.AlternativeCredential;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.FileSystem;
using Shawn.Utils.Wpf.Image;
using Stylet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Credential = Lemora.Model.Protocol.Base.Credential;

namespace Lemora.View.Editor
{
    public partial class ServerEditorPageViewModel : NotifyPropertyChangedBase
    {
        public class ParamsServerAddPreset
        {
            public DataSourceBase? DataSource = null;
            public List<string> TreeNodes = new List<string>();
            public List<string> TagNames = new List<string>();
            /// <summary>protocollo con cui aprire la creazione (nome, es. "VNC"); vuoto = predefinito</summary>
            public string? Protocol = null;
            /// <summary>indirizzo (con porta facoltativa) da inserire gia' nel form</summary>
            public string? Host = null;
            /// <summary>nome del server e credenziali da inserire gia' nel form (es. salvataggio di una connessione rapida appena chiusa)</summary>
            public string? DisplayName = null;
            public string? UserName = null;
            public string? Password = null;
            public string? PrivateKey = null;
        }

        private readonly GlobalData _globalData;

        public bool IsBuckEdit => _serversInBuckEdit?.Count() > 1;
        private readonly ProtocolBase _orgServer; // to remember original protocol's options, for restore data when switching protocols

        public readonly ParamsServerAddPreset? PresetParams = null; // for Add mode, to remember the preset params, such as tags, tree nodes, etc.

        public static ServerEditorPageViewModel Add(GlobalData globalData, DataSourceBase addToDataSource, ParamsServerAddPreset? preset)
        {
            ProtocolBase server = new RDP();
            if (!string.IsNullOrEmpty(preset?.Protocol))
            {
                var proto = IoC.Get<Lemora.View.Launcher.QuickConnectionViewModel>().Protocols.FirstOrDefault(x => x.Protocol == preset!.Protocol);
                if (proto != null) server = (ProtocolBase)Activator.CreateInstance(proto.GetType())!;
            }
            // un server nuovo parte con "Chiedi sempre quale utente usare" attivo (si toglie nel modulo se non serve)
            if (server is ProtocolBaseWithAddressPortUserPwd askUser) askUser.AskWhichUser = true;
            server.Tags = new List<string>(preset?.TagNames ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(preset?.Host) && server is ProtocolBaseWithAddressPort withAddress)
            {
                var host = preset!.Host!.Trim();
                var i = host.LastIndexOf(':');
                if (i > 0 && host.IndexOf(':') == i && int.TryParse(host.Substring(i + 1), out _))
                {
                    withAddress.Address = host.Substring(0, i);
                    withAddress.Port = host.Substring(i + 1);
                }
                else
                {
                    withAddress.Address = host;
                }
            }
            if (!string.IsNullOrWhiteSpace(preset?.DisplayName)) server.DisplayName = preset!.DisplayName!.Trim();
            if (server is ProtocolBaseWithAddressPortUserPwd withUser)
            {
                if (!string.IsNullOrEmpty(preset?.UserName)) withUser.UserName = preset!.UserName!;
                if (!string.IsNullOrEmpty(preset?.PrivateKey))
                {
                    withUser.UsePrivateKeyForConnect = true;
                    withUser.PrivateKey = preset!.PrivateKey!;
                }
                else if (!string.IsNullOrEmpty(preset?.Password))
                {
                    withUser.Password = preset!.Password!;
                }
            }
            return new ServerEditorPageViewModel(globalData, server, addToDataSource, preset);
        }

        public static ServerEditorPageViewModel Duplicate(GlobalData globalData, DataSourceBase dataSource, ProtocolBase server)
        {
            Debug.Assert(server.IsTmpSession() == false);
            var s = (ProtocolBase)server.Clone();
            s.Id = "";
            return new ServerEditorPageViewModel(globalData, s, dataSource);
        }

        public static ServerEditorPageViewModel Edit(GlobalData globalData, ProtocolBase server)
        {
            Debug.Assert(server.IsTmpSession() == false);
            Debug.Assert(server.DataSource != null);
            return new ServerEditorPageViewModel(globalData, server, server.DataSource);
        }

        public static ServerEditorPageViewModel BuckEdit(GlobalData globalData, IEnumerable<ProtocolBase> servers)
        {
            return new ServerEditorPageViewModel(globalData, servers);
        }

        /// <summary>
        /// Add or Edit or Duplicate
        /// </summary>
        private ServerEditorPageViewModel(GlobalData globalData, ProtocolBase server, DataSourceBase addToDataSource, ParamsServerAddPreset? preset = null)
        {
            EditorUiState.Instance.Highlight = false;
            PresetParams = preset;
            _globalData = globalData;

            server.DecryptToConnectLevel();

            Server = (ProtocolBase)server.Clone();
            if (Server.IsTmpSession()) // Add or Duplicate mode
            {
                Server.DataSource = addToDataSource;
                Server.Id = string.Empty; // set id to empty so that we turn into Add / Duplicate mode
                // destinazione predefinita: Teams se il PC ha il permesso richieste o scrittura (origine Teams scrivibile) E l'interruttore "Condividi le mie connessioni" e' acceso;
                // a interruttore spento Locale (ERRORI 97). Se il modulo si e' aperto dall'origine Teams resta Teams.
                // Permesso richieste: sempre Teams (con l'interruttore acceso non si chiede, spento si chiede con "Manda al server", acceso in partenza)
                var link = Lemora.Setup.TeamsLink.Load();
                _saveTargetIndex = addToDataSource is TeamsSource || (WritableTeams != null && (link.SharePersonal || link.Permission == "requests")) ? 1 : 0;
            }
            else // edit mode
            {
            }
            _orgServer = (ProtocolBase)Server.Clone();
            Title = "";


            // reflect remote protocols
            ProtocolList = ProtocolBase.GetAllSubInstance();
            // set selected protocol
            try
            {
                SelectedProtocol = ProtocolList.First(x => x.GetType() == Server.GetType());
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                UnifyTracing.Error(e);
                SelectedProtocol = ProtocolList.First();
            }

            Debug.Assert(IsBuckEdit == false);
        }


        #region buck edit
        /// <summary>
        /// to remember original protocols' options, for restore use
        /// </summary>
        private readonly IEnumerable<ProtocolBase>? _serversInBuckEdit = null;
        /// <summary>
        /// the common parent class of _serversInBuckEdit
        /// </summary>
        private readonly Type? _sharedTypeInBuckEdit = null;
        private readonly List<string> _sharedTagsInBuckEdit = new List<string>();
        private readonly List<Credential> _sharedCredentialsInBuckEdit = new List<Credential>();


        /// <summary>
        /// BuckEdit
        /// </summary>
        private ServerEditorPageViewModel(GlobalData globalData, IEnumerable<ProtocolBase> servers)
        {
            EditorUiState.Instance.Highlight = false;
            _globalData = globalData;
            var serverBases = servers.Select(x => x.Clone()).ToArray();
            Debug.Assert(serverBases.Count() > 1);
            // decrypt
            for (int i = 0; i < serverBases.Length; i++)
            {
                // decrypt pwd
                serverBases[i].DecryptToConnectLevel();
            }

            // must be bulk edit
            Debug.Assert(serverBases.Length > 1);
            // init title
            Title = IoC.Translate("server_editor_bulk_editing_title") + " ";
            foreach (var serverBase in serverBases)
            {
                Title += serverBase.DisplayName;
                if (serverBases.Last() != serverBase)
                    Title += ", ";
            }


            Server = (ProtocolBase)serverBases.First().Clone();
            _serversInBuckEdit = serverBases;


            // find the common base class
            {
                var types = new List<Type>();
                foreach (var server in serverBases)
                {
                    if (types.All(x => x != server.GetType()))
                        types.Add(server.GetType());
                }

                var type = types.First();
                for (int i = 1; i < types.Count; i++)
                {
                    var type_new = AssemblyHelper.FindCommonBaseClass(type, types[i]);
                    if (type != type_new && type_new != null)
                    {
                        type = type_new;
                        // 将 Server 转换成这个公共的类型，以便批量编辑时 AlternativeCredentialEditViewModel 能够正确用户名、密码等字段的显示
                        Server = (ProtocolBase)serverBases[i].Clone();
                    }
                }

                Debug.Assert(type == typeof(ProtocolBase) || type.IsSubclassOf(typeof(ProtocolBase)));
                _sharedTypeInBuckEdit = type;
            }

            // copy the same value properties
            {
                // set the different options to `ServerEditorDifferentOptions` or null
                var properties = _sharedTypeInBuckEdit.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var property in properties)
                {
                    if (property.SetMethod?.IsPublic != true || property.SetMethod.IsAbstract != false) continue;
                    var x = serverBases.Select(x => property.GetValue(x)).ToArray();
                    if (x.Distinct().Count() <= 1) continue;
                    if (property.PropertyType == typeof(string))
                        property.SetValue(Server, Server.ServerEditorDifferentOptions);
                    else
                        property.SetValue(Server, null);
                }
            }

            // tags
            {
                _sharedTagsInBuckEdit = new List<string>(); // remember the common tags
                bool isAllTheSameFlag = true;
                for (var i = 0; i < serverBases.Length; i++)
                {
                    foreach (var tagName in serverBases[i].Tags)
                    {
                        if (serverBases.All(x => x.Tags.Contains(tagName)))
                        {
                            _sharedTagsInBuckEdit.Add(tagName);
                        }
                        else
                        {
                            isAllTheSameFlag = false;
                        }
                    }
                }

                var list = new List<string>();
                if (isAllTheSameFlag == false)
                    list.Add(Server.ServerEditorDifferentOptions);
                list.AddRange(_sharedTagsInBuckEdit);
                Server.Tags = list;
            }


            // AlternateCredentials
            if (Server is ProtocolBaseWithAddressPort protocol
                && (_sharedTypeInBuckEdit.IsSubclassOf(typeof(ProtocolBaseWithAddressPort)) || _sharedTypeInBuckEdit == typeof(ProtocolBaseWithAddressPort)))
            {
                var ss = serverBases.Select(x => (ProtocolBaseWithAddressPort)x).ToArray();
                bool isAllTheSameFlag = true;
                foreach (var s in ss)
                {
                    foreach (var c in s.AlternateCredentials)
                    {
                        if (ss.All(x => x.AlternateCredentials.Any(y => y.IsValueEqualTo(c) == true)))
                        {
                            if (_sharedCredentialsInBuckEdit.All(x => x.IsValueEqualTo(c) == false))
                            {
                                _sharedCredentialsInBuckEdit.Add(c);
                            }
                        }
                        else
                        {
                            isAllTheSameFlag = false;
                        }
                    }
                }

                var list = new List<Credential>();
                if (isAllTheSameFlag == false)
                    list.Add(new Credential(isEditable: false) { Name = Server.ServerEditorDifferentOptions });
                list.AddRange(_sharedCredentialsInBuckEdit);
                protocol.AlternateCredentials = new ObservableCollection<Credential>(list);
            }

            AppArgumentsBulkInit(_serversInBuckEdit);

            Server.DataSource = serverBases.All(x => x.DataSource == serverBases.First().DataSource) ? serverBases.First().DataSource : null;
            _orgServer = Server.Clone();

            // init ui
            if (_serversInBuckEdit.All(x => x.GetType() == _sharedTypeInBuckEdit))
                UpdateRunners(_serversInBuckEdit.First().Protocol);
            ReflectProtocolEditControl(_sharedTypeInBuckEdit);
            Debug.Assert(IsBuckEdit == true);
        }

        #endregion

        public string Title { get; }

        /// <summary>true se si sta creando una nuova connessione (titolo della barra dell'editor)</summary>
        public bool IsAddMode => Server.IsTmpSession();

        /// <summary>casella "non sincronizzare" della sezione Sessione (connessione solo di questo PC, mai al server Teams)</summary>
        public string DoNotSyncLabel => Lemora.Setup.SetupText.T("tm_nosync");
        public string DoNotSyncHint => Lemora.Setup.SetupText.T("tm_nosync_hint");

        /// <summary>origine Teams se questo PC e' abbinato con permesso di scrittura (TeamsSync la rende scrivibile solo con State "approved" e Permission "write"), altrimenti null</summary>
        private static TeamsSource? WritableTeams => IoC.TryGet<DataSourceService>()?.GetDataSource(TeamsSource.SourceName) is TeamsSource { IsWritable: true } t ? t : null;

        /// <summary>voce nuova (o duplicata) su un PC che puo' scrivere su Teams: scrittura o richieste</summary>
        private bool TeamsAdd => IsAddMode && !IsBuckEdit && WritableTeams != null;
        private static bool IsRequests => Lemora.Setup.TeamsLink.Load().Permission == "requests";

        /// <summary>riga "Salva in: Locale / Teams (cloud)" del modulo: voce nuova con il permesso di scrittura su Teams</summary>
        public bool ShowSaveTarget => TeamsAdd && !IsRequests;

        /// <summary>
        /// Permesso richieste: con "Condividi le mie connessioni" acceso nelle impostazioni ogni voce nuova va al server come richiesta, senza chiedere
        /// (sarebbe una ripetizione); spento, il modulo mostra l'interruttore "Manda al server come richiesta", acceso in partenza
        /// </summary>
        public bool ShowSendRequest => TeamsAdd && IsRequests && !Lemora.Setup.TeamsLink.Load().SharePersonal;

        public bool SendRequest
        {
            get => _saveTargetIndex == 1;
            set { if (SendRequest == value) return; SaveTargetIndex = value ? 1 : 0; RaisePropertyChanged(); }
        }

        private int _saveTargetIndex;
        /// <summary>0 = locale, 1 = Teams (cloud). Si applica al salvataggio: cambiando protocollo il server viene ricreato</summary>
        public int SaveTargetIndex
        {
            get => _saveTargetIndex;
            set => SetAndNotifyIfChanged(ref _saveTargetIndex, value);
        }


        private ProtocolBase _server = new RDP();
        public ProtocolBase Server
        {
            get => _server;
            set
            {
                _server.PropertyChanged -= OnServerPropertyChanged;
                if (SetAndNotifyIfChanged(ref _server, value))
                {
                    _server.PropertyChanged += OnServerPropertyChanged;
                    RaisePropertyChanged(nameof(MissingFieldsText));
                }
            }
        }

        private void OnServerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RaisePropertyChanged(nameof(MissingFieldsText));
            RaisePropertyChanged(nameof(IsReadyToSave));
            RaisePropertyChanged(nameof(SummaryAccess));
            RaisePropertyChanged(nameof(SummaryAdvanced));
        }

        /// <summary>true quando ci sono nome e indirizzo: sblocca il pulsante Aggiungi/Salva</summary>
        public bool IsReadyToSave => !string.IsNullOrWhiteSpace(Server.DisplayName) && !IsAddressMissing;

        /// <summary>manca l'indirizzo (con il prefisso IP attivo l'indirizzo e' solo il prefisso)</summary>
        private bool IsAddressMissing =>
            EditorViewModel is ProtocolBaseWithAddressPortFormViewModel f ? f.HostViewModel.IsAddressEmpty
            : Server is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort a && string.IsNullOrWhiteSpace(a.Address);

        /// <summary>sottotitolo della barra: "in Cartella · Protocollo"</summary>
        public string Subtitle
        {
            get
            {
                var proto = SelectedProtocol?.ProtocolDisplayName ?? "";
                return string.IsNullOrEmpty(SelectedFolder) ? proto : $"{string.Format(IoC.Translate("editor_in_folder"), SelectedFolder)} · {proto}";
            }
        }

        // ---- riepilogo: accesso e opzioni avanzate modificate
        public string SummaryAccess
        {
            get
            {
                if (Server is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd u) return "—";
                if (Server.Protocol == "VNC")
                    return IoC.Translate(u.AskPasswordWhenConnect == true || string.IsNullOrEmpty(u.Password) ? "editor_password_asked" : "editor_password_saved");
                return string.IsNullOrEmpty(u.UserName) ? "—" : u.UserName;
            }
        }

        /// <summary>proprieta' specifiche del protocollo (quelle che stanno nelle sezioni avanzate)</summary>
        private IEnumerable<PropertyInfo> AdvancedProperties()
        {
            var basics = new[] { typeof(ProtocolBase), typeof(ProtocolBaseWithAddressPort), typeof(ProtocolBaseWithAddressPortUserPwd) };
            return Server.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.SetMethod?.IsPublic == true && !basics.Contains(x.DeclaringType!)
                            && (x.PropertyType.IsPrimitive || x.PropertyType.IsEnum || x.PropertyType == typeof(string) || Nullable.GetUnderlyingType(x.PropertyType) != null));
        }

        public string SummaryAdvanced
        {
            get
            {
                try
                {
                    var def = Activator.CreateInstance(Server.GetType());
                    var n = AdvancedProperties().Count(x => !Equals(x.GetValue(Server), x.GetValue(def)));
                    return n == 0 ? IoC.Translate("editor_advanced_default") : string.Format(IoC.Translate(n == 1 ? "editor_advanced_one" : "editor_advanced_n"), n);
                }
                catch { return IoC.Translate("editor_advanced_default"); }
            }
        }

        private RelayCommand? _cmdResetDefaults;
        public RelayCommand CmdResetDefaults => _cmdResetDefaults ??= new RelayCommand(o =>
        {
            var def = Activator.CreateInstance(Server.GetType());
            foreach (var x in AdvancedProperties()) x.SetValue(Server, x.GetValue(def));
            RaisePropertyChanged(nameof(SummaryAdvanced));
        });

        // ---- barra di stato: cosa manca per poter salvare
        public string MissingFieldsText
        {
            get
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(Server.DisplayName)) missing.Add(IoC.Translate("Name").ToLower());
                if (Server is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort && IsAddressMissing) missing.Add(IoC.Translate("Hostname").ToLower());
                return missing.Count == 0 ? IoC.Translate("editor_ready") : string.Format(IoC.Translate("editor_missing"), string.Join(", ", missing));
            }
        }

        // ---- cartella: percorso "A / B" salvato in Server.TreeNodes
        private const string FolderSeparator = " / ";
        /// <summary>valore speciale dell'ultima voce dell'elenco cartelle: "Nuova cartella..."</summary>
        public const string NewFolderMarker = "new-folder";

        public class FolderItem
        {
            public string Path { get; set; } = "";
            public string Display => Path == NewFolderMarker ? "＋  " + IoC.Translate("editor_new_folder") : string.IsNullOrEmpty(Path) ? IoC.Translate("editor_no_folder") : Path;
        }

        public List<FolderItem> FolderChoices => FolderPaths.Append(NewFolderMarker).Select(x => new FolderItem { Path = x }).ToList();

        /// <summary>apre "Nuova cartella" (nome, dominio e rete), crea la cartella e la seleziona</summary>
        private async Task CreateFolderFromEditorAsync()
        {
            var main = IoC.Get<MainWindowViewModel>();
            var dlg = new NewFolderDialogViewModel("", IoC.Translate("folder_new_default_name"));
            MaskLayerController.ShowWindowWithMask(dlg, main);
            if (await dlg.WaitDialogResult() != true) { RaisePropertyChanged(nameof(SelectedFolder)); return; }
            var path = dlg.CreatedPath;
            RaisePropertyChanged(nameof(FolderChoices));
            SelectedFolder = path;
        }

        private List<string> FolderPaths
        {
            get
            {
                var list = new List<string> { "" };
                list.AddRange(Lemora.Service.FolderService.AllPaths());
                if (!string.IsNullOrEmpty(SelectedFolder) && !list.Contains(SelectedFolder)) list.Add(SelectedFolder);
                return list;
            }
        }

        private string? _selectedFolder;
        public string SelectedFolder
        {
            get
            {
                _selectedFolder ??= string.Join(FolderSeparator, PresetParams?.TreeNodes ?? Server.TreeNodes);
                return _selectedFolder;
            }
            set
            {
                if (value == NewFolderMarker)
                {
                    // non e' una cartella: apre la creazione e, intanto, la casella torna al valore di prima.
                    // PostToUIThread (dopo, non subito): OnUIThread girava DENTRO l'aggiornamento della tendina, che ignorava il ritorno al valore
                    // di prima e restava su "Nuova cartella..."; riscegliendola non cambiava nulla e la tendina sembrava non funzionare
                    Execute.PostToUIThread(() => { RaisePropertyChanged(nameof(SelectedFolder)); _ = CreateFolderFromEditorAsync(); });
                    return;
                }
                var previousFolder = SelectedFolder;
                var before = CaptureFolderState();
                if (SetAndNotifyIfChanged(ref _selectedFolder, value ?? ""))
                {
                    ApplyFolderToServer();
                    EditorViewModel?.OnFolderChanged(_selectedFolder ?? "");
                    ApplyFolderDefaults(previousFolder, before);
                    RaisePropertyChanged(nameof(Subtitle));
                }
            }
        }
        private void ApplyFolderToServer()
        {
            var nodes = string.IsNullOrEmpty(SelectedFolder) ? new List<string>() : SelectedFolder.Split(new[] { FolderSeparator }, StringSplitOptions.RemoveEmptyEntries).ToList();
            Server.TreeNodes = nodes;
            if (_serversInBuckEdit == null || !_serversInBuckEdit.Any()) return;
        }

        // ---- preferito: si applica al salvataggio (il server nuovo non ha ancora un id definitivo)
        private bool? _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite ??= ProtocolBaseViewModel.IsFavoriteId(Server.Id);
            set => SetAndNotifyIfChanged(ref _isFavorite, value);
        }
        private RelayCommand? _cmdToggleFavorite;
        public RelayCommand CmdToggleFavorite => _cmdToggleFavorite ??= new RelayCommand(o => IsFavorite = !IsFavorite);

        public ObservableCollection<string> Runners { get; } = new ObservableCollection<string>();

        private ProtocolBase _selectedProtocol = new RDP();
        public ProtocolBase SelectedProtocol
        {
            get => _selectedProtocol;
            set
            {
                if (IsBuckEdit == false)
                {
                    // bulk edit do not allow change protocol
                    if (SetAndNotifyIfChanged(ref _selectedProtocol, value))
                    {
                        if (_orgServer.GetType() == Server.GetType())
                            _orgServer.Update(Server);
                        UpdateRunners(_selectedProtocol.Protocol);
                        UpdateServerWhenProtocolChanged(SelectedProtocol.GetType());
                        ReflectProtocolEditControl(SelectedProtocol.GetType());
                        RaisePropertyChanged(nameof(Subtitle));
                        RaisePropertyChanged(nameof(SummaryAccess));
                        RaisePropertyChanged(nameof(SummaryAdvanced));
                        RaisePropertyChanged(nameof(IsReadyToSave));
                    }
                }
            }
        }


        public List<ProtocolBase> ProtocolList { get; set; } = new List<ProtocolBase>();


        private ProtocolBaseFormViewModel? _editorViewModel;
        public ProtocolBaseFormViewModel? EditorViewModel
        {
            get => _editorViewModel;
            set
            {
                if (_editorViewModel is RdpFormViewModel oldRdp) oldRdp.DomainCommitted -= OnDomainCommitted;
                if (SetAndNotifyIfChanged(ref _editorViewModel, value))
                {
                    if (value is RdpFormViewModel rdp)
                    {
                        rdp.DomainCommitted += OnDomainCommitted;
                        _domainBeforeEdit = rdp.New.Domain ?? "";
                    }
                    value?.OnFolderChanged(SelectedFolder);
                    // nuovo server creato da una cartella: il dominio della cartella e' gia' proposto
                    if (value is RdpFormViewModel r2 && IsAddMode && SelectedFolder.Length > 0 && string.IsNullOrEmpty(r2.New.Domain))
                    {
                        r2.New.Domain = FolderService.EffectiveDomain(SelectedFolder);
                        _domainBeforeEdit = r2.New.Domain;
                    }
                }
            }
        }


        /// <summary>
        /// suggested name for name field
        /// </summary>
        public List<string> NameSelections => _globalData.VmItemList.Select(x => x.Server.DisplayName).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        /// <summary>
        /// suggested tag for tag field
        /// </summary>
        public List<string> TagSelections => _globalData.TagList.Select(x => x.Name).ToList();

        private RelayCommand? _cmdSave;
        public RelayCommand CmdSave
        {
            get
            {
                if (_cmdSave != null) return _cmdSave;
                _cmdSave = new((o) =>
                {
                    if (string.IsNullOrEmpty(Server.DisplayName) || IsAddressMissing || EditorViewModel?.CanSave() != true)
                    {
                        EditorUiState.Instance.Highlight = true;
                        return;
                    }

                    EditorViewModel?.BeforeSave();   // es. crea le credenziali condivise richieste e scrive gli utenti sul server

                    MaskLayerController.ShowMask(IoC.Get<ProcessingRingViewModel>(), IoC.Get<MainWindowViewModel>());


                    Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            var ret = Result.Success();
                            // bulk edit
                            if (IsBuckEdit)
                            {
                                if (_sharedTypeInBuckEdit == null) throw new NullReferenceException($"{nameof(_sharedTypeInBuckEdit)} should not be null!");
                                if (_serversInBuckEdit == null) throw new NullReferenceException($"{nameof(_serversInBuckEdit)} should not be null!");
                                // copy the same value properties
                                var properties = _sharedTypeInBuckEdit.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                foreach (var property in properties)
                                {
                                    if (property.SetMethod?.IsPublic != true
                                        || property.SetMethod.IsAbstract == true
                                        || property.Name == nameof(ProtocolBase.Id)
                                        || property.Name == nameof(ProtocolBase.Tags)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.InheritedCredentialName)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.UsePrivateKeyForConnect)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.UserName)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.Password)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.PrivateKey)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.AlternateCredentials)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.Users)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.ActiveFolderCredentialIds)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.DefaultUserKey)
                                        || property.Name == nameof(ProtocolBaseWithAddressPortUserPwd.AskWhichUser)
                                        || property.Name == nameof(LocalApp.ArgumentList))
                                    {
                                        continue;
                                    }


                                    if (property.PropertyType.IsGenericType
                                        && (property.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                                            || property.PropertyType.GetGenericTypeDefinition() == typeof(ObservableCollection<>)))
                                    {
                                        SimpleLogHelper.Warning(property.Name + " IsGenericType!");
                                    }
                                    var obj = property.GetValue(Server);
                                    if (obj == null)
                                        continue;
                                    else if (obj.ToString() == Server.ServerEditorDifferentOptions)
                                        continue;
                                    else
                                        foreach (var server in _serversInBuckEdit)
                                        {
                                            property.SetValue(server, obj);
                                        }
                                }

                                // handle credentials
                                if (Server is ProtocolBaseWithAddressPortUserPwd sp && sp.InheritedCredentialName != sp.ServerEditorDifferentOptions)
                                {
                                    foreach (var server in _serversInBuckEdit)
                                    {
                                        if (server is not ProtocolBaseWithAddressPortUserPwd protocol) continue;
                                        // do not overwrite the user & pass when the server is inherited from credential
                                        if (protocol.InheritedCredentialName == sp.ServerEditorDifferentOptions && !string.IsNullOrEmpty(protocol.InheritedCredentialName)) continue;
                                        if (sp.InheritedCredentialName != sp.ServerEditorDifferentOptions)
                                            protocol.InheritedCredentialName = sp.InheritedCredentialName;
                                        if (sp.UsePrivateKeyForConnect != null)
                                            protocol.UsePrivateKeyForConnect = sp.UsePrivateKeyForConnect;
                                        if (sp.UserName != sp.ServerEditorDifferentOptions)
                                            protocol.UserName = sp.UserName;
                                        if (sp.Password != sp.ServerEditorDifferentOptions)
                                            protocol.Password = sp.Password;
                                        if (sp.PrivateKey != sp.ServerEditorDifferentOptions)
                                            protocol.PrivateKey = sp.PrivateKey;
                                    }
                                }

                                // merge tags
                                foreach (var server in _serversInBuckEdit)
                                {
                                    // process old tags, remove the not existed tags.
                                    foreach (var tag in server.Tags.ToArray())
                                    {
                                        if (_sharedTagsInBuckEdit.Contains(tag) == true)
                                        {
                                            if (Server.Tags.Contains(tag) == false)
                                                server.Tags.Remove(tag);
                                        }
                                        else
                                        {
                                            if (Server.Tags.Contains(Server.ServerEditorDifferentOptions) == false)
                                                server.Tags.Remove(tag);
                                        }
                                    }

                                    // add new tags
                                    foreach (var tag in Server.Tags.Where(tag => tag != Server.ServerEditorDifferentOptions))
                                    {
                                        server.Tags.Add(tag);
                                    }
                                    server.Tags = server.Tags.Distinct().ToList();
                                }



                                // merge AlternateCredentials
                                if (Server is ProtocolBaseWithAddressPort newServer)
                                    foreach (var server in _serversInBuckEdit)
                                    {
                                        if (server is not ProtocolBaseWithAddressPort protocol) continue;

                                        foreach (var credential in protocol.AlternateCredentials.ToArray())
                                        {
                                            if (_sharedCredentialsInBuckEdit.Any(x => x.IsValueEqualTo(credential))) // 编辑之前共有，编辑后不再共有，删除
                                            {
                                                if (newServer.AlternateCredentials.All(x => x.IsValueEqualTo(credential) == false))
                                                {
                                                    protocol.AlternateCredentials.Remove(credential);
                                                }
                                            }
                                            else
                                            {
                                                if (newServer.AlternateCredentials.All(x => x.Name != Server.ServerEditorDifferentOptions && x.IsEditable != false))
                                                {
                                                    protocol.AlternateCredentials.Remove(credential);
                                                }
                                            }
                                        }

                                        foreach (var credential in newServer.AlternateCredentials.Where(x => x.Name != Server.ServerEditorDifferentOptions))
                                        {
                                            if (protocol.AlternateCredentials.All(x => x.IsValueEqualTo(credential) == false))
                                            {
                                                protocol.AlternateCredentials.Add(credential);
                                            }
                                        }
                                    }

                                AppArgumentsBulkMerge(_serversInBuckEdit);

                                // save
                                ret = _globalData.UpdateServer(_serversInBuckEdit);
                            }
                            else
                            {
                                // if is the first time a tag is added, we pin it to let user know that tags can be pinned on the header.
                                if (LocalityTagService.IsFirstTimeUse() && Server.Tags.Count > 0)
                                {
                                    for (var i = 0; i < Server.Tags.Count; i++)
                                    {
                                        LocalityTagService.UpdateTag(new Tag(Server.Tags[i], true, i));
                                    }
                                }
                                // la cartella scelta nell'editor
                                ApplyFolderToServer();
                                // edit
                                if (Server.IsTmpSession() == false)
                                {
                                    ret = _globalData.UpdateServer(Server);
                                    if (ret.IsSuccess) ProtocolBaseViewModel.SetFavoriteId(Server.Id, IsFavorite);
                                }
                                // add
                                else if (Server.DataSource != null)
                                {
                                    // "Salva in" (scrittura) o richiesta al server (richieste): la voce nuova va nell'origine scelta (Teams passa da TeamsSource.AddServer, cioe' dal server)
                                    if (TeamsAdd)
                                        Server.DataSource = SaveTargetIndex == 1 ? WritableTeams!
                                            : Server.DataSource is TeamsSource ? IoC.Get<DataSourceService>().LocalDataSource ?? Server.DataSource : Server.DataSource;
                                    ret = _globalData.AddServer(Server, Server.DataSource);
                                    if (ret.IsSuccess) ProtocolBaseViewModel.SetFavoriteId(Server.Id, IsFavorite);
                                }
                            }

                            if (ret.IsSuccess)
                            {
                                IoC.Get<MainWindowViewModel>().ShowList(true);
                            }
                            else
                            {
                                MessageBoxHelper.ErrorAlert(ret.ErrorInfo);
                            }
                        }
                        catch (Exception e)
                        {
                            UnifyTracing.Error(e);
                            MessageBoxHelper.ErrorAlert(e.Message);
                        }
                        finally
                        {
                            MaskLayerController.HideMask();
                        }
                    });

                }, o => (this.Server.DisplayName?.Trim() != "" && (EditorViewModel?.CanSave() ?? true)));
                return _cmdSave;
            }
        }



        private RelayCommand? _cmdCancel;
        public RelayCommand CmdCancel
        {
            get
            {
                if (_cmdCancel != null) return _cmdCancel;
                _cmdCancel = new RelayCommand((o) =>
                {
                    IoC.Get<MainWindowViewModel>().ShowList(false);
                });
                return _cmdCancel;
            }
        }




        private void UpdateServerWhenProtocolChanged(Type newProtocolType)
        {
            if (string.IsNullOrEmpty(newProtocolType?.FullName)) return;
            // change protocol
            var protocolServerBaseAssembly = typeof(ProtocolBase).Assembly;
            var server = (ProtocolBase)protocolServerBaseAssembly.CreateInstance(newProtocolType!.FullName)!;
            // restore original server base info
            if (_orgServer.GetType() == server.GetType())
            {
                server.Update(_orgServer);
            }
            else if (_orgServer.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)) && server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)))
            {
                server.Update(_orgServer, typeof(ProtocolBaseWithAddressPortUserPwd));
            }
            else if (_orgServer.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPort)) && server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPort)))
            {
                server.Update(_orgServer, typeof(ProtocolBaseWithAddressPort));
            }
            else
            {
                server.Update(_orgServer, typeof(ProtocolBase));
            }


            // switch protocol and hold user name & pwd.
            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)) && Server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)))
            {
                server.Update(Server, typeof(ProtocolBaseWithAddressPortUserPwd));
            }
            else if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPort)) && Server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPort)))
            {
                server.Update(Server, typeof(ProtocolBaseWithAddressPort));
            }
            // switch just hold base info
            else
            {
                server.Update(Server, typeof(ProtocolBase));
            }


            #region change port and username if the old velue is the default port and username
            if (server is ProtocolBaseWithAddressPort newPort)
            {
                bool isDefaultPort = false;
                if (Server is ProtocolBaseWithAddressPort)
                {
                    var oldPortDefault = (ProtocolBaseWithAddressPort)protocolServerBaseAssembly.CreateInstance(Server.GetType().FullName!)!;
                    isDefaultPort = newPort.Port == oldPortDefault.Port;
                }
                else if (string.IsNullOrEmpty(newPort.Port) || newPort.Port == "3389")
                {
                    isDefaultPort = true;
                }
                if (isDefaultPort)
                {
                    var newDefault = (ProtocolBaseWithAddressPort)protocolServerBaseAssembly.CreateInstance(newProtocolType.FullName)!;
                    newPort.Port = newDefault.Port;
                }
            }


            #endregion

            Server = server;
        }

        /// <summary>
        /// switch UI when Selected protocol changed
        /// keep the common field value between 2 protocols
        /// </summary>
        /// <param name="protocolType"></param>
        private void ReflectProtocolEditControl(Type protocolType)
        {
            Execute.OnUIThreadSync(() =>
            {
                try
                {
                    if (protocolType == typeof(RDP))
                    {
                        EditorViewModel = new RdpFormViewModel((RDP)Server);
                    }
                    else if (protocolType == typeof(RdpApp))
                    {
                        EditorViewModel = new RdpAppFormViewModel((RdpApp)Server);
                    }
                    else if (protocolType == typeof(SSH))
                    {
                        EditorViewModel = new SshFormViewModel((SSH)Server);
                    }
                    else if (protocolType == typeof(Telnet))
                    {
                        EditorViewModel = new TelnetFormViewModel((Telnet)Server);
                    }
                    else if (protocolType == typeof(Serial))
                    {
                        EditorViewModel = new SerialFormViewModel((Serial)Server);
                    }
                    else if (protocolType == typeof(FTP))
                    {
                        EditorViewModel = new FtpFormViewModel((FTP)Server);
                    }
                    else if (protocolType == typeof(SFTP))
                    {
                        EditorViewModel = new SftpFormViewModel((SFTP)Server);
                    }
                    else if (protocolType == typeof(VNC))
                    {
                        EditorViewModel = new VncFormViewModel((VNC)Server);
                    }
                    else if (protocolType == typeof(LocalApp))
                    {
                        EditorViewModel = new LocalAppFormViewModel((LocalApp)Server);
                    }
                    else if (protocolType == typeof(ProtocolBaseWithAddressPortUserPwd))
                    {
                        EditorViewModel = new ProtocolBaseWithAddressPortUserPwdFormViewModel((ProtocolBaseWithAddressPortUserPwd)Server);
                    }
                    else if (protocolType == typeof(ProtocolBaseWithAddressPort))
                    {
                        EditorViewModel = new ProtocolBaseWithAddressPortFormViewModel((ProtocolBaseWithAddressPort)Server);
                    }
                    else if (protocolType == typeof(ProtocolBase))
                    {
                        EditorViewModel = null;
                    }
                    else
                        throw new NotImplementedException($"can not find from for '{protocolType.Name}' in {nameof(ServerEditorPageViewModel)}");
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                    throw;
                }
            });
        }

        private void UpdateRunners(string protocolName)
        {
            var selectedRunner = Server.SelectedRunnerName;
            Runners.Clear();
            if (IoC.Get<ProtocolConfigurationService>().ProtocolConfigs.ContainsKey(protocolName))
            {
                var c = IoC.Get<ProtocolConfigurationService>().ProtocolConfigs[protocolName];
                Runners.Add("Follow the global settings");
                foreach (var runner in c.Runners)
                {
                    Runners.Add(runner.Name);
                }
                if (IsBuckEdit && _orgServer.SelectedRunnerName == Server.ServerEditorDifferentOptions)
                {
                    Runners.Add(Server.ServerEditorDifferentOptions);
                }
                Server.SelectedRunnerName = Runners.Any(x => x == selectedRunner) ? selectedRunner : Runners.First();
            }
            else
            {
                Server.SelectedRunnerName = "";
            }
        }




        private RelayCommand? _cmdSelectScript;
        public RelayCommand CmdSelectScript
        {
            get
            {
                return _cmdSelectScript ??= new RelayCommand((o) =>
                {
                    lock (this)
                    {
                        var path = SelectFileHelper.OpenFile(title: "Select a script", filter: $"script|*.bat;*.cmd;*.ps1;*.py|*|*.*");
                        if (path == null || File.Exists(path) == false) return;
                        if (o?.ToString()?.ToLower() == "before")
                        {
                            Server.CommandBeforeConnected = path;
                        }
                        else
                        {
                            Server.CommandAfterDisconnected = path;
                        }
                    }
                });
            }
        }


        private RelayCommand? _cmdSelectImage;
        public RelayCommand CmdSelectImage
        {
            get
            {
                return _cmdSelectImage ??= new RelayCommand((o) =>
                {
                    var dlg = new IconPopupDialogViewModel(Server.IconImg);
                    MaskLayerController.ShowWindowWithMask(dlg, onCloseWithTrue: () =>
                    {
                        Server.IconBase64 = dlg.Icon.ToBase64();
                    });
                });
            }
        }




        private RelayCommand? _cmdTestScript;
        public RelayCommand CmdTestScript
        {
            get
            {
                return _cmdTestScript ??= new RelayCommand((o) =>
                {
                    MaskLayerController.ShowProcessingRing(assignLayerContainer: IoC.Get<MainWindowViewModel>());
                    Task.Factory.StartNew(() =>
                    {
                        if (o?.ToString()?.ToLower() == "before")
                        {
                            Server.RunScriptBeforeConnect(true);
                        }
                        else
                        {
                            Server.RunScriptAfterDisconnected(true);
                        }
                        MaskLayerController.HideMask(IoC.Get<MainWindowViewModel>());
                    });
                });
            }
        }







        private RelayCommand? _cmdEditCredential;
        public RelayCommand CmdEditCredential
        {
            get
            {
                return _cmdEditCredential ??= new RelayCommand((o) =>
                {
                    if (Server is ProtocolBaseWithAddressPort protocol)
                    {
                        var credential = o as Credential;
                        var existedNames = protocol.AlternateCredentials.Select(x => x.Name).ToList();
                        if (IsBuckEdit && _serversInBuckEdit?.Count() > 0)
                        {
                            foreach (var s in _serversInBuckEdit)
                            {
                                if (s is ProtocolBaseWithAddressPort p)
                                    existedNames.AddRange(p.AlternateCredentials.Select(x => x.Name));
                            }
                        }
                        existedNames = existedNames.Distinct().ToList();
                        var vm = AlternativeCredentialEditViewModel.NewFormProtocol(protocol, existedNames, credential);
                        vm.OnSave += () =>
                        {
                            if (credential != null && protocol.AlternateCredentials.Any(x => x.Equals(credential)))
                            {
                                // edit
                                var i = protocol.AlternateCredentials.IndexOf(credential);
                                protocol.AlternateCredentials.Remove(credential);
                                protocol.AlternateCredentials.Insert(i, vm.New);
                            }
                            else
                            {
                                // add
                                protocol.AlternateCredentials.Add(vm.New);
                            }
                            return true; // close the dialog
                        };
                        MaskLayerController.ShowWindowWithMask(vm);
                    }
                }, o => Server is ProtocolBaseWithAddressPort);
            }
        }




        private RelayCommand? _cmdDeleteCredential;
        public RelayCommand CmdDeleteCredential
        {
            get
            {
                return _cmdDeleteCredential ??= new RelayCommand((o) =>
                {
                    if (o is Credential credential
                        && Server is ProtocolBaseWithAddressPort protocol)
                    {
                        if (MessageBoxHelper.Confirm(IoC.Translate("confirm_to_delete_selected"), ownerViewModel: IoC.Get<MainWindowViewModel>()))
                        {
                            if (protocol.AlternateCredentials.Contains(credential) == true)
                            {
                                protocol.AlternateCredentials.Remove(credential);
                            }
                        }
                    }
                }, o => Server is ProtocolBaseWithAddressPort);
            }
        }
    }
}