using System;
using System.Drawing;
using Lemora.Service.Mcp;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// attendi ("screen still") and clic elemento=N ("screen changed") compare screens: small but real differences must count
    /// (before 26/11 a 96x54 thumbnail saw a duplicated row, a missing text and a list scrolled by one row as "equal")
    /// </summary>
    public class McpScreenCompareTests
    {
        private static readonly string[] Rows = { "Desktop", "Download", "Documenti", "Immagini", "Musica", "Video", "Questo PC", "Rete" };

        /// <summary>an Explorer-like list: one row of text every 32 px, starting from row <paramref name="first"/></summary>
        private static Bitmap List(int first = 0, Action<Graphics>? extra = null)
        {
            var b = new Bitmap(1280, 800);
            using var g = Graphics.FromImage(b);
            g.Clear(Color.White);
            using var font = new Font("Segoe UI", 10);
            for (var i = 0; i < 6; i++) g.DrawString(Rows[(first + i) % Rows.Length], font, Brushes.Black, 300, 450 + i * 32);
            extra?.Invoke(g);
            return b;
        }

        private static bool Same(Bitmap a, Bitmap b) => McpTools.SameScreen(McpTools.Signature(a), McpTools.Signature(b));

        [Fact]
        public void SameScreen_IsEqual()
        {
            using var a = List(); using var b = List();
            Assert.True(Same(a, b));
        }

        [Fact]
        public void BlinkingCursor_IsEqual()
        {
            using var a = List(); using var b = List(extra: g => g.FillRectangle(Brushes.Black, 700, 300, 2, 18));
            Assert.True(Same(a, b));
        }

        [Fact]
        public void ListScrolledByOneRow_IsDifferent()
        {
            using var a = List(); using var b = List(first: 1);
            Assert.False(Same(a, b));
        }

        [Fact]
        public void DuplicatedRow_IsDifferent()
        {
            using var a = List();
            using var b = List(extra: g => { g.FillRectangle(Brushes.White, 300, 514, 200, 24); g.DrawString("Download", new Font("Segoe UI", 10), Brushes.Black, 300, 514); });
            Assert.False(Same(a, b));
        }

        [Fact]
        public void MissingText_IsDifferent()
        {
            using var a = List(); using var b = List(extra: g => g.FillRectangle(Brushes.White, 300, 546, 200, 24));
            Assert.False(Same(a, b));
        }

        [Fact]
        public void OtherSize_IsDifferent()
        {
            using var a = List(); using var b = new Bitmap(1024, 1000);
            Assert.False(Same(a, b));
        }
    }
}
