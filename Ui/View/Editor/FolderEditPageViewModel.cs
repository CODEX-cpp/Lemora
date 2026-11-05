using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Setup;
using Lemora.Utils;
using Lemora.View.Host;
using Lemora.View.Utils;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor
{
    /// <summary>testi della pagina cartella: <c>{Binding Tx[chiave]}</c> (italiano o inglese come il resto delle schermate nuove)</summary>
    public class TextProxy
    {
        public string this[string key] => SetupText.T(key);
    }

    /// <summary>una sottocartella con dominio e rete modificabili direttamente (scheda Dettagli)</summary>
    public class SubfolderRow : NotifyPropertyChangedBase
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        private string _domain = "";
        public string Domain { get => _domain; set => SetAndNotifyIfChanged(ref _domain, value); }
        private string _ipPrefix = "";
        public string IpPrefix { get => _ipPrefix; set => SetAndNotifyIfChanged(ref _ipPrefix, value); }
    }

    /// <summary>un server nella scheda Server</summary>
    public class FolderServerRow
    {
        public ProtocolBaseViewModel Vm { get; init; } = null!;
        public string Protocol { get; init; } = "";
        public string Name { get; init; } = "";
        public string Address { get; init; } = "";
        public string Users { get; init; } = "";
        /// <summary>Sposta ed Elimina: libere con dati locali o portable; nella versione Teams dipendono dal permesso (la sola lettura non puo')</summary>
        public bool CanChange => Vm.IsEditable && Vm.Server.DataSource?.IsWritable == true;
    }

    /// <summary>una voce del gestore password nella scheda "Password condivise"</summary>
    public class DeviceRow
    {
        public DeviceEntry Entry { get; init; } = new();
        public string Name => Entry.Name;
        public string Ip => Entry.Ip;
        private List<DeviceUserRow>? _users;
        public List<DeviceUserRow> Users => _users ??= Entry.Users.Select(u => new DeviceUserRow { Device = this, User = u.User, Password = u.Password }).ToList();
    }

    /// <summary>una riga utente di un dispositivo (nome, password mascherata) con il dispositivo a cui appartiene: serve ai pulsanti Utente e Password</summary>
    public class DeviceUserRow
    {
        public DeviceRow Device { get; init; } = null!;
        public string User { get; init; } = "";
        public string Password { get; init; } = "";
    }

    /// <summary>una credenziale nel modulo di una voce del gestore password</summary>
    public class EditUserRow : NotifyPropertyChangedBase
    {
        private string _user = "";
        public string User { get => _user; set => SetAndNotifyIfChanged(ref _user, value); }
        private string _password = "";
        public string Password { get => _password; set => SetAndNotifyIfChanged(ref _password, value); }
        private string _label = "";
        public string Label { get => _label; set => SetAndNotifyIfChanged(ref _label, value); }
        private bool _canRemove;
        public bool CanRemove { get => _canRemove; set => SetAndNotifyIfChanged(ref _canRemove, value); }
    }

    /// <summary>
    /// Pagina di modifica di una cartella (disegno di Claude Design, CARTELLA-BLUEPRINT.md): al centro della Home, al posto di lista e dettaglio.
    /// Schede: Dettagli, Server, Credenziali di dominio, Password condivise. Salva applica nome, posizione, dominio e rete; credenziali e password condivise
    /// si salvano subito, come nella vecchia finestra.
    /// </summary>
    public class FolderEditPageViewModel : NotifyPropertyChangedBaseScreen
    {
        public TextProxy Tx { get; } = new();

        /// <summary>aperta da "Rinomina": il campo Nome si mostra gia' selezionato e con il focus</summary>
        public bool FocusName { get; init; }

        public string FolderPath { get; private set; }
        public string Initial => string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1).ToUpper();
        public string HeaderName => FolderService.Leaf(FolderPath);

        private readonly Action _close;
        private readonly Action<string>? _afterDelete;
        private readonly Action<string, string>? _afterRename;

        private int _selectedTab;
        /// <summary>0 Dettagli, 1 Server, 2 Credenziali di dominio, 3 Password condivise</summary>
        public int SelectedTab { get => _selectedTab; set => SetAndNotifyIfChanged(ref _selectedTab, value); }

        private string _error = "";
        public string Error { get => _error; set => SetAndNotifyIfChanged(ref _error, value); }

        // ---- Dettagli
        private string _name = "";
        public string Name { get => _name; set { if (SetAndNotifyIfChanged(ref _name, value)) { Error = ""; RaisePropertyChanged(nameof(Initial)); } } }

        private string _parent = "";
        public new string Parent { get => _parent; set => SetAndNotifyIfChanged(ref _parent, value ?? ""); }
        public List<NewFolderDialogViewModel.ParentItem> ParentChoices { get; }

        private string _domain = "";
        public string Domain { get => _domain; set => SetAndNotifyIfChanged(ref _domain, value); }
        private string _ipPrefix = "";
        public string IpPrefix { get => _ipPrefix; set => SetAndNotifyIfChanged(ref _ipPrefix, value); }
        public string SuggestedDomain { get; }
        public string SuggestedIpPrefix { get; }

        private bool _applyDomainToServers;
        public bool ApplyDomainToServers { get => _applyDomainToServers; set => SetAndNotifyIfChanged(ref _applyDomainToServers, value); }
        public string ApplyDomainText => string.Format(SetupText.T("fe_apply_domain"), Servers.Count);

        public ObservableCollection<SubfolderRow> Subfolders { get; } = new();
        public string SubfoldersTitle => string.Format(SetupText.T("fe_subfolders"), Subfolders.Count);
        public bool HasNoSubfolders => Subfolders.Count == 0;

        // ---- Server
        public List<FolderServerRow> Servers { get; private set; } = new();
        public bool HasNoServers => Servers.Count == 0;
        public string ServersTabTitle => SetupText.T("fe_tab_servers");
        public string ServersTitle => string.Format(SetupText.T("fe_servers_in_folder"), Servers.Count);

        // ---- Credenziali di dominio
        public ObservableCollection<FolderCredentialRow> Credentials { get; } = new();
        public bool HasNoCredentials => Credentials.Count == 0;
        public string CredentialsTitle => string.Format(SetupText.T("fe_creds_title"), Credentials.Count);
        private string _newUserName = "";
        public string NewUserName { get => _newUserName; set => SetAndNotifyIfChanged(ref _newUserName, value); }
        private string _newPassword = "";
        public string NewPassword { get => _newPassword; set => SetAndNotifyIfChanged(ref _newPassword, value); }
        private bool _isAddingCredential;
        public bool IsAddingCredential { get => _isAddingCredential; set => SetAndNotifyIfChanged(ref _isAddingCredential, value); }

        // ---- Password condivise (gestore password)
        public ObservableCollection<DeviceRow> Devices { get; } = new();
        public bool HasNoDevices => Devices.Count == 0;
        public string DevicesTitle => string.Format(SetupText.T("fe_devices_title"), Devices.Count);
        public bool CanEditDevices => TeamsShared.CanEditDevices;
        public bool CanDeleteDevices => TeamsShared.CanDeleteDevices;

        private bool _isEditingDevice;
        public bool IsEditingDevice { get => _isEditingDevice; set => SetAndNotifyIfChanged(ref _isEditingDevice, value); }
        private DeviceEntry? _editingDevice;
        public bool IsEditingExisting => _editingDevice != null;
        /// <summary>"Elimina" del modulo: solo per una voce che esiste gia' e solo a chi puo' eliminare (la sola lettura no)</summary>
        public bool CanDeleteExistingDevice => _editingDevice != null && TeamsShared.CanDeleteDevices;
        private string _deviceFormTitle = "";
        public string DeviceFormTitle { get => _deviceFormTitle; set => SetAndNotifyIfChanged(ref _deviceFormTitle, value); }
        private string _deviceName = "";
        public string DeviceName { get => _deviceName; set => SetAndNotifyIfChanged(ref _deviceName, value); }
        private string _deviceIp = "";
        public string DeviceIp { get => _deviceIp; set => SetAndNotifyIfChanged(ref _deviceIp, value); }
        public ObservableCollection<EditUserRow> DeviceUsers { get; } = new();

        public FolderEditPageViewModel(string folderPath, Action close, Action<string>? afterDelete = null, Action<string, string>? afterRename = null)
        {
            FolderPath = folderPath;
            _close = close;
            _afterDelete = afterDelete;
            _afterRename = afterRename;
            _name = FolderService.Leaf(folderPath);
            _parent = FolderService.Parent(folderPath);
            ParentChoices = new List<NewFolderDialogViewModel.ParentItem> { new() { Path = "" } }
                .Concat(FolderService.AllPaths().Where(x => !FolderService.IsIn(FolderService.ToNodes(x), folderPath)).Select(x => new NewFolderDialogViewModel.ParentItem { Path = x }))
                .ToList();
            var settings = FolderService.GetSettings(folderPath);
            _domain = settings.Domain;
            _ipPrefix = settings.IpPrefix;
            SuggestedDomain = FolderService.SuggestDomain(folderPath);
            SuggestedIpPrefix = FolderService.SuggestIpPrefix(folderPath);
            ReloadAll();
        }

        /// <summary>"10.0.4.22:3389 (Administrator)" -> "10.0.4.22:3389 · Administrator" (come il mock)</summary>
        private static string NiceAddress(string sub)
        {
            var i = sub.IndexOf(" (", StringComparison.Ordinal);
            return i > 0 && sub.EndsWith(')') ? sub.Substring(0, i) + " · " + sub.Substring(i + 2, sub.Length - i - 3) : sub;
        }

        private void ReloadAll()
        {
            Servers = IoC.Get<GlobalData>().VmItemList
                .Where(vm => FolderService.IsIn(vm.Server.TreeNodes, FolderPath))
                .OrderBy(vm => vm.Server.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(vm => new FolderServerRow { Vm = vm, Protocol = vm.Server.ProtocolDisplayName, Name = vm.Server.DisplayName, Address = NiceAddress(vm.Server.SubTitle), Users = "" })
                .ToList();
            Subfolders.Clear();
            foreach (var p in FolderService.AllPaths().Where(x => string.Equals(FolderService.Parent(x), FolderPath, StringComparison.CurrentCultureIgnoreCase)))
            {
                var s = FolderService.GetSettings(p);
                Subfolders.Add(new SubfolderRow { Path = p, Name = FolderService.Leaf(p), Domain = s.Domain, IpPrefix = s.IpPrefix });
            }
            ReloadCredentials();
            ReloadDevices();
            foreach (var n in new[] { nameof(Servers), nameof(HasNoServers), nameof(ServersTitle), nameof(ServersTabCount), nameof(ApplyDomainText), nameof(SubfoldersTitle), nameof(HasNoSubfolders) }) RaisePropertyChanged(n);
        }

        private void ReloadCredentials()
        {
            Credentials.Clear();
            foreach (var c in FolderCredentialService.ForFolder(FolderPath))
            {
                var plain = c.CloneMe();
                plain.DecryptToConnectLevel();
                var used = FolderCredentialService.UsedByCount(c.DatabaseId);
                Credentials.Add(new FolderCredentialRow(c, plain.Password, used) { UsedText = string.Format(SetupText.T("fe_used_of"), used, Servers.Count) });
            }
            RaisePropertyChanged(nameof(HasNoCredentials));
            RaisePropertyChanged(nameof(CredentialsTitle));
            RaisePropertyChanged(nameof(CredentialsTabCount));
        }

        public string CredentialsTabCount => Credentials.Count.ToString();
        public string ServersTabCount => Servers.Count.ToString();
        public string DevicesTabCount => Devices.Count.ToString();

        private void ReloadDevices()
        {
            Devices.Clear();
            foreach (var e in DevicePasswordService.All().Where(x => string.Equals(x.Folder, FolderPath, StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                Devices.Add(new DeviceRow { Entry = e });
            RaisePropertyChanged(nameof(HasNoDevices));
            RaisePropertyChanged(nameof(DevicesTitle));
            RaisePropertyChanged(nameof(DevicesTabCount));
            RaisePropertyChanged(nameof(CanEditDevices));
            RaisePropertyChanged(nameof(CanDeleteDevices));
        }

        // ---- comandi: pagina
        private RelayCommand? _cmdCancel;
        public RelayCommand CmdCancel => _cmdCancel ??= new RelayCommand(o => _close());

        private RelayCommand? _cmdSave;
        /// <summary>applica nome, posizione, dominio e rete (e dominio e rete delle sottocartelle modificate); le credenziali e le password si salvano da sole</summary>
        public RelayCommand CmdSave => _cmdSave ??= new RelayCommand(o =>
        {
            var newName = (Name ?? "").Trim();
            if (newName != FolderService.Leaf(FolderPath))
            {
                var old = FolderPath;
                if (!FolderService.Rename(FolderPath, newName, out var error)) { Error = error; SelectedTab = 0; return; }
                var parent = FolderService.Parent(old);
                FolderPath = string.IsNullOrEmpty(parent) ? newName : FolderService.ToPath(FolderService.ToNodes(parent).Append(newName));
                _afterRename?.Invoke(old, FolderPath);
                ToastMoved(old, FolderPath);
            }
            if (!string.Equals(Parent, FolderService.Parent(FolderPath), StringComparison.CurrentCulture))
            {
                var old = FolderPath;
                if (!FolderService.Move(FolderPath, Parent, out var moveError)) { Error = moveError; SelectedTab = 0; return; }
                FolderPath = string.IsNullOrEmpty(Parent) ? FolderService.Leaf(old) : FolderService.ToPath(FolderService.ToNodes(Parent).Append(FolderService.Leaf(old)));
                _afterRename?.Invoke(old, FolderPath);
                ToastMoved(old, FolderPath);
            }
            FolderService.SetSettings(FolderPath, Domain, IpPrefix);
            foreach (var sub in Subfolders)
            {
                var subPath = FolderService.ToPath(FolderService.ToNodes(FolderPath).Append(sub.Name));
                var cur = FolderService.GetSettings(subPath);
                if (cur.Domain != (sub.Domain ?? "").Trim() || cur.IpPrefix != (sub.IpPrefix ?? "").Trim()) FolderService.SetSettings(subPath, sub.Domain ?? "", sub.IpPrefix ?? "");
            }
            if (ApplyDomainToServers && !string.IsNullOrWhiteSpace(Domain))
            {
                var servers = IoC.Get<GlobalData>().VmItemList.Select(vm => vm.Server)
                    .Where(sv => FolderService.IsIn(sv.TreeNodes, FolderPath)).OfType<Lemora.Model.Protocol.RDP>().ToList();
                foreach (var sv in servers) sv.Domain = Domain.Trim();
                if (servers.Count > 0) IoC.Get<GlobalData>().UpdateServer(servers);
            }
            ToastService.Show(SetupText.T("fe_saved"));
            _close();
        });

        private void ToastMoved(string oldPath, string newPath)
        {
            var afterRename = _afterRename;
            ToastService.Show(string.Format(IoC.Translate("toast_folder_changed"), FolderService.Leaf(newPath)), ToastKind.Success, IoC.Translate("Cancel"), () =>
            {
                var sameParent = string.Equals(FolderService.Parent(oldPath), FolderService.Parent(newPath), StringComparison.CurrentCulture);
                var ok = sameParent ? FolderService.Rename(newPath, FolderService.Leaf(oldPath), out _) : FolderService.Move(newPath, FolderService.Parent(oldPath), out _);
                if (ok) afterRename?.Invoke(newPath, oldPath);
            });
        }

        private RelayCommand? _cmdDelete;
        public RelayCommand CmdDelete => _cmdDelete ??= new RelayCommand(o =>
        {
            var credCount = FolderCredentialService.ForFolder(FolderPath).Count;
            if (credCount > 0 && FolderService.CountServers(FolderPath) == 0
                && !MessageBoxHelper.Confirm(string.Format(IoC.Translate("folder_delete_with_credentials_confirm"), FolderService.Leaf(FolderPath), credCount))) return;
            var path = FolderPath;
            if (!FolderService.Delete(path, out var error)) { Error = error; SelectedTab = 0; return; }
            _afterDelete?.Invoke(path);
            _close();
        });

        private RelayCommand? _cmdUseSuggestedDomain;
        public RelayCommand CmdUseSuggestedDomain => _cmdUseSuggestedDomain ??= new RelayCommand(o => Domain = SuggestedDomain);
        private RelayCommand? _cmdUseSuggestedIpPrefix;
        public RelayCommand CmdUseSuggestedIpPrefix => _cmdUseSuggestedIpPrefix ??= new RelayCommand(o => IpPrefix = SuggestedIpPrefix);

        private RelayCommand? _cmdDeleteSubfolder;
        /// <summary>elimina una sottocartella; se dentro ci sono server chiede di spostarli o eliminarli prima</summary>
        public RelayCommand CmdDeleteSubfolder => _cmdDeleteSubfolder ??= new RelayCommand(o =>
        {
            if (o is not SubfolderRow r) return;
            var servers = FolderService.CountServers(r.Path);
            if (servers > 0)
            {
                MessageBoxHelper.Info(string.Format(SetupText.T("fe_sub_not_empty"), r.Name, servers));
                return;
            }
            var credCount = FolderCredentialService.ForFolder(r.Path).Count;
            var ask = credCount > 0 ? string.Format(IoC.Translate("folder_delete_with_credentials_confirm"), r.Name, credCount) : string.Format(SetupText.T("fe_sub_delete_confirm"), r.Name);
            if (!MessageBoxHelper.Confirm(ask)) return;
            if (!FolderService.Delete(r.Path, out var error)) { Error = error; return; }
            _afterDelete?.Invoke(r.Path);
            ReloadAll();
            ToastService.Show(string.Format(SetupText.T("fe_sub_deleted"), r.Name));
        });

        private RelayCommand? _cmdUseParent;
        /// <summary>una sottocartella prende dominio e rete di questa</summary>
        public RelayCommand CmdUseParent => _cmdUseParent ??= new RelayCommand(o => { if (o is SubfolderRow r) { r.Domain = Domain; r.IpPrefix = IpPrefix; } });

        private RelayCommand? _cmdNewSubfolder;
        public RelayCommand CmdNewSubfolder => _cmdNewSubfolder ??= new RelayCommand(async o =>
        {
            var dlg = new NewFolderDialogViewModel(FolderPath);
            MaskLayerController.ShowWindowWithMask(dlg, IoC.Get<MainWindowViewModel>());
            await dlg.WaitDialogResult();
            ReloadAll();
        });

        private RelayCommand? _cmdAddServerHere;
        /// <summary>"+ Server": chiude la pagina e apre la creazione con questa cartella gia' scelta</summary>
        public RelayCommand CmdAddServerHere => _cmdAddServerHere ??= new RelayCommand(o =>
        {
            var nodes = FolderService.ToNodes(FolderPath);
            _close();
            Lemora.Model.GlobalEventHelper.OnGoToServerAddPage?.Invoke(new List<string>(), null, new ServerEditorPageViewModel.ParamsServerAddPreset { TreeNodes = nodes });
        });

        private RelayCommand? _cmdEditServer;
        public RelayCommand CmdEditServer => _cmdEditServer ??= new RelayCommand(o => { if (o is FolderServerRow r) { _close(); r.Vm.CmdEditServer.Execute(); } });

        // ---- Sposta server / Elimina server (finestre dentro la pagina)
        private FolderServerRow? _dialogServer;
        public FolderServerRow? DialogServer { get => _dialogServer; private set { if (SetAndNotifyIfChanged(ref _dialogServer, value)) RaisePropertyChanged(nameof(DialogTitle)); } }
        private bool _isMoving;
        public bool IsMoving { get => _isMoving; private set => SetAndNotifyIfChanged(ref _isMoving, value); }
        private bool _isDeletingServer;
        public bool IsDeletingServer { get => _isDeletingServer; private set => SetAndNotifyIfChanged(ref _isDeletingServer, value); }
        public bool IsDialogOpen => IsMoving || IsDeletingServer;
        public string DialogTitle => DialogServer == null ? "" : string.Format(SetupText.T(IsMoving ? "fe_move_title" : "fe_srv_delete_title"), DialogServer.Name);
        public string DeleteText => SetupText.T(TeamsShared.Linked ? "fe_srv_delete_text_teams" : "fe_srv_delete_text");
        public List<NewFolderDialogViewModel.ParentItem> MoveChoices { get; } = FolderService.AllPaths().Select(x => new NewFolderDialogViewModel.ParentItem { Path = x }).ToList();
        private string _moveTarget = "";
        public string MoveTarget { get => _moveTarget; set { if (SetAndNotifyIfChanged(ref _moveTarget, value ?? "")) RaisePropertyChanged(nameof(MoveNote)); } }
        public string MoveNote
        {
            get
            {
                if (MoveTarget.Length == 0) return "";
                var d = FolderService.EffectiveDomain(MoveTarget); var n = FolderService.EffectiveIpPrefix(MoveTarget);
                return string.Format(SetupText.T("fe_move_note"), d.Length > 0 ? d : "-", Lemora.View.ServerView.FolderSummaryViewModel.NetworkText(n));
            }
        }
        private string _confirmPassword = "";
        public string ConfirmPassword { get => _confirmPassword; set { if (SetAndNotifyIfChanged(ref _confirmPassword, value ?? "")) DialogError = ""; } }
        private string _dialogError = "";
        public string DialogError { get => _dialogError; private set => SetAndNotifyIfChanged(ref _dialogError, value); }

        private void OpenDialog(FolderServerRow? row, bool move)
        {
            if (row == null) return;
            if (!row.CanChange) { ToastService.Show(SetupText.T("fe_no_perm")); return; }
            DialogServer = row; DialogError = ""; ConfirmPassword = "";
            MoveTarget = FolderPath;
            IsMoving = move; IsDeletingServer = !move;
            RaisePropertyChanged(nameof(IsDialogOpen)); RaisePropertyChanged(nameof(DialogTitle)); RaisePropertyChanged(nameof(DeleteText));
        }

        public void CloseDialog()
        {
            IsMoving = false; IsDeletingServer = false; DialogServer = null; ConfirmPassword = ""; DialogError = "";
            RaisePropertyChanged(nameof(IsDialogOpen));
        }

        private RelayCommand? _cmdMoveServer;
        public RelayCommand CmdMoveServer => _cmdMoveServer ??= new RelayCommand(o => OpenDialog(o as FolderServerRow, true));
        private RelayCommand? _cmdDeleteServer;
        public RelayCommand CmdDeleteServer => _cmdDeleteServer ??= new RelayCommand(o => OpenDialog(o as FolderServerRow, false));
        private RelayCommand? _cmdCloseDialog;
        public RelayCommand CmdCloseDialog => _cmdCloseDialog ??= new RelayCommand(o => CloseDialog());

        private RelayCommand? _cmdConfirmMove;
        public RelayCommand CmdConfirmMove => _cmdConfirmMove ??= new RelayCommand(o =>
        {
            var row = DialogServer;
            if (row == null || !row.CanChange) return;
            if (string.Equals(MoveTarget, FolderPath, StringComparison.CurrentCultureIgnoreCase)) { CloseDialog(); return; }
            var server = row.Vm.Server;
            server.TreeNodes = FolderService.ToNodes(MoveTarget);
            var ret = IoC.Get<GlobalData>().UpdateServer(server);
            if (!ret.IsSuccess) { DialogError = ret.ErrorInfo; return; }
            var target = MoveTarget;
            CloseDialog();
            ReloadAll();
            ToastService.Show(string.Format(SetupText.T("fe_moved"), row.Name, FolderService.Leaf(target)));
        });

        private RelayCommand? _cmdConfirmDeleteServer;
        public RelayCommand CmdConfirmDeleteServer => _cmdConfirmDeleteServer ??= new RelayCommand(o =>
        {
            var row = DialogServer;
            if (row == null || !row.CanChange) return;
            if (ConfirmPassword.Length == 0) { DialogError = SetupText.T("fe_pw_required"); return; }
            // "la tua password": quella di Windows di chi sta usando il programma
            if (Lemora.Utils.WindowsApi.Credential.CredentialPrompt.LogonUser(Environment.UserName, ConfirmPassword, Environment.UserDomainName)
                != Lemora.Utils.WindowsApi.Credential.CredentialPrompt.LogonUserStatus.Success)
            { DialogError = SetupText.T("fe_pw_wrong"); return; }
            var ret = IoC.Get<GlobalData>().DeleteServer(new List<ProtocolBase> { row.Vm.Server });
            if (!ret.IsSuccess) { DialogError = ret.ErrorInfo; return; }
            CloseDialog();
            ReloadAll();
            ToastService.Show(string.Format(SetupText.T("fe_srv_deleted"), row.Name));
        });

        // ---- comandi: credenziali di dominio
        private RelayCommand? _cmdShowAddCredential;
        public RelayCommand CmdShowAddCredential => _cmdShowAddCredential ??= new RelayCommand(o => { IsAddingCredential = !IsAddingCredential; Error = ""; });

        private RelayCommand? _cmdAddCredential;
        public RelayCommand CmdAddCredential => _cmdAddCredential ??= new RelayCommand(o =>
        {
            var ret = FolderCredentialService.Add(FolderPath, NewUserName, NewPassword, out _);
            if (!ret.IsSuccess) { Error = ret.ErrorInfo; return; }
            Error = ""; NewUserName = ""; NewPassword = ""; IsAddingCredential = false;
            ReloadCredentials();
        }, o => !string.IsNullOrWhiteSpace(NewUserName));

        private RelayCommand? _cmdEditCredential;
        /// <summary>matita: apre o chiude il modulo (password, Salva, Elimina) della credenziale</summary>
        public RelayCommand CmdEditCredential => _cmdEditCredential ??= new RelayCommand(o => { if (o is FolderCredentialRow r) r.IsEditing = !r.IsEditing; });

        private RelayCommand? _cmdCopyCredential;
        /// <summary>copia la password senza verifiche (come il gestore password); gli appunti si puliscono dopo qualche secondo</summary>
        public RelayCommand CmdCopyCredential => _cmdCopyCredential ??= new RelayCommand(o =>
        {
            if (o is not FolderCredentialRow r) return;
            if (SecretClipboard.Set(r.Password, DevicePasswordService.ClipboardClearSeconds))
                ToastService.Show(string.Format(SetupText.T("fe_cred_copied"), r.UserName, DevicePasswordService.ClipboardClearSeconds));
        });

        private RelayCommand? _cmdSaveCredential;
        public RelayCommand CmdSaveCredential => _cmdSaveCredential ??= new RelayCommand(o =>
        {
            if (o is not FolderCredentialRow row) return;
            var ret = FolderCredentialService.Update(row.Credential, row.UserName, row.Password);
            Error = ret.IsSuccess ? "" : ret.ErrorInfo;
            ReloadCredentials();
        });

        private RelayCommand? _cmdDeleteCredential;
        public RelayCommand CmdDeleteCredential => _cmdDeleteCredential ??= new RelayCommand(o =>
        {
            if (o is not FolderCredentialRow row) return;
            if (row.UsedBy > 0 && MessageBoxHelper.Confirm(string.Format(IoC.Translate("folder_credential_delete_confirm"), row.UserName, row.UsedBy)) != true) return;
            var ret = FolderCredentialService.Delete(row.Credential);
            Error = ret.IsSuccess ? "" : ret.ErrorInfo;
            ReloadCredentials();
        });

        // ---- comandi: password condivise
        private void CopyDeviceText(string text, string toast, bool sensitive, string what, string device, string user)
        {
            if (SecretClipboard.Set(text, sensitive ? DevicePasswordService.ClipboardClearSeconds : 0)) { ToastService.Show(toast); TeamsShared.AuditCopy(what, device, user); }
        }

        private RelayCommand? _cmdCopyUser;
        public RelayCommand CmdCopyUser => _cmdCopyUser ??= new RelayCommand(o =>
        {
            if (o is not DeviceUserRow u) return;
            CopyDeviceText(u.User, string.Format(SetupText.T("dp_copied_user"), u.Device.Name, u.User), false, "user", u.Device.Name, u.User);
        });

        private RelayCommand? _cmdCopyPassword;
        /// <summary>copia la password senza chiedere nulla a nessuno (decisione di Ivan) e la toglie dagli appunti dopo 30 s</summary>
        public RelayCommand CmdCopyPassword => _cmdCopyPassword ??= new RelayCommand(o =>
        {
            if (o is not DeviceUserRow u) return;
            CopyDeviceText(u.Password, string.Format(SetupText.T("dp_copied_pass"), u.Device.Name, u.User, DevicePasswordService.ClipboardClearSeconds), true, "password", u.Device.Name, u.User);
        });

        private void RenumberUsers()
        {
            for (var i = 0; i < DeviceUsers.Count; i++)
            {
                DeviceUsers[i].Label = DeviceUsers.Count == 1 ? SetupText.T("dp_cred") : string.Format(SetupText.T("dp_user_n"), i + 1);
                DeviceUsers[i].CanRemove = DeviceUsers.Count > 1;
            }
        }

        private void OpenDeviceForm(DeviceEntry? e)
        {
            _editingDevice = e;
            RaisePropertyChanged(nameof(IsEditingExisting));
            RaisePropertyChanged(nameof(CanDeleteExistingDevice));
            DeviceFormTitle = e == null ? SetupText.T("dp_new_title") : string.Format(SetupText.T("dp_edit_title"), e.Name);
            DeviceName = e?.Name ?? "";
            var prefix = IpPrefix.Length > 0 ? IpPrefix : SuggestedIpPrefix;
            DeviceIp = e?.Ip ?? prefix;
            DeviceUsers.Clear();
            foreach (var u in e?.Users.Count > 0 ? e.Users : new List<DeviceUser> { new() }) DeviceUsers.Add(new EditUserRow { User = u.User, Password = u.Password });
            RenumberUsers();
            Error = "";
            IsEditingDevice = true;
        }

        private RelayCommand? _cmdNewDevice;
        public RelayCommand CmdNewDevice => _cmdNewDevice ??= new RelayCommand(o => OpenDeviceForm(null));
        private RelayCommand? _cmdEditDevice;
        public RelayCommand CmdEditDevice => _cmdEditDevice ??= new RelayCommand(o => { if (o is DeviceRow r) OpenDeviceForm(r.Entry); });
        private RelayCommand? _cmdAddDeviceUser;
        public RelayCommand CmdAddDeviceUser => _cmdAddDeviceUser ??= new RelayCommand(o => { DeviceUsers.Add(new EditUserRow()); RenumberUsers(); });
        private RelayCommand? _cmdRemoveDeviceUser;
        public RelayCommand CmdRemoveDeviceUser => _cmdRemoveDeviceUser ??= new RelayCommand(o => { if (o is EditUserRow r && DeviceUsers.Count > 1) { DeviceUsers.Remove(r); RenumberUsers(); } });
        private RelayCommand? _cmdCancelDevice;
        public RelayCommand CmdCancelDevice => _cmdCancelDevice ??= new RelayCommand(o => { IsEditingDevice = false; Error = ""; });

        private RelayCommand? _cmdSaveDevice;
        public RelayCommand CmdSaveDevice => _cmdSaveDevice ??= new RelayCommand(o =>
        {
            var name = (DeviceName ?? "").Trim(); var ip = (DeviceIp ?? "").Trim();
            var prefix = IpPrefix.Length > 0 ? IpPrefix : SuggestedIpPrefix;
            if (_editingDevice == null && ip == prefix) ip = "";   // il solo prefisso proposto non e' un indirizzo
            if (name.Length == 0 && ip.Length == 0) { Error = SetupText.T("dp_need_name"); return; }
            var entry = _editingDevice != null ? _editingDevice.Clone() : new DeviceEntry { Folder = FolderPath };
            entry.Name = name.Length > 0 ? name : ip;
            entry.Ip = ip;
            entry.Users = DeviceUsers.Select(u => new DeviceUser { User = u.User ?? "", Password = u.Password ?? "" }).ToList();
            var err = TeamsShared.SaveDevice(entry, _editingDevice == null, out var saved);
            if (err != null) { Error = err; return; }
            ToastService.Show(string.Format(SetupText.T("dp_saved"), saved?.Name ?? entry.Name));
            IsEditingDevice = false; Error = "";
            ReloadDevices();
        });

        private RelayCommand? _cmdDeleteDevice;
        public RelayCommand CmdDeleteDevice => _cmdDeleteDevice ??= new RelayCommand(o =>
        {
            if (_editingDevice == null || !CanDeleteDevices) return;
            if (MessageBoxHelper.Confirm(string.Format(SetupText.T("dp_delete_ask"), _editingDevice.Name)) != true) return;
            var err = TeamsShared.RemoveDevice(_editingDevice);
            if (err != null) { Error = err; return; }
            IsEditingDevice = false; Error = "";
            ReloadDevices();
        });
    }
}
