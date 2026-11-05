using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lemora.Service.FileTransfer
{
    /// <summary>
    /// Il "motore" del trasferimento file di una sessione VNC: sfogliare il PC remoto, creare/rinominare/cancellare e, soprattutto, caricare e
    /// scaricare file e cartelle (anche trascinati o incollati: i percorsi arrivano dall'interfaccia). Ogni trasferimento e' un <see cref="TransferJob"/>
    /// con avanzamento, annullamento ed elenco degli esiti; i trasferimenti vanno in coda e si eseguono uno alla volta.
    /// Usa due connessioni: una per sfogliare (sempre reattiva) e una per i trasferimenti.
    /// </summary>
    public sealed class FileTransferManager : IDisposable
    {
        /// <summary>registro diagnostico (sostituibile; nel programma va nel registro dei log)</summary>
        public static Action<string>? DiagLog;
        private static void Log(string m) { try { DiagLog?.Invoke("FT: " + m); } catch { } }

        public const string PartSuffix = ".lm-part";

        private readonly Func<CancellationToken, Task<TightFtConnection>> _connect;
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly Channel<TransferJob> _queue = Channel.CreateUnbounded<TransferJob>();
        private readonly Dictionary<Guid, Func<TransferJob, Task>> _work = new();
        private readonly List<TransferJob> _jobs = new();
        private readonly ConnSlot _browse, _xfer;
        private DateTime _lastCloseUtc = DateTime.MinValue;
        private readonly object _gapLock = new();

        public FileTransferOptions Options { get; }
        /// <summary>chiamata quando un file esiste gia' e la regola e' "Ask"; l'interfaccia mostra la scelta (sostituisci / salta / tieni entrambi, per tutti)</summary>
        public Func<ConflictInfo, Task<ConflictDecision>>? ConflictResolver { get; set; }
        /// <summary>attesa minima tra la chiusura di una connessione e l'apertura della successiva (TightVNC ha bisogno di un attimo per ripulire)</summary>
        public int MinReconnectGapMs { get; set; } = 1500;
        public event Action<TransferJob>? JobAdded;

        public IReadOnlyList<TransferJob> Jobs { get { lock (_jobs) return _jobs.ToArray(); } }

        public FileTransferManager(Func<CancellationToken, Task<TightFtConnection>> connect, FileTransferOptions? options = null)
        {
            _connect = connect;
            Options = options ?? new FileTransferOptions();
            _browse = new ConnSlot(this, "sfoglia");
            _xfer = new ConnSlot(this, "trasferimenti");
            _ = Task.Run(WorkerAsync);
        }

        public void Dispose()
        {
            _disposeCts.Cancel();
            foreach (var j in Jobs) j.Cancel();
            _queue.Writer.TryComplete();
            _browse.Drop(); _xfer.Drop();
        }

        #region connessioni

        private sealed class ConnSlot
        {
            private readonly FileTransferManager _m; private readonly string _name;
            private readonly SemaphoreSlim _lock = new(1, 1);
            public TightFtConnection? Conn;
            public ConnSlot(FileTransferManager m, string name) { _m = m; _name = name; }

            public async Task<TightFtConnection> GetAsync(CancellationToken ct)
            {
                await _lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Conn is { IsBroken: false }) return Conn;
                    Drop();
                    for (var attempt = 0; ; attempt++)
                    {
                        await _m.WaitGapAsync(ct).ConfigureAwait(false);
                        try
                        {
                            Conn = await _m._connect(ct).ConfigureAwait(false);
                            Log($"connessione '{_name}' aperta");
                            return Conn;
                        }
                        catch (FileTransferException e) when (e.IsTransient && attempt < 2)
                        {
                            Log($"connessione '{_name}' non riuscita ({e.Message}), nuovo tentativo");
                            _m.MarkClosed();
                        }
                    }
                }
                finally { _lock.Release(); }
            }

            public void Drop()
            {
                var c = Conn; Conn = null;
                if (c == null) return;
                try { c.Dispose(); } catch { }
                _m.MarkClosed();
            }
        }

        private void MarkClosed() { lock (_gapLock) _lastCloseUtc = DateTime.UtcNow; }

        private async Task WaitGapAsync(CancellationToken ct)
        {
            double wait;
            lock (_gapLock) wait = MinReconnectGapMs - (DateTime.UtcNow - _lastCloseUtc).TotalMilliseconds;
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
        }

        /// <summary>esegue un'operazione; se la connessione cade (o non risponde) la riapre e riprova una volta</summary>
        private async Task<T> WithConnAsync<T>(ConnSlot slot, Func<TightFtConnection, Task<T>> op, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var c = await slot.GetAsync(ct).ConfigureAwait(false);
                try { return await op(c).ConfigureAwait(false); }
                catch (FileTransferException e) when (e.IsTransient && attempt < Math.Max(1, Options.Retries))
                {
                    Log($"operazione interrotta ({e.Message}), riapro la connessione");
                    slot.Drop();
                }
                catch (OperationCanceledException) { slot.Drop(); throw; }
                catch when (c.IsBroken) { slot.Drop(); throw; }
            }
        }

        #endregion

        #region sfogliare e gestire il PC remoto

        /// <summary>contenuto di una cartella ("/" = dischi), cartelle prima, poi file, in ordine alfabetico</summary>
        public async Task<List<RemoteEntry>> ListAsync(string wireDir, CancellationToken ct = default)
        {
            var l = await WithConnAsync(_browse, c => c.ListAsync(wireDir, ct), ct).ConfigureAwait(false);
            return l.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public Task MkdirAsync(string wirePath, CancellationToken ct = default) =>
            WithConnAsync(_browse, async c => { await c.MkdirAsync(wirePath, ct).ConfigureAwait(false); return 0; }, ct);

        public Task RenameAsync(string wireOld, string wireNew, CancellationToken ct = default) =>
            WithConnAsync(_browse, async c => { await c.RenameAsync(wireOld, wireNew, ct).ConfigureAwait(false); return 0; }, ct);

        /// <summary>cancella un file o una cartella con tutto il contenuto (il server cancella solo cartelle vuote: si scende e si cancella dal fondo)</summary>
        public Task DeleteAsync(RemoteEntry entry, CancellationToken ct = default) =>
            WithConnAsync(_browse, c => DeleteTreeAsync(c, entry.FullPath, entry.IsDirectory, ct), ct);

        private static async Task<int> DeleteTreeAsync(TightFtConnection c, string wirePath, bool isDir, CancellationToken ct)
        {
            if (isDir)
                foreach (var child in await c.ListAsync(wirePath, ct).ConfigureAwait(false))
                    await DeleteTreeAsync(c, child.FullPath, child.IsDirectory, ct).ConfigureAwait(false);
            await c.DeleteAsync(wirePath, ct).ConfigureAwait(false);
            return 0;
        }

        /// <summary>true se il server risponde e il trasferimento file e' disponibile (apre la connessione di navigazione)</summary>
        public async Task<bool> CheckAvailableAsync(CancellationToken ct = default) => (await ProbeAsync(ct).ConfigureAwait(false)) == null;

        /// <summary>apre la connessione di navigazione; null = tutto bene, altrimenti il motivo per cui il trasferimento file non e' disponibile</summary>
        public async Task<FileTransferException?> ProbeAsync(CancellationToken ct = default)
        {
            try { await _browse.GetAsync(ct).ConfigureAwait(false); return null; }
            catch (FileTransferException e) { return e; }
        }

        /// <summary>toglie un trasferimento finito dalla lista (non annulla quelli in corso)</summary>
        public void RemoveJob(TransferJob job) { if (job.IsFinished) lock (_jobs) _jobs.Remove(job); }

        #endregion

        /// <summary>
        /// Legge un file remoto "al volo", senza salvarlo: serve al trascinamento verso Esplora risorse con file virtuali (il file si scarica mentre Esplora lo copia).
        /// Usa una connessione propria; chiudere lo stream la chiude e interrompe lo scarico.
        /// </summary>
        public async Task<Stream> OpenReadStreamAsync(RemoteEntry entry, CancellationToken ct = default)
        {
            if (entry.IsDirectory) throw new ArgumentException("Serve un file, non una cartella", nameof(entry));
            await WaitGapAsync(ct).ConfigureAwait(false);
            var conn = await _connect(ct).ConfigureAwait(false);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            var pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 4 * 1024 * 1024, resumeWriterThreshold: 1024 * 1024));
            _ = Task.Run(async () =>
            {
                try
                {
                    var w = pipe.Writer.AsStream(leaveOpen: true);
                    await conn.DownloadAsync(entry.FullPath, w, null, Options.DownloadPipelineDepth, cts.Token).ConfigureAwait(false);
                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception e) { await pipe.Writer.CompleteAsync(e).ConfigureAwait(false); }
                finally { conn.Dispose(); MarkClosed(); }
            });
            return new DisposeHookStream(pipe.Reader.AsStream(), () => { try { cts.Cancel(); } catch { } });
        }

        /// <summary>stream in sola lettura che alla chiusura esegue un'azione (qui: annulla lo scarico in corso)</summary>
        private sealed class DisposeHookStream : Stream
        {
            private readonly Stream _inner; private readonly Action _onDispose; private bool _done;
            public DisposeHookStream(Stream inner, Action onDispose) { _inner = inner; _onDispose = onDispose; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_done) { _done = true; _onDispose(); _inner.Dispose(); }
                base.Dispose(disposing);
            }
        }

        #region coda dei trasferimenti

        /// <summary>
        /// Carica file e cartelle (anche piu' insieme) dentro <paramref name="remoteDir"/> (formato server, es. /C:/Users/Nome; non la radice).
        /// Pensato per il trascinamento (drop) e per "incolla" dagli appunti: i percorsi locali arrivano dall'interfaccia.
        /// </summary>
        public TransferJob EnqueueUpload(IEnumerable<string> localPaths, string remoteDir)
        {
            if (remoteDir == RemotePath.Root || string.IsNullOrEmpty(remoteDir)) throw new ArgumentException("Scegli un disco o una cartella di destinazione sul PC remoto", nameof(remoteDir));
            var paths = localPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) throw new ArgumentException("Nessun file da caricare", nameof(localPaths));
            var job = NewJob(TransferDirection.Upload, remoteDir, paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\', '/')) is { Length: > 0 } n ? n : paths[0] : $"{paths.Count} elementi");
            Enqueue(job, j => RunUploadAsync(j, paths, remoteDir));
            return job;
        }

        /// <summary>Scarica file e cartelle del PC remoto dentro la cartella locale <paramref name="localDir"/> (anche per copia verso Esplora risorse: vedi ResultPaths)</summary>
        public TransferJob EnqueueDownload(IEnumerable<RemoteEntry> items, string localDir)
        {
            var list = items.Where(i => !i.IsDrive).ToList();
            if (list.Count == 0) throw new ArgumentException("Nessun elemento da scaricare", nameof(items));
            Directory.CreateDirectory(localDir);
            var job = NewJob(TransferDirection.Download, localDir, list.Count == 1 ? list[0].Name : $"{list.Count} elementi");
            Enqueue(job, j => RunDownloadAsync(j, list, localDir));
            return job;
        }

        private TransferJob NewJob(TransferDirection d, string targetDir, string title) => new() { Direction = d, TargetDir = targetDir, Title = title };

        private void Enqueue(TransferJob job, Func<TransferJob, Task> work)
        {
            lock (_jobs) { _jobs.Add(job); _work[job.Id] = work; }
            JobAdded?.Invoke(job);
            _queue.Writer.TryWrite(job);
        }

        /// <summary>toglie dalla lista i trasferimenti finiti (per il pulsante "pulisci")</summary>
        public void ClearFinished() { lock (_jobs) _jobs.RemoveAll(j => j.IsFinished); }

        private async Task WorkerAsync()
        {
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
                {
                    Func<TransferJob, Task>? work;
                    lock (_jobs) { _work.Remove(job.Id, out work); }
                    if (work == null) continue;
                    if (job.Token.IsCancellationRequested) { Finish(job, TransferState.Cancelled, null); continue; }
                    try { await work(job).ConfigureAwait(false); }
                    catch (OperationCanceledException) { Finish(job, TransferState.Cancelled, null); }
                    catch (FileTransferException e) { Log($"trasferimento '{job.Title}' fallito: {e.Message}"); Finish(job, e.Kind == FtErrorKind.Cancelled ? TransferState.Cancelled : TransferState.Failed, e); }
                    catch (Exception e) { Log($"trasferimento '{job.Title}' fallito: {e}"); Finish(job, TransferState.Failed, new FileTransferException(FtErrorKind.Unknown, e.Message, null, e)); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private static void Finish(TransferJob job, TransferState state, FileTransferException? error)
        {
            if (job.IsFinished && job.FinishedAt != null) return;
            job.Error ??= error;
            job.FinishedAt = DateTime.Now;
            job.BytesPerSecond = 0;
            job.CurrentItem = "";
            job.WaitingForDecision = false;
            job.State = state;
            // esito di ogni trasferimento nel log: riuscito a Medio, fallito o incompleto a ogni livello (col motivo)
            var secs = (job.FinishedAt.Value - job.CreatedAt).TotalSeconds;
            var line = $"Trasferimento file ({job.Direction}) '{job.Title}' -> {job.TargetDir}: {state}, {job.DoneFiles}/{job.TotalFiles} file, {job.DoneBytes / 1048576.0:0.0}/{job.TotalBytes / 1048576.0:0.0} MB in {secs:0} s"
                       + (job.Error != null ? $", motivo: {job.Error.Kind} {job.Error.Message}" : "");
            if (state == TransferState.Completed) Shawn.Utils.SimpleLogHelper.Info(line); else Shawn.Utils.SimpleLogHelper.Warning(line);
            job._tcs.TrySetResult();
        }

        #endregion

        #region progresso

        private sealed class Meter
        {
            private readonly TransferJob _job; private readonly Stopwatch _sw = Stopwatch.StartNew();
            private long _published, _lastBytes; private double _lastT, _speed;
            private long _done;
            public Meter(TransferJob job) { _job = job; }
            public long Done => Interlocked.Read(ref _done);
            public void Add(long n)
            {
                var d = Interlocked.Add(ref _done, n);
                var t = _sw.Elapsed.TotalSeconds;
                if (t - _lastT < 0.15) return;
                var inst = (d - _lastBytes) / (t - _lastT);
                _speed = _speed <= 0 ? inst : _speed * 0.7 + inst * 0.3;
                _lastT = t; _lastBytes = d;
                _job.BytesPerSecond = _speed;
                _published = d; _job.DoneBytes = d;
            }
            /// <summary>un tentativo e' fallito a meta': i byte del file non contano piu'</summary>
            public void Rollback(long n) { var d = Interlocked.Add(ref _done, -n); _job.DoneBytes = d; _lastBytes = d; }
            public void Flush() { _job.DoneBytes = Done; }
        }

        #endregion

        #region conflitti

        private sealed class ConflictState { public ConflictAction? Sticky; }

        private async Task<ConflictAction> ResolveAsync(TransferJob job, ConflictState st, ConflictInfo info)
        {
            if (st.Sticky != null) return st.Sticky.Value;
            switch (Options.Conflict)
            {
                case ConflictPolicy.Overwrite: return ConflictAction.Overwrite;
                case ConflictPolicy.Skip: return ConflictAction.Skip;
                case ConflictPolicy.KeepBoth: return ConflictAction.KeepBoth;
            }
            var resolver = ConflictResolver;
            if (resolver == null) return ConflictAction.KeepBoth;   // senza chi chiede, la scelta che non perde dati
            job.WaitingForDecision = true;
            try
            {
                var d = await resolver(info).ConfigureAwait(false);
                if (d.Action == ConflictAction.CancelJob) { job.Cancel(); throw new OperationCanceledException(); }
                if (d.ApplyToAll) st.Sticky = d.Action;
                return d.Action;
            }
            finally { job.WaitingForDecision = false; }
        }

        #endregion

        #region carico

        private sealed record LocalFile(string Path, string RemoteRel, long Size, DateTime ModifiedUtc);

        private async Task RunUploadAsync(TransferJob job, List<string> locals, string remoteDir)
        {
            var ct = job.Token;
            job.State = TransferState.Scanning;
            var dirs = new List<string>();   // cartelle da creare, relative alla destinazione (con "/")
            var files = new List<LocalFile>();
            var skipped = 0;
            foreach (var p in locals)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(p.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) { AddSkip(job, p, "", "saltato: unita' o percorso radice"); skipped++; continue; }
                if (!RemotePath.IsValidWindowsName(name)) { AddSkip(job, p, "", "saltato: nome non valido per Windows"); skipped++; continue; }
                if (Directory.Exists(p)) { dirs.Add(name); WalkLocal(job, p, name, dirs, files, ref skipped, ct); job.ResultPaths.Add(RemotePath.Join(remoteDir, name)); }
                else if (File.Exists(p)) { AddLocalFile(job, p, name, files, ref skipped); job.ResultPaths.Add(RemotePath.Join(remoteDir, name)); }
                else { AddSkip(job, p, "", "saltato: non esiste piu'"); skipped++; }
            }
            ApplyLimits(job, files, ref skipped);
            job.TotalFiles = files.Count;
            job.TotalBytes = files.Sum(f => f.Size);
            if (Options.MaxJobBytes > 0 && job.TotalBytes > Options.MaxJobBytes)
                throw new FileTransferException(FtErrorKind.TooLarge, $"Il trasferimento ({Fmt(job.TotalBytes)}) supera il limite di {Fmt(Options.MaxJobBytes)}");

            job.State = TransferState.Running;
            var meter = new Meter(job);
            var st = new ConflictState();
            var listings = new Dictionary<string, Dictionary<string, RemoteEntry>>(StringComparer.OrdinalIgnoreCase);   // elenchi remoti visti durante questo trasferimento
            var failed = 0;

            // cartelle (dalla piu' esterna): creare; se esiste gia' si unisce il contenuto
            foreach (var rel in dirs.OrderBy(d => d.Count(ch => ch == '/')))
            {
                ct.ThrowIfCancellationRequested();
                var path = RemotePath.Join(remoteDir, rel);
                job.CurrentItem = rel;
                try { await WithConnAsync(_xfer, c => MkdirIgnoreExistsAsync(c, path, ct), ct).ConfigureAwait(false); job.Items.Add(new TransferItem { Source = rel, Target = path, IsDirectory = true, Status = "ok" }); }
                catch (FileTransferException e) when (e.Kind != FtErrorKind.Cancelled && !e.IsTransient)
                {
                    failed++; job.Items.Add(new TransferItem { Source = rel, Target = path, IsDirectory = true, Status = "errore: " + e.Message });
                    job.Error ??= e;
                    // senza la cartella, i file dentro falliranno da soli
                }
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                job.CurrentItem = f.RemoteRel;
                var item = new TransferItem { Source = f.Path, Target = RemotePath.Join(remoteDir, f.RemoteRel), Size = f.Size };
                job.Items.Add(item);
                try
                {
                    var target = item.Target;
                    var parent = RemotePath.Parent(target);
                    var existing = await ExistingNamesAsync(listings, parent, ct).ConfigureAwait(false);
                    var existsAs = existing.TryGetValue(RemotePath.NameOf(target), out var ex) ? ex : null;
                    if (existsAs != null)
                    {
                        var action = await ResolveAsync(job, st, new ConflictInfo(TransferDirection.Upload, f.Path, target, f.Size, existsAs.Size, f.ModifiedUtc, existsAs.ModifiedUtc)).ConfigureAwait(false);
                        if (action == ConflictAction.Skip) { item.Status = "saltato: esiste gia'"; skipped++; meter.Add(f.Size); job.DoneFiles++; continue; }
                        if (action == ConflictAction.KeepBoth)
                        {
                            var n = 2; string newName;
                            do newName = RemotePath.NumberedName(RemotePath.NameOf(target), n++); while (existing.ContainsKey(newName));
                            target = RemotePath.Join(parent, newName); item.Target = target; existsAs = null;
                        }
                        else if (existsAs.IsDirectory) throw new FileTransferException(FtErrorKind.Exists, "Sul PC remoto esiste una cartella con lo stesso nome");
                    }
                    await UploadOneAsync(job, meter, f, target, existsAs != null, ct).ConfigureAwait(false);
                    existing[RemotePath.NameOf(target)] = new RemoteEntry(parent, RemotePath.NameOf(target), false, f.Size, f.ModifiedUtc);
                    item.Status = "ok"; job.DoneFiles++;
                }
                catch (FileTransferException e) when (e.Kind != FtErrorKind.Cancelled && !(e.IsTransient))
                {
                    failed++; item.Status = "errore: " + e.Message; job.Error ??= e; meter.Add(f.Size); job.DoneFiles++;
                    Log($"carico di '{f.Path}' non riuscito: {e.Message}");
                }
            }
            meter.Flush();
            var ok = job.Items.Count(i => i.Status == "ok" && !i.IsDirectory);
            Finish(job, failed > 0 && ok == 0 ? TransferState.Failed : failed + skipped > 0 ? TransferState.CompletedWithSkips : TransferState.Completed, job.Error);
        }

        private async Task UploadOneAsync(TransferJob job, Meter meter, LocalFile f, string target, bool overwrite, CancellationToken ct)
        {
            var tmp = Options.UseTemporaryNames ? target + PartSuffix : target;
            for (var attempt = 0; ; attempt++)
            {
                long sent = 0;
                try
                {
                    await WithConnAsync(_xfer, async c =>
                    {
                        if (sent > 0) { meter.Rollback(sent); sent = 0; }   // secondo giro dopo una riconnessione: il file riparte da capo
                        await using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
                        await c.UploadAsync(tmp, fs, f.ModifiedUtc, n => { sent += n; meter.Add(n); }, ct).ConfigureAwait(false);
                        return 0;
                    }, ct).ConfigureAwait(false);
                    if (Options.UseTemporaryNames)
                    {
                        await WithConnAsync(_xfer, async c =>
                        {
                            if (overwrite) { try { await c.DeleteAsync(target, ct).ConfigureAwait(false); } catch (FileTransferException e) when (e.Kind == FtErrorKind.NotFound) { } }
                            await c.RenameAsync(tmp, target, ct).ConfigureAwait(false);
                            return 0;
                        }, ct).ConfigureAwait(false);
                    }
                    return;
                }
                catch (FileTransferException e) when (e.IsTransient && attempt < Options.Retries)
                {
                    meter.Rollback(sent);
                    Log($"carico di '{f.Path}' interrotto ({e.Message}), nuovo tentativo");
                }
                catch (OperationCanceledException) { meter.Rollback(sent); await CleanupRemotePartAsync(tmp).ConfigureAwait(false); throw; }
                catch (FileTransferException) { meter.Rollback(sent); await CleanupRemotePartAsync(tmp).ConfigureAwait(false); throw; }
            }
        }

        /// <summary>toglie il file provvisorio rimasto a meta' (con una connessione nuova: quella dell'operazione non e' piu' utilizzabile)</summary>
        private async Task CleanupRemotePartAsync(string tmpWirePath)
        {
            if (!tmpWirePath.EndsWith(PartSuffix, StringComparison.Ordinal)) return;
            try
            {
                using var cts = new CancellationTokenSource(15000);
                _xfer.Drop();
                await WithConnAsync(_xfer, async c => { try { await c.DeleteAsync(tmpWirePath, cts.Token).ConfigureAwait(false); } catch (FileTransferException) { } return 0; }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) { Log($"pulizia di '{tmpWirePath}' non riuscita: {e.Message}"); }
        }

        private static async Task<int> MkdirIgnoreExistsAsync(TightFtConnection c, string path, CancellationToken ct)
        {
            try { await c.MkdirAsync(path, ct).ConfigureAwait(false); }
            catch (FileTransferException e) when (e.Kind == FtErrorKind.Exists) { }
            return 0;
        }

        private async Task<Dictionary<string, RemoteEntry>> ExistingNamesAsync(Dictionary<string, Dictionary<string, RemoteEntry>> cache, string wireDir, CancellationToken ct)
        {
            if (cache.TryGetValue(wireDir, out var cached)) return cached;
            Dictionary<string, RemoteEntry> map;
            try
            {
                var l = await WithConnAsync(_xfer, c => c.ListAsync(wireDir, ct), ct).ConfigureAwait(false);
                map = l.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            }
            catch (FileTransferException e) when (e.Kind == FtErrorKind.NotFound) { map = new(StringComparer.OrdinalIgnoreCase); }   // cartella appena creata o non ancora visibile
            cache[wireDir] = map;
            return map;
        }

        private void WalkLocal(TransferJob job, string dirPath, string rel, List<string> dirs, List<LocalFile> files, ref int skipped, CancellationToken ct)
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(dirPath).ToList(); }
            catch (Exception e) { AddSkip(job, dirPath, rel, "saltato: " + e.Message); skipped++; return; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                var childRel = rel + "/" + name;
                FileAttributes attr;
                try { attr = File.GetAttributes(child); } catch (Exception e) { AddSkip(job, child, childRel, "saltato: " + e.Message); skipped++; continue; }
                if ((attr & FileAttributes.ReparsePoint) != 0) { AddSkip(job, child, childRel, "saltato: collegamento simbolico"); skipped++; continue; }   // evita cicli e uscite dalla cartella scelta
                if (!RemotePath.IsValidWindowsName(name)) { AddSkip(job, child, childRel, "saltato: nome non valido per Windows"); skipped++; continue; }
                if ((attr & FileAttributes.Directory) != 0) { dirs.Add(childRel); WalkLocal(job, child, childRel, dirs, files, ref skipped, ct); }
                else AddLocalFile(job, child, childRel, files, ref skipped);
            }
        }

        private static void AddLocalFile(TransferJob job, string path, string rel, List<LocalFile> files, ref int skipped)
        {
            try
            {
                var fi = new FileInfo(path);
                files.Add(new LocalFile(path, rel, fi.Length, fi.LastWriteTimeUtc));
            }
            catch (Exception e) { AddSkip(job, path, rel, "saltato: " + e.Message); skipped++; }
        }

        private static void AddSkip(TransferJob job, string src, string target, string status) => job.Items.Add(new TransferItem { Source = src, Target = target, Status = status });

        private void ApplyLimits(TransferJob job, List<LocalFile> files, ref int skipped)
        {
            if (Options.MaxFileBytes <= 0) return;
            foreach (var f in files.Where(f => f.Size > Options.MaxFileBytes).ToList())
            {
                AddSkip(job, f.Path, f.RemoteRel, $"saltato: {Fmt(f.Size)} supera il limite per file di {Fmt(Options.MaxFileBytes)}");
                files.Remove(f); skipped++;
            }
        }

        #endregion

        #region scarico

        private sealed record RemoteFile(RemoteEntry Entry, string LocalRel);

        private async Task RunDownloadAsync(TransferJob job, List<RemoteEntry> items, string localDir)
        {
            var ct = job.Token;
            job.State = TransferState.Scanning;
            var dirs = new List<string>();
            var files = new List<RemoteFile>();
            var skipped = 0;
            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();
                if (!RemotePath.IsValidWindowsName(it.Name)) { AddSkip(job, it.FullPath, "", "saltato: nome non valido"); skipped++; continue; }
                if (it.IsDirectory) { dirs.Add(it.Name); await WalkRemoteAsync(job, it, it.Name, dirs, files, ct).ConfigureAwait(false); }
                else files.Add(new RemoteFile(it, it.Name));
            }
            if (Options.MaxFileBytes > 0)
                foreach (var f in files.Where(f => f.Entry.Size > Options.MaxFileBytes).ToList())
                { AddSkip(job, f.Entry.FullPath, f.LocalRel, $"saltato: {Fmt(f.Entry.Size)} supera il limite per file di {Fmt(Options.MaxFileBytes)}"); files.Remove(f); skipped++; }
            job.TotalFiles = files.Count;
            job.TotalBytes = files.Sum(f => f.Entry.Size);
            if (Options.MaxJobBytes > 0 && job.TotalBytes > Options.MaxJobBytes)
                throw new FileTransferException(FtErrorKind.TooLarge, $"Il trasferimento ({Fmt(job.TotalBytes)}) supera il limite di {Fmt(Options.MaxJobBytes)}");

            job.State = TransferState.Running;
            var meter = new Meter(job);
            var st = new ConflictState();
            var failed = 0;

            foreach (var rel in dirs.OrderBy(d => d.Count(ch => ch == '/')))
            {
                var local = Path.Combine(localDir, rel.Replace('/', Path.DirectorySeparatorChar));
                try { Directory.CreateDirectory(local); job.Items.Add(new TransferItem { Source = rel, Target = local, IsDirectory = true, Status = "ok" }); }
                catch (Exception e) { failed++; job.Items.Add(new TransferItem { Source = rel, Target = local, IsDirectory = true, Status = "errore: " + e.Message }); }
            }
            foreach (var it in items) if (RemotePath.IsValidWindowsName(it.Name)) job.ResultPaths.Add(Path.Combine(localDir, it.Name));

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                job.CurrentItem = f.LocalRel;
                var target = Path.Combine(localDir, f.LocalRel.Replace('/', Path.DirectorySeparatorChar));
                var item = new TransferItem { Source = f.Entry.FullPath, Target = target, Size = f.Entry.Size };
                job.Items.Add(item);
                try
                {
                    var overwrite = false;
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        var fi = File.Exists(target) ? new FileInfo(target) : null;
                        var action = await ResolveAsync(job, st, new ConflictInfo(TransferDirection.Download, f.Entry.FullPath, target, f.Entry.Size, fi?.Length ?? 0, f.Entry.ModifiedUtc, fi?.LastWriteTimeUtc ?? DateTime.MinValue)).ConfigureAwait(false);
                        if (action == ConflictAction.Skip) { item.Status = "saltato: esiste gia'"; skipped++; meter.Add(f.Entry.Size); job.DoneFiles++; continue; }
                        if (action == ConflictAction.KeepBoth)
                        {
                            var dir = Path.GetDirectoryName(target)!; var name = Path.GetFileName(target); var n = 2; string cand;
                            do cand = Path.Combine(dir, RemotePath.NumberedName(name, n++)); while (File.Exists(cand) || Directory.Exists(cand));
                            target = cand; item.Target = target;
                            if (!f.LocalRel.Contains('/')) { var idx = job.ResultPaths.FindIndex(p => string.Equals(p, Path.Combine(localDir, f.LocalRel), StringComparison.OrdinalIgnoreCase)); if (idx >= 0) job.ResultPaths[idx] = target; }
                        }
                        else if (Directory.Exists(target)) throw new FileTransferException(FtErrorKind.Exists, "Sul PC locale esiste una cartella con lo stesso nome");
                        else overwrite = true;
                    }
                    await DownloadOneAsync(job, meter, f, target, overwrite, ct).ConfigureAwait(false);
                    item.Status = "ok"; job.DoneFiles++;
                }
                catch (FileTransferException e) when (e.Kind != FtErrorKind.Cancelled && !e.IsTransient)
                {
                    failed++; item.Status = "errore: " + e.Message; job.Error ??= e; job.DoneFiles++;
                    Log($"scarico di '{f.Entry.FullPath}' non riuscito: {e.Message}");
                }
                catch (IOException e)
                {
                    failed++; item.Status = "errore: " + e.Message; job.Error ??= new FileTransferException(FtErrorKind.Unknown, e.Message, null, e); job.DoneFiles++;
                }
                catch (UnauthorizedAccessException e)
                {
                    failed++; item.Status = "errore: " + e.Message; job.Error ??= new FileTransferException(FtErrorKind.Denied, e.Message, null, e); job.DoneFiles++;
                }
            }
            meter.Flush();
            var ok = job.Items.Count(i => i.Status == "ok" && !i.IsDirectory);
            Finish(job, failed > 0 && ok == 0 ? TransferState.Failed : failed + skipped > 0 ? TransferState.CompletedWithSkips : TransferState.Completed, job.Error);
        }

        private async Task DownloadOneAsync(TransferJob job, Meter meter, RemoteFile f, string target, bool overwrite, CancellationToken ct)
        {
            var tmp = target + PartSuffix;
            for (var attempt = 0; ; attempt++)
            {
                long got = 0;
                try
                {
                    DateTime modified = DateTime.MinValue;
                    await WithConnAsync(_xfer, async c =>
                    {
                        if (got > 0) { meter.Rollback(got); got = 0; }
                        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                            modified = await c.DownloadAsync(f.Entry.FullPath, fs, n => { got += n; meter.Add(n); }, Options.DownloadPipelineDepth, ct).ConfigureAwait(false);
                        return 0;
                    }, ct).ConfigureAwait(false);
                    File.Move(tmp, target, overwrite);
                    if (modified != DateTime.MinValue) { try { File.SetLastWriteTimeUtc(target, modified); } catch { } }
                    return;
                }
                catch (FileTransferException e) when (e.IsTransient && attempt < Options.Retries)
                {
                    meter.Rollback(got);
                    Log($"scarico di '{f.Entry.FullPath}' interrotto ({e.Message}), nuovo tentativo");
                }
                catch { meter.Rollback(got); try { File.Delete(tmp); } catch { } throw; }
            }
        }

        private async Task WalkRemoteAsync(TransferJob job, RemoteEntry dir, string rel, List<string> dirs, List<RemoteFile> files, CancellationToken ct)
        {
            List<RemoteEntry> children;
            try { children = await WithConnAsync(_xfer, c => c.ListAsync(dir.FullPath, ct), ct).ConfigureAwait(false); }
            catch (FileTransferException e) when (!e.IsTransient && e.Kind != FtErrorKind.Cancelled) { AddSkip(job, dir.FullPath, rel, "saltato: " + e.Message); return; }
            foreach (var ch in children)
            {
                ct.ThrowIfCancellationRequested();
                var childRel = rel + "/" + ch.Name;
                if (!RemotePath.IsValidWindowsName(ch.Name)) { AddSkip(job, ch.FullPath, childRel, "saltato: nome non valido"); continue; }
                if (ch.IsDirectory) { dirs.Add(childRel); await WalkRemoteAsync(job, ch, childRel, dirs, files, ct).ConfigureAwait(false); }
                else files.Add(new RemoteFile(ch, childRel));
            }
        }

        #endregion

        public static string Fmt(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; var i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.#} {u[i]}";
        }
    }
}
