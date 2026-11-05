using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lemora.Service;
using Lemora.Service.FileTransfer;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.FileTransfer
{
    /// <summary>una riga di una colonna del navigatore (file o cartella, locale o remoto)</summary>
    public class PaneItem
    {
        public string Name { get; init; } = "";
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public DateTime? Modified { get; init; }
        public RemoteEntry? Remote { get; init; }
        public string? LocalPath { get; init; }
        public string SizeText => IsDirectory ? "" : FileTransferManager.Fmt(Size);
        public string DateText => Modified is { } d ? d.ToString("dd/MM/yyyy HH:mm") : "";
        public string TypeText => IsDirectory ? FtText.T("ft_type_folder") : Path.GetExtension(Name) is { Length: > 1 } e ? e.TrimStart('.').ToUpperInvariant() : FtText.T("ft_type_file");
    }

    public class PathCrumb { public string Text { get; init; } = ""; public string Path { get; init; } = ""; }

    /// <summary>una colonna del navigatore (questo PC o PC remoto): percorso, cronologia, elenco ordinato, selezione, stati</summary>
    public abstract class FilePaneViewModel : NotifyPropertyChangedBase
    {
        protected readonly FileNavigatorViewModel Owner;
        public FileNavigatorViewModel Navigator => Owner;
        public ObservableCollection<PaneItem> Items { get; } = new();
        public ObservableCollection<PathCrumb> Crumbs { get; } = new();
        private readonly List<string> _back = new(), _fwd = new();
        public abstract string Label { get; }
        public abstract string Sub { get; }
        public abstract bool IsRemote { get; }
        public abstract string RootPath { get; }
        public abstract Task<List<PaneItem>> ListAsync(string path);
        public abstract string ParentOf(string path);
        protected abstract IEnumerable<PathCrumb> BuildCrumbs(string path);

        private string _current = "";
        public string CurrentPath { get => _current; private set { if (SetAndNotifyIfChanged(ref _current, value)) { RaisePropertyChanged(nameof(CanUp)); } } }
        public bool CanBack => _back.Count > 0;
        public bool CanForward => _fwd.Count > 0;
        public bool CanUp => CurrentPath != RootPath;

        private bool _loading; public bool Loading { get => _loading; private set { if (SetAndNotifyIfChanged(ref _loading, value)) RaisePropertyChanged(nameof(ShowEmpty)); } }
        private string _errTitle = ""; public string ErrorTitle { get => _errTitle; private set { if (SetAndNotifyIfChanged(ref _errTitle, value)) { RaisePropertyChanged(nameof(HasError)); RaisePropertyChanged(nameof(ShowEmpty)); } } }
        private string _errText = ""; public string ErrorText { get => _errText; private set => SetAndNotifyIfChanged(ref _errText, value); }
        public bool HasError => _errTitle.Length > 0;
        public bool ShowEmpty => !Loading && !HasError && Items.Count == 0;

        private string _sortKey = "Name"; private bool _sortAsc = true;
        public string SortKey => _sortKey; public bool SortAsc => _sortAsc;

        private IReadOnlyList<PaneItem> _selection = Array.Empty<PaneItem>();
        public IReadOnlyList<PaneItem> Selection { get => _selection; set { _selection = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(FooterText)); Owner.SelectionChanged(); } }
        public string FooterText
        {
            get
            {
                var s = string.Format(FtText.T("ft_items_count"), Items.Count);
                if (Selection.Count == 1) s += " · " + string.Format(FtText.T("ft_selected_one"), Selection[0].Name) + (Selection[0].IsDirectory ? "" : $" ({Selection[0].SizeText})");
                else if (Selection.Count > 1) s += " · " + string.Format(FtText.T("ft_selected_n"), Selection.Count);
                return s;
            }
        }

        public RelayCommand CmdBack { get; }
        public RelayCommand CmdForward { get; }
        public RelayCommand CmdUp { get; }
        public RelayCommand CmdRefresh { get; }
        public RelayCommand CmdGo { get; }
        public RelayCommand CmdNewFolder { get; }
        public RelayCommand CmdSort { get; }

        protected FilePaneViewModel(FileNavigatorViewModel owner)
        {
            Owner = owner;
            CmdBack = new RelayCommand(async _ => { if (_back.Count == 0) return; _fwd.Add(CurrentPath); var p = _back[^1]; _back.RemoveAt(_back.Count - 1); await GoAsync(p, false); });
            CmdForward = new RelayCommand(async _ => { if (_fwd.Count == 0) return; _back.Add(CurrentPath); var p = _fwd[^1]; _fwd.RemoveAt(_fwd.Count - 1); await GoAsync(p, false); });
            CmdUp = new RelayCommand(async _ => await GoAsync(ParentOf(CurrentPath)));
            CmdRefresh = new RelayCommand(async _ => await RefreshAsync());
            CmdGo = new RelayCommand(async o => { if (o is string p) await GoAsync(p); });
            CmdNewFolder = new RelayCommand(async _ => await Owner.NewFolderAsync(this));
            CmdSort = new RelayCommand(o => { var k = o as string ?? "Name"; if (k == _sortKey) _sortAsc = !_sortAsc; else { _sortKey = k; _sortAsc = true; } RaisePropertyChanged(nameof(SortKey)); RaisePropertyChanged(nameof(SortAsc)); Resort(); });
        }

        public async Task GoAsync(string path, bool addHistory = true)
        {
            if (addHistory && _hasLoaded && path != CurrentPath) { if (_back.Count == 0 || _back[^1] != CurrentPath) _back.Add(CurrentPath); _fwd.Clear(); }
            await LoadAsync(path);
            RaisePropertyChanged(nameof(CanBack)); RaisePropertyChanged(nameof(CanForward));
        }

        public Task RefreshAsync() => LoadAsync(string.IsNullOrEmpty(CurrentPath) && !IsRemote ? RootPath : CurrentPath == "" ? RootPath : CurrentPath);

        private int _loadId;
        private bool _hasLoaded;
        private async Task LoadAsync(string path)
        {
            var id = ++_loadId;
            Loading = true; ErrorTitle = ""; ErrorText = "";
            try
            {
                var l = await ListAsync(path);
                if (id != _loadId) return;
                Items.Clear();
                foreach (var i in Sort(l)) Items.Add(i);
                CurrentPath = path; _hasLoaded = true;
                Crumbs.Clear(); foreach (var c in BuildCrumbs(path)) Crumbs.Add(c);
                Selection = Array.Empty<PaneItem>();
            }
            catch (FileTransferException e) when (id == _loadId)
            {
                ErrorTitle = e.Kind switch { FtErrorKind.Denied => FtText.T("ft_pane_denied_title"), FtErrorKind.NotFound => FtText.T("ft_pane_notfound_title"), FtErrorKind.Timeout or FtErrorKind.Disconnected => FtText.T("ft_unavail_noresponse_title"), _ => FtText.T("ft_pane_error_title") };
                ErrorText = e.Kind == FtErrorKind.Denied ? FtText.T("ft_pane_denied_text") : FtText.Describe(e);
            }
            finally { if (id == _loadId) { Loading = false; RaisePropertyChanged(nameof(ShowEmpty)); RaisePropertyChanged(nameof(FooterText)); } }
        }

        private IEnumerable<PaneItem> Sort(IEnumerable<PaneItem> l)
        {
            Func<PaneItem, object> key = _sortKey switch { "Date" => i => i.Modified ?? DateTime.MinValue, "Type" => i => i.TypeText, "Size" => i => i.Size, _ => i => i.Name.ToLowerInvariant() };
            var dirs = l.Where(i => i.IsDirectory); var files = l.Where(i => !i.IsDirectory);
            return (_sortAsc ? dirs.OrderBy(key) : dirs.OrderByDescending(key)).Concat(_sortAsc ? files.OrderBy(key) : files.OrderByDescending(key));
        }

        private void Resort() { var l = Sort(Items.ToList()).ToList(); Items.Clear(); foreach (var i in l) Items.Add(i); }
    }

    public class LocalPaneViewModel : FilePaneViewModel
    {
        public LocalPaneViewModel(FileNavigatorViewModel o) : base(o) { }
        public override string Label => FtText.T("ft_pane_local");
        public override string Sub => Environment.MachineName;
        public override bool IsRemote => false;
        public override string RootPath => "";
        public override string ParentOf(string path) => string.IsNullOrEmpty(path) ? "" : Directory.GetParent(path.TrimEnd('\\'))?.FullName ?? "";

        public override Task<List<PaneItem>> ListAsync(string path) => Task.Run(() =>
        {
            var l = new List<PaneItem>();
            try
            {
                if (string.IsNullOrEmpty(path))
                    foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady)) l.Add(new PaneItem { Name = d.Name.TrimEnd('\\'), IsDirectory = true, LocalPath = d.Name });
                else
                    foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos())
                    {
                        if ((e.Attributes & FileAttributes.System) != 0 && (e.Attributes & FileAttributes.Hidden) != 0) continue;   // file di sistema nascosti
                        var dir = (e.Attributes & FileAttributes.Directory) != 0;
                        l.Add(new PaneItem { Name = e.Name, IsDirectory = dir, Size = dir ? 0 : ((FileInfo)e).Length, Modified = e.LastWriteTime, LocalPath = e.FullName });
                    }
            }
            catch (UnauthorizedAccessException ex) { throw new FileTransferException(FtErrorKind.Denied, ex.Message, null, ex); }
            catch (DirectoryNotFoundException ex) { throw new FileTransferException(FtErrorKind.NotFound, ex.Message, null, ex); }
            catch (IOException ex) { throw new FileTransferException(FtErrorKind.Unknown, ex.Message, null, ex); }
            return l;
        });

        protected override IEnumerable<PathCrumb> BuildCrumbs(string path)
        {
            yield return new PathCrumb { Text = FtText.T("ft_this_pc"), Path = "" };
            if (string.IsNullOrEmpty(path)) yield break;
            var acc = "";
            foreach (var part in path.TrimEnd('\\').Split('\\'))
            {
                acc = acc.Length == 0 ? part + "\\" : Path.Combine(acc, part);
                yield return new PathCrumb { Text = part, Path = acc };
            }
        }
    }

    public class RemotePaneViewModel : FilePaneViewModel
    {
        private readonly FileTransferViewModel _vm;
        public RemotePaneViewModel(FileNavigatorViewModel o, FileTransferViewModel vm) : base(o) { _vm = vm; }
        public override string Label => FtText.T("ft_pane_remote");
        public override string Sub => $"{_vm.HostName}";
        public override bool IsRemote => true;
        public override string RootPath => RemotePath.Root;
        public override string ParentOf(string path) => RemotePath.Parent(path);

        public override async Task<List<PaneItem>> ListAsync(string path)
        {
            var m = _vm.Manager ?? throw new FileTransferException(FtErrorKind.Disconnected, FtText.T("ft_err_not_connected"));
            var l = await m.ListAsync(path);
            return l.Select(e => new PaneItem { Name = e.Name, IsDirectory = e.IsDirectory, Size = e.Size, Modified = e.ModifiedUtc == DateTime.MinValue ? null : e.ModifiedUtc.ToLocalTime(), Remote = e }).ToList();
        }

        protected override IEnumerable<PathCrumb> BuildCrumbs(string path)
        {
            yield return new PathCrumb { Text = _vm.HostName, Path = RemotePath.Root };
            var acc = "";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + part; yield return new PathCrumb { Text = part, Path = acc }; }
        }
    }

    /// <summary>il navigatore a due colonne a pagina intera: questo PC a sinistra, il PC remoto a destra, azioni al centro, coda in basso</summary>
    public class FileNavigatorViewModel : NotifyPropertyChangedBase
    {
        public FileTransferViewModel Vm { get; }
        public LocalPaneViewModel Local { get; }
        public RemotePaneViewModel Remote { get; }
        public string Title => $"{FtText.T("ft_this_pc")} ⇄ {Vm.HostName}";

        public FileNavigatorViewModel(FileTransferViewModel vm)
        {
            Vm = vm;
            Local = new LocalPaneViewModel(this);
            Remote = new RemotePaneViewModel(this, vm);
            CmdUpload = new RelayCommand(async _ => await UploadSelectionAsync());
            CmdDownload = new RelayCommand(_ => DownloadSelection());
            CmdRename = new RelayCommand(async _ => await RenameAsync());
            CmdDelete = new RelayCommand(async _ => await DeleteRemoteAsync());
            CmdCopyRemote = new RelayCommand(async _ => await Vm.CopyRemoteToClipboardAsync(RemoteSelection()));
            CmdPasteRemote = new RelayCommand(async _ => await PasteIntoRemoteAsync());
            vm.JobFinished += OnJobFinished;
        }

        private bool _started;
        public async Task StartAsync(string? remoteWire)
        {
            if (!_started)
            {
                _started = true;
                var baseDir = IoC.Get<ConfigurationService>().FileTransfer.DefaultLocalFolder;
                await Local.GoAsync(System.IO.Directory.Exists(baseDir) ? baseDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), false);
            }
            if (!await Vm.EnsureAvailableAsync()) return;
            var start = remoteWire ?? (string.IsNullOrEmpty(Vm.DestinationWire) ? RemotePath.Root : Vm.DestinationWire);
            await Remote.GoAsync(start, false);
        }

        public RelayCommand CmdUpload { get; }
        public RelayCommand CmdDownload { get; }
        public RelayCommand CmdRename { get; }
        public RelayCommand CmdDelete { get; }
        public RelayCommand CmdCopyRemote { get; }
        public RelayCommand CmdPasteRemote { get; }
        /// <summary>"Nuova cartella" dal pulsante centrale: nella colonna con il fuoco (di default quella remota)</summary>
        public RelayCommand ActivePaneNewFolder => new RelayCommand(async _ => await NewFolderAsync(ActivePane ?? Remote));

        /// <summary>colonna con il fuoco (per F2, Canc, F7)</summary>
        public FilePaneViewModel? ActivePane { get; set; }

        public bool CanUpload => Local.Selection.Count > 0 && Remote.CurrentPath != RemotePath.Root && !Vm.IsUnavailable;
        public bool CanDownload => RemoteSelection().Count > 0 && !string.IsNullOrEmpty(Local.CurrentPath) && !Vm.IsUnavailable;
        public bool CanRename => ActivePane?.Selection.Count == 1 && !(ActivePane.IsRemote && ActivePane.Selection[0].Remote?.IsDrive == true) && !(!ActivePane.IsRemote && ActivePane.CurrentPath == "");
        public bool CanDelete => RemoteSelection().Count > 0 && !Vm.IsUnavailable;
        public bool CanNewFolder => true;
        public void SelectionChanged()
        {
            RaisePropertyChanged(nameof(CanUpload)); RaisePropertyChanged(nameof(CanDownload)); RaisePropertyChanged(nameof(CanRename)); RaisePropertyChanged(nameof(CanDelete));
        }

        private List<RemoteEntry> RemoteSelection() => Remote.Selection.Where(i => i.Remote != null && !i.Remote.IsDrive).Select(i => i.Remote!).ToList();

        private async Task UploadSelectionAsync()
        {
            var paths = Local.Selection.Where(i => i.LocalPath != null).Select(i => i.LocalPath!).ToList();
            if (paths.Count == 0) return;
            if (Remote.CurrentPath == RemotePath.Root) { Vm.ShowMessage(FtText.T("ft_pick_choose_drive"), FtMessageKind.Error); return; }
            await Vm.UploadAsync(paths, Remote.CurrentPath);
        }

        private void DownloadSelection()
        {
            var items = RemoteSelection();
            if (items.Count == 0 || string.IsNullOrEmpty(Local.CurrentPath)) return;
            Vm.EnqueueDownload(items, Local.CurrentPath, Remote.CurrentPath == RemotePath.Root ? Vm.HostName : RemotePath.NameOf(Remote.CurrentPath));
        }

        /// <summary>rilascio su una cartella o sull'elenco: da Esplora risorse o dalla colonna locale verso il remoto</summary>
        public async Task DropOnRemoteAsync(IReadOnlyList<string> localPaths, string? folderWire)
        {
            var target = folderWire ?? Remote.CurrentPath;
            if (target == RemotePath.Root) { Vm.ShowMessage(FtText.T("ft_pick_choose_drive"), FtMessageKind.Error); return; }
            await Vm.UploadAsync(localPaths, target);
        }

        public void DropOnLocal(IReadOnlyList<RemoteEntry> items, string? folderLocal)
        {
            var target = folderLocal ?? Local.CurrentPath;
            if (string.IsNullOrEmpty(target)) return;
            Vm.EnqueueDownload(items, target, Remote.CurrentPath == RemotePath.Root ? Vm.HostName : RemotePath.NameOf(Remote.CurrentPath));
        }

        private async Task PasteIntoRemoteAsync()
        {
            var paths = FileTransferShell.GetLocalPathsFromClipboard();
            if (paths.Count > 0) await DropOnRemoteAsync(paths, null);
        }

        private void OnJobFinished(TransferJob job)
        {
            if (job.Direction == TransferDirection.Upload && string.Equals(job.TargetDir, Remote.CurrentPath, StringComparison.OrdinalIgnoreCase)) _ = Remote.RefreshAsync();
            if (job.Direction == TransferDirection.Download && string.Equals(job.TargetDir, Local.CurrentPath, StringComparison.OrdinalIgnoreCase)) _ = Local.RefreshAsync();
        }

        public async Task NewFolderAsync(FilePaneViewModel pane)
        {
            if (pane.CurrentPath == pane.RootPath) { Vm.ShowMessage(FtText.T("ft_pick_choose_drive"), FtMessageKind.Error); return; }
            var name = FtDialogs.Prompt(FtText.T("ft_newfolder_title"), FtText.T("ft_newfolder_heading"), FtText.T("ft_newfolder_default"), FtText.T("ft_create"));
            if (name == null) return;
            try
            {
                if (pane.IsRemote) await Vm.Manager!.MkdirAsync(RemotePath.Join(pane.CurrentPath, name));
                else Directory.CreateDirectory(Path.Combine(pane.CurrentPath, name));
            }
            catch (Exception e) { Vm.ShowMessage(e is FileTransferException f ? FtText.Describe(f) : e.Message, FtMessageKind.Error); }
            await pane.RefreshAsync();
        }

        public async Task RenameAsync()
        {
            var pane = ActivePane; if (pane == null || pane.Selection.Count != 1) return;
            var item = pane.Selection[0];
            var name = FtDialogs.Prompt(FtText.T("ft_rename_title"), string.Format(FtText.T("ft_rename_heading"), item.Name), item.Name, FtText.T("ft_rename"));
            if (name == null || name == item.Name) return;
            try
            {
                if (pane.IsRemote && item.Remote != null) await Vm.Manager!.RenameAsync(item.Remote.FullPath, RemotePath.Join(item.Remote.Dir, name));
                else if (item.LocalPath != null)
                {
                    var dest = Path.Combine(Path.GetDirectoryName(item.LocalPath.TrimEnd('\\'))!, name);
                    if (item.IsDirectory) Directory.Move(item.LocalPath, dest); else File.Move(item.LocalPath, dest);
                }
            }
            catch (Exception e) { Vm.ShowMessage(e is FileTransferException f ? FtText.Describe(f) : e.Message, FtMessageKind.Error); }
            await pane.RefreshAsync();
        }

        public async Task DeleteRemoteAsync()
        {
            var sel = RemoteSelection();
            if (sel.Count == 0) return;
            var cfg = IoC.Get<ConfigurationService>();
            if (cfg.FileTransfer.ConfirmRemoteDelete)
            {
                var (ok, dontAsk) = FtDialogs.ConfirmRemoteDelete(sel);
                if (!ok) return;
                if (dontAsk) { cfg.FileTransfer.ConfirmRemoteDelete = false; cfg.Save(); }
            }
            try { foreach (var e in sel) await Vm.Manager!.DeleteAsync(e); }
            catch (FileTransferException e) { Vm.ShowMessage(FtText.Describe(e), FtMessageKind.Error); }
            await Remote.RefreshAsync();
        }
    }
}
