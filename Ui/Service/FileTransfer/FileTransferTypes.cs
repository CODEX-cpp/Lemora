using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

// File transfer in VNC sessions (TightVNC 2.x file protocol). This file: common types, no dependencies on the GUI.
namespace Lemora.Service.FileTransfer
{
    public enum FtErrorKind
    {
        Unknown,
        /// <summary>the server is not TightVNC (no Tight security type) or has file transfer disabled</summary>
        NotSupported,
        AuthFailed,
        Timeout,
        Disconnected,
        /// <summary>access denied (Windows permissions of the user logged on to the remote PC, or lock screen)</summary>
        Denied,
        NotFound,
        InUse,
        Exists,
        NotEmpty,
        DiskFull,
        InvalidName,
        Protocol,
        TooLarge,
        Cancelled,
    }

    public class FileTransferException : Exception
    {
        public FtErrorKind Kind { get; }
        /// <summary>Windows error code reported by the server (2 = not found, 5 = access denied, 32 = file in use...), if known</summary>
        public int? WinError { get; }
        public FileTransferException(FtErrorKind kind, string message, int? winError = null, Exception? inner = null) : base(message, inner) { Kind = kind; WinError = winError; }

        /// <summary>errors that may go away when retrying with a new connection</summary>
        public bool IsTransient => Kind is FtErrorKind.Timeout or FtErrorKind.Disconnected;

        public static FileTransferException FromServerText(string text)
        {
            // the server sends "Error code N" (Windows codes) or fixed phrases
            int? win = null;
            const string p = "Error code ";
            if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase) && int.TryParse(text.AsSpan(p.Length), out var n)) win = n;
            var kind = win switch
            {
                2 or 3 or 53 or 67 => FtErrorKind.NotFound,
                5 => FtErrorKind.Denied,
                32 or 33 => FtErrorKind.InUse,
                39 or 112 => FtErrorKind.DiskFull,
                80 or 183 => FtErrorKind.Exists,
                123 or 161 or 206 => FtErrorKind.InvalidName,
                145 => FtErrorKind.NotEmpty,
                _ => FtErrorKind.Unknown,
            };
            if (kind == FtErrorKind.Unknown)
            {
                if (text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)) kind = FtErrorKind.Denied;
                else if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase)) kind = FtErrorKind.Exists;
                else if (text.Contains("No active", StringComparison.OrdinalIgnoreCase)) kind = FtErrorKind.Protocol;
            }
            return new FileTransferException(kind, text, win);
        }
    }

    /// <summary>an item of the remote listing (file or folder)</summary>
    public sealed record RemoteEntry(string Dir, string Name, bool IsDirectory, long Size, DateTime ModifiedUtc)
    {
        /// <summary>full path in the server format (/C:/Users/Name/file.txt)</summary>
        public string FullPath => RemotePath.Join(Dir, Name);
        /// <summary>Windows-style path (C:\Users\Name\file.txt)</summary>
        public string WindowsPath => RemotePath.ToWindows(FullPath);
        /// <summary>true for drives in the root listing (C:, D:...)</summary>
        public bool IsDrive => Dir == "/" && IsDirectory;
    }

    /// <summary>
    /// Remote paths. On the wire the server uses "/" as the root (list of drives) and "/C:/folder/file"; the UI uses "C:\folder\file".
    /// </summary>
    public static class RemotePath
    {
        public const string Root = "/";

        public static string FromWindows(string? p)
        {
            if (string.IsNullOrEmpty(p)) return Root;
            var s = p.Replace('\\', '/');
            if (!s.StartsWith('/')) s = "/" + s;
            if (s.Length > 1 && s.EndsWith('/')) s = s.TrimEnd('/');
            return s.Length == 0 ? Root : s;
        }

        public static string ToWindows(string wire)
        {
            if (wire == Root || string.IsNullOrEmpty(wire)) return "";
            var s = wire.TrimStart('/').Replace('/', '\\');
            // "C:" alone is the current directory on the drive, not the root: C:\
            return s.Length == 2 && s[1] == ':' ? s + "\\" : s;
        }

        public static string Join(string dir, string name) => dir == Root || dir.Length == 0 ? Root + name : dir.TrimEnd('/') + "/" + name;

        public static string Parent(string wire)
        {
            if (wire == Root || wire.Length == 0) return Root;
            var i = wire.TrimEnd('/').LastIndexOf('/');
            return i <= 0 ? Root : wire[..i];
        }

        public static string NameOf(string wire)
        {
            var t = wire.TrimEnd('/');
            var i = t.LastIndexOf('/');
            return i >= 0 ? t[(i + 1)..] : t;
        }

        /// <summary>is the name valid for Windows (no forbidden characters, no reserved names)? Applies to both the local and the remote PC.</summary>
        public static bool IsValidWindowsName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ')) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var stem = name.Split('.')[0].ToUpperInvariant();
            return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
        }

        /// <summary>"name.txt" -> "name (2).txt" (for "keep both")</summary>
        public static string NumberedName(string name, int n)
        {
            var ext = Path.GetExtension(name);
            var stem = name.Substring(0, name.Length - ext.Length);
            return $"{stem} ({n}){ext}";
        }
    }

    public enum TransferDirection { Upload, Download }

    public enum TransferState { Queued, Scanning, Running, Completed, CompletedWithSkips, Failed, Cancelled }

    public enum ConflictPolicy
    {
        /// <summary>ask the user (needs ConflictResolver; without it, both are kept)</summary>
        Ask,
        Overwrite,
        Skip,
        /// <summary>the new file gets another name: "name (2).ext"</summary>
        KeepBoth,
    }

    public enum ConflictAction { Overwrite, Skip, KeepBoth, CancelJob }

    public sealed record ConflictInfo(TransferDirection Direction, string SourcePath, string TargetPath, long SourceSize, long TargetSize, DateTime SourceModifiedUtc, DateTime TargetModifiedUtc);

    public sealed record ConflictDecision(ConflictAction Action, bool ApplyToAll);

    public sealed class FileTransferOptions
    {
        /// <summary>0 = no limit</summary>
        public long MaxFileBytes { get; set; }
        /// <summary>total size of the files in a single transfer; 0 = no limit</summary>
        public long MaxJobBytes { get; set; }
        public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Ask;
        /// <summary>retries (new connection, the file restarts from scratch) on network errors</summary>
        public int Retries { get; set; } = 2;
        /// <summary>files in progress have a temporary name and are renamed when the copy ends, so an interruption does not leave half-written files with the real name</summary>
        public bool UseTemporaryNames { get; set; } = true;
        /// <summary>blocks requested ahead during download (1 = one at a time)</summary>
        public int DownloadPipelineDepth { get; set; } = 4;
    }

    /// <summary>an item of the transfer, with its outcome</summary>
    public sealed class TransferItem
    {
        public string Source { get; init; } = "";
        public string Target { get; set; } = "";
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public string Status { get; set; } = "";   // "ok", SkippedMark + reason, ErrorMark + reason: the prefixes are markers matched by the UI, the reason is translated
        public const string SkippedMark = "skipped: ";
        public const string ErrorMark = "error: ";
        public bool IsSkippedOrError => Status.StartsWith(SkippedMark) || Status.StartsWith(ErrorMark);
    }

    /// <summary>a transfer (one or more items, files and folders): the UI shows it in the list and can cancel it</summary>
    public sealed class TransferJob : INotifyPropertyChanged
    {
        private readonly CancellationTokenSourceHolder _cts = new();
        internal CancellationToken Token => _cts.Token;

        public Guid Id { get; } = Guid.NewGuid();
        public TransferDirection Direction { get; init; }
        /// <summary>name to show: "3 items" or the file name</summary>
        public string Title { get; internal set; } = "";
        /// <summary>destination folder (remote in server format for upload, local for download)</summary>
        public string TargetDir { get; init; } = "";
        public DateTime CreatedAt { get; } = DateTime.Now;

        private TransferState _state = TransferState.Queued;
        public TransferState State { get => _state; internal set => Set(ref _state, value); }
        private long _totalBytes; public long TotalBytes { get => _totalBytes; internal set => Set(ref _totalBytes, value); }
        private long _doneBytes; public long DoneBytes { get => _doneBytes; internal set => Set(ref _doneBytes, value); }
        private int _totalFiles; public int TotalFiles { get => _totalFiles; internal set => Set(ref _totalFiles, value); }
        private int _doneFiles; public int DoneFiles { get => _doneFiles; internal set => Set(ref _doneFiles, value); }
        private bool _waitingForDecision; public bool WaitingForDecision { get => _waitingForDecision; internal set => Set(ref _waitingForDecision, value); }
        /// <summary>top-level paths created by the transfer (local for download, remote in server format for upload): used to copy/drag the downloaded files</summary>
        public List<string> ResultPaths { get; } = new();
        private string _currentItem = ""; public string CurrentItem { get => _currentItem; internal set => Set(ref _currentItem, value); }
        private double _bytesPerSecond; public double BytesPerSecond { get => _bytesPerSecond; internal set => Set(ref _bytesPerSecond, value); }
        private FileTransferException? _error; public FileTransferException? Error { get => _error; internal set => Set(ref _error, value); }
        public DateTime? FinishedAt { get; internal set; }

        /// <summary>from 0 to 1 (0 if the total is not known yet)</summary>
        public double Progress => TotalBytes > 0 ? Math.Min(1.0, (double)DoneBytes / TotalBytes) : (State is TransferState.Completed or TransferState.CompletedWithSkips ? 1 : 0);
        public TimeSpan? Remaining => BytesPerSecond > 1 && TotalBytes > DoneBytes ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / BytesPerSecond) : null;
        public bool IsFinished => State is TransferState.Completed or TransferState.CompletedWithSkips or TransferState.Failed or TransferState.Cancelled;

        public List<TransferItem> Items { get; } = new();
        public Task Completion => _tcs.Task;
        internal readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>free text for the UI (source/destination folder to show)</summary>
        public string Hint { get; set; } = "";

        public void Cancel()
        {
            _cts.CancelNow();
            if (State == TransferState.Queued)   // queued: close it right away, without waiting for its turn
            {
                FinishedAt = DateTime.Now;
                State = TransferState.Cancelled;
                _tcs.TrySetResult();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name is nameof(DoneBytes) or nameof(TotalBytes) or nameof(State))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remaining)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFinished)));
            }
        }
    }

    internal sealed class CancellationTokenSourceHolder
    {
        private readonly System.Threading.CancellationTokenSource _src = new();
        public CancellationToken Token => _src.Token;
        public Action? Cancel { get; set; }
        public void CancelNow() { try { _src.Cancel(); } catch { } Cancel?.Invoke(); }
    }
}
