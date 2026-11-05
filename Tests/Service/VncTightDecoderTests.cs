using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using VncSharpCore;
using VncSharpCore.Encodings;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// Decoder Tight: i byte del rettangolo si costruiscono a mano, si mandano su un socket locale e si
    /// controllano i pixel finiti nel framebuffer. Formato pixel: 32 bit, 8 bit per colore, rosso a shift 16 (Tight manda 3 byte R,G,B).
    /// </summary>
    public class VncTightDecoderTests
    {
        private static readonly byte[] Format = { 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 };

        private static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | r << 16 | g << 8 | b;

        private sealed class Harness : IDisposable
        {
            private readonly TcpListener listener = new(IPAddress.Loopback, 0);
            private readonly TcpClient server;
            public readonly RfbProtocol Rfb = new();
            public readonly Framebuffer Fb;

            public Harness(int w, int h)
            {
                listener.Start();
                var accept = listener.AcceptTcpClientAsync();
                Rfb.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
                server = accept.GetAwaiter().GetResult();
                Fb = Framebuffer.FromPixelFormat(Format, w, h, 0, 0);
            }

            public void Send(byte[] bytes) { server.GetStream().Write(bytes, 0, bytes.Length); server.GetStream().Flush(); }

            public void Decode(int w, int h) => new TightRectangle(Rfb, Fb, new Rectangle(0, 0, w, h)).Decode();

            public void Dispose() { try { Rfb.Close(); } catch { } server.Dispose(); listener.Stop(); }
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p);
            return ms.ToArray();
        }

        /// <summary>lunghezza "compact" Tight: 1-3 byte, 7 bit ciascuno, bit alto = ne segue un altro</summary>
        private static byte[] Compact(int len)
        {
            if (len < 0x80) return new[] { (byte)len };
            if (len < 0x4000) return new[] { (byte)(len & 0x7F | 0x80), (byte)(len >> 7) };
            return new[] { (byte)(len & 0x7F | 0x80), (byte)(len >> 7 & 0x7F | 0x80), (byte)(len >> 14) };
        }

        private static byte[] Rgb(params int[] argb)
        {
            var b = new byte[argb.Length * 3];
            for (var i = 0; i < argb.Length; i++) { b[i * 3] = (byte)(argb[i] >> 16); b[i * 3 + 1] = (byte)(argb[i] >> 8); b[i * 3 + 2] = (byte)argb[i]; }
            return b;
        }

        /// <summary>un flusso zlib continuo: ogni Block() restituisce solo i byte nuovi (sync flush), come un server Tight</summary>
        private sealed class ZStream
        {
            private readonly MemoryStream wire = new();
            private readonly ZLibStream z;
            public ZStream() { z = new ZLibStream(wire, CompressionLevel.Fastest, leaveOpen: true); }
            public byte[] Block(byte[] data)
            {
                wire.SetLength(0);
                z.Write(data, 0, data.Length);
                z.Flush();
                return wire.ToArray();
            }
        }

        private static byte[] RandomRgb(int n, int seed)
        {
            var b = new byte[n * 3];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static void AssertPixels(Framebuffer fb, int[] expected)
        {
            for (var i = 0; i < expected.Length; i++)
                if (fb[i] != expected[i]) Assert.Fail($"pixel {i}: atteso {expected[i]:X8}, letto {fb[i]:X8}");
        }

        [Fact]
        public void Fill_PaintsWholeRectangleWithOneColour()
        {
            using var h = new Harness(5, 4);
            h.Send(Concat(new byte[] { 0x80 }, Rgb(Argb(10, 20, 30))));
            h.Decode(5, 4);
            AssertPixels(h.Fb, Enumerable_Repeat(Argb(10, 20, 30), 20));
        }

        private static int[] Enumerable_Repeat(int v, int n) { var a = new int[n]; Array.Fill(a, v); return a; }

        [Fact]
        public void BasicCopy_UncompressedUnder12Bytes()
        {
            // 3x1 pixel = 9 byte: niente zlib, nessuna lunghezza
            var px = new[] { Argb(1, 2, 3), Argb(200, 100, 50), Argb(255, 0, 128) };
            using var h = new Harness(3, 1);
            h.Send(Concat(new byte[] { 0x00 }, Rgb(px)));
            h.Decode(3, 1);
            AssertPixels(h.Fb, px);
        }

        [Fact]
        public void BasicCopy_CompressedWithZlib_LongCompactLength()
        {
            const int w = 40, hh = 30;               // 3600 byte casuali: la lunghezza compressa supera 127 (2 byte) e 16383? no, 2 byte
            var raw = RandomRgb(w * hh, 11);
            var px = new int[w * hh];
            for (var i = 0; i < px.Length; i++) px[i] = Argb(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
            var comp = new ZStream().Block(raw);
            Assert.InRange(comp.Length, 0x80, 0x3FFF);
            using var h = new Harness(w, hh);
            h.Send(Concat(new byte[] { 0x00 }, Compact(comp.Length), comp));
            h.Decode(w, hh);
            AssertPixels(h.Fb, px);
        }

        [Fact]
        public void BasicCopy_CompactLengthThreeBytes()
        {
            const int w = 100, hh = 100;             // 30000 byte casuali: compressi > 16383 -> lunghezza a 3 byte
            var raw = RandomRgb(w * hh, 12);
            var px = new int[w * hh];
            for (var i = 0; i < px.Length; i++) px[i] = Argb(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
            var comp = new ZStream().Block(raw);
            Assert.True(comp.Length > 0x3FFF);
            using var h = new Harness(w, hh);
            h.Send(Concat(new byte[] { 0x00 }, Compact(comp.Length), comp));
            h.Decode(w, hh);
            AssertPixels(h.Fb, px);
        }

        [Fact]
        public void Palette_TwoColours_Bitmap()
        {
            const int w = 10, hh = 8;                // righe da 2 byte (bit alto = pixel a sinistra), 16 byte totali -> compresso
            var c0 = Argb(0, 0, 0);
            var c1 = Argb(250, 240, 230);
            var bits = new bool[w * hh];
            var bmp = new byte[2 * hh];
            var rnd = new Random(5);
            var expected = new int[w * hh];
            for (var y = 0; y < hh; y++)
                for (var x = 0; x < w; x++)
                {
                    var on = rnd.Next(2) == 1;
                    bits[y * w + x] = on;
                    expected[y * w + x] = on ? c1 : c0;
                    if (on) bmp[y * 2 + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            var comp = new ZStream().Block(bmp);
            using var h = new Harness(w, hh);
            // control 0x40 = flusso 0 + filtro esplicito; filtro 1 = palette; n-1 = 1; 2 colori
            h.Send(Concat(new byte[] { 0x40, 0x01, 0x01 }, Rgb(c0, c1), Compact(comp.Length), comp));
            h.Decode(w, hh);
            AssertPixels(h.Fb, expected);
        }

        [Fact]
        public void Gradient_Filter_ReconstructsImage()
        {
            const int w = 16, hh = 16;
            // immagine con sfumatura + rumore; il server manda la differenza dalla previsione sinistra+sopra-sopraSinistra (0..255)
            var rnd = new Random(3);
            var img = new int[w * hh * 3];
            for (var y = 0; y < hh; y++)
                for (var x = 0; x < w; x++)
                    for (var c = 0; c < 3; c++)
                        img[(y * w + x) * 3 + c] = Math.Clamp(x * 12 + y * 5 + c * 20 + rnd.Next(-6, 7), 0, 255);
            var res = new byte[w * hh * 3];
            for (var y = 0; y < hh; y++)
                for (var x = 0; x < w; x++)
                    for (var c = 0; c < 3; c++)
                    {
                        var left = x > 0 ? img[(y * w + x - 1) * 3 + c] : 0;
                        var up = y > 0 ? img[((y - 1) * w + x) * 3 + c] : 0;
                        var ul = x > 0 && y > 0 ? img[((y - 1) * w + x - 1) * 3 + c] : 0;
                        var pred = Math.Clamp(left + up - ul, 0, 255);
                        res[(y * w + x) * 3 + c] = (byte)(img[(y * w + x) * 3 + c] - pred);
                    }
            var expected = new int[w * hh];
            for (var i = 0; i < expected.Length; i++) expected[i] = Argb(img[i * 3], img[i * 3 + 1], img[i * 3 + 2]);
            var comp = new ZStream().Block(res);
            using var h = new Harness(w, hh);
            h.Send(Concat(new byte[] { 0x40, 0x02 }, Compact(comp.Length), comp));   // filtro 2 = gradiente
            h.Decode(w, hh);
            AssertPixels(h.Fb, expected);
        }

        [Fact]
        public void ZlibStream_PersistsBetweenRectangles_WhenResetBitNotSet()
        {
            const int w = 20, hh = 10;
            var z = new ZStream();                   // un solo flusso zlib per entrambi i rettangoli (header zlib solo nel primo)
            var raw1 = RandomRgb(w * hh, 21);
            var raw2 = RandomRgb(w * hh, 22);
            var b1 = z.Block(raw1);
            var b2 = z.Block(raw2);
            int[] Px(byte[] r) { var p = new int[w * hh]; for (var i = 0; i < p.Length; i++) p[i] = Argb(r[i * 3], r[i * 3 + 1], r[i * 3 + 2]); return p; }

            using var h = new Harness(w, hh);
            h.Send(Concat(new byte[] { 0x00 }, Compact(b1.Length), b1, new byte[] { 0x00 }, Compact(b2.Length), b2));
            h.Decode(w, hh);
            AssertPixels(h.Fb, Px(raw1));
            h.Decode(w, hh);
            AssertPixels(h.Fb, Px(raw2));
        }

        [Fact]
        public void ZlibStream_ResetBit_StartsFreshStream()
        {
            const int w = 20, hh = 10;
            var raw1 = RandomRgb(w * hh, 31);
            var raw2 = RandomRgb(w * hh, 32);
            var b1 = new ZStream().Block(raw1);
            var b2 = new ZStream().Block(raw2);      // flusso nuovo con proprio header
            int[] Px(byte[] r) { var p = new int[w * hh]; for (var i = 0; i < p.Length; i++) p[i] = Argb(r[i * 3], r[i * 3 + 1], r[i * 3 + 2]); return p; }

            using var h = new Harness(w, hh);
            h.Send(Concat(new byte[] { 0x00 }, Compact(b1.Length), b1, new byte[] { 0x01 }, Compact(b2.Length), b2)); // bit 0 = azzera il flusso 0
            h.Decode(w, hh);
            h.Decode(w, hh);
            AssertPixels(h.Fb, Px(raw2));
        }
    }
}
