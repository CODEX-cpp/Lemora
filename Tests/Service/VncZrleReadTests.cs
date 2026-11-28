using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VncSharpCore;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// The ZRLE reader slept 100 ms whenever the data had not arrived yet: with VPN latency the VNC screen
    /// ran at a few frames per second even with a few Mbit/s. Here a test server sends small rectangles
    /// with some delay, and the reader must stay responsive and return the right data.
    /// </summary>
    public class VncZrleReadTests
    {
        private static byte[] BuildZrleRawTiles(int w, int h, int[] px)
        {
            // each 64x64 tile as "raw pixels" (subencoding 0), 3 bytes per pixel in blue, green, red order
            var ms = new MemoryStream();
            for (var ty = 0; ty < h; ty += 64)
                for (var tx = 0; tx < w; tx += 64)
                {
                    ms.WriteByte(0);
                    for (var y = ty; y < Math.Min(h, ty + 64); y++)
                        for (var x = tx; x < Math.Min(w, tx + 64); x++)
                        {
                            var p = px[y * w + x];
                            ms.WriteByte((byte)p); ms.WriteByte((byte)(p >> 8)); ms.WriteByte((byte)(p >> 16));
                        }
                }
            return ms.ToArray();
        }

        [Fact]
        public async Task ZrleRectangle_MustDrawInStripsWhileDataArrives_AndKeepTheStreamAligned()
        {
            const int w = 200, h = 300;
            var rnd = new Random(7);
            int[] NewImage() { var a = new int[w * h]; for (var i = 0; i < a.Length; i++) a[i] = unchecked((int)0xFF000000) | rnd.Next(0x1000000); return a; }
            var image1 = NewImage();
            var image2 = NewImage();

            // a single continuous zlib stream for both rectangles (like a real server), one block each
            var wire = new MemoryStream();
            var zl = new ZLibStream(wire, CompressionLevel.Fastest, leaveOpen: true);
            byte[] Block(int[] image)
            {
                var start = wire.Position;
                var data = BuildZrleRawTiles(w, h, image);
                zl.Write(data, 0, data.Length);
                zl.Flush();
                var blk = new byte[wire.Position - start];
                wire.Position = start;
                wire.ReadExactly(blk, 0, blk.Length);
                return blk;
            }
            var block1 = Block(image1);
            var block2 = Block(image2);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var s = await listener.AcceptTcpClientAsync();
                    var ns = s.GetStream();
                    async Task SendBlock(byte[] blk, int parts, int delayMs)
                    {
                        var len = blk.Length;
                        await ns.WriteAsync(new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len }, TestContext.Current.CancellationToken);
                        var step = (blk.Length + parts - 1) / parts;
                        for (var o = 0; o < blk.Length; o += step)
                        {
                            await ns.WriteAsync(blk.AsMemory(o, Math.Min(step, blk.Length - o)), TestContext.Current.CancellationToken);
                            await ns.FlushAsync(TestContext.Current.CancellationToken);
                            if (delayMs > 0) await Task.Delay(delayMs, TestContext.Current.CancellationToken);
                        }
                    }
                    await SendBlock(block1, 6, 120);   // arrives in pieces, as on a slow connection
                    await SendBlock(block2, 1, 0);     // right after, all at once: needs the stream aligned
                    await Task.Delay(300, TestContext.Current.CancellationToken);
                }, TestContext.Current.CancellationToken);

                var rfb = new RfbProtocol();
                rfb.Connect("127.0.0.1", port);
                var format = new byte[] { 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 };
                var fb = Framebuffer.FromPixelFormat(format, w, h, 0, 0);

                var z1 = new VncSharpCore.Encodings.ZrleRectangle(rfb, fb, new System.Drawing.Rectangle(0, 0, w, h));
                var strips = new System.Collections.Generic.List<(long ms, int y0, int y1)>();
                var sw = Stopwatch.StartNew();
                z1.RowsDecoded = (a, b) => strips.Add((sw.ElapsedMilliseconds, a, b));
                z1.Decode();
                var total = sw.ElapsedMilliseconds;

                // contiguous, complete strips, the first well before the end of the data
                Assert.Equal(5, strips.Count); // 300 rows = 5 strips of 64-pixel tiles
                Assert.Equal(0, strips[0].y0);
                for (var i = 1; i < strips.Count; i++) Assert.Equal(strips[i - 1].y1, strips[i].y0);
                Assert.Equal(h, strips[^1].y1);
                Assert.True(strips[0].ms < total * 0.6, $"the first strip arrived at {strips[0].ms} ms of {total} ms: it does not draw as the data arrives");

                // the decoded pixels are the ones sent
                for (var i = 0; i < w * h; i++) if (fb[i] != image1[i]) Assert.Fail($"rectangle 1, pixel {i}: expected {image1[i]:X8}, read {fb[i]:X8}");

                // second rectangle: decodes correctly only if the first consumed exactly its own bytes
                var z2 = new VncSharpCore.Encodings.ZrleRectangle(rfb, fb, new System.Drawing.Rectangle(0, 0, w, h));
                z2.Decode();
                for (var i = 0; i < w * h; i++) if (fb[i] != image2[i]) Assert.Fail($"rectangle 2, pixel {i}: expected {image2[i]:X8}, read {fb[i]:X8}");

                rfb.Close();
                await server;
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task ZrleReader_MustWakeUpAsSoonAsDataArrives_AndReturnTheRightBytes()
        {
            const int rectangles = 20;
            const int delayMs = 15;
            var rnd = new Random(1234);
            var payloads = Enumerable.Range(0, rectangles).Select(_ => { var b = new byte[3000]; rnd.NextBytes(b); return b; }).ToArray();

            // a single continuous zlib stream (as a real ZRLE server does), cut into one block per rectangle
            var wire = new MemoryStream();
            var zlib = new ZLibStream(wire, CompressionLevel.Fastest, leaveOpen: true);
            var blocks = new byte[rectangles][];
            for (var i = 0; i < rectangles; i++)
            {
                var start = wire.Position;
                zlib.Write(payloads[i], 0, payloads[i].Length);
                zlib.Flush();
                var end = wire.Position;
                wire.Position = start;
                blocks[i] = new byte[end - start];
                wire.ReadExactly(blocks[i], 0, blocks[i].Length);
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var s = await listener.AcceptTcpClientAsync();
                    var ns = s.GetStream();
                    foreach (var block in blocks)
                    {
                        await Task.Delay(delayMs, TestContext.Current.CancellationToken); // the rectangle arrives after the reader is already waiting
                        var len = block.Length;
                        await ns.WriteAsync(new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len }, TestContext.Current.CancellationToken);
                        await ns.WriteAsync(block, TestContext.Current.CancellationToken);
                        await ns.FlushAsync(TestContext.Current.CancellationToken);
                    }
                    await Task.Delay(200, TestContext.Current.CancellationToken);
                }, TestContext.Current.CancellationToken);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
                var reader = new RfbProtocol.ZRLECompressedReader(client.GetStream());

                var sw = Stopwatch.StartNew();
                for (var i = 0; i < rectangles; i++)
                {
                    reader.DecodeStream();
                    var got = reader.ReadBytes(payloads[i].Length);
                    Assert.True(payloads[i].AsSpan().SequenceEqual(got), $"rectangle {i}: the bytes read do not match the ones sent");
                }
                sw.Stop();

                // 20 rectangles x 15 ms = about 0.3 s of real waiting. With the 100 ms sleeps it took over 2 s.
                Assert.True(sw.ElapsedMilliseconds < 1200, $"reading too slow: {sw.ElapsedMilliseconds} ms for {rectangles} rectangles");
                await server;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
