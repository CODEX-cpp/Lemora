using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace VncSharpCore
{
    /// <summary>
    /// NetworkStream that counts the bytes received. Used for diagnostics: it tells how much data the server really sends
    /// and therefore whether the screen is slowed down by the connection or by the program's work.
    /// </summary>
    internal sealed class CountingNetworkStream : NetworkStream
    {
        private long _bytes;

        public long Bytes => Interlocked.Read(ref _bytes);

        public CountingNetworkStream(Socket socket, bool ownsSocket) : base(socket, FileAccess.ReadWrite, ownsSocket)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            if (n > 0) Interlocked.Add(ref _bytes, n);
            return n;
        }

#if NET5_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            var n = base.Read(buffer);
            if (n > 0) Interlocked.Add(ref _bytes, n);
            return n;
        }
#endif
    }
}
