using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Lemora.Service;
using Lemora.Service.FileTransfer;
using Lemora.View.Host;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.Controls;

namespace Lemora.View.FileTransfer
{
    public enum FtAvailability { Unknown, Checking, Available, Unavailable }
    public enum FtMessageKind { Info, Accent, Error }
    public enum FtRowState { Queued, Scanning, Running, Waiting, Done, Warn, Failed, Cancelled }

    /// <summary>plain-language texts for file transfer errors</summary>
    public static class FtText
    {
        public static string T(string key) => IoC.Translate(key);

        public static string Describe(FileTransferException? e)
        {
            if (e == null) return "";
            switch (e.Kind)
            {
                case FtErrorKind.NotSupported: return e.Message == FtText.T("ft_err_ft_disabled") ? T("ft_unavail_disabled_title") : T("ft_unavail_nottight_title");
                case FtErrorKind.AuthFailed: return T("ft_err_auth");
                case FtErrorKind.Timeout:
                case FtErrorKind.Disconnected: return T("ft_err_timeout");
                case FtErrorKind.Denied: return T("ft_err_denied");
                case FtErrorKind.NotFound: return T("ft_err_notfound");
                case FtErrorKind.InUse: return T("ft_err_inuse");
                case FtErrorKind.Exists: return T("ft_err_exists");
                case FtErrorKind.NotEmpty: return T("ft_err_notempty");
                case FtErrorKind.DiskFull: return T("ft_err_diskfull");
                case FtErrorKind.InvalidName: return T("ft_err_invalidname");
                default: return e.Message;
            }
        }

        public static string Duration(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";
    }

    /// <summary>a row of the transfer queue</summary>
    public class TransferJobViewModel : NotifyPropertyChangedBase
    {
        public TransferJob Job { get; }
        private readonly FileTransferViewModel _owner;
        /// <summary>requeues the same transfer (for "Retry")</summary>
        public Func<TransferJob>? Retry { get; set; }
        public bool Notified { get; set; }
        /// <summary>transfer made to copy files to the clipboard: raises no notifications</summary>
        public bool IsStaging { get; set; }

        public TransferJobViewModel(TransferJob job, FileTransferViewModel owner)
        {
            Job = job; _owner = owner;
            CmdCancel = new RelayCommand(_ => Job.Cancel());
            CmdRetry = new RelayCommand(_ => { var j = Retry?.Invoke(); if (j != null) _owner.Remove(this); });
            CmdRemove = new RelayCommand(_ => _owner.Remove(this));
            CmdDetails = new RelayCommand(_ => ShowDetails = !ShowDetails);
            CmdShowDestination = new RelayCommand(_ => ShowDestination());
        }

        public bool IsUpload => Job.Direction == TransferDirection.Upload;
        public string Title => Job.Title;
        public string SubTitle => IsUpload
            ? string.Format(FtText.T("ft_row_to_remote"), RemotePath.NameOf(Job.TargetDir) is { Length: > 0 } n ? n : Job.TargetDir)
            : string.Format(FtText.T("ft_row_from_remote"), Job.Hint);

        public FtRowState Row => Job.State switch
        {
            TransferState.Queued => FtRowState.Queued,
            TransferState.Scanning => FtRowState.Scanning,
            TransferState.Running => Job.WaitingForDecision ? FtRowState.Waiting : FtRowState.Running,
            TransferState.Completed => FtRowState.Done,
            TransferState.CompletedWithSkips => FtRowState.Warn,
            TransferState.Failed => FtRowState.Failed,
            _ => FtRowState.Cancelled,
        };

        public double ProgressValue => Row is FtRowState.Done or FtRowState.Warn or FtRowState.Scanning ? 1 : Job.Progress;
        public bool ShowBar => Row is not (FtRowState.Queued or FtRowState.Cancelled);
        public bool IsIndeterminate => Row == FtRowState.Scanning;

        public string StatusText
        {
            get
            {
                switch (Row)
                {
                    case FtRowState.Queued: return FtText.T("ft_st_queued");
                    case FtRowState.Scanning: return FtText.T("ft_st_scanning");
                    case FtRowState.Waiting: return FtText.T("ft_st_waiting");
                    case FtRowState.Running:
                        var s = $"{FileTransferManager.Fmt(Job.DoneBytes)} / {FileTransferManager.Fmt(Job.TotalBytes)}";
                        if (Job.BytesPerSecond > 1) s += $" · {FileTransferManager.Fmt((long)Job.BytesPerSecond)}/s";
                        if (Job.Remaining is { } r) s += " · " + FtText.Duration(r);
                        if (Job.TotalFiles > 1) s += " · " + string.Format(FtText.T("ft_st_file_n_of"), Math.Min(Job.DoneFiles + 1, Job.TotalFiles), Job.TotalFiles);
                        return s;
                    case FtRowState.Done:
                        return string.Format(FtText.T("ft_st_done"), FtText.Duration((Job.FinishedAt ?? DateTime.Now) - Job.CreatedAt));
                    case FtRowState.Warn:
                        var ok = Job.Items.Count(i => i.Status == "ok" && !i.IsDirectory);
                        var total = Job.TotalFiles + Job.Items.Count(i => i.Status.StartsWith(TransferItem.SkippedMark) && i.Size == 0 && !i.IsDirectory && string.IsNullOrEmpty(i.Target));
                        var skipped = Job.Items.Count(i => i.IsSkippedOrError);
                        // no file copied: say why right away (before: "0 of 1 copied" with no reason)
                        if (ok == 0 && Job.Items.FirstOrDefault(i => i.IsSkippedOrError) is { } first)
                            return string.Format(FtText.T("ft_st_none_copied"), first.Status[(first.Status.IndexOf(':') + 1)..].Trim());
                        return string.Format(FtText.T("ft_st_warn"), ok, ok + skipped, skipped);
                    case FtRowState.Failed: return FtText.Describe(Job.Error);
                    default: return FtText.T("ft_st_cancelled");
                }
            }
        }

        public bool CanCancel => Row is FtRowState.Queued or FtRowState.Scanning or FtRowState.Running or FtRowState.Waiting;
        public bool CanRetry => Row is FtRowState.Failed or FtRowState.Cancelled && Retry != null;
        public bool CanRemove => Job.IsFinished;
        public bool CanDetails => Row == FtRowState.Warn || (Row == FtRowState.Failed && Job.Items.Count > 1);
        public bool CanShowDestination => Row is FtRowState.Done or FtRowState.Warn;

        private bool _showDetails;
        public bool ShowDetails { get => _showDetails; set => SetAndNotifyIfChanged(ref _showDetails, value); }
        public IEnumerable<string> DetailLines => Job.Items.Where(i => i.Status != "ok").Select(i => $"{System.IO.Path.GetFileName(i.Source.TrimEnd('\\', '/'))}: {ShownStatus(i.Status)}").Take(200).ToList();
        // the status markers are internal: shown in the user's language
        private static string ShownStatus(string s) =>
            s.StartsWith(TransferItem.SkippedMark) ? FtText.T("ft_mark_skipped") + ": " + s[TransferItem.SkippedMark.Length..]
            : s.StartsWith(TransferItem.ErrorMark) ? FtText.T("ft_mark_error") + ": " + s[TransferItem.ErrorMark.Length..]
            : s;

        public RelayCommand CmdCancel { get; }
        public RelayCommand CmdRetry { get; }
        public RelayCommand CmdRemove { get; }
        public RelayCommand CmdDetails { get; }
        public RelayCommand CmdShowDestination { get; }

        private void ShowDestination()
        {
            try
            {
                if (IsUpload) { _owner.OpenNavigator(Job.TargetDir); return; }
                var first = Job.ResultPaths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
                if (first != null) Process.Start("explorer.exe", $"/select,\"{first}\"");
                else if (Directory.Exists(Job.TargetDir)) Process.Start("explorer.exe", $"\"{Job.TargetDir}\"");
            }
            catch (Exception e) { SimpleLogHelper.Warning("FILE-TRANSFER show destination: " + e.Message); }
        }

        public void Refresh()
        {
            RaisePropertyChanged(nameof(Row)); RaisePropertyChanged(nameof(ProgressValue)); RaisePropertyChanged(nameof(ShowBar)); RaisePropertyChanged(nameof(IsIndeterminate));
            RaisePropertyChanged(nameof(StatusText)); RaisePropertyChanged(nameof(CanCancel)); RaisePropertyChanged(nameof(CanRetry)); RaisePropertyChanged(nameof(CanRemove));
            RaisePropertyChanged(nameof(CanDetails)); RaisePropertyChanged(nameof(CanShowDestination)); RaisePropertyChanged(nameof(Title));
            if (ShowDetails) RaisePropertyChanged(nameof(DetailLines));
        }
    }

    /// <summary>
    /// File transfer of a VNC session as seen by the UI: availability, default destination, queue, popup, navigator,
    /// "paste" and "copy from remote" bars, messages in the session bar. One instance per session (<see cref="VncHost.FileTransferVm"/>).
    /// </summary>
    public class FileTransferViewModel : NotifyPropertyChangedBase, IDisposable
    {
        private readonly VncHost _host;
        private readonly DispatcherTimer _timer;
        private FileTransferManager? _attached;
        private DateTime _messageUntil;

        public ObservableCollection<TransferJobViewModel> Jobs { get; } = new();

        public FileTransferViewModel(VncHost host)
        {
            _host = host;
            var cfg = IoC.Get<ConfigurationService>().FileTransfer;
            if (RememberedDir() is { } d) _remoteDest = d;
            else if (!string.IsNullOrWhiteSpace(cfg.DefaultRemoteDir)) _remoteDest = RemotePath.FromWindows(cfg.DefaultRemoteDir.Trim()); // folder chosen in the settings, valid until another one is chosen for this PC

            // clicking the button while the popup is open already closes it (click outside the popup): without this check it would reopen it right away
            CmdTogglePopup = new RelayCommand(_ => { if (!IsPopupOpen && (DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 300) return; IsPopupOpen = !IsPopupOpen; });
            CmdClosePopup = new RelayCommand(_ => IsPopupOpen = false);
            CmdOpenNavigator = new RelayCommand(_ => OpenNavigator(null));
            CmdCloseNavigator = new RelayCommand(_ => IsNavigatorOpen = false);
            CmdChooseFiles = new RelayCommand(_ => ChooseFiles());
            CmdChangeDestination = new RelayCommand(async _ => await ChooseDestinationAsync());
            CmdClearFinished = new RelayCommand(_ => { foreach (var j in Jobs.Where(x => x.CanRemove).ToList()) Remove(j); });
            CmdCancelAll = new RelayCommand(_ => { foreach (var j in Jobs.Where(x => x.CanCancel).ToList()) j.Job.Cancel(); });
            CmdPasteConfirm = new RelayCommand(async _ => await ConfirmPasteAsync());
            CmdPasteChangeFolder = new RelayCommand(async _ => { if (await ChooseDestinationAsync()) RaisePropertyChanged(nameof(PasteText)); });
            CmdPasteCancel = new RelayCommand(_ => { _pendingPaste = null; IsPasteBarOpen = false; });
            CmdPrepareCancel = new RelayCommand(_ => { _prepJob?.Cancel(); ClosePrepareBar(); });
            CmdPrepareClean = new RelayCommand(_ => { FileTransferShell.CleanStaging(); ClosePrepareBar(); });
            CmdUnavailableAction = new RelayCommand(async _ => await UnavailableActionAsync());

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        public void Dispose() { _timer.Stop(); if (_attached != null) _attached.JobAdded -= OnJobAdded; }

        public FileTransferManager? Manager => _host.FileTransfer;
        public string ServerKey => _host.ConnectionId;
        public string HostName => _host.ProtocolServer.DisplayName;

        #region availability

        private FtAvailability _availability = FtAvailability.Unknown;
        public FtAvailability Availability { get => _availability; private set { if (SetAndNotifyIfChanged(ref _availability, value)) { RaisePropertyChanged(nameof(IsUnavailable)); RaisePropertyChanged(nameof(IsChecking)); RaisePropertyChanged(nameof(IsAvailableOrUnknown)); RaisePropertyChanged(nameof(ButtonTooltip)); } } }
        public bool IsUnavailable => Availability == FtAvailability.Unavailable;
        public bool IsChecking => Availability == FtAvailability.Checking;
        public bool IsAvailableOrUnknown => Availability != FtAvailability.Unavailable;
        private string _unavailTitle = "", _unavailText = "", _unavailAction = "";
        public string UnavailableTitle { get => _unavailTitle; private set => SetAndNotifyIfChanged(ref _unavailTitle, value); }
        public string UnavailableText { get => _unavailText; private set => SetAndNotifyIfChanged(ref _unavailText, value); }
        public string UnavailableAction { get => _unavailAction; private set => SetAndNotifyIfChanged(ref _unavailAction, value); }
        private FtUnavail _unavailKind = FtUnavail.None;
        private enum FtUnavail { None, NotTight, Disabled, Auth, NoResponse }
        public string ButtonTooltip => IsUnavailable ? UnavailableTitle : IsChecking ? FtText.T("ft_btn_checking") : HasActive ? FtText.T("ft_btn_active") : FtText.T("ft_btn_tooltip");

        /// <summary>opens the probe connection (if needed) and tells whether file transfer can be used; otherwise prepares the texts explaining why</summary>
        public async Task<bool> EnsureAvailableAsync(bool force = false)
        {
            if (Availability == FtAvailability.Available && !force) return true;
            if (Availability == FtAvailability.Checking) return false;
            var m = Manager;
            if (m == null) { SetUnavailable(FtUnavail.NoResponse); return false; }
            Availability = FtAvailability.Checking;
            FileTransferException? err;
            try { err = await m.ProbeAsync(); }
            catch (Exception e) { err = new FileTransferException(FtErrorKind.Unknown, e.Message, null, e); }
            if (err == null) { Availability = FtAvailability.Available; return true; }
            SimpleLogHelper.Warning("FILE-TRANSFER not available: " + err.Message);
            SetUnavailable(err.Kind switch
            {
                FtErrorKind.NotSupported => err.Message == FtText.T("ft_err_ft_disabled") ? FtUnavail.Disabled : FtUnavail.NotTight,
                FtErrorKind.AuthFailed => FtUnavail.Auth,
                _ => FtUnavail.NoResponse,
            });
            return false;
        }

        private void SetUnavailable(FtUnavail kind)
        {
            _unavailKind = kind;
            var k = kind switch { FtUnavail.NotTight => "nottight", FtUnavail.Disabled => "disabled", FtUnavail.Auth => "auth", _ => "noresponse" };
            UnavailableTitle = FtText.T($"ft_unavail_{k}_title");
            UnavailableText = FtText.T($"ft_unavail_{k}_text");
            UnavailableAction = kind == FtUnavail.NotTight ? "" : FtText.T($"ft_unavail_{k}_action");
            Availability = FtAvailability.Unavailable;
            RaisePropertyChanged(nameof(HasUnavailableAction));
            RaisePropertyChanged(nameof(ButtonTooltip));
            ShowMessage(UnavailableTitle, FtMessageKind.Error);
        }

        public bool HasUnavailableAction => !string.IsNullOrEmpty(UnavailableAction);
        public RelayCommand CmdUnavailableAction { get; }
        private async Task UnavailableActionAsync()
        {
            switch (_unavailKind)
            {
                case FtUnavail.Disabled: FtDialogs.Info(FtText.T("ft_unavail_disabled_title"), FtText.T("ft_unavail_disabled_howto")); break;
                case FtUnavail.Auth: _host.ReConn(); Availability = FtAvailability.Unknown; break;
                default: await EnsureAvailableAsync(true); break;
            }
        }

        #endregion

        #region status shown in the bar and on the button

        public bool HasActive => Jobs.Any(j => j.CanCancel);
        public int ActiveCount => Jobs.Count(j => j.CanCancel);
        public double OverallProgress
        {
            get
            {
                var act = Jobs.Where(j => j.CanCancel && j.Job.TotalBytes > 0).Select(j => j.Job).ToList();
                var tot = act.Sum(j => j.TotalBytes);
                return tot > 0 ? Math.Min(1.0, (double)act.Sum(j => j.DoneBytes) / tot) : 0;
            }
        }
        /// <summary>progress ring around the button (32 px circle, 2 px line: circumference ~50 stroke-thickness units)</summary>
        public DoubleCollection RingDash => new DoubleCollection { Math.Max(0.01, OverallProgress * 50.3), 100 };
        public bool ShowRing => HasActive || IsChecking;
        public bool ShowBadge => ActiveCount > 0;
        public string BadgeText => ActiveCount.ToString();
        /// <summary>a progress line under the tab (0..1)</summary>
        public double TabProgress => HasActive ? OverallProgress : 0;

        private string _barMessage = "";
        public string BarMessage { get => _barMessage; private set { if (SetAndNotifyIfChanged(ref _barMessage, value)) RaisePropertyChanged(nameof(HasBarMessage)); } }
        public bool HasBarMessage => !string.IsNullOrEmpty(_barMessage);
        private FtMessageKind _barKind;
        public FtMessageKind BarKind { get => _barKind; private set => SetAndNotifyIfChanged(ref _barKind, value); }
        /// <summary>
        /// With seconds = 0 the message stays in the session bar for as long as it lasts (only while dragging); otherwise it is a toast,
        /// because a text in the bar widens it and moves the buttons.
        /// </summary>
        public void ShowMessage(string text, FtMessageKind kind, int seconds = 6)
        {
            if (seconds > 0) { if (kind == FtMessageKind.Error) { SetIssue(2); LastError = text; } ToastService.Show(text, kind == FtMessageKind.Error ? ToastKind.Error : ToastKind.Success); return; }
            BarMessage = text; BarKind = kind;
            _messageUntil = DateTime.MaxValue;
        }
        // warning/error icon next to the button: stays until the transfer panel is opened (1 = warning, 2 = error)
        private int _issue;
        public bool HasIssue => _issue > 0;
        public bool IssueIsError => _issue == 2;
        private void SetIssue(int level)
        {
            if (level <= _issue) return;
            _issue = level; RaisePropertyChanged(nameof(HasIssue)); RaisePropertyChanged(nameof(IssueIsError));
        }
        // the toast lasts a few seconds: the last error text stays in the panel until it is closed, so the "!" always has an explanation
        private string _lastError = "";
        public string LastError { get => _lastError; private set { if (SetAndNotifyIfChanged(ref _lastError, value)) RaisePropertyChanged(nameof(HasLastError)); } }
        public bool HasLastError => !string.IsNullOrEmpty(_lastError);
        private void ClearIssue() { if (_issue == 0) return; _issue = 0; RaisePropertyChanged(nameof(HasIssue)); RaisePropertyChanged(nameof(IssueIsError)); }

        private void ClearMessage() { BarMessage = ""; _messageUntil = DateTime.MinValue; }

        private DateTime _popupClosedAt = DateTime.MinValue;
        private bool _isPopupOpen;
        public bool IsPopupOpen
        {
            get => _isPopupOpen;
            set
            {
                if (!SetAndNotifyIfChanged(ref _isPopupOpen, value)) return;
                if (!value) { _popupClosedAt = DateTime.UtcNow; LastError = ""; } else ClearIssue();
                if (value && Availability is FtAvailability.Unknown) _ = EnsureAvailableAsync();   // popup opened: check the server
            }
        }
        public RelayCommand CmdTogglePopup { get; }
        public RelayCommand CmdClosePopup { get; }

        #endregion

        #region dragging onto the remote screen

        private bool _isDragActive, _isDragDenied;
        public bool IsDragActive { get => _isDragActive; private set => SetAndNotifyIfChanged(ref _isDragActive, value); }
        public bool IsDragDenied { get => _isDragDenied; private set => SetAndNotifyIfChanged(ref _isDragDenied, value); }

        /// <summary>files dragged over the screen (or the popup)</summary>
        public void DragEnter()
        {
            var denied = Availability == FtAvailability.Unavailable || _host.Status != ProtocolHostStatus.Connected || Manager == null;
            IsDragDenied = denied; IsDragActive = true;
            ShowMessage(denied ? string.Format(FtText.T("ft_drop_denied"), IsUnavailable ? UnavailableTitle : FtText.T("ft_err_not_connected")) : string.Format(FtText.T("ft_drop_hint"), DestinationName), denied ? FtMessageKind.Error : FtMessageKind.Accent, 0);
        }

        public void DragLeave() { IsDragActive = false; IsDragDenied = false; ClearMessage(); }

        /// <summary>files dropped on the screen or the popup: they are uploaded to the default destination</summary>
        public async Task DropAsync(IReadOnlyList<string> paths)
        {
            DragLeave();
            if (paths.Count == 0) return;
            await UploadAsync(paths);
        }

        #endregion

        #region destination

        private string _remoteDest = "";
        public string DestinationWire => _remoteDest;
        public string DestinationName => string.IsNullOrEmpty(_remoteDest) ? FtText.T("ft_dest_choose") : RemotePath.NameOf(_remoteDest);
        public string DestinationPathText => string.IsNullOrEmpty(_remoteDest) ? "" : RemotePath.ToWindows(_remoteDest);
        public RelayCommand CmdChangeDestination { get; }

        public void SetDestination(string wire, bool remember)
        {
            _remoteDest = wire;
            RaisePropertyChanged(nameof(DestinationName)); RaisePropertyChanged(nameof(DestinationPathText)); RaisePropertyChanged(nameof(DestinationWire)); RaisePropertyChanged(nameof(PasteText));
            if (!remember) return;
            Lemora.Service.Locality.AppDbStore.Set(DirKey, Newtonsoft.Json.JsonConvert.SerializeObject(wire));
        }

        // remote folder remembered per entry, in the encrypted database and keyed by the entry id (before: plaintext profile with address and port). Security hardening.
        private string DirKey => "ft.remotedir." + _host.ProtocolServer.Id;

        private string? RememberedDir()
        {
            try
            {
                if (Lemora.Service.Locality.AppDbStore.TryGet(DirKey, out var j) && !string.IsNullOrEmpty(j))
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<string>(j!);
            }
            catch { /* corrupted value: fall back to the default folder */ }
            return null;
        }

        public async Task<bool> EnsureDestinationAsync()
        {
            if (!string.IsNullOrEmpty(_remoteDest)) return true;
            var guess = await GuessDestinationAsync();
            if (guess != null) { SetDestination(guess, true); return true; }
            return await ChooseDestinationAsync();
        }

        /// <summary>if the remote PC has only one real user, the destination is their Desktop (the program does not know the logged-in user's name)</summary>
        private async Task<string?> GuessDestinationAsync()
        {
            try
            {
                var m = Manager; if (m == null) return null;
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Public", "Default", "Default User", "All Users", "defaultuser0", "WDAGUtilityAccount", "Tutti gli utenti" };   // folder names on the remote PC, also the Italian Windows name of "All Users"
                var users = (await m.ListAsync("/C:/Users")).Where(e => e.IsDirectory && !skip.Contains(e.Name) && !e.Name.StartsWith("$")).ToList();
                if (users.Count != 1) return null;
                var desk = "/C:/Users/" + users[0].Name + "/Desktop";
                await m.ListAsync(desk);
                return desk;
            }
            catch (Exception e) { SimpleLogHelper.Debug("FILE-TRANSFER: destination not guessed: " + e.Message); return null; }
        }

        public async Task<bool> ChooseDestinationAsync()
        {
            var m = Manager; if (m == null) return false;
            if (!await EnsureAvailableAsync()) return false;
            var r = FtDialogs.PickRemoteFolder(m, string.IsNullOrEmpty(_remoteDest) ? "/C:/Users" : _remoteDest, _host.ProtocolServer.DisplayName);
            if (r == null) return false;
            SetDestination(r.Value.path, r.Value.remember);
            return true;
        }

        #endregion

        #region upload

        public async Task UploadAsync(IReadOnlyList<string> paths, string? destinationWire = null)
        {
            if (!await EnsureAvailableAsync()) { IsPopupOpen = true; return; }
            var dest = destinationWire;
            if (dest == null) { if (!await EnsureDestinationAsync()) return; dest = _remoteDest; }
            EnqueueUpload(paths, dest);
        }

        public TransferJob? EnqueueUpload(IReadOnlyList<string> paths, string dest, bool silent = false)
        {
            var m = Manager; if (m == null) return null;
            ApplyOptions(m);
            TransferJob job;
            try { job = m.EnqueueUpload(paths, dest); }
            catch (ArgumentException e) { ShowMessage(e.Message, FtMessageKind.Error); return null; }
            var vm = Attach(job);
            if (vm != null) vm.Retry = () => EnqueueUpload(paths, dest, true)!;
            if (!silent) ShowMessage(string.Format(FtText.T("ft_upload_started"), RemotePath.NameOf(dest)), FtMessageKind.Info);
            return job;
        }

        public TransferJob? EnqueueDownload(IReadOnlyList<RemoteEntry> items, string localDir, string hint)
        {
            var m = Manager; if (m == null) return null;
            ApplyOptions(m);
            var job = m.EnqueueDownload(items, localDir);
            job.Hint = hint;
            var vm = Attach(job);
            if (vm != null) vm.Retry = () => EnqueueDownload(items, localDir, hint)!;
            return job;
        }

        /// <summary>limits and rules are read from the settings on every transfer (they change without reopening the session)</summary>
        private static void ApplyOptions(FileTransferManager m)
        {
            var cfg = IoC.Get<ConfigurationService>().FileTransfer;
            m.Options.MaxFileBytes = cfg.MaxFileSizeMB * 1024L * 1024L;
            m.Options.MaxJobBytes = cfg.MaxJobSizeMB * 1024L * 1024L;
            m.Options.Conflict = (ConflictPolicy)cfg.ConflictPolicy;
            m.Options.UseTemporaryNames = cfg.UseTemporaryNames;
        }

        public RelayCommand CmdChooseFiles { get; }
        private async void ChooseFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = FtText.T("ft_choose_files_title") };
            if (dlg.ShowDialog() == true) await UploadAsync(dlg.FileNames);
        }

        #endregion

        #region queue

        public RelayCommand CmdClearFinished { get; }
        public RelayCommand CmdCancelAll { get; }
        /// <summary>a transfer finished (on the UI thread): the navigator refreshes its list</summary>
        public event Action<TransferJob>? JobFinished;

        private TransferJobViewModel? Attach(TransferJob job)
        {
            var m = Manager;
            if (m != null && !ReferenceEquals(m, _attached))
            {
                if (_attached != null) _attached.JobAdded -= OnJobAdded;
                _attached = m;
                m.ConflictResolver = ResolveConflictAsync;
            }
            return Jobs.FirstOrDefault(j => j.Job == job) ?? AddJob(job);
        }

        private void OnJobAdded(TransferJob job) { }

        private TransferJobViewModel AddJob(TransferJob job)
        {
            var vm = new TransferJobViewModel(job, this);
            Jobs.Insert(0, vm);
            RaisePropertyChanged(nameof(HasJobs));
            return vm;
        }

        public bool HasJobs => Jobs.Count > 0;
        public bool HasFinished => Jobs.Any(j => j.CanRemove);

        public void Remove(TransferJobViewModel vm)
        {
            if (!vm.CanRemove) return;
            Manager?.RemoveJob(vm.Job);
            Jobs.Remove(vm);
            RaisePropertyChanged(nameof(HasJobs));
        }

        /// <summary>the engine asks what to do with a file that already exists (from the transfer thread): the choice is made on the UI thread</summary>
        private Task<ConflictDecision> ResolveConflictAsync(ConflictInfo info)
        {
            var tcs = new TaskCompletionSource<ConflictDecision>();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try { tcs.SetResult(FtDialogs.Conflict(info, _host.ProtocolServer.DisplayName)); }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }

        private void Tick()
        {
            foreach (var j in Jobs.ToList())
            {
                j.Refresh();
                if (j.Job.IsFinished && !j.Notified) { j.Notified = true; Notify(j); JobFinished?.Invoke(j.Job); }
            }
            RaisePropertyChanged(nameof(HasActive)); RaisePropertyChanged(nameof(ActiveCount)); RaisePropertyChanged(nameof(OverallProgress)); RaisePropertyChanged(nameof(RingDash));
            RaisePropertyChanged(nameof(ShowRing)); RaisePropertyChanged(nameof(ShowBadge)); RaisePropertyChanged(nameof(BadgeText)); RaisePropertyChanged(nameof(TabProgress)); RaisePropertyChanged(nameof(HasFinished));
            RaisePropertyChanged(nameof(ButtonTooltip));
            ReportTaskbar();
            if (HasBarMessage && DateTime.Now > _messageUntil && !IsDragActive) ClearMessage();
            UpdatePrepare();
        }

        // progress on the Windows taskbar icon: average of the active transfers of all sessions
        private static readonly Dictionary<FileTransferViewModel, double> TaskbarActive = new();
        private void ReportTaskbar()
        {
            var wasActive = TaskbarActive.ContainsKey(this);
            if (HasActive) TaskbarActive[this] = OverallProgress; else if (!TaskbarActive.Remove(this) && !wasActive) return;
            try
            {
                if (IoC.Get<MainWindowViewModel>().View is not Window w) return;
                var info = w.TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
                if (TaskbarActive.Count == 0) { info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None; return; }
                info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
                info.ProgressValue = Math.Clamp(TaskbarActive.Values.Average(), 0.01, 1);
            }
            catch { /* window closing */ }
        }

        private void Notify(TransferJobViewModel j)
        {
            if (j.IsStaging || j.Row == FtRowState.Cancelled) return;
            var kind = j.Row switch { FtRowState.Done => ToastKind.Success, FtRowState.Warn => ToastKind.Warning, _ => ToastKind.Error };
            if (kind != ToastKind.Success) SetIssue(kind == ToastKind.Error ? 2 : 1);
            ToastService.Show($"{j.Title}: {j.StatusText}", kind);
            try { (IoC.Get<MainWindowViewModel>().View as Window)?.FlashIfNotActive(3, 400); } catch { }
        }

        #endregion

        #region full-page browser

        private bool _isNavigatorOpen;
        public bool IsNavigatorOpen
        {
            get => _isNavigatorOpen;
            set { if (SetAndNotifyIfChanged(ref _isNavigatorOpen, value)) { if (value) IsPopupOpen = false; NavigatorOpenChanged?.Invoke(value); } }
        }
        public event Action<bool>? NavigatorOpenChanged;
        public RelayCommand CmdOpenNavigator { get; }
        public RelayCommand CmdCloseNavigator { get; }
        private FileNavigatorViewModel? _navigator;
        public FileNavigatorViewModel Navigator => _navigator ??= new FileNavigatorViewModel(this);

        /// <summary>opens the navigator; with a remote path (server format) the remote column starts there</summary>
        public void OpenNavigator(string? remoteWire)
        {
            IsNavigatorOpen = true;
            _ = Navigator.StartAsync(remoteWire);
        }

        #endregion

        #region paste files (Ctrl+Shift+V)

        private List<string>? _pendingPaste;
        private bool _isPasteBarOpen;
        public bool IsPasteBarOpen { get => _isPasteBarOpen; private set => SetAndNotifyIfChanged(ref _isPasteBarOpen, value); }
        private string _pasteSummary = "";
        public string PasteText => string.Format(FtText.T("ft_paste_question"), _pasteSummary, DestinationName);
        public string PasteSummary => _pasteSummary;
        public RelayCommand CmdPasteConfirm { get; }
        public RelayCommand CmdPasteChangeFolder { get; }
        public RelayCommand CmdPasteCancel { get; }

        /// <summary>the clipboard holds files: show the confirmation bar (never a silent upload)</summary>
        public bool RequestPaste()
        {
            var paths = FileTransferShell.GetLocalPathsFromClipboard();
            if (paths.Count == 0) return false;
            _pendingPaste = paths;
            _pasteSummary = string.Format(FtText.T("ft_n_items"), paths.Count);
            RaisePropertyChanged(nameof(PasteText));
            IsPasteBarOpen = true;
            _ = EnsureAvailableAsync();
            // sizes (even of big folders) are computed separately, the bar appears right away
            _ = Task.Run(() => Summarize(paths)).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && ReferenceEquals(_pendingPaste, paths))
                    Application.Current?.Dispatcher.BeginInvoke(() => { _pasteSummary = t.Result; RaisePropertyChanged(nameof(PasteText)); });
            });
            return true;
        }

        private static string Summarize(List<string> paths)
        {
            long size = 0; var files = 0; var dirs = 0;
            foreach (var p in paths)
            {
                try
                {
                    if (File.Exists(p)) { files++; size += new FileInfo(p).Length; }
                    else if (Directory.Exists(p)) { dirs++; foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(20000)) size += f.Length; }
                }
                catch { /* unreadable folder: count what can be seen */ }
            }
            var n = paths.Count;
            var what = dirs == 0 ? string.Format(FtText.T("ft_n_files"), n) : files == 0 ? string.Format(FtText.T("ft_n_folders"), n) : string.Format(FtText.T("ft_n_items"), n);
            return $"{what} ({FileTransferManager.Fmt(size)})";
        }

        private async Task ConfirmPasteAsync()
        {
            var paths = _pendingPaste; _pendingPaste = null; IsPasteBarOpen = false;
            if (paths != null) await UploadAsync(paths);
        }

        #endregion

        #region copy from the remote to File Explorer

        private TransferJob? _prepJob;
        private bool _isPrepareBarOpen;
        public bool IsPrepareBarOpen { get => _isPrepareBarOpen; private set => SetAndNotifyIfChanged(ref _isPrepareBarOpen, value); }
        private string _prepareText = "";
        public string PrepareText { get => _prepareText; private set => SetAndNotifyIfChanged(ref _prepareText, value); }
        private double _prepareProgress;
        public double PrepareProgress { get => _prepareProgress; private set => SetAndNotifyIfChanged(ref _prepareProgress, value); }
        private bool _prepareDone;
        public bool PrepareDone { get => _prepareDone; private set { if (SetAndNotifyIfChanged(ref _prepareDone, value)) RaisePropertyChanged(nameof(PrepareInProgress)); } }
        public bool PrepareInProgress => !PrepareDone;
        public RelayCommand CmdPrepareCancel { get; }
        public RelayCommand CmdPrepareClean { get; }

        /// <summary>"Copy" on the remote selection: downloads to a temporary folder and then puts the files on the Windows clipboard</summary>
        public async Task CopyRemoteToClipboardAsync(IReadOnlyList<RemoteEntry> items)
        {
            if (items.Count == 0) return;
            var staging = FileTransferShell.NewStagingDir();
            var job = EnqueueDownload(items, staging, items[0].Dir is { Length: > 0 } d ? RemotePath.NameOf(d) is { Length: > 0 } n ? n : d : "");
            if (job == null) return;
            var vm = Jobs.FirstOrDefault(j => j.Job == job); if (vm != null) vm.IsStaging = true;
            _prepJob = job; PrepareDone = false; PrepareProgress = 0; PrepareText = FtText.T("ft_prepare_start"); IsPrepareBarOpen = true;
            ShowMessage(PrepareText, FtMessageKind.Info, 4); // progress stays in the file panel queue
        }

        /// <summary>
        /// "Copy files" button: the files selected on the remote PC (File Explorer or Desktop) land in this PC's clipboard, then Ctrl+V pastes them.
        /// The remote PC receives Ctrl+Shift+C ("Copy as path"): the paths come back as text (the VNC clipboard only carries text), then the files are downloaded.
        /// </summary>
        public async Task CopyFromRemoteAsync()
        {
            if (!await EnsureAvailableAsync()) { IsPopupOpen = true; return; }
            var m = Manager; if (m == null) return;
            var text = await _host.CaptureServerClipboardAsync(() => _host.SendChord(new uint[] { 0xFFE3 /*Ctrl_L*/, 0xFFE1 /*Shift_L*/ }, 0x43 /*uppercase C: with Shift held the server expects the uppercase keysym, with 'c' it would do a plain Ctrl+C*/), 3000);
            Lemora.Utils.Diag.Log($"FILE-TRANSFER copy files: text received from the remote PC = {(text == null ? "NONE (timed out)" : $"{text.Length} characters: '{(text.Length > 300 ? text[..300] : text)}'")}");
            var wirePaths = new List<string>();
            foreach (var line in (text ?? "").Split('\n', '\r'))
            {
                var p = line.Trim().Trim('"');
                if (p.Length > 2 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) wirePaths.Add(RemotePath.FromWindows(p));
            }
            Lemora.Utils.Diag.Log($"FILE-TRANSFER copy files: paths recognized = {wirePaths.Count}");
            if (wirePaths.Count == 0)
            {
                // no reply: usually the remote PC runs Windows 10, where File Explorer has no Ctrl+Shift+C ("Copy as path")
                SimpleLogHelper.Warning($"FILE-TRANSFER copy files: no path from the remote PC ({(text == null ? "no reply in 3 s" : $"text of {text.Length} characters without paths")})");
                ShowMessage(FtText.T(text == null ? "ft_copy_no_reply" : "ft_copy_none"), FtMessageKind.Error);
                return;
            }
            var entries = new List<RemoteEntry>();
            try
            {
                foreach (var g in wirePaths.GroupBy(RemotePath.Parent))
                {
                    var list = await m.ListAsync(g.Key);
                    Lemora.Utils.Diag.Log($"FILE-TRANSFER copy files: folder {g.Key} has {list.Count} entries; looking for:{string.Join(", ", g.Select(RemotePath.NameOf))}");
                    foreach (var w in g) if (list.FirstOrDefault(e => string.Equals(e.Name, RemotePath.NameOf(w), StringComparison.OrdinalIgnoreCase)) is { } hit) entries.Add(hit);
                }
            }
            catch (FileTransferException e) { SimpleLogHelper.Warning("FILE-TRANSFER copy files: listing failed: " + e.Message); ShowMessage(FtText.Describe(e), FtMessageKind.Error); return; }
            Lemora.Utils.Diag.Log($"FILE-TRANSFER copy files: entries found = {entries.Count}");
            if (entries.Count == 0) { SimpleLogHelper.Warning($"FILE-TRANSFER copy files: {wirePaths.Count} paths received but no file found in the listing"); ShowMessage(FtText.T("ft_copy_none"), FtMessageKind.Error); return; }
            Lemora.Utils.Diag.Log($"FILE-TRANSFER copy files from remote:{string.Join(" | ", entries.Select(e => e.WindowsPath))}");
            await CopyRemoteToClipboardAsync(entries);
        }

        public RelayCommand CmdCopyFromRemote => new RelayCommand(async _ => await CopyFromRemoteAsync());

        private List<string>? _staged;

        private void UpdatePrepare()
        {
            // files put on the clipboard as "cut": once File Explorer has moved them (Ctrl+V succeeded) the temporary folder is emptied automatically
            if (_staged != null && _staged.All(p => !File.Exists(p) && !Directory.Exists(p))) { _staged = null; Task.Run(FileTransferShell.CleanStaging); }
            var j = _prepJob; if (j == null || !IsPrepareBarOpen) return;
            if (!j.IsFinished) { PrepareProgress = j.Progress; PrepareText = string.Format(FtText.T("ft_prepare_progress"), (int)(j.Progress * 100)); return; }
            _prepJob = null;
            // in hidden test runs the user's real clipboard is not touched
            var demo = Lemora.Utils.DevOnly.Env("LEMORA_DEMO") != null;
            if (j.State is TransferState.Completed or TransferState.CompletedWithSkips && (demo || FileTransferShell.SetClipboardFiles(j.ResultPaths, cut: true)))
            { if (!demo) _staged = j.ResultPaths.ToList(); ClosePrepareBar(); ShowMessage(string.Format(FtText.T("ft_prepare_done"), j.ResultPaths.Count), FtMessageKind.Info, 8); }
            else ClosePrepareBar();
        }
        private void ClosePrepareBar() { IsPrepareBarOpen = false; _prepJob = null; }

        #endregion
    }
}
