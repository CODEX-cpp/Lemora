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
    /// <summary>voce della navigazione a cartelle</summary>
    public class FolderNavItem : NotifyPropertyChangedBase
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public int Level { get; init; }
        public int Count { get; init; }
        public bool HasChildren { get; init; }
        public bool IsExpanded { get; init; }
        /// <summary>contiene voci in sola lettura (Teams senza permesso di scrittura): Rinomina ed Elimina non si mostrano</summary>
        public bool IsReadOnly { get; init; }
        public bool IsTop => Level == 0;
        public string Initial => string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1).ToUpper();
        /// <summary>rientro a sinistra delle sottocartelle (16 px per livello)</summary>
        public System.Windows.Thickness Indent => new System.Windows.Thickness(Math.Max(0, Level - 1) * 16, 4, 0, 0);
    }

    public abstract partial class ServerPageViewModelBase
    {
        /// <summary>cartelle visibili nella navigazione (albero appiattito; le sottocartelle compaiono solo se il genitore e' aperto)</summary>
        public ObservableCollection<FolderNavItem> FolderNav { get; } = new ObservableCollection<FolderNavItem>();

        private readonly HashSet<string> _expandedFolders = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        private string _selectedFolderPath = "";
        /// <summary>cartella scelta nella navigazione; vuoto = nessun filtro per cartella</summary>
        public string SelectedFolderPath
        {
            get => _selectedFolderPath;
            private set
            {
                if (SetAndNotifyIfChanged(ref _selectedFolderPath, value))
                    CalcServerVisibleAndRefresh(true);
            }
        }

        /// <summary>la cartella contiene voci di un'origine in sola lettura (Teams senza permesso di scrittura)</summary>
        private bool IsFolderReadOnly(string path) => path.Length > 0
            && AppData.VmItemList.Any(vm => vm.Server.DataSource?.IsWritable == false && FolderService.IsIn(vm.Server.TreeNodes, path));

        public void RefreshFolderNav()
        {
            Execute.OnUIThread(() =>
            {
                try
                {
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
                            Count = FolderService.CountServers(p),
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
        /// <summary>pagina di modifica della cartella, al centro della Home (null = chiusa)</summary>
        public Lemora.View.Editor.FolderEditPageViewModel? FolderEditViewModel
        {
            get => _folderEditViewModel;
            set => SetAndNotifyIfChanged(ref _folderEditViewModel, value);
        }

        private FolderSummaryViewModel? _folderSummary;
        /// <summary>riepilogo della cartella scelta, nel pannello destro (null = nessuna cartella, o e' scelto un server)</summary>
        public FolderSummaryViewModel? FolderSummary
        {
            get => _folderSummary;
            set { if (SetAndNotifyIfChanged(ref _folderSummary, value)) OnFolderSummaryChanged(value); }
        }

        /// <summary>la vista a elenco nasconde il dettaglio del server quando si mostra il riepilogo della cartella</summary>
        protected virtual void OnFolderSummaryChanged(FolderSummaryViewModel? summary) { }

        /// <summary>ricostruisce il riepilogo dalla cartella scelta (dopo una scelta, una modifica o la chiusura della pagina di modifica)</summary>
        public void RefreshFolderSummary()
        {
            var path = SelectedFolderPath;
            RefreshFolderChildren(path);
            FolderSummary = path.Length == 0 || !FolderService.Exists(path) ? null
                : new FolderSummaryViewModel(path,
                    edit: () => OpenFolderEdit(path, false),
                    addServer: () => GlobalEventHelper.OnGoToServerAddPage?.Invoke(new List<string>(), null,
                        new Lemora.View.Editor.ServerEditorPageViewModel.ParamsServerAddPreset { TreeNodes = FolderService.ToNodes(path) }));
        }

        /// <summary>sottocartelle dirette della cartella scelta, mostrate sopra i server nella lista centrale</summary>
        public ObservableCollection<FolderNavItem> FolderChildren { get; } = new ObservableCollection<FolderNavItem>();
        public bool HasFolderChildren => FolderChildren.Count > 0;
        public string FolderServersTitle => Lemora.Setup.SetupText.T("fs_servers").ToUpper();
        public string FolderChildrenTitle => Lemora.Setup.SetupText.T("fs_subfolders").ToUpper();

        private void RefreshFolderChildren(string path)
        {
            FolderChildren.Clear();
            if (path.Length > 0)
                foreach (var p in FolderService.AllPaths().Where(x => string.Equals(FolderService.Parent(x), path, StringComparison.CurrentCultureIgnoreCase)))
                    FolderChildren.Add(new FolderNavItem { Path = p, Name = FolderService.Leaf(p), Level = FolderService.Level(p), Count = FolderService.CountServers(p), IsReadOnly = IsFolderReadOnly(p) });
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
            // aprendo una cartella se ne vede TUTTO il contenuto: il testo scritto nella ricerca (che si sommerebbe al filtro per cartella) viene tolto
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
        /// <summary>nuova cartella; con una voce come parametro la crea dentro quella cartella</summary>
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
        /// <summary>"Rinomina": apre la pagina di modifica con il campo Nome selezionato e con il focus</summary>
        public RelayCommand CmdRenameFolder => _cmdRenameFolder ??= new RelayCommand(o =>
        {
            var path = (o as FolderNavItem)?.Path ?? o as string;
            if (string.IsNullOrEmpty(path) || IsFolderReadOnly(path)) return;
            OpenFolderEdit(path, true);
        });

        private RelayCommand? _cmdFolderSettings;
        /// <summary>"Modifica" (menu e doppio clic): apre la pagina di modifica della cartella</summary>
        public RelayCommand CmdFolderSettings => _cmdFolderSettings ??= new RelayCommand(o =>
        {
            var path = (o as FolderNavItem)?.Path ?? o as string;
            if (string.IsNullOrEmpty(path) || IsFolderReadOnly(path)) return;   // anche dal doppio clic sulla cartella
            OpenFolderEdit(path, false);
        });

        private void OpenFolderEdit(string path, bool focusName)
        {
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
                }) { FocusName = focusName };
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
