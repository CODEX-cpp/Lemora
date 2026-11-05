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
    /// La lettura ZRLE dormiva 100 ms ogni volta che i dati non erano ancora arrivati: con la latenza di una VPN
    /// lo schermo VNC andava a pochi fotogrammi al secondo anche con pochi Mbit/s. Qui un server di prova manda
    /// rettangoli piccoli con un po' di ritardo e la lettura deve restare reattiva e restituire i dati giusti.
    /// </summary>
    public class VncZrleReadTests
    {
        private static byte[] BuildZrleRawTiles(int w, int h, int[] px)
        {
            // ogni tile 64x64 come "pixel grezzi" (sottocodifica 0), 3 byte per pixel nell'ordine blu, verde, rosso
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

            // un unico flusso zlib continuo per i due rettangoli (come un vero server), un blocco ciascuno
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
                    await SendBlock(block1, 6, 120);   // arriva a pezzi, come su una connessione lenta
                    await SendBlock(block2, 1, 0);     // subito dopo, tutto insieme: serve il flusso allineato
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

                // fasce contigue e complete, la prima molto prima della fine dei dati
                Assert.Equal(5, strips.Count); // 300 righe = 5 fasce di tile da 64
                Assert.Equal(0, strips[0].y0);
                for (var i = 1; i < strips.Count; i++) Assert.Equal(strips[i - 1].y1, strips[i].y0);
                Assert.Equal(h, strips[^1].y1);
                Assert.True(strips[0].ms < total * 0.6, $"la prima fascia e' arrivata a {strips[0].ms} ms su {total} ms: non si disegna man mano che i dati arrivano");

                // i pixel decodificati sono quelli inviati
                for (var i = 0; i < w * h; i++) if (fb[i] != image1[i]) Assert.Fail($"rettangolo 1, pixel {i}: atteso {image1[i]:X8}, letto {fb[i]:X8}");

                // secondo rettangolo: decodifica giusta solo se il primo ha consumato esattamente i suoi byte
                var z2 = new VncSharpCore.Encodings.ZrleRectangle(rfb, fb, new System.Drawing.Rectangle(0, 0, w, h));
                z2.Decode();
                for (var i = 0; i < w * h; i++) if (fb[i] != image2[i]) Assert.Fail($"rettangolo 2, pixel {i}: atteso {image2[i]:X8}, letto {fb[i]:X8}");

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

            // un unico flusso zlib continuo (come fa un vero server ZRLE), tagliato in un blocco per rettangolo
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
                        await Task.Delay(delayMs, TestContext.Current.CancellationToken); // il rettangolo arriva dopo che la lettura e' gia' in attesa
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
                    Assert.True(payloads[i].AsSpan().SequenceEqual(got), $"rettangolo {i}: i byte letti non coincidono con quelli inviati");
                }
                sw.Stop();

                // 20 rettangoli x 15 ms = circa 0,3 s di attesa vera. Con le pause da 100 ms servivano oltre 2 s.
                Assert.True(sw.ElapsedMilliseconds < 1200, $"lettura troppo lenta: {sw.ElapsedMilliseconds} ms per {rectangles} rettangoli");
                await server;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
