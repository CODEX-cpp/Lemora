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
    /// A file transfer connection to a TightVNC 2.x server (same port as the screen, but a separate connection: as the
    /// official Viewer does). Protocol derived from real traffic: see the "tightvnc-file-transfer-protocol" note in the project memory.
    /// All operations are one request at a time (serialized); after a timeout or a cancellation the connection is "broken"
    /// (IsBroken) and must be closed: the stream state is no longer known.
    /// </summary>
    public sealed class TightFtConnection : IDisposable
    {
        // message codes (0xFC000100 + n)
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
        /// <summary>true after a timeout, a cancellation or a network error: the connection is no longer usable</summary>
        public bool IsBroken { get; private set; }
        /// <summary>maximum wait for each read from the server</summary>
        public int IoTimeoutMs { get; set; } = 20000;
        /// <summary>bytes per upload block (the official Viewer uses 40000, 80000, 160000...)</summary>
        public int UploadChunkSize { get; set; } = 160000;
        /// <summary>bytes requested for each download block</summary>
        public int DownloadChunkSize { get; set; } = 262144;
        public DateTime OpenedAtUtc { get; } = DateTime.UtcNow;

        private TightFtConnection(TcpClient tcp, string host, int port)
        {
            _tcp = tcp; _s = tcp.GetStream(); Host = host; Port = port;
        }

        #region connecting

        /// <summary>
        /// Opens the connection and authenticates with the VNC password (as for the screen). Exceptions: NotSupported (server not TightVNC or file transfer
        /// disabled), AuthFailed (password rejected), Timeout, Disconnected.
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
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new FileTransferException(FtErrorKind.Timeout, IoC.Translate("ft_err_host_no_response", host, port)); }
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
            if (ver[0] != (byte)'R' || ver[1] != (byte)'F' || ver[2] != (byte)'B') throw new FileTransferException(FtErrorKind.NotSupported, IoC.Translate("ft_err_not_vnc"));
            await WriteAsync(ct, Encoding.ASCII.GetBytes("RFB 003.008\n")).ConfigureAwait(false);

            var nSec = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
            if (nSec == 0)
            {
                var len = await ReadU32Async(ct).ConfigureAwait(false);
                var reason = len is > 0 and < 1024 ? Encoding.UTF8.GetString(await ReadAsync((int)len, ct).ConfigureAwait(false)) : "";
                throw new FileTransferException(FtErrorKind.Disconnected, IoC.Translate("ft_err_refused") + (reason.Length > 0 ? ": " + reason : ""));
            }
            var secTypes = await ReadAsync(nSec, ct).ConfigureAwait(false);
            if (Array.IndexOf(secTypes, (byte)16) < 0)
                throw new FileTransferException(FtErrorKind.NotSupported, IoC.Translate("ft_err_not_tight"));
            await WriteAsync(ct, new byte[] { 16 }).ConfigureAwait(false);

            // tunnel: chosen only if the server offers any (it does not); then the authentication types
            var nTun = await ReadU32Async(ct).ConfigureAwait(false);
            if (nTun > 64) throw ProtocolError("tunnel list");
            if (nTun > 0) { await ReadAsync((int)nTun * 16, ct).ConfigureAwait(false); await WriteU32Async(ct, 0).ConfigureAwait(false); }
            var nAuth = await ReadU32Async(ct).ConfigureAwait(false);
            if (nAuth is 0 or > 64) throw ProtocolError("authentication list");
            var authCaps = await ReadAsync((int)nAuth * 16, ct).ConfigureAwait(false);
            var hasVncAuth = false;
            for (var i = 0; i < nAuth; i++) if (BinaryPrimitives.ReadUInt32BigEndian(authCaps.AsSpan(i * 16, 4)) == 2) hasVncAuth = true;
            if (!hasVncAuth) throw new FileTransferException(FtErrorKind.NotSupported, IoC.Translate("ft_err_no_vnc_auth"));
            await WriteU32Async(ct, 2).ConfigureAwait(false);

            var challenge = await ReadAsync(16, ct).ConfigureAwait(false);
            await WriteAsync(ct, EncryptChallenge(challenge, password)).ConfigureAwait(false);
            var result = await ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0) throw new FileTransferException(FtErrorKind.AuthFailed, IoC.Translate("ft_err_password_rejected"));

            await WriteAsync(ct, new byte[] { 1 }).ConfigureAwait(false);   // ClientInit: shared session (does not disconnect whoever is watching the screen)
            await ReadAsync(20, ct).ConfigureAwait(false);                  // ServerInit: size and pixel format (not needed)
            var nameLen = await ReadU32Async(ct).ConfigureAwait(false);
            if (nameLen > 4096) throw ProtocolError("desktop name");
            await ReadAsync((int)nameLen, ct).ConfigureAwait(false);

            // Tight header: number of server messages, client messages and encodings, then 16 bytes for each
            var hdr = await ReadAsync(8, ct).ConfigureAwait(false);
            int nServer = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(0)), nClient = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2)), nEnc = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
            if (nServer + nClient + nEnc > 512) throw ProtocolError("capability list");
            var caps = await ReadAsync((nServer + nClient + nEnc) * 16, ct).ConfigureAwait(false);
            var hasFt = false;
            for (var i = 0; i < nServer; i++) if (BinaryPrimitives.ReadUInt32BigEndian(caps.AsSpan(i * 16, 4)) == LIST_REP) hasFt = true;
            // FileTransferViewModel compares the message with this key to tell "disabled" from "not TightVNC"
            if (!hasFt) throw new FileTransferException(FtErrorKind.NotSupported, IoC.Translate("ft_err_ft_disabled"));
        }

        /// <summary>response to the VNC challenge: DES with the password (at most 8 characters) with the bits of each byte reversed</summary>
        private static byte[] EncryptChallenge(byte[] challenge, string password)
        {
            if (string.IsNullOrEmpty(password)) throw new FileTransferException(FtErrorKind.AuthFailed, IoC.Translate("ft_err_password_missing"));
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
            catch (CryptographicException e) { throw new FileTransferException(FtErrorKind.AuthFailed, IoC.Translate("ft_err_password_unusable"), null, e); }
        }

        #endregion

        #region operations

        /// <summary>contents of a folder; "/" is the list of drives. The path is in the server format (/C:/Users).</summary>
        public Task<List<RemoteEntry>> ListAsync(string wireDir, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(LIST_REQ), new byte[] { 0 }, Str(wireDir)).ConfigureAwait(false);
            var code = await ReadCodeAsync(ct).ConfigureAwait(false);
            if (code == ERROR_REP) throw await ReadServerErrorAsync(ct).ConfigureAwait(false);
            if (code != LIST_REP) throw ProtocolError($"reply to the listing 0x{code:x8}");
            await ReadAsync(1, ct).ConfigureAwait(false);   // flags
            var tot = await ReadU32Async(ct).ConfigureAwait(false);
            var tot2 = await ReadU32Async(ct).ConfigureAwait(false);
            if (tot != tot2) throw new FileTransferException(FtErrorKind.NotSupported, IoC.Translate("ft_err_compressed_list"));
            if (tot > 64 * 1024 * 1024) throw ProtocolError("listing too large");
            var count = await ReadU32Async(ct).ConfigureAwait(false);
            // each item takes at least 22 bytes: the declared count cannot exceed what fits in the length (security hardening)
            if (count > (tot - 4) / 22) throw ProtocolError("item count inconsistent with the listing length");
            var list = new List<RemoteEntry>((int)Math.Min(count, 4096));
            long consumed = 4;
            for (var i = 0; i < count; i++)
            {
                var fixedPart = await ReadAsync(8 + 8 + 2 + 4, ct).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadInt64BigEndian(fixedPart.AsSpan(0));
                var ms = BinaryPrimitives.ReadInt64BigEndian(fixedPart.AsSpan(8));
                var flags = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(16));
                var nl = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(18));
                if (nl > 4096) throw ProtocolError("name too long");
                if (consumed + 22 + nl > tot) throw ProtocolError("listing longer than the declared length");
                var name = Encoding.UTF8.GetString(await ReadAsync((int)nl, ct).ConfigureAwait(false)).TrimEnd('\0');
                consumed += 22 + nl;
                list.Add(new RemoteEntry(wireDir, name, (flags & 1) != 0, (flags & 1) != 0 ? 0 : Math.Max(0, size), ToUtc(ms)));
            }
            if (consumed != tot) throw ProtocolError($"unexpected listing length ({consumed} instead of {tot})");
            return list;
        }, ct);

        public Task MkdirAsync(string wirePath, CancellationToken ct = default) => RunAsync(async () =>
        {
            await SendAsync(ct, U32(MKDIR_REQ), Str(wirePath)).ConfigureAwait(false);
            await ExpectAsync(MKDIR_REP, ct).ConfigureAwait(false);
            return 0;
        }, ct);

        /// <summary>deletes a file or an EMPTY folder (for non-empty folders see FileTransferManager.DeleteAsync)</summary>
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
        /// Uploads a file. If the server refuses midway, the partial file stays on the remote PC (the caller deletes it).
        /// <paramref name="onBytes"/> receives the bytes just confirmed by the server.
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
        /// Downloads a file writing it into <paramref name="destination"/>; returns the modification date of the remote file.
        /// <paramref name="pipelineDepth"/> block requests stay in flight together (with 1 the server is the bottleneck).
        /// </summary>
        /// <param name="maxBytes">cap on the bytes to write (usually the size declared in the listing): beyond it, the download stops.
        /// Without it, a hostile server declares 1 KB (passes the limits) and then sends data forever, filling the disk (security review).</param>
        public Task<DateTime> DownloadAsync(string wirePath, Stream destination, Action<long>? onBytes, int pipelineDepth = 4, CancellationToken ct = default, long maxBytes = long.MaxValue) => RunAsync(async () =>
        {
            if (pipelineDepth < 1) pipelineDepth = 1;
            await SendAsync(ct, U32(DN_REQ), Str(wirePath), new byte[8]).ConfigureAwait(false);
            await ExpectAsync(DN_REP, ct).ConfigureAwait(false);
            var outstanding = 0;
            var ended = false;
            long total = 0;
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
                    if (comp != real) throw new FileTransferException(FtErrorKind.NotSupported, IoC.Translate("ft_err_compressed_data"));
                    if (real > (uint)DownloadChunkSize) throw ProtocolError("block bigger than requested");
                    total += real;
                    if (total > maxBytes) throw ProtocolError("the server sends more data than the declared size");
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
                    // requests sent ahead past the end get "No active download": they are discarded; a real error stops the download
                    if (!ended) { IsBroken = outstanding > 0; throw ex; }
                }
                else throw ProtocolError($"reply to the download 0x{code:x8}");
            }
            return modified;
        }, ct);

        #endregion

        #region low level

        private async Task<T> RunAsync<T>(Func<Task<T>> op, CancellationToken ct)
        {
            if (_disposed || IsBroken) throw new FileTransferException(FtErrorKind.Disconnected, IoC.Translate("ft_err_connection_closed"));
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || IsBroken) throw new FileTransferException(FtErrorKind.Disconnected, IoC.Translate("ft_err_connection_closed"));
                return await op().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { IsBroken = true; throw; }
            finally { _gate.Release(); }
        }

        private FileTransferException ProtocolError(string what)
        {
            IsBroken = true;
            return new FileTransferException(FtErrorKind.Protocol, IoC.Translate("ft_err_protocol", what));
        }

        /// <summary>
        /// Code of the next file transfer message. The server also sends the normal VNC protocol messages to ALL connected clients:
        /// "clipboard copied on the remote PC" (type 3, with the text) and the bell (type 2). Here they are read and discarded: they used to be mistaken
        /// for a reply and the listing failed ("unexpected reply 0x03000000") every time something was copied on the remote PC.
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
                    await ReadAsync(3, ct).ConfigureAwait(false);   // padding
                    var len = await ReadU32Async(ct).ConfigureAwait(false);
                    if (len > 16 * 1024 * 1024) throw ProtocolError("clipboard text too long");
                    await ReadAsync((int)len, ct).ConfigureAwait(false);
                    continue;
                }
                if (t == 2) continue;   // bell: a single byte
                throw ProtocolError($"unexpected VNC message 0x{t:x2}");
            }
        }

        /// <summary>expects the given reply code; a 0x119 is a server error (the connection stays valid)</summary>
        private async Task ExpectAsync(uint expected, CancellationToken ct)
        {
            var code = await ReadCodeAsync(ct).ConfigureAwait(false);
            if (code == expected) return;
            if (code == ERROR_REP) throw await ReadServerErrorAsync(ct).ConfigureAwait(false);
            throw ProtocolError($"expected 0x{expected:x8}, got 0x{code:x8}");
        }

        private async Task<FileTransferException> ReadServerErrorAsync(CancellationToken ct)
        {
            var len = await ReadU32Async(ct).ConfigureAwait(false);
            if (len > 4096) return ProtocolError("error message too long");
            var text = Encoding.UTF8.GetString(await ReadAsync((int)len, ct).ConfigureAwait(false)).TrimEnd('\0');
            return FileTransferException.FromServerText(text);
        }

        private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
        private static byte[] I64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
        /// <summary>path as the server wants it: length (including the trailing NUL) and NUL-terminated UTF-8 text</summary>
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
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { IsBroken = true; throw new FileTransferException(FtErrorKind.Timeout, IoC.Translate("ft_err_no_longer_receives")); }
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
                    if (r <= 0) { IsBroken = true; throw new FileTransferException(FtErrorKind.Disconnected, IoC.Translate("ft_err_server_closed")); }
                    o += r;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { IsBroken = true; throw new FileTransferException(FtErrorKind.Timeout, IoC.Translate("ft_err_no_response")); }
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
