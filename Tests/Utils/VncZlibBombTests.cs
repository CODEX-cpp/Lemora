using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using VncSharpCore;
using VncSharpCore.Encodings;
using Xunit;

namespace Tests.Utils
{
    /// <summary>a hostile VNC server sends a few compressed KB that become hundreds of MB ("zlib bomb")</summary>
    public class VncZlibBombTests
    {
        // 200 MB of zeros compressed with zlib: about 200 KB
        private static byte[] Bomb(int mb = 200)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var zeros = new byte[1 << 20];
                for (var i = 0; i < mb; i++) z.Write(zeros);
            }
            return ms.ToArray();
        }

        [Fact]
        public void TightStopsAtTheRectangleSize()
        {
            var bomb = Bomb();
            Assert.True(bomb.Length < 2 * 1024 * 1024);
            var before = GC.GetTotalAllocatedBytes(true);
            Assert.Throws<VncProtocolException>(() => new TightState().Inflate(0, bomb, 64 * 64 * 3));
            var allocatedMb = (GC.GetTotalAllocatedBytes(true) - before) / (1024 * 1024);
            Assert.True(allocatedMb < 50, $"allocated {allocatedMb} MB for a 12 KB rectangle");
        }

        [Fact]
        public void ZrleStopsAtTheRectangleSize()
        {
            var bomb = Bomb();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient();
                client.Connect((IPEndPoint)listener.LocalEndpoint);
                using var server = listener.AcceptTcpClient();
                var len = new[] { (byte)(bomb.Length >> 24), (byte)(bomb.Length >> 16), (byte)(bomb.Length >> 8), (byte)bomb.Length };
                var send = System.Threading.Tasks.Task.Run(() => { try { var s = server.GetStream(); s.Write(len); s.Write(bomb); } catch { /* the client closes */ } });

                var reader = new RfbProtocol.ZRLECompressedReader(client.GetStream());
                reader.DecodeStream(64 * 64 * 5 + 600 + 65536);   // like ZrleRectangle for a 64x64 tile
                var before = GC.GetTotalAllocatedBytes(true);
                Assert.Throws<VncProtocolException>((Action)(() => { while (true) reader.ReadBytes(64 * 1024); }));
                var allocatedMb = (GC.GetTotalAllocatedBytes(true) - before) / (1024 * 1024);
                Assert.True(allocatedMb < 50, $"allocated {allocatedMb} MB for a 64x64 rectangle");
            }
            finally { listener.Stop(); }
        }
    }
}
