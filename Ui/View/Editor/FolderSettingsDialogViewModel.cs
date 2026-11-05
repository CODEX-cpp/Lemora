using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Utils;
using Lemora.View.Utils;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor
{
    /// <summary>una credenziale condivisa nell'elenco del pannello della cartella</summary>
    public class FolderCredentialRow : NotifyPropertyChangedBase
    {
        public Credential Credential { get; }
        public int UsedBy { get; set; }
        /// <summary>"2 di 4 server" (pagina di modifica cartella)</summary>
        public string UsedText { get; set; } = "";
        private bool _isEditing;
        /// <summary>la matita ha aperto il modulo di modifica della password</summary>
        public bool IsEditing { get => _isEditing; set => SetAndNotifyIfChanged(ref _isEditing, value); }
        public string UserName { get; }
        /// <summary>nome senza il dominio davanti (ACME\Administrator -> Administrator)</summary>
        public string ShortName => UserName.Contains('\\') ? UserName.Substring(UserName.LastIndexOf('\\') + 1) : UserName;
        private string _password;
        private readonly string _originalPassword;

        public FolderCredentialRow(Credential credential, string plainPassword, int usedBy)
        {
            Credential = credential;
            UserName = credential.UserName;
            _password = _originalPassword = plainPassword;
            UsedBy = usedBy;
        }

        public string Password
        {
            get => _password;
            set
            {
                if (SetAndNotifyIfChanged(ref _password, value))
                    RaisePropertyChanged(nameof(IsDirty));
            }
        }
        public bool IsDirty => _password != _originalPassword;
    }

    /// <summary>pannello "Credenziali e rete" di una cartella: utenti condivisi, dominio e prefisso IP della sede</summary>
    public class FolderSettingsDialogViewModel : PopupBase
    {
        public string FolderPath { get; private set; }
        public string Title => string.Format(IoC.Translate("folder_manage_title"), FolderService.Leaf(FolderPath));

        /// <summary>una riga della scheda "Server"</summary>
        public class ServerRow
        {
            public string Protocol { get; init; } = "";
            public string Name { get; init; } = "";
            public string Address { get; init; } = "";
        }

        private readonly Action<string>? _afterDelete;
        private readonly Action<string, string>? _afterRename;

        /// <summary>0 credenziali condivise, 1 generale, 2 server</summary>
        private int _selectedTab;
        public int SelectedTab { get => _selectedTab; set => SetAndNotifyIfChanged(ref _selectedTab, value); }

        public List<ServerRow> Servers { get; private set; } = new List<ServerRow>();
        public bool HasNoServers => Servers.Count == 0;
        public string ServersTabTitle => string.Format(IoC.Translate("folder_tab_servers"), Servers.Count);
        public string NewCredentialHint => string.Format(IoC.Translate("folder_credential_scope"), Servers.Count);
        public string CredentialsHint => string.Format(IoC.Translate("folder_credentials_hint_n"), Servers.Count);
        /// <summary>cartelle in cui si puo' spostare questa (tutte tranne se stessa e le sue sottocartelle)</summary>
        public List<NewFolderDialogViewModel.ParentItem> ParentChoices { get; private set; } = new List<NewFolderDialogViewModel.ParentItem>();

        private string _parent = "";
        /// <summary>cartella che contiene questa ("" = radice); cambiandola e salvando la cartella si sposta</summary>
        public new string Parent { get => _parent; set => SetAndNotifyIfChanged(ref _parent, value ?? ""); }

        private bool _applyDomainToServers;
        /// <summary>al salvataggio scrive il dominio anche nei server (RDP) che sono gia' dentro la cartella</summary>
        public bool ApplyDomainToServers { get => _applyDomainToServers; set => SetAndNotifyIfChanged(ref _applyDomainToServers, value); }
        public string ApplyDomainText => string.Format(IoC.Translate("folder_apply_domain"), Servers.Count);

        public string ParentDisplay { get { var p = FolderService.Parent(FolderPath); return string.IsNullOrEmpty(p) ? IoC.Translate("folder_parent_none") : p; } }

        private string _name = "";
        public string Name { get => _name; set { if (SetAndNotifyIfChanged(ref _name, value)) Error = ""; } }

        /// <summary>"4 server · 2 sottocartelle · 2 credenziali condivise"</summary>
        public string Summary => string.Format(IoC.Translate("folder_items_summary"), Servers.Count,
            FolderService.AllPaths().Count(x => x.StartsWith(FolderPath + FolderService.Separator, StringComparison.CurrentCultureIgnoreCase)), Credentials.Count);

        public ObservableCollection<FolderCredentialRow> Credentials { get; } = new ObservableCollection<FolderCredentialRow>();
        public bool HasNoCredentials => Credentials.Count == 0;

        private string _domain = "";
        public string Domain { get => _domain; set => SetAndNotifyIfChanged(ref _domain, value); }

        private string _ipPrefix = "";
        public string IpPrefix { get => _ipPrefix; set => SetAndNotifyIfChanged(ref _ipPrefix, value); }

        public string SuggestedDomain { get; }
        public string SuggestedIpPrefix { get; }

        private string _newUserName = "";
        public string NewUserName
        {
            get => _newUserName;
            set => SetAndNotifyIfChanged(ref _newUserName, value);
        }

        private string _newPassword = "";
        public string NewPassword { get => _newPassword; set => SetAndNotifyIfChanged(ref _newPassword, value); }

        private string _error = "";
        public string Error { get => _error; set => SetAndNotifyIfChanged(ref _error, value); }

        public FolderSettingsDialogViewModel(string folderPath, Action<string>? afterDelete = null, Action<string, string>? afterRename = null)
        {
            FolderPath = folderPath;
            _afterDelete = afterDelete;
            _afterRename = afterRename;
            _name = FolderService.Leaf(folderPath);
            _parent = FolderService.Parent(folderPath);
            ParentChoices = new List<NewFolderDialogViewModel.ParentItem> { new NewFolderDialogViewModel.ParentItem { Path = "" } }
                .Concat(FolderService.AllPaths().Where(x => !FolderService.IsIn(FolderService.ToNodes(x), folderPath)).Select(x => new NewFolderDialogViewModel.ParentItem { Path = x }))
                .ToList();
            var settings = FolderService.GetSettings(folderPath);
            _domain = settings.Domain;
            _ipPrefix = settings.IpPrefix;
            SuggestedDomain = FolderService.SuggestDomain(folderPath);
            SuggestedIpPrefix = FolderService.SuggestIpPrefix(folderPath);
            ReloadServers();
            Reload();
        }

        private void ReloadServers()
        {
            Servers = IoC.Get<GlobalData>().VmItemList
                .Where(vm => FolderService.IsIn(vm.Server.TreeNodes, FolderPath))
                .OrderBy(vm => vm.Server.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(vm => new ServerRow { Protocol = vm.Server.ProtocolDisplayName, Name = vm.Server.DisplayName, Address = vm.Server.SubTitle })
                .ToList();
            RaisePropertyChanged(nameof(Servers));
            RaisePropertyChanged(nameof(ServersTabTitle));
            RaisePropertyChanged(nameof(CredentialsHint));
            RaisePropertyChanged(nameof(ApplyDomainText));
            RaisePropertyChanged(nameof(NewCredentialHint));
            RaisePropertyChanged(nameof(Summary));
        }

        private void Reload()
        {
            Credentials.Clear();
            foreach (var c in FolderCredentialService.ForFolder(FolderPath))
            {
                var plain = c.CloneMe();
                plain.DecryptToConnectLevel();
                Credentials.Add(new FolderCredentialRow(c, plain.Password, FolderCredentialService.UsedByCount(c.DatabaseId)));
            }
            RaisePropertyChanged(nameof(HasNoCredentials));
            RaisePropertyChanged(nameof(Summary));
        }

        private RelayCommand? _cmdAdd;
        public RelayCommand CmdAdd => _cmdAdd ??= new RelayCommand(o =>
        {
            var ret = FolderCredentialService.Add(FolderPath, NewUserName, NewPassword, out _);
            if (!ret.IsSuccess) { Error = ret.ErrorInfo; return; }
            Error = "";
            NewUserName = "";
            NewPassword = "";
            Reload();
        }, o => !string.IsNullOrWhiteSpace(NewUserName));

        private RelayCommand? _cmdSaveRow;
        public RelayCommand CmdSaveRow => _cmdSaveRow ??= new RelayCommand(o =>
        {
            if (o is not FolderCredentialRow row) return;
            var ret = FolderCredentialService.Update(row.Credential, row.UserName, row.Password);
            Error = ret.IsSuccess ? "" : ret.ErrorInfo;
            Reload();
        });

        private RelayCommand? _cmdDeleteRow;
        public RelayCommand CmdDeleteRow => _cmdDeleteRow ??= new RelayCommand(o =>
        {
            if (o is not FolderCredentialRow row) return;
            if (row.UsedBy > 0 && MessageBoxHelper.Confirm(string.Format(IoC.Translate("folder_credential_delete_confirm"), row.UserName, row.UsedBy)) != true) return;
            var ret = FolderCredentialService.Delete(row.Credential);
            Error = ret.IsSuccess ? "" : ret.ErrorInfo;
            Reload();
        });

        private RelayCommand? _cmdUseSuggestedDomain;
        public RelayCommand CmdUseSuggestedDomain => _cmdUseSuggestedDomain ??= new RelayCommand(o => Domain = SuggestedDomain);

        private RelayCommand? _cmdUseSuggestedIpPrefix;
        public RelayCommand CmdUseSuggestedIpPrefix => _cmdUseSuggestedIpPrefix ??= new RelayCommand(o => IpPrefix = SuggestedIpPrefix);

        private RelayCommand? _cmdSave;
        /// <summary>salva dominio e prefisso IP e chiude (le credenziali si salvano da sole mentre si lavora)</summary>
        public RelayCommand CmdSave => _cmdSave ??= new RelayCommand(o =>
        {
            var newName = (Name ?? "").Trim();
            if (newName != FolderService.Leaf(FolderPath))
            {
                var old = FolderPath;
                if (!FolderService.Rename(FolderPath, newName, out var error)) { Error = error; SelectedTab = 1; return; }
                var parent = FolderService.Parent(old);
                FolderPath = string.IsNullOrEmpty(parent) ? newName : FolderService.ToPath(FolderService.ToNodes(parent).Append(newName));
                _afterRename?.Invoke(old, FolderPath);
                ToastRename(old, FolderPath);
            }
            // spostamento: nuovo percorso = nuova cartella contenitore + nome
            if (!string.Equals(Parent, FolderService.Parent(FolderPath), StringComparison.CurrentCulture))
            {
                var old = FolderPath;
                if (!FolderService.Move(FolderPath, Parent, out var moveError)) { Error = moveError; SelectedTab = 1; return; }
                FolderPath = string.IsNullOrEmpty(Parent) ? FolderService.Leaf(old) : FolderService.ToPath(FolderService.ToNodes(Parent).Append(FolderService.Leaf(old)));
                _afterRename?.Invoke(old, FolderPath);
                ToastRename(old, FolderPath);
            }
            FolderService.SetSettings(FolderPath, Domain, IpPrefix);
            if (ApplyDomainToServers && !string.IsNullOrWhiteSpace(Domain))
            {
                var servers = IoC.Get<GlobalData>().VmItemList.Select(vm => vm.Server)
                    .Where(sv => FolderService.IsIn(sv.TreeNodes, FolderPath)).OfType<Lemora.Model.Protocol.RDP>().ToList();
                foreach (var sv in servers) sv.Domain = Domain.Trim();
                if (servers.Count > 0) IoC.Get<GlobalData>().UpdateServer(servers);
            }
            RequestClose(true);
        });

        /// <summary>avviso con "Annulla": rimette la cartella com'era (nome o posizione)</summary>
        private void ToastRename(string oldPath, string newPath)
        {
            var afterRename = _afterRename;
            ToastService.Show(string.Format(IoC.Translate("toast_folder_changed"), FolderService.Leaf(newPath)), ToastKind.Success, IoC.Translate("Cancel"), () =>
            {
                var sameParent = string.Equals(FolderService.Parent(oldPath), FolderService.Parent(newPath), StringComparison.CurrentCulture);
                var ok = sameParent
                    ? FolderService.Rename(newPath, FolderService.Leaf(oldPath), out _)
                    : FolderService.Move(newPath, FolderService.Parent(oldPath), out _);
                if (ok) afterRename?.Invoke(newPath, oldPath);
            });
        }

        private RelayCommand? _cmdDelete;
        /// <summary>elimina la cartella (come dal menu): chiede conferma se contiene credenziali condivise</summary>
        public RelayCommand CmdDelete => _cmdDelete ??= new RelayCommand(o =>
        {
            var credCount = FolderCredentialService.ForFolder(FolderPath).Count;
            if (credCount > 0 && FolderService.CountServers(FolderPath) == 0
                && !MessageBoxHelper.Confirm(string.Format(IoC.Translate("folder_delete_with_credentials_confirm"), FolderService.Leaf(FolderPath), credCount))) return;
            var path = FolderPath;
            if (!FolderService.Delete(path, out var error)) { Error = error; return; }
            _afterDelete?.Invoke(path);
            RequestClose(false);
        });

        private RelayCommand? _cmdNewSubfolder;
        /// <summary>apre "Nuova cartella" gia' dentro questa cartella</summary>
        public RelayCommand CmdNewSubfolder => _cmdNewSubfolder ??= new RelayCommand(async o =>
        {
            var dlg = new NewFolderDialogViewModel(FolderPath);
            MaskLayerController.ShowWindowWithMask(dlg, IoC.Get<MainWindowViewModel>());
            await dlg.WaitDialogResult();
            RaisePropertyChanged(nameof(Summary));
        });
    }
}
