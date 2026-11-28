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
    /// <summary>editor "local user" row</summary>
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
        /// <summary>on save the user becomes a shared credential of the folder</summary>
        public bool Share { get => _share; set => SetAndNotifyIfChanged(ref _share, value); }

        /// <summary>private keys are not shared: only user and password</summary>
        public bool CanShare => !UsePrivateKey;
    }

    /// <summary>shared credential of the folder, can be activated on this server</summary>
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

    /// <summary>"Default user" menu item</summary>
    public class UserChoice
    {
        public string Key { get; init; } = "";
        public string Display { get; init; } = "";
    }

    /// <summary>
    /// Editor "Users" section: the folder's shared credentials (can be activated), the server's local users,
    /// default user and "always ask which user to use". On save it writes everything to the server.
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
        /// <summary>"manage in folder" link: not for a read-only Teams PC (it cannot change the folder credentials)</summary>
        public bool CanManageFolder => !Lemora.Setup.TeamsShared.ReadOnlyTeam;
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
            _askWhichUser = server.IsTmpSession() || server.AskWhichUser == true;   // on by default for a new server (only matters with more than one user)
            _defaultUserKey = server.DefaultUserKey;

            // a new server starts with the constructor's default user (Administrator / root) without a password: it is not a choice, so no row
            var constructorDefault = server.Users.Count == 0 && server.IsTmpSession() && string.IsNullOrEmpty(server.Password) && string.IsNullOrEmpty(server.PrivateKey) && server.UserName is "Administrator" or "root";
            if (constructorDefault)
            {
                // ...and it must not be shown either: the summary said "Administrator" but on save (no row) the user stayed empty
                server.UserName = "";
            }
            else if (server.Users.Count > 0)
            {
                foreach (var u in server.Users) Rows.Add(FromEntry(u));
            }
            else if (UserResolver.LegacyUserName(server).Length > 0 || (string.IsNullOrEmpty(server.UserName) && (!string.IsNullOrEmpty(server.Password) || !string.IsNullOrEmpty(server.PrivateKey))))
            {
                // server saved before multiple users existed: its usual user becomes the first row
                Rows.Add(new UserRow
                {
                    UserName = server.UserName,
                    Password = server.Password,
                    PrivateKey = server.PrivateKey,
                    UsePrivateKey = CanUsePrivateKey && server.UsePrivateKeyForConnect == true,
                });
            }
            // no initial row: a new server has no users until "+" is pressed (a prefilled row left there used to be saved as a user)
            Rows.CollectionChanged += (_, _) => RebuildDefaults();
            foreach (var r in Rows) r.PropertyChanged += OnRowChanged;
            RebuildDefaults();
        }

        private static UserRow FromEntry(UserEntry u) => new UserRow { UserName = u.UserName, Password = u.Password, PrivateKey = u.PrivateKey, UsePrivateKey = u.UsePrivateKey };

        private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UserRow.UserName)) RebuildDefaults();
        }

        /// <summary>called by the editor when the folder is chosen or changed</summary>
        public void SetFolder(string folderPath)
        {
            // the first time (editor opening) the folder does not "change": credentials already active on the server stay active
            var changed = !string.IsNullOrEmpty(_folderPath) && !string.Equals(_folderPath, folderPath, StringComparison.CurrentCultureIgnoreCase);
            var activeBefore = SharedCredentials.Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
            if (SharedCredentials.Count == 0) foreach (var id in _server.ActiveFolderCredentialIds) activeBefore.Add(id);
            _folderPath = folderPath ?? "";
            RebuildShared(changed ? new System.Collections.Generic.HashSet<string>() : activeBefore);
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

        /// <summary>the list of possible default users = active shared credentials + local users with a name</summary>
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

        // ---- commands
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
            // a still empty row is removed without asking; one with data (user, password) asks for confirmation
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
        /// <summary>opens the folder credentials panel and, when it closes, reloads the list</summary>
        public RelayCommand CmdManageFolder => _cmdManageFolder ??= new RelayCommand(async o =>
        {
            if (!HasFolder) return;
            var dlg = new FolderSettingsDialogViewModel(_folderPath);
            MaskLayerController.ShowWindowWithMask(dlg, IoC.Get<MainWindowViewModel>());
            await dlg.WaitDialogResult();
            var active = SharedCredentials.Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
            RebuildShared(active);
        });

        /// <summary>writes users, active credentials, default user and "always ask" to the server; creates the requested shared credentials</summary>
        public void ApplyToServer()
        {
            var activeIds = SharedCredentials.Where(x => x.IsActive).Select(x => x.Id).ToList();

            // users to share with the folder: they become folder credentials, active on this server
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
                if (existing == null) continue;   // it could not be created: the user stays local
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
            // with only one usable credential there is nothing to ask: the Home row shows that one, not "Always ask"
            // the setting is saved as is (even with a single user: if you add more it applies immediately); on connect and in the Home row it only matters with more than one choice
            _server.AskWhichUser = AskWhichUser;

            // valid default user
            var keys = activeIds.Select(UserResolver.FolderKey).Concat(_server.Users.Select(x => UserResolver.LocalKey(x.UserName))).ToList();
            _server.DefaultUserKey = keys.Contains(DefaultUserKey) ? DefaultUserKey : keys.FirstOrDefault() ?? "";

            // the server's "legacy" fields (list, summary, exports): they mirror the default user
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
            // with configured users, whether to ask for the password is decided on connect, for the chosen user;
            // with no user at all it is asked on connect
            _server.AskPasswordWhenConnect = keys.Count == 0;
        }
    }
}
