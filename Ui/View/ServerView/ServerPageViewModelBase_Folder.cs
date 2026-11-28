using Lemora.Model;
using Lemora.Service;
using Lemora.Utils;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lemora.View.ServerView
{
    /// <summary>folder navigation item</summary>
    public class FolderNavItem : NotifyPropertyChangedBase
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public int Level { get; init; }
        public int Count { get; init; }
        public bool HasChildren { get; init; }
        public bool IsExpanded { get; init; }
        /// <summary>contains read-only entries (Teams without write permission): Rename and Delete are not shown</summary>
        public bool IsReadOnly { get; init; }
        public bool IsTop => Level == 0;
        public string Initial => string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1).ToUpper();
        /// <summary>left indent of subfolders (16 px per level)</summary>
        public System.Windows.Thickness Indent => new System.Windows.Thickness(Math.Max(0, Level - 1) * 16, 4, 0, 0);
    }

    public abstract partial class ServerPageViewModelBase
    {
        /// <summary>folders visible in the navigation (flattened tree; subfolders appear only if the parent is expanded)</summary>
        public ObservableCollection<FolderNavItem> FolderNav { get; } = new ObservableCollection<FolderNavItem>();

        private readonly HashSet<string> _expandedFolders = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        private string _selectedFolderPath = "";
        /// <summary>folder chosen in the navigation; empty = no folder filter</summary>
        public string SelectedFolderPath
        {
            get => _selectedFolderPath;
            private set
            {
                if (!SetAndNotifyIfChanged(ref _selectedFolderPath, value)) return;
                RaisePropertyChanged(nameof(IsNoFolderSelected));
                CalcServerVisibleAndRefresh(true);
            }
        }

        // ---- "No folder": fixed item under Home, only if there are saved connections outside any folder
        private int _noFolderCount;
        public int NoFolderCount { get => _noFolderCount; private set => SetAndNotifyIfChanged(ref _noFolderCount, value); }
        public bool IsNoFolderSelected => SelectedFolderPath == FolderService.NoFolder;

        private RelayCommand? _cmdShowNoFolder;
        public RelayCommand CmdShowNoFolder => _cmdShowNoFolder ??= new RelayCommand(_ =>
        {
            if (IsNoFolderSelected) return;   // a second click does not close it (like Home)
            CmdSelectFolder.Execute(FolderService.NoFolder);
            OnNoFolderOpened();
        });

        /// <summary>the list view removes the detail left on the right (a connection that is not among those without a folder)</summary>
        protected virtual void OnNoFolderOpened() { }

        /// <summary>read-only Teams PC (all folders, root included), or a folder with entries from a read-only source (Teams without write permission)</summary>
        private bool IsFolderReadOnly(string path) => Lemora.Setup.TeamsShared.ReadOnlyTeam
            || path.Length > 0 && AppData.VmItemList.Any(vm => vm.Server.DataSource?.IsWritable == false && FolderService.IsIn(vm.Server.TreeNodes, path));

        /// <summary>"New folder" button at the end of the list: not for a read-only Teams PC</summary>
        public bool CanCreateFolder => !Lemora.Setup.TeamsShared.ReadOnlyTeam;

        public void RefreshFolderNav()
        {
            Execute.OnUIThread(() =>
            {
                try
                {
                    RaisePropertyChanged(nameof(CanCreateFolder));   // the Teams permission can change with the sync
                    NoFolderCount = FolderService.CountDirectServers(FolderService.NoFolder);
                    // the last connection without a folder moved elsewhere: the item disappears, back to Home
                    if (NoFolderCount == 0 && IsNoFolderSelected) { SelectedFolderPath = ""; IsHomeMode = true; }
                    var paths = FolderService.AllPaths();
                    var parentsWithChildren = new HashSet<string>(paths.Select(FolderService.Parent).Where(x => x.Length > 0), StringComparer.CurrentCultureIgnoreCase);
                    FolderNav.Clear();
                    foreach (var p in paths)
                    {
                        var visible = true;
                        for (var par = FolderService.Parent(p); par.Length > 0; par = FolderService.Parent(par))
                        {
                            if (!_expandedFolders.Contains(par)) { visible = false; break; }
                        }
                        if (!visible) continue;
                        FolderNav.Add(new FolderNavItem
                        {
                            Path = p,
                            Name = FolderService.Leaf(p),
                            Level = FolderService.Level(p),
                            // folder with subfolders: collapsed it counts everything inside, expanded only its own connections (the subfolders count their own)
                            Count = parentsWithChildren.Contains(p) && !_expandedFolders.Contains(p) ? FolderService.CountServers(p) : FolderService.CountDirectServers(p),
                            HasChildren = parentsWithChildren.Contains(p),
                            IsExpanded = _expandedFolders.Contains(p),
                            IsReadOnly = IsFolderReadOnly(p),
                        });
                    }
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                }
            });
        }

        private Lemora.View.Editor.FolderEditPageViewModel? _folderEditViewModel;
        /// <summary>folder edit page, in the middle of Home (null = closed)</summary>
        public Lemora.View.Editor.FolderEditPageViewModel? FolderEditViewModel
        {
            get => _folderEditViewModel;
            set => SetAndNotifyIfChanged(ref _folderEditViewModel, value);
        }

        private FolderSummaryViewModel? _folderSummary;
        /// <summary>summary of the chosen folder, in the right panel (null = no folder, or a server is chosen)</summary>
        public FolderSummaryViewModel? FolderSummary
        {
            get => _folderSummary;
            set { if (SetAndNotifyIfChanged(ref _folderSummary, value)) OnFolderSummaryChanged(value); }
        }

        /// <summary>the list view hides the server detail when the folder summary is shown</summary>
        protected virtual void OnFolderSummaryChanged(FolderSummaryViewModel? summary) { }

        /// <summary>rebuilds the summary from the chosen folder (after a choice, an edit or closing the edit page)</summary>
        public void RefreshFolderSummary()
        {
            var path = SelectedFolderPath;
            RefreshFolderChildren(path);
            FolderSummary = path.Length == 0 || !FolderService.Exists(path) ? null
                : new FolderSummaryViewModel(path,
                    edit: () => OpenFolderEdit(path, false),
                    addServer: () => GlobalEventHelper.OnGoToServerAddPage?.Invoke(new List<string>(), null,
                        new Lemora.View.Editor.ServerEditorPageViewModel.ParamsServerAddPreset { TreeNodes = FolderService.ToNodes(path) }),
                    canEdit: !IsFolderReadOnly(path));
        }

        /// <summary>direct subfolders of the chosen folder, shown above the servers in the central list</summary>
        public ObservableCollection<FolderNavItem> FolderChildren { get; } = new ObservableCollection<FolderNavItem>();
        public bool HasFolderChildren => FolderChildren.Count > 0;
        public string FolderServersTitle => Lemora.Setup.SetupText.T("fs_servers").ToUpper();
        public string FolderChildrenTitle => Lemora.Setup.SetupText.T("fs_subfolders").ToUpper();

        private void RefreshFolderChildren(string path)
        {
            FolderChildren.Clear();
            if (path.Length > 0)
                foreach (var p in FolderService.AllPaths().Where(x => string.Equals(FolderService.Parent(x), path, StringComparison.CurrentCultureIgnoreCase)))
                    FolderChildren.Add(new FolderNavItem { Path = p, Name = FolderService.Leaf(p), Level = FolderService.Level(p), Count = FolderService.AllPaths().Any(x => string.Equals(FolderService.Parent(x), p, StringComparison.CurrentCultureIgnoreCase)) && !_expandedFolders.Contains(p) ? FolderService.CountServers(p) : FolderService.CountDirectServers(p), IsReadOnly = IsFolderReadOnly(p) });
            RaisePropertyChanged(nameof(HasFolderChildren));
        }

        private RelayCommand? _cmdSelectFolder;
        public RelayCommand CmdSelectFolder => _cmdSelectFolder ??= new RelayCommand(o =>
        {
            var path = (o as FolderNavItem)?.Path ?? o as string ?? "";
            TagListViewModel = null;
            FolderEditViewModel = null;
            IsHomeMode = false;
            for (var par = FolderService.Parent(path); par.Length > 0; par = FolderService.Parent(par)) _expandedFolders.Add(par);
            var opening = !string.Equals(SelectedFolderPath, path, StringComparison.CurrentCultureIgnoreCase);
            // opening a folder shows ALL of its content: the text typed in the search (which would add to the folder filter) is cleared
            if (opening && path.Length > 0 && TagAndKeywordEncodeHelper.DecodeKeyword(IoC.Get<MainWindowViewModel>().MainFilterString).KeyWords.Count > 0)
                IoC.Get<MainWindowViewModel>().SetMainFilterString(TagFilters, null);
            SelectedFolderPath = opening ? path : "";
            RefreshFolderNav();
            RefreshFolderSummary();
        });

        private RelayCommand? _cmdToggleFolder;
        public RelayCommand CmdToggleFolder => _cmdToggleFolder ??= new RelayCommand(o =>
        {
            if (o is not FolderNavItem item) return;
            if (!_expandedFolders.Remove(item.Path)) _expandedFolders.Add(item.Path);
            RefreshFolderNav();
        });

        private RelayCommand? _cmdNewFolder;
        /// <summary>new folder; with an item as parameter it is created inside that folder</summary>
        public RelayCommand CmdNewFolder => _cmdNewFolder ??= new RelayCommand(async o =>
        {
            var parent = (o as FolderNavItem)?.Path ?? "";
            if (IsFolderReadOnly(parent)) return;
            var dlg = new Lemora.View.Editor.NewFolderDialogViewModel(parent, IoC.Translate("folder_new_default_name"));
            Lemora.View.Utils.MaskLayerController.ShowWindowWithMask(dlg, IoC.Get<MainWindowViewModel>());
            if (await dlg.WaitDialogResult() != true) return;
            if (parent.Length > 0) _expandedFolders.Add(parent);
            RefreshFolderNav();
        });

        private RelayCommand? _cmdRenameFolder;
        /// <summary>"Rename": opens the edit page with the Name field selected and focused</summary>
        public RelayCommand CmdRenameFolder => _cmdRenameFolder ??= new RelayCommand(o =>
        {
            var path = (o as FolderNavItem)?.Path ?? o as string;
            if (string.IsNullOrEmpty(path) || IsFolderReadOnly(path)) return;
            OpenFolderEdit(path, true);
        });

        private RelayCommand? _cmdFolderSettings;
        /// <summary>"Edit" (menu and double click): opens the folder edit page</summary>
        public RelayCommand CmdFolderSettings => _cmdFolderSettings ??= new RelayCommand(o =>
        {
            var path = (o as FolderNavItem)?.Path ?? o as string;
            if (string.IsNullOrEmpty(path) || IsFolderReadOnly(path)) return;   // also from the double click on the folder
            OpenFolderEdit(path, false);
        });

        private void OpenFolderEdit(string path, bool focusName)
        {
            if (IsFolderReadOnly(path)) return;   // single entry point (menu, summary, return from the editor): with read-only Teams entries the page does not open
            TagListViewModel = null;
            FolderEditViewModel = new Lemora.View.Editor.FolderEditPageViewModel(path,
                close: () => { FolderEditViewModel = null; RefreshFolderSummary(); },
                afterDelete: deleted =>
                {
                    if (SelectedFolderPath.Length > 0 && FolderService.IsIn(FolderService.ToNodes(SelectedFolderPath), deleted)) SelectedFolderPath = "";
                    RefreshFolderNav();
                    RefreshFolderSummary();
                },
                afterRename: (old, now) =>
                {
                    var wasExpanded = _expandedFolders.Remove(old);
                    if (wasExpanded) _expandedFolders.Add(now);
                    if (SelectedFolderPath.Length > 0 && FolderService.IsIn(FolderService.ToNodes(SelectedFolderPath), old)) SelectedFolderPath = now + SelectedFolderPath.Substring(old.Length);
                    RefreshFolderNav();
                    RefreshFolderSummary();
                })
            {
                FocusName = focusName,
                ReopenAtTab = (p, tab) =>
                {
                    if (!FolderService.Exists(p)) return;
                    OpenFolderEdit(p, false);
                    if (FolderEditViewModel != null) FolderEditViewModel.SelectedTab = tab;
                }
            };
        }

        private RelayCommand? _cmdDeleteFolder;
        public RelayCommand CmdDeleteFolder => _cmdDeleteFolder ??= new RelayCommand(o =>
        {
            if (o is not FolderNavItem item || IsFolderReadOnly(item.Path)) return;
            var credCount = FolderCredentialService.ForFolder(item.Path).Count;
            if (credCount > 0 && FolderService.CountServers(item.Path) == 0
                && !MessageBoxHelper.Confirm(string.Format(IoC.Translate("folder_delete_with_credentials_confirm"), item.Name, credCount))) return;
            if (!FolderService.Delete(item.Path, out var error))
            {
                MessageBoxHelper.ErrorAlert(error);
                return;
            }
            if (SelectedFolderPath.Length > 0 && FolderService.IsIn(FolderService.ToNodes(SelectedFolderPath), item.Path)) SelectedFolderPath = "";
            RefreshFolderNav();
        });
    }
}
