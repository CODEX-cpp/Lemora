using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lemora.Service.FileTransfer
{
    /// <summary>
    /// Una connessione di trasferimento file verso un server TightVNC 2.x (stessa porta dello schermo, ma connessione separata: come fa il
    /// Viewer ufficiale). Protocollo ricavato dal traffico reale: vedi la nota "tightvnc-file-transfer-protocol" nella memoria del progetto.
    /// Tutte le operazioni sono a una richiesta alla volta (serializzate); dopo un timeout o un annullamento la connessione e' "rotta"
    /// (IsBroken) e va chiusa: lo stato del flusso non e' piu' noto.
    /// </summary>
    public sealed class TightFtConnection : IDisposable
    {
        // codici dei messaggi (0xFC000100 + n)
        private const uint LIST_REQ = 0xFC000102, LIST_REP = 0xFC000103;
        private const uint UP_REQ = 0xFC000106, UP_REP = 0xFC000107, UP_DATA = 0xFC000108, UP_DATA_REP = 0xFC000109, UP_END = 0xFC00010A, UP_END_REP = 0xFC00010B;
        private const uint DN_REQ = 0xFC00010C, DN_REP = 0xFC00010D, DN_DATA_REQ = 0xFC00010E, DN_DATA = 0xFC00010F, DN_END = 0xFC000110;
        private const uint MKDIR_REQ = 0xFC000111, MKDIR_REP = 0xFC000112;
        private const uint DEL_REQ = 0xFC000113, DEL_REP = 0xFC000114;
        private const uint REN_REQ = 0xFC000115, REN_REP = 0xFC000116;
        private const uint ERROR_REP = 0xFC000119;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _s;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public string Host { get; }
        public int Port { get; }
        /// <summary>vero dopo un timeout, un annullamento o un errore di rete: la connessione non e' piu' utilizzabile</summary>
        public bool IsBroken { get; private set; }
        /// <summary>attesa massima per ogni lettura dal server</summary>
        public int IoTimeoutMs { get; set; } = 20000;
        /// <summary>byte per blocco in carico (il Viewer ufficiale usa 40000, 80000, 160000...)</summary>
        public int UploadChunkSize { get; set; } = 160000;
        /// <summary>byte chiesti per ogni blocco in scarico</summary>
        public int DownloadChunkSize { get; set; } = 262144;
        public DateTime OpenedAtUtc { get; } = DateTime.UtcNow;

        private TightFtConnection(TcpClient tcp, string host, int port)
        {
            _tcp = tcp; _s = tcp.GetStream(); Host = host; Port = port;
        }

        #region collegamento

        /// <summary>
        /// Apre la connessione e si autentica con la password VNC (come per lo schermo). Eccezioni: NotSupported (server non TightVNC o trasferimento file
        /// disattivato), AuthFailed (password rifiutata), Timeout, Disconnected.
        /// </summary>
        public static async Task<TightFtConnection> ConnectAsync(string host, int port, string password, CancellationToken ct, int connectTimeoutMs = 8000)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(connectTimeoutMs);
                    try { await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new FileTransferException(FtErrorKind.Timeout, $"Il server {host}:{port} non risponde"); }
                    catch (SocketException e) { throw new FileTransferException(FtErrorKind.Disconnected, e.Message, null, e); }
                }
                var c = new TightFtConnection(tcp, host, port);
                try { await c.HandshakeAsync(password, ct).ConfigureAwait(false); }
                catch { c.Dispose(); throw; }
                return c;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        private async Task HandshakeAsync(string password, CancellationToken ct)
        {
            var ver = await ReadAsync(12, ct).ConfigureAwait(false);
            if (ver[0] != (byte)'R' || ver[1] != (byte)'F' || ver[2] != (byte)'B') throw new FileTransferException(FtErrorKind.NotSupported, "Il server non parla il protocollo VNC");
            await WriteAsync(ct, Encoding.ASCII.GetBytes("RFB 003.008\n")).ConfigureAwait(false);

            var nSec = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
            if (nSec == 0)
            {
                var len = await ReadU32Async(ct).ConfigureAwait(false);
                var reason = len is > 0 and < 1024 ? Encoding.UTF8.GetString(await ReadAsync((int)len, ct).ConfigureAwait(false)) : "";
                throw new FileTransferException(FtErrorKind.Disconnected, "Il server rifiuta la connessione" + (reason.Length > 0 ? ": " + reason : ""));
            }
            var secTypes = await ReadAsync(nSec, ct).ConfigureAwait(false);
            if (Array.IndexOf(secTypes, (byte)16) < 0)
                throw new FileTransferException(FtErrorKind.NotSupported, "Il server non e' un TightVNC: il trasferimento file non e' disponibile");
            await WriteAsync(ct, new byte[] { 16 }).ConfigureAwait(false);

            // tunnel: si sceglie solo se il server ne propone (non ne propone); poi i tipi di autenticazione
            var nTun = await ReadU32Async(ct).ConfigureAwait(false);
            if (nTun > 64) throw ProtocolError("elenco tunnel");
            if (nTun > 0) { await ReadAsync((int)nTun * 16, ct).ConfigureAwait(false); await WriteU32Async(ct, 0).ConfigureAwait(false); }
            var nAuth = await ReadU32Async(ct).ConfigureAwait(false);
            if (nAuth is 0 or > 64) throw ProtocolError("elenco autenticazioni");
            var authCaps = await ReadAsync((int)nAuth * 16, ct).ConfigureAwait(false);
            var hasVncAuth = false;
            for (var i = 0; i < nAuth; i++) if (BinaryPrimitives.ReadUInt32BigEndian(authCaps.AsSpan(i * 16, 4)) == 2) hasVncAuth = true;
            if (!hasVncAuth) throw new FileTransferException(FtErrorKind.NotSupported, "Il server non accetta la password VNC");
            await WriteU32Async(ct, 2).ConfigureAwait(false);

            var challenge = await ReadAsync(16, ct).ConfigureAwait(false);
            await WriteAsync(ct, EncryptChallenge(challenge, password)).ConfigureAwait(false);
            var result = await ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0) throw new FileTransferException(FtErrorKind.AuthFailed, "Password rifiutata dal server");

            await WriteAsync(ct, new byte[] { 1 }).ConfigureAwait(false);   // ClientInit: sessione condivisa (non scollega chi sta guardando lo schermo)
            await ReadAsync(20, ct).ConfigureAwait(false);                  // ServerInit: dimensioni e formato pixel (non servono)
            var nameLen = await ReadU32Async(ct).ConfigureAwait(false);
            if (nameLen > 4096) throw ProtocolError("nome del desktop");
            await ReadAsync((int)nameLen, ct).ConfigureAwait(false);

            // intestazione Tight: numero di messaggi del server, del client e di codifiche, poi 16 byte per ciascuno
            var hdr = await ReadAsync(8, ct).ConfigureAwait(false);
            int nServer = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(0)), nClient = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2)), nEnc = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
            if (nServer + nClient + nEnc > 512) throw ProtocolError("elenco capacita");
            var caps = await ReadAsync((nServer + nClient + nEnc) * 16, ct).ConfigureAwait(false);
            var hasFt = false;
            for (var i = 0; i < nServer; i++) if (BinaryPrimitives.ReadUInt32BigEndian(caps.AsSpan(i * 16, 4)) == LIST_REP) hasFt = true;
            if (!hasFt) throw new FileTransferException(FtErrorKind.NotSupported, "Il trasferimento file e' disattivato sul server (impostazione \"Enable file transfers\" di TightVNC)");
        }

        /// <summary>risposta alla sfida VNC: DES con la password (al massimo 8 caratteri) a cui si invertono i bit di ogni byte</summary>
        private static byte[] EncryptChallenge(byte[] challenge, string password)
        {
            if (string.IsNullOrEmpty(password)) throw new FileTransferException(FtErrorKind.AuthFailed, "Password mancante");
            var key = new byte[8];
            var pb = Encoding.Latin1.GetBytes(password);
            for (var i = 0; i < 8; i++)
            {
                var k = i < pb.Length ? pb[i] : (byte)0;
                byte r = 0;
                for (var j = 0; j < 8; j++) if ((k & (1 << j)) != 0) r |= (byte)(1 << (7 - j));
                key[i] = r;
            }
            try
            {
                using var des = DES.Create();
                des.Mode = CipherMode.ECB; des.Padding = PaddingMode.None; des.Key = key;
                using var enc = des.CreateEncryptor();
                return enc.TransformFinalBlock(challenge, 0, 16);
            }
            catch (CryptographicException e) { throw new FileTransferException(FtErrorKind.AuthFailed, "Password non utilizzabile per l'autenticazione VNC", null, e); }
        }

        #endregion

        #region operazioni

        /// <summary>contenuto di una cartella; "/" e' l'elenco dei dischi. Il percorso e' nel formato del server (/C:/Users).</summary>
        public Task<List<RemoteEntry>> ListAsync(string wireDir, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(LIST_REQ), new byte[] { 0 }, Str(wireDir)).ConfigureAwait(false);
            var code = await ReadCodeAsync(ct).ConfigureAwait(false);
            if (code == ERROR_REP) throw await ReadServerErrorAsync(ct).ConfigureAwait(false);
            if (code != LIST_REP) throw ProtocolError($"risposta all'elenco 0x{code:x8}");
            await ReadAsync(1, ct).ConfigureAwait(false);   // flags
            var tot = await ReadU32Async(ct).ConfigureAwait(false);
            var tot2 = await ReadU32Async(ct).ConfigureAwait(false);
            if (tot != tot2) throw new FileTransferException(FtErrorKind.NotSupported, "Elenco compresso dal server: non gestito");
            if (tot > 64 * 1024 * 1024) throw ProtocolError("elenco troppo grande");
            var count = await ReadU32Async(ct).ConfigureAwait(false);
            var list = new List<RemoteEntry>((int)Math.Min(count, 4096));
            long consumed = 4;
            for (var i = 0; i < count; i++)
            {
                var fixedPart = await ReadAsync(8 + 8 + 2 + 4, ct).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadInt64BigEndian(fixedPart.AsSpan(0));
                var ms = BinaryPrimitives.ReadInt64BigEndian(fixedPart.AsSpan(8));
                var flags = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(16));
                var nl = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(18));
                if (nl > 4096) throw ProtocolError("nome troppo lungo");
                var name = Encoding.UTF8.GetString(await ReadAsync((int)nl, ct).ConfigureAwait(false)).TrimEnd('\0');
                consumed += 22 + nl;
                list.Add(new RemoteEntry(wireDir, name, (flags & 1) != 0, (flags & 1) != 0 ? 0 : Math.Max(0, size), ToUtc(ms)));
            }
            if (consumed != tot) throw ProtocolError($"lunghezza dell'elenco inattesa ({consumed} invece di {tot})");
            return list;
        }, ct);

        public Task MkdirAsync(string wirePath, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(MKDIR_REQ), Str(wirePath)).ConfigureAwait(false);
            await ExpectAsync(MKDIR_REP, ct).ConfigureAwait(false);
            return 0;
        }, ct);

        /// <summary>cancella un file o una cartella VUOTA (per le cartelle piene vedi FileTransferManager.DeleteAsync)</summary>
        public Task DeleteAsync(string wirePath, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(DEL_REQ), Str(wirePath)).ConfigureAwait(false);
            await ExpectAsync(DEL_REP, ct).ConfigureAwait(false);
            return 0;
        }, ct);

        public Task RenameAsync(string wireOld, string wireNew, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(REN_REQ), Str(wireOld), Str(wireNew)).ConfigureAwait(false);
            await ExpectAsync(REN_REP, ct).ConfigureAwait(false);
            return 0;
        }, ct);

        /// <summary>
        /// Carica un file. Se il server rifiuta in corso d'opera, il file parziale resta sul PC remoto (lo cancella chi chiama).
        /// <paramref name="onBytes"/> riceve i byte appena confermati dal server.
        /// </summary>
        public Task UploadAsync(string wirePath, Stream source, DateTime modifiedUtc, Action<long>? onBytes, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(UP_REQ), Str(wirePath), new byte[] { 1 }, new byte[8]).ConfigureAwait(false);
            await ExpectAsync(UP_REP, ct).ConfigureAwait(false);
            var buf = ArrayPool<byte>.Shared.Rent(UploadChunkSize);
            try
            {
                while (true)
                {
                    var n = await ReadFullAsync(source, buf, UploadChunkSize, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    await SendAsync(ct, U32(UP_DATA), new byte[] { 0 }, U32((uint)n), U32((uint)n), new ReadOnlyMemory<byte>(buf, 0, n)).ConfigureAwait(false);
                    await ExpectAsync(UP_DATA_REP, ct).ConfigureAwait(false);
                    onBytes?.Invoke(n);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            await SendAsync(ct, U32(UP_END), new byte[2], I64(ToUnixMs(modifiedUtc))).ConfigureAwait(false);
            await ExpectAsync(UP_END_REP, ct).ConfigureAwait(false);
            return 0;
        }, ct);

        /// <summary>
        /// Scarica un file scrivendolo in <paramref name="destination"/>; restituisce la data di modifica del file remoto.
        /// <paramref name="pipelineDepth"/> richieste di blocco restano in volo insieme (con 1 il server e' il collo di bottiglia).
        /// </summary>
        public Task<DateTime> DownloadAsync(string wirePath, Stream destination, Action<long>? onBytes, int pipelineDepth = 4, CancellationToken ct = default) => RunAsync(async () =>
        {
            if (pipelineDepth < 1) pipelineDepth = 1;
            await SendAsync(ct, U32(DN_REQ), Str(wirePath), new byte[8]).ConfigureAwait(false);
            await ExpectAsync(DN_REP, ct).ConfigureAwait(false);
            var outstanding = 0;
            var ended = false;
            var modified = DateTime.MinValue;
            while (true)
            {
                while (!ended && outstanding < pipelineDepth)
                {
                    await SendAsync(ct, U32(DN_DATA_REQ), new byte[] { 0 }, U32((uint)DownloadChunkSize)).ConfigureAwait(false);
                    outstanding++;
                }
                if (outstanding == 0) break;
                var code = await ReadCodeAsync(ct).ConfigureAwait(false);
                outstanding--;
                if (code == DN_DATA)
                {
                    var h = await ReadAsync(9, ct).ConfigureAwait(false);
                    var comp = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(1));
                    var real = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(5));
                    if (comp != real) throw new FileTransferException(FtErrorKind.NotSupported, "Dati compressi dal server: non gestiti");
                    if (real > 16 * 1024 * 1024) throw ProtocolError("blocco troppo grande");
                    var buf = ArrayPool<byte>.Shared.Rent((int)real);
                    try
                    {
                        await ReadIntoAsync(buf, (int)real, ct).ConfigureAwait(false);
                        await destination.WriteAsync(new ReadOnlyMemory<byte>(buf, 0, (int)real), ct).ConfigureAwait(false);
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }
                    onBytes?.Invoke(real);
                }
                else if (code == DN_END)
                {
                    var e = await ReadAsync(9, ct).ConfigureAwait(false);
                    if (!ended) modified = ToUtc(BinaryPrimitives.ReadInt64BigEndian(e.AsSpan(1)));
                    ended = true;
                }
                else if (code == ERROR_REP)
                {
                    var ex = await ReadServerErrorAsync(ct).ConfigureAwait(false);
                    // le richieste in anticipo oltre la fine ricevono "No active download": si scartano; un errore vero interrompe
                    if (!ended) { IsBroken = outstanding > 0; throw ex; }
                }
                else throw ProtocolError($"risposta allo scarico 0x{code:x8}");
            }
            return modified;
        }, ct);

        #endregion

        #region basso livello

        private async Task<T> RunAsync<T>(Func<Task<T>> op, CancellationToken ct)
        {
            if (_disposed || IsBroken) throw new FileTransferException(FtErrorKind.Disconnected, "Connessione chiusa");
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || IsBroken) throw new FileTransferException(FtErrorKind.Disconnected, "Connessione chiusa");
                return await op().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { IsBroken = true; throw; }
            finally { _gate.Release(); }
        }

        private FileTransferException ProtocolError(string what)
        {
            IsBroken = true;
            return new FileTransferException(FtErrorKind.Protocol, "Risposta inattesa dal server: " + what);
        }

        /// <summary>
        /// Codice del prossimo messaggio del trasferimento file. Il server manda a TUTTI i client collegati anche i messaggi normali del protocollo VNC:
        /// "appunti copiati sul PC remoto" (tipo 3, con il testo) e il campanello (tipo 2). Qui si leggono e si scartano: prima si scambiavano
        /// per una risposta e l'elenco falliva ("risposta inattesa 0x03000000") ogni volta che sul PC remoto si copiava qualcosa.
        /// </summary>
        private async Task<uint> ReadCodeAsync(CancellationToken ct)
        {
            while (true)
            {
                var t = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
                if (t == 0xFC)
                {
                    var rest = await ReadAsync(3, ct).ConfigureAwait(false);
                    return 0xFC000000u | ((uint)rest[0] << 16) | ((uint)rest[1] << 8) | rest[2];
                }
                if (t == 3)
                {
                    await ReadAsync(3, ct).ConfigureAwait(false);   // riempimento
                    var len = await ReadU32Async(ct).ConfigureAwait(false);
                    if (len > 16 * 1024 * 1024) throw ProtocolError("testo degli appunti troppo lungo");
                    await ReadAsync((int)len, ct).ConfigureAwait(false);
                    continue;
                }
                if (t == 2) continue;   // campanello: un solo byte
                throw ProtocolError($"messaggio VNC 0x{t:x2} inatteso");
            }
        }

        /// <summary>si aspetta il codice di risposta dato; un 0x119 e' un errore del server (la connessione resta valida)</summary>
        private async Task ExpectAsync(uint expected, CancellationToken ct)
        {
            var code = await ReadCodeAsync(ct).ConfigureAwait(false);
            if (code == expected) return;
            if (code == ERROR_REP) throw await ReadServerErrorAsync(ct).ConfigureAwait(false);
            throw ProtocolError($"atteso 0x{expected:x8}, arrivato 0x{code:x8}");
        }

        private async Task<FileTransferException> ReadServerErrorAsync(CancellationToken ct)
        {
            var len = await ReadU32Async(ct).ConfigureAwait(false);
            if (len > 4096) return ProtocolError("messaggio d'errore troppo lungo");
            var text = Encoding.UTF8.GetString(await ReadAsync((int)len, ct).ConfigureAwait(false)).TrimEnd('\0');
            return FileTransferException.FromServerText(text);
        }

        private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
        private static byte[] I64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
        /// <summary>percorso come lo vuole il server: lunghezza (con il NUL finale) e testo UTF-8 terminato da NUL</summary>
        private static byte[] Str(string path)
        {
            var bytes = Encoding.UTF8.GetBytes(path + "\0");
            var b = new byte[4 + bytes.Length];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)bytes.Length);
            bytes.CopyTo(b, 4);
            return b;
        }
        private static DateTime ToUtc(long ms) => ms <= 0 || ms > 253402300799999 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        private static long ToUnixMs(DateTime utc) => utc == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        private Task SendAsync(CancellationToken ct, params ReadOnlyMemory<byte>[] parts) => WriteAsync(ct, parts);

        private async Task WriteAsync(CancellationToken ct, params ReadOnlyMemory<byte>[] parts)
        {
            var total = 0;
            foreach (var p in parts) total += p.Length;
            var buf = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                var o = 0;
                foreach (var p in parts) { p.CopyTo(buf.AsMemory(o)); o += p.Length; }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(IoTimeoutMs);
                try { await _s.WriteAsync(new ReadOnlyMemory<byte>(buf, 0, total), cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { IsBroken = true; throw new FileTransferException(FtErrorKind.Timeout, "Il server non riceve piu' i dati"); }
                catch (IOException e) { IsBroken = true; throw new FileTransferException(FtErrorKind.Disconnected, e.Message, null, e); }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        private async Task<byte[]> ReadAsync(int n, CancellationToken ct)
        {
            var b = new byte[n];
            await ReadIntoAsync(b, n, ct).ConfigureAwait(false);
            return b;
        }

        private async Task<uint> ReadU32Async(CancellationToken ct) => BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, ct).ConfigureAwait(false));
        private Task WriteU32Async(CancellationToken ct, uint v) => WriteAsync(ct, U32(v));

        private async Task ReadIntoAsync(byte[] buf, int n, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(IoTimeoutMs);
            try
            {
                var o = 0;
                while (o < n)
                {
                    var r = await _s.ReadAsync(buf.AsMemory(o, n - o), cts.Token).ConfigureAwait(false);
                    if (r <= 0) { IsBroken = true; throw new FileTransferException(FtErrorKind.Disconnected, "Il server ha chiuso la connessione"); }
                    o += r;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { IsBroken = true; throw new FileTransferException(FtErrorKind.Timeout, "Il server non risponde"); }
            catch (IOException e) { IsBroken = true; throw new FileTransferException(FtErrorKind.Disconnected, e.Message, null, e); }
        }

        private static async Task<int> ReadFullAsync(Stream s, byte[] buf, int max, CancellationToken ct)
        {
            var o = 0;
            while (o < max)
            {
                var r = await s.ReadAsync(buf.AsMemory(o, max - o), ct).ConfigureAwait(false);
                if (r <= 0) break;
                o += r;
            }
            return o;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsBroken = true;
            try { _tcp.Dispose(); } catch { }
        }

        #endregion
    }
}
