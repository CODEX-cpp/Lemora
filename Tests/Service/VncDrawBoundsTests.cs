using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using VncSharpCore;
using VncSharpCore.Encodings;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// Security review vnc/encodedrectangle-draw-unbounded-rect-native-heap-write: rectangles declared by the server outside the screen must not
    /// write (or read) beyond the desktop bitmap; a legitimate area copy (even overlapping) must stay correct.
    /// </summary>
    public class VncDrawBoundsTests
    {
        private static readonly byte[] Format = { 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 };
        private const int W = 8, H = 8;

        private sealed class Harness : IDisposable
        {
            private readonly TcpListener listener = new(IPAddress.Loopback, 0);
            private readonly TcpClient server;
            public readonly RfbProtocol Rfb = new();
            public readonly Framebuffer Fb = Framebuffer.FromPixelFormat(Format, W, H, 0, 0);
            public Harness()
            {
                listener.Start();
                var accept = listener.AcceptTcpClientAsync();
                Rfb.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
                server = accept.GetAwaiter().GetResult();
            }
            public void Send(params byte[] b) { server.GetStream().Write(b, 0, b.Length); server.GetStream().Flush(); }
            public void Dispose() { try { Rfb.Close(); } catch { } server.Dispose(); listener.Stop(); }
        }

        private static int Px(int x, int y) => unchecked((int)0xFF000000) | (y * 16 + x) << 8 | 0x11;

        private static Bitmap Desktop()
        {
            var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) b.SetPixel(x, y, Color.FromArgb(Px(x, y)));
            return b;
        }

        private static byte[] U16(int sx, int sy) => new[] { (byte)(sx >> 8), (byte)sx, (byte)(sy >> 8), (byte)sy };

        private static void AssertUnchanged(Bitmap b)
        {
            for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) Assert.Equal(Px(x, y), b.GetPixel(x, y).ToArgb());
        }

        [Fact]
        public void CopyRect_Legit_CopiesTheRightPixels()
        {
            using var h = new Harness();
            using var b = Desktop();
            var r = new CopyRectRectangle(h.Rfb, h.Fb, new Rectangle(4, 4, 3, 2));   // 3x2 area at (4,4) copied from (1,0)
            h.Send(U16(1, 0)); r.Decode(); r.Draw(b);
            for (var y = 0; y < 2; y++) for (var x = 0; x < 3; x++) Assert.Equal(Px(1 + x, y), b.GetPixel(4 + x, 4 + y).ToArgb());
            Assert.Equal(Px(3, 3), b.GetPixel(3, 3).ToArgb());   // nothing around it changes
            Assert.Equal(Px(7, 6), b.GetPixel(7, 6).ToArgb());
        }

        [Fact]
        public void CopyRect_Overlapping_Downwards_UsesTheOldPixels()
        {
            using var h = new Harness();
            using var b = Desktop();
            var r = new CopyRectRectangle(h.Rfb, h.Fb, new Rectangle(0, 1, W, 4));   // rows 0-3 moved down by one row (they overlap)
            h.Send(U16(0, 0)); r.Decode(); r.Draw(b);
            for (var y = 0; y < 4; y++) for (var x = 0; x < W; x++) Assert.Equal(Px(x, y), b.GetPixel(x, y + 1).ToArgb());
            Assert.Equal(Px(0, 5), b.GetPixel(0, 5).ToArgb());
        }

        [Fact]
        public void CopyRect_SourceOutsideTheScreen_DoesNothing()
        {
            using var h = new Harness();
            using var b = Desktop();
            var r = new CopyRectRectangle(h.Rfb, h.Fb, new Rectangle(0, 0, 4, 4));
            h.Send(U16(0, 60000)); r.Decode(); r.Draw(b);
            AssertUnchanged(b);
        }

        [Fact]
        public void Raw_RectangleOutsideTheScreen_DoesNotWriteOutsideTheBitmap()
        {
            using var h = new Harness();
            using var b = Desktop();
            var r = new RawRectangle(h.Rfb, h.Fb, new Rectangle(0, 60000, 1, 1));
            h.Send(0x00, 0x00, 0xFF, 0x00); r.Decode(); r.Draw(b);
            AssertUnchanged(b);
        }

        [Fact]
        public void Raw_RectangleCrossingTheEdge_DrawsOnlyTheInsidePart()
        {
            using var h = new Harness();
            using var b = Desktop();
            var r = new RawRectangle(h.Rfb, h.Fb, new Rectangle(6, 6, 4, 4));   // 4x4 from (6,6): only 2x2 inside
            var px = new byte[4 * 16];
            for (var i = 0; i < 16; i++) { px[i * 4] = (byte)i; px[i * 4 + 1] = 0x22; px[i * 4 + 2] = 0x33; px[i * 4 + 3] = 0; }
            h.Send(px); r.Decode(); r.Draw(b);
            for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++)
                Assert.Equal(unchecked((int)0xFF332200) | (y * 4 + x), b.GetPixel(6 + x, 6 + y).ToArgb());
            Assert.Equal(Px(5, 5), b.GetPixel(5, 5).ToArgb());
        }
    }
}
