using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Lemora.Service.FileTransfer;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.FileTransfer
{
    /// <summary>"Carica in...": le sole cartelle del PC remoto, si entra con il doppio clic</summary>
    public class RemoteFolderPickerViewModel : NotifyPropertyChangedBase
    {
        private readonly FileTransferManager _m;
        public string Host { get; }
        public ObservableCollection<RemoteEntry> Items { get; } = new();
        public string? Chosen { get; private set; }
        public bool Remember { get; set; } = true;
        public event Action? RequestClose;

        private string _current = RemotePath.Root;
        public string Current { get => _current; private set { if (SetAndNotifyIfChanged(ref _current, value)) { RaisePropertyChanged(nameof(CurrentText)); RebuildCrumbs(); } } }
        /// <summary>il percorso a pulsanti come in Esplora risorse: C: › Users › Laboratorio (un clic porta in quella cartella)</summary>
        public ObservableCollection<PathCrumb> Crumbs { get; } = new();
        public RelayCommand CmdGo => new RelayCommand(async o => { if (o is string p) await NavigateAsync(p); });
        private void RebuildCrumbs()
        {
            Crumbs.Clear();
            Crumbs.Add(new PathCrumb { Text = FtText.T("ft_pick_root_crumb"), Path = RemotePath.Root });
            var acc = "";
            foreach (var part in Current.Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + part; Crumbs.Add(new PathCrumb { Text = part, Path = acc }); }
        }
        public string CurrentText => Current == RemotePath.Root ? FtText.T("ft_pick_root") : RemotePath.ToWindows(Current);
        private RemoteEntry? _selected;
        public RemoteEntry? Selected { get => _selected; set => SetAndNotifyIfChanged(ref _selected, value); }
        private string _status = "";
        public string StatusText { get => _status; private set { if (SetAndNotifyIfChanged(ref _status, value)) RaisePropertyChanged(nameof(HasStatus)); } }
        public bool HasStatus => _status.Length > 0;

        public RelayCommand CmdUp { get; }
        public RelayCommand CmdChoose { get; }
        public RelayCommand CmdCancel { get; }

        public RemoteFolderPickerViewModel(FileTransferManager m, string startWire, string host)
        {
            _m = m; Host = host;
            RebuildCrumbs();
            CmdUp = new RelayCommand(async _ => await NavigateAsync(RemotePath.Parent(Current)));
            CmdChoose = new RelayCommand(_ =>
            {
                // una cartella selezionata nell'elenco vale quanto entrarci; altrimenti si sceglie quella mostrata (non la radice dei dischi)
                var target = Selected is { IsDirectory: true } s ? s.FullPath : Current;
                if (target == RemotePath.Root) { StatusText = FtText.T("ft_pick_choose_drive"); return; }
                Chosen = target; RequestClose?.Invoke();
            });
            CmdCancel = new RelayCommand(_ => { Chosen = null; RequestClose?.Invoke(); });
            _ = NavigateAsync(startWire);
        }

        public async System.Threading.Tasks.Task NavigateAsync(string wire)
        {
            StatusText = FtText.T("ft_loading");
            try
            {
                var l = await _m.ListAsync(wire);
                Items.Clear();
                foreach (var e in l.Where(x => x.IsDirectory)) Items.Add(e);
                Current = wire; Selected = null;
                StatusText = Items.Count == 0 ? FtText.T("ft_pick_empty") : "";
            }
            catch (FileTransferException e)
            {
                StatusText = FtText.Describe(e);
                // un percorso ricordato che non esiste piu': si riparte dai dischi
                if (wire != RemotePath.Root && e.Kind == FtErrorKind.NotFound) await NavigateAsync(RemotePath.Root);
            }
        }

        public void Enter() { if (Selected is { IsDirectory: true } s) _ = NavigateAsync(s.FullPath); }
    }

    public partial class RemoteFolderPickerWindow : WindowChromeBase
    {
        private readonly RemoteFolderPickerViewModel _vm;
        public RemoteFolderPickerWindow(RemoteFolderPickerViewModel vm)
        {
            InitializeComponent();
            _vm = vm; DataContext = vm;
            vm.RequestClose += () => Close();
        }
        private void List_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => _vm.Enter();
    }
}
