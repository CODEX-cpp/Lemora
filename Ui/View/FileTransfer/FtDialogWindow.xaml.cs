using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Lemora.Service.FileTransfer;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.FileTransfer
{
    public enum FtDialogKind { Info, Question, Warning, Error }

    public class FtButton
    {
        public string Label { get; init; } = "";
        public bool Primary { get; init; }
        public bool Danger { get; init; }
        public bool Normal => !Primary && !Danger;
        public object? Tag { get; init; }
    }

    public class FtConflictCards
    {
        public string ExistingSize { get; init; } = ""; public string ExistingDate { get; init; } = "";
        public string NewSize { get; init; } = ""; public string NewDate { get; init; } = "";
        public bool NewIsNewer { get; init; }
        public string NewTag { get; init; } = "";
        public bool HasNewTag => NewTag.Length > 0;
    }

    public class FtRuleLine { public string Text { get; init; } = ""; }

    /// <summary>dati e comandi di una finestra di dialogo del trasferimento file (titolo, testo, contenuto extra, casella, campo di testo, pulsanti)</summary>
    public class FtDialogModel : NotifyPropertyChangedBase
    {
        public string WindowTitle { get; set; } = "";
        public string Heading { get; set; } = "";
        public string Message { get; set; } = "";
        public bool HasMessage => Message.Length > 0;
        public FtDialogKind Kind { get; set; } = FtDialogKind.Info;
        public string IconGlyph => Kind switch { FtDialogKind.Question => "?", FtDialogKind.Warning => "!", FtDialogKind.Error => "✕", _ => "i" };
        public Brush IconBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Kind switch { FtDialogKind.Error => "#FF9B94", FtDialogKind.Info => "#60CDFF", _ => "#FDD663" }));
        public object? Extra { get; set; }
        public string CheckText { get; set; } = "";
        public bool HasCheck => CheckText.Length > 0;
        public bool IsChecked { get; set; }
        public bool HasInput { get; set; }
        private string _input = "";
        public string InputText { get => _input; set { if (SetAndNotifyIfChanged(ref _input, value)) InputError = ""; } }
        private string _inputError = "";
        public string InputError { get => _inputError; set { if (SetAndNotifyIfChanged(ref _inputError, value)) RaisePropertyChanged(nameof(HasInputError)); } }
        public bool HasInputError => _inputError.Length > 0;
        /// <summary>controllo del testo scritto: restituisce il motivo dell'errore o null</summary>
        public Func<string, string?>? Validate { get; set; }
        public List<FtButton> Buttons { get; set; } = new();
        public FtButton? Result { get; private set; }
        public event Action? Closed;

        public RelayCommand CmdChoose => new RelayCommand(o =>
        {
            if (o is not FtButton b) return;
            if (b.Primary && HasInput && Validate?.Invoke(InputText) is { } err) { InputError = err; return; }
            Result = b; Closed?.Invoke();
        });
        public RelayCommand CmdClose => new RelayCommand(_ => { Result = null; Closed?.Invoke(); });
    }

    public partial class FtDialogWindow : WindowChromeBase
    {
        public FtDialogWindow(FtDialogModel model)
        {
            InitializeComponent();
            DataContext = model;
            model.Closed += () => Close();
            Loaded += (_, _) => { if (model.HasInput) { TbInput.Focus(); TbInput.SelectAll(); } };
        }
    }

    /// <summary>le finestre di dialogo del trasferimento file (da chiamare dal thread dell'interfaccia)</summary>
    public static class FtDialogs
    {
        private static string T(string key) => FtText.T(key);

        public static FtDialogModel Show(FtDialogModel m)
        {
            var w = new FtDialogWindow(m);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
            if (owner != null && owner != w) w.Owner = owner;
            w.ShowDialog();
            return m;
        }

        public static void Info(string title, string text) =>
            Show(new FtDialogModel { WindowTitle = T("ft_title"), Heading = title, Message = text, Kind = FtDialogKind.Info, Buttons = { new FtButton { Label = T("ft_ok"), Primary = true } } });

        public static ConflictDecision Conflict(ConflictInfo info, string host)
        {
            string Date(DateTime d) => d == DateTime.MinValue ? "—" : d.ToLocalTime().ToString("g");
            var newer = info.SourceModifiedUtc > info.TargetModifiedUtc;
            var bigger = info.SourceSize > info.TargetSize;
            var tag = newer && bigger ? T("ft_conflict_newer_bigger") : newer ? T("ft_conflict_newer") : bigger ? T("ft_conflict_bigger") : "";
            var name = RemotePath.NameOf(info.TargetPath.Replace('\\', '/'));
            var where = RemotePath.NameOf(RemotePath.Parent(info.TargetPath.Replace('\\', '/')));
            var m = new FtDialogModel
            {
                WindowTitle = T("ft_conflict_title"),
                Heading = string.Format(T("ft_conflict_heading"), name, where),
                Message = T("ft_conflict_question"),
                Kind = FtDialogKind.Question,
                Extra = new FtConflictCards { ExistingSize = FileTransferManager.Fmt(info.TargetSize), ExistingDate = Date(info.TargetModifiedUtc), NewSize = FileTransferManager.Fmt(info.SourceSize), NewDate = Date(info.SourceModifiedUtc), NewIsNewer = newer, NewTag = tag },
                CheckText = T("ft_conflict_all"),
                Buttons =
                {
                    new FtButton { Label = T("ft_conflict_cancel_job"), Tag = ConflictAction.CancelJob },
                    new FtButton { Label = T("ft_conflict_skip"), Tag = ConflictAction.Skip },
                    new FtButton { Label = T("ft_conflict_both"), Tag = ConflictAction.KeepBoth },
                    new FtButton { Label = T("ft_conflict_replace"), Tag = ConflictAction.Overwrite, Primary = true },
                },
            };
            Show(m);
            return new ConflictDecision(m.Result?.Tag as ConflictAction? ?? ConflictAction.CancelJob, m.IsChecked);
        }

        /// <summary>conferma di cancellazione sul PC remoto (e' definitiva: non c'e' cestino)</summary>
        public static (bool confirmed, bool dontAskAgain) ConfirmRemoteDelete(IReadOnlyList<RemoteEntry> items)
        {
            var folders = items.Count(i => i.IsDirectory);
            var m = new FtDialogModel
            {
                WindowTitle = T("ft_delete_title"),
                Heading = items.Count == 1 ? string.Format(T("ft_delete_one"), items[0].Name) : string.Format(T("ft_delete_many"), items.Count),
                Message = T("ft_delete_warning") + (folders > 0 ? "\n" + T("ft_delete_folders") : ""),
                Kind = FtDialogKind.Error,
                CheckText = T("ft_delete_dont_ask"),
                Buttons = { new FtButton { Label = T("ft_cancel") }, new FtButton { Label = T("ft_delete"), Danger = true, Tag = true } },
            };
            Show(m);
            return (m.Result?.Tag is true, m.IsChecked);
        }

        /// <summary>chiede un nome (nuova cartella, rinomina); le regole di Windows sono spiegate sotto il campo se il nome non va</summary>
        public static string? Prompt(string windowTitle, string heading, string initial, string okLabel)
        {
            var m = new FtDialogModel
            {
                WindowTitle = windowTitle, Heading = heading, Kind = FtDialogKind.Question, HasInput = true, InputText = initial,
                Validate = s => RemotePath.IsValidWindowsName(s.Trim()) ? null : T("ft_invalid_name_rule"),
                Buttons = { new FtButton { Label = T("ft_cancel") }, new FtButton { Label = okLabel, Primary = true, Tag = true } },
            };
            Show(m);
            return m.Result?.Tag is true ? m.InputText.Trim() : null;
        }

        /// <summary>sceglie una cartella del PC remoto; restituisce percorso (formato server) e "ricorda per questo server", o null</summary>
        public static (string path, bool remember)? PickRemoteFolder(FileTransferManager manager, string startWire, string host)
        {
            var vm = new RemoteFolderPickerViewModel(manager, startWire, host);
            var w = new RemoteFolderPickerWindow(vm);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
            if (owner != null) w.Owner = owner;
            w.ShowDialog();
            return vm.Chosen == null ? null : (vm.Chosen, vm.Remember);
        }
    }
}
