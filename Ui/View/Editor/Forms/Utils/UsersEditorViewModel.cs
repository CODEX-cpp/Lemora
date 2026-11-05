using System;
using System.Collections.ObjectModel;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Utils;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.View.Editor.Forms.Utils
{
    /// <summary>riga "utente locale" dell'editor</summary>
    public class UserRow : NotifyPropertyChangedBase
    {
        private string _userName = "";
        public string UserName { get => _userName; set => SetAndNotifyIfChanged(ref _userName, value); }

        private string _password = "";
        public string Password { get => _password; set => SetAndNotifyIfChanged(ref _password, value); }

        private string _privateKey = "";
        public string PrivateKey { get => _privateKey; set => SetAndNotifyIfChanged(ref _privateKey, value); }

        private bool _usePrivateKey;
        public bool UsePrivateKey
        {
            get => _usePrivateKey;
            set { if (SetAndNotifyIfChanged(ref _usePrivateKey, value)) RaisePropertyChanged(nameof(CanShare)); }
        }

        private bool _share;
        /// <summary>al salvataggio l'utente diventa una credenziale condivisa della cartella</summary>
        public bool Share { get => _share; set => SetAndNotifyIfChanged(ref _share, value); }

        /// <summary>le chiavi private non si condividono: solo utente e password</summary>
        public bool CanShare => !UsePrivateKey;
    }

    /// <summary>credenziale condivisa della cartella, attivabile su questo server</summary>
    public class SharedCredentialToggle : NotifyPropertyChangedBase
    {
        public Credential Credential { get; }
        public string Id => Credential.DatabaseId;
        public string UserName => Credential.UserName;
        public string Key => UserResolver.FolderKey(Id);

        private bool _isActive;
        public bool IsActive { get => _isActive; set => SetAndNotifyIfChanged(ref _isActive, value); }

        public SharedCredentialToggle(Credential credential, bool isActive)
        {
            Credential = credential;
            _isActive = isActive;
        }
    }

    /// <summary>voce del menu "Utente predefinito"</summary>
    public class UserChoice
    {
        public string Key { get; init; } = "";
        public string Display { get; init; } = "";
    }

    /// <summary>
    /// Sezione "Utenti" dell'editor: credenziali condivise della cartella (attivabili), utenti locali del server,
    /// utente predefinito e "chiedi sempre quale utente usare". Al salvataggio scrive tutto sul server.
    /// </summary>
    public class UsersEditorViewModel : NotifyPropertyChangedBaseScreen
    {
        private readonly ProtocolBaseWithAddressPortUserPwd _server;

        public bool CanUsePrivateKey { get; }
        public ObservableCollection<UserRow> Rows { get; } = new ObservableCollection<UserRow>();
        public ObservableCollection<SharedCredentialToggle> SharedCredentials { get; } = new ObservableCollection<SharedCredentialToggle>();
        public ObservableCollection<UserChoice> DefaultChoices { get; } = new ObservableCollection<UserChoice>();

        private string _folderPath = "";
        public string FolderPath => _folderPath;
        public string FolderName => FolderService.Leaf(_folderPath);
        public bool HasFolder => !string.IsNullOrEmpty(_folderPath);
        public bool HasSharedCredentials => SharedCredentials.Count > 0;
        public bool ShowNoSharedHint => HasFolder && !HasSharedCredentials;

        private bool _askWhichUser;
        public bool AskWhichUser
        {
            get => _askWhichUser;
            set
            {
                if (SetAndNotifyIfChanged(ref _askWhichUser, value))
                    RaisePropertyChanged(nameof(ShowDefault));
            }
        }

        private string _defaultUserKey = "";
        public string DefaultUserKey { get => _defaultUserKey; set => SetAndNotifyIfChanged(ref _defaultUserKey, value ?? ""); }

        public string SharedHeader => string.Format(IoC.Translate("users_shared_header"), FolderName);
        public string SharedNote => string.Format(IoC.Translate("users_shared_note"), FolderName);
        public string NoSharedText => string.Format(IoC.Translate("users_no_shared"), FolderName);

        public bool ShowDefault => !AskWhichUser && DefaultChoices.Count > 1;

        public UsersEditorViewModel(ProtocolBaseWithAddressPortUserPwd server)
        {
            _server = server;
            CanUsePrivateKey = server is SSH or SFTP;
            _askWhichUser = server.AskWhichUser == true;
            _defaultUserKey = server.DefaultUserKey;

            // un server nuovo nasce con l'utente di default del costruttore (Administrator / root) senza password: non e' una scelta, quindi nessuna riga
            var constructorDefault = server.Users.Count == 0 && server.IsTmpSession() && string.IsNullOrEmpty(server.Password) && string.IsNullOrEmpty(server.PrivateKey) && server.UserName is "Administrator" or "root";
            if (constructorDefault)
            {
                // ...e non va nemmeno mostrato: il riepilogo diceva "Administrator" ma al salvataggio (nessuna riga) l'utente restava vuoto
                server.UserName = "";
            }
            else if (server.Users.Count > 0)
            {
                foreach (var u in server.Users) Rows.Add(FromEntry(u));
            }
            else if (!string.IsNullOrEmpty(server.UserName) || !string.IsNullOrEmpty(server.Password) || !string.IsNullOrEmpty(server.PrivateKey))
            {
                // server salvato prima dei piu' utenti: il suo utente di sempre diventa la prima riga
                Rows.Add(new UserRow
                {
                    UserName = server.UserName,
                    Password = server.Password,
                    PrivateKey = server.PrivateKey,
                    UsePrivateKey = CanUsePrivateKey && server.UsePrivateKeyForConnect == true,
                });
            }
            // nessuna riga di partenza: un nuovo server non ha utenti finche' non si preme "+" (una riga pre-compilata e lasciata li' veniva salvata come utente)
            Rows.CollectionChanged += (_, _) => RebuildDefaults();
            foreach (var r in Rows) r.PropertyChanged += OnRowChanged;
            RebuildDefaults();
        }

        private static UserRow FromEntry(UserEntry u) => new UserRow { UserName = u.UserName, Password = u.Password, PrivateKey = u.PrivateKey, UsePrivateKey = u.UsePrivateKey };

        private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UserRow.UserName)) RebuildDefaults();
        }

        /// <summary>chiamato dall'editor quando si sceglie o si cambia la cartella</summary>
        public void SetFolder(string folderPath)
        {
            var changed = !string.Equals(_folderPath, folderPath, StringComparison.CurrentCultureIgnoreCase);
            var activeBefore = SharedCredentials.Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
            if (SharedCredentials.Count == 0) foreach (var id in _server.ActiveFolderCredentialIds) activeBefore.Add(id);
            _folderPath = folderPath ?? "";
            RebuildShared(changed ? new System.Collections.Generic.HashSet<string>() : activeBefore);
            foreach (var row in Rows) if (!HasFolder) row.Share = false;
            RaisePropertyChanged(nameof(FolderPath));
            RaisePropertyChanged(nameof(FolderName));
            RaisePropertyChanged(nameof(SharedHeader));
            RaisePropertyChanged(nameof(SharedNote));
            RaisePropertyChanged(nameof(NoSharedText));
            RaisePropertyChanged(nameof(HasFolder));
        }

        private void RebuildShared(System.Collections.Generic.HashSet<string> active)
        {
            foreach (var t in SharedCredentials) t.PropertyChanged -= OnToggleChanged;
            SharedCredentials.Clear();
            if (HasFolder)
            {
                foreach (var c in FolderCredentialService.ApplicableTo(FolderService.ToNodes(_folderPath)))
                {
                    var t = new SharedCredentialToggle(c, active.Contains(c.DatabaseId));
                    t.PropertyChanged += OnToggleChanged;
                    SharedCredentials.Add(t);
                }
            }
            RaisePropertyChanged(nameof(HasSharedCredentials));
            RaisePropertyChanged(nameof(ShowNoSharedHint));
            RebuildDefaults();
        }

        private void OnToggleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SharedCredentialToggle.IsActive)) RebuildDefaults();
        }

        /// <summary>l'elenco dei possibili utenti predefiniti = credenziali condivise attive + utenti locali con nome</summary>
        private void RebuildDefaults()
        {
            var previous = DefaultUserKey;
            DefaultChoices.Clear();
            foreach (var t in SharedCredentials.Where(x => x.IsActive))
                DefaultChoices.Add(new UserChoice { Key = t.Key, Display = $"{t.UserName}  ·  {FolderService.Leaf(t.Credential.FolderPath)}" });
            foreach (var r in Rows.Where(x => !string.IsNullOrWhiteSpace(x.UserName)))
                DefaultChoices.Add(new UserChoice { Key = UserResolver.LocalKey(r.UserName.Trim()), Display = r.UserName.Trim() });
            DefaultUserKey = DefaultChoices.Any(x => x.Key == previous) ? previous : DefaultChoices.FirstOrDefault()?.Key ?? "";
            RaisePropertyChanged(nameof(ShowDefault));
        }

        // ---- comandi
        private RelayCommand? _cmdAddUser;
        public RelayCommand CmdAddUser => _cmdAddUser ??= new RelayCommand(o =>
        {
            var row = new UserRow();
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        });

        private RelayCommand? _cmdRemoveUser;
        public RelayCommand CmdRemoveUser => _cmdRemoveUser ??= new RelayCommand(o =>
        {
            if (o is not UserRow row) return;
            // una riga ancora vuota si toglie senza chiedere; una con dati dentro (utente, password) chiede conferma
            var hasData = !string.IsNullOrWhiteSpace(row.UserName) || !string.IsNullOrEmpty(row.Password) || !string.IsNullOrEmpty(row.PrivateKey);
            if (hasData && MessageBoxHelper.Confirm(IoC.Translate("confirm_to_delete"), ownerViewModel: IoC.Get<MainWindowViewModel>()) != true) return;
            row.PropertyChanged -= OnRowChanged;
            Rows.Remove(row);
        });

        private RelayCommand? _cmdSelectKey;
        public RelayCommand CmdSelectKey => _cmdSelectKey ??= new RelayCommand(o =>
        {
            if (o is not UserRow row) return;
            var path = SelectFileHelper.OpenFile(filter: "ppk|*.*", currentDirectoryForShowingRelativePath: Environment.CurrentDirectory, selectedFileName: row.PrivateKey);
            if (path != null) row.PrivateKey = path;
        });

        private RelayCommand? _cmdManageFolder;
        /// <summary>apre il pannello delle credenziali della cartella e, alla chiusura, ricarica l'elenco</summary>
        public RelayCommand CmdManageFolder => _cmdManageFolder ??= new RelayCommand(async o =>
        {
            if (!HasFolder) return;
            var dlg = new FolderSettingsDialogViewModel(_folderPath);
            MaskLayerController.ShowWindowWithMask(dlg, IoC.Get<MainWindowViewModel>());
            await dlg.WaitDialogResult();
            var active = SharedCredentials.Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
            RebuildShared(active);
        });

        /// <summary>scrive sul server utenti, credenziali attive, utente predefinito e "chiedi sempre"; crea le credenziali condivise richieste</summary>
        public void ApplyToServer()
        {
            var activeIds = SharedCredentials.Where(x => x.IsActive).Select(x => x.Id).ToList();

            // utenti da condividere con la cartella: diventano credenziali di cartella e attive su questo server
            foreach (var row in Rows.Where(x => x.Share && HasFolder && x.CanShare && !string.IsNullOrWhiteSpace(x.UserName)).ToList())
            {
                var name = row.UserName.Trim();
                var existing = FolderCredentialService.ForFolder(_folderPath)
                    .FirstOrDefault(c => string.Equals(c.UserName, name, StringComparison.CurrentCultureIgnoreCase));
                if (existing == null)
                {
                    var ret = FolderCredentialService.Add(_folderPath, name, row.Password, out var added);
                    if (ret.IsSuccess && added != null) existing = added;
                }
                if (existing == null) continue;   // non si e' potuta creare: l'utente resta locale
                if (!activeIds.Contains(existing.DatabaseId)) activeIds.Add(existing.DatabaseId);
                if (DefaultUserKey == UserResolver.LocalKey(name)) DefaultUserKey = UserResolver.FolderKey(existing.DatabaseId);
                Rows.Remove(row);
            }

            _server.Users = new ObservableCollection<UserEntry>(Rows.Where(x => !string.IsNullOrWhiteSpace(x.UserName)).Select(x => new UserEntry
            {
                UserName = x.UserName.Trim(),
                Password = x.UsePrivateKey ? "" : x.Password,
                PrivateKey = x.UsePrivateKey ? x.PrivateKey : "",
                UsePrivateKey = x.UsePrivateKey,
            }));
            _server.ActiveFolderCredentialIds = activeIds;
            // con una sola credenziale utilizzabile non c'e' nulla da chiedere: la riga nella Home mostra quella, non "Chiedi sempre"
            _server.AskWhichUser = AskWhichUser && activeIds.Count + _server.Users.Count > 1;

            // utente predefinito valido
            var keys = activeIds.Select(UserResolver.FolderKey).Concat(_server.Users.Select(x => UserResolver.LocalKey(x.UserName))).ToList();
            _server.DefaultUserKey = keys.Contains(DefaultUserKey) ? DefaultUserKey : keys.FirstOrDefault() ?? "";

            // campi "storici" del server (elenco, riepilogo, esportazioni): rispecchiano l'utente predefinito
            var local = _server.Users.FirstOrDefault(x => UserResolver.LocalKey(x.UserName) == _server.DefaultUserKey);
            if (local != null)
            {
                _server.UserName = local.UserName;
                _server.UsePrivateKeyForConnect = local.UsePrivateKey;
                _server.Password = local.UsePrivateKey ? "" : local.Password;
                _server.PrivateKey = local.UsePrivateKey ? local.PrivateKey : "";
            }
            else
            {
                var shared = SharedCredentials.FirstOrDefault(x => x.Key == _server.DefaultUserKey);
                _server.UserName = shared?.UserName ?? "";
                _server.UsePrivateKeyForConnect = false;
                _server.Password = "";
                _server.PrivateKey = "";
            }
            // con utenti configurati la richiesta della password e' decisa alla connessione, per l'utente scelto;
            // senza nessun utente si chiede alla connessione
            _server.AskPasswordWhenConnect = keys.Count == 0;
        }
    }
}
