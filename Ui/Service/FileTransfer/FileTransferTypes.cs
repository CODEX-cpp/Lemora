using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

// Trasferimento file nelle sessioni VNC (protocollo file di TightVNC 2.x). Questo file: tipi comuni, senza dipendenze dall'interfaccia grafica.
namespace Lemora.Service.FileTransfer
{
    public enum FtErrorKind
    {
        Unknown,
        /// <summary>il server non e' un TightVNC (nessun tipo di sicurezza Tight) o ha il trasferimento file disattivato</summary>
        NotSupported,
        AuthFailed,
        Timeout,
        Disconnected,
        /// <summary>accesso negato (permessi di Windows dell'utente collegato al PC remoto, oppure schermata di blocco)</summary>
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
        /// <summary>codice di errore di Windows riportato dal server (2 = non trovato, 5 = accesso negato, 32 = file in uso...), se noto</summary>
        public int? WinError { get; }
        public FileTransferException(FtErrorKind kind, string message, int? winError = null, Exception? inner = null) : base(message, inner) { Kind = kind; WinError = winError; }

        /// <summary>errori che possono sparire riprovando con una nuova connessione</summary>
        public bool IsTransient => Kind is FtErrorKind.Timeout or FtErrorKind.Disconnected;

        public static FileTransferException FromServerText(string text)
        {
            // il server manda "Error code N" (codici di Windows) o frasi fisse
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

    /// <summary>una voce dell'elenco remoto (file o cartella)</summary>
    public sealed record RemoteEntry(string Dir, string Name, bool IsDirectory, long Size, DateTime ModifiedUtc)
    {
        /// <summary>percorso completo nel formato del server (/C:/Users/Nome/file.txt)</summary>
        public string FullPath => RemotePath.Join(Dir, Name);
        /// <summary>percorso in stile Windows (C:\Users\Nome\file.txt)</summary>
        public string WindowsPath => RemotePath.ToWindows(FullPath);
        /// <summary>vero per i dischi nell'elenco radice (C:, D:...)</summary>
        public bool IsDrive => Dir == "/" && IsDirectory;
    }

    /// <summary>
    /// Percorsi remoti. Sul filo il server usa "/" come radice (elenco dei dischi) e "/C:/cartella/file"; l'interfaccia usa "C:\cartella\file".
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
            // "C:" da solo e' il disco corrente, non la radice: C:\
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

        /// <summary>nome non valido per Windows (caratteri vietati, nomi riservati)? Vale sia per il PC locale sia per il remoto.</summary>
        public static bool IsValidWindowsName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ')) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var stem = name.Split('.')[0].ToUpperInvariant();
            return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
        }

        /// <summary>"nome.txt" -> "nome (2).txt" (per "tieni entrambi")</summary>
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
        /// <summary>si chiede (serve ConflictResolver; senza, si tengono entrambi)</summary>
        Ask,
        Overwrite,
        Skip,
        /// <summary>il file nuovo prende un altro nome: "nome (2).ext"</summary>
        KeepBoth,
    }

    public enum ConflictAction { Overwrite, Skip, KeepBoth, CancelJob }

    public sealed record ConflictInfo(TransferDirection Direction, string SourcePath, string TargetPath, long SourceSize, long TargetSize, DateTime SourceModifiedUtc, DateTime TargetModifiedUtc);

    public sealed record ConflictDecision(ConflictAction Action, bool ApplyToAll);

    public sealed class FileTransferOptions
    {
        /// <summary>0 = nessun limite</summary>
        public long MaxFileBytes { get; set; }
        /// <summary>somma dei file di un singolo trasferimento; 0 = nessun limite</summary>
        public long MaxJobBytes { get; set; }
        public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Ask;
        /// <summary>nuovi tentativi (nuova connessione, il file riparte da capo) per errori di rete</summary>
        public int Retries { get; set; } = 2;
        /// <summary>i file in corso hanno un nome provvisorio e si rinominano a fine copia, cosi' un'interruzione non lascia file a meta' col nome vero</summary>
        public bool UseTemporaryNames { get; set; } = true;
        /// <summary>blocchi richiesti in anticipo durante lo scarico (1 = uno alla volta)</summary>
        public int DownloadPipelineDepth { get; set; } = 4;
    }

    /// <summary>un elemento del trasferimento, con l'esito</summary>
    public sealed class TransferItem
    {
        public string Source { get; init; } = "";
        public string Target { get; set; } = "";
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public string Status { get; set; } = "";   // "ok", "saltato: ...", "errore: ..."
    }

    /// <summary>un trasferimento (una o piu' voci, file e cartelle): l'interfaccia lo mostra nella lista e puo' annullarlo</summary>
    public sealed class TransferJob : INotifyPropertyChanged
    {
        private readonly CancellationTokenSourceHolder _cts = new();
        internal CancellationToken Token => _cts.Token;

        public Guid Id { get; } = Guid.NewGuid();
        public TransferDirection Direction { get; init; }
        /// <summary>nome da mostrare: "3 elementi" o il nome del file</summary>
        public string Title { get; internal set; } = "";
        /// <summary>cartella di arrivo (remota in formato server per il carico, locale per lo scarico)</summary>
        public string TargetDir { get; init; } = "";
        public DateTime CreatedAt { get; } = DateTime.Now;

        private TransferState _state = TransferState.Queued;
        public TransferState State { get => _state; internal set => Set(ref _state, value); }
        private long _totalBytes; public long TotalBytes { get => _totalBytes; internal set => Set(ref _totalBytes, value); }
        private long _doneBytes; public long DoneBytes { get => _doneBytes; internal set => Set(ref _doneBytes, value); }
        private int _totalFiles; public int TotalFiles { get => _totalFiles; internal set => Set(ref _totalFiles, value); }
        private int _doneFiles; public int DoneFiles { get => _doneFiles; internal set => Set(ref _doneFiles, value); }
        private bool _waitingForDecision; public bool WaitingForDecision { get => _waitingForDecision; internal set => Set(ref _waitingForDecision, value); }
        /// <summary>percorsi di primo livello creati dal trasferimento (locali per lo scarico, remoti in formato server per il carico): servono per copiare/trascinare i file scaricati</summary>
        public List<string> ResultPaths { get; } = new();
        private string _currentItem = ""; public string CurrentItem { get => _currentItem; internal set => Set(ref _currentItem, value); }
        private double _bytesPerSecond; public double BytesPerSecond { get => _bytesPerSecond; internal set => Set(ref _bytesPerSecond, value); }
        private FileTransferException? _error; public FileTransferException? Error { get => _error; internal set => Set(ref _error, value); }
        public DateTime? FinishedAt { get; internal set; }

        /// <summary>da 0 a 1 (0 se ancora non si conosce il totale)</summary>
        public double Progress => TotalBytes > 0 ? Math.Min(1.0, (double)DoneBytes / TotalBytes) : (State is TransferState.Completed or TransferState.CompletedWithSkips ? 1 : 0);
        public TimeSpan? Remaining => BytesPerSecond > 1 && TotalBytes > DoneBytes ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / BytesPerSecond) : null;
        public bool IsFinished => State is TransferState.Completed or TransferState.CompletedWithSkips or TransferState.Failed or TransferState.Cancelled;

        public List<TransferItem> Items { get; } = new();
        public Task Completion => _tcs.Task;
        internal readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>testo libero per l'interfaccia (cartella di origine/destinazione da mostrare)</summary>
        public string Hint { get; set; } = "";

        public void Cancel()
        {
            _cts.CancelNow();
            if (State == TransferState.Queued)   // in coda: si chiude subito, senza aspettare il suo turno
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
