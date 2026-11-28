using System.Drawing;
using System.Linq;
using Lemora.Service.Mcp;
using Xunit;

namespace Tests.Service
{
    /// <summary>searching for text in the lines read by OCR: equal, contained, nearly equal (a few misread letters)</summary>
    public class McpOcrFindTests
    {
        private static McpOcr.Line L(int y, params string[] words)
        {
            var ws = words.Select((w, i) => new McpOcr.Word(w, new Rectangle(i * 100, y, 90, 16))).ToList();
            return new McpOcr.Line(string.Join(" ", words), ws.Select(w => w.Box).Aggregate(Rectangle.Union), ws);
        }

        [Fact]
        public void ExactWins_OverNearlyEqual()
        {
            var lines = new[] { L(0, "VALKOPIA"), L(20, "VALKORIA") };
            var f = McpOcr.Find(lines, "valkoria");
            Assert.Single(f);
            Assert.False(f[0].approx);
            Assert.Equal(20, f[0].box.Y);
        }

        [Fact]
        public void NearlyEqual_FoundWithWhatWasRead()
        {
            var f = McpOcr.Find(new[] { L(0, "PS", "C:NUsersNAdninistrator>") }, @"C:\Users\Administrator>");
            Assert.Single(f);
            Assert.True(f[0].approx);
            Assert.Equal("C:NUsersNAdninistrator>", f[0].text);
            Assert.Single(McpOcr.Find(new[] { L(0, "domain", "VALKOPIA") }, "VALKORIA"));
        }

        [Fact]
        public void ShortOrDifferentWords_AreNotNearlyEqual()
        {
            Assert.Empty(McpOcr.Find(new[] { L(0, "Saved") }, "Saves"));            // under 6 characters no errors allowed
            Assert.Empty(McpOcr.Find(new[] { L(0, "Connect") }, "Connected"));      // 2 errors out of 9: too many
            Assert.Single(McpOcr.Find(new[] { L(0, "PS", "C:", @"\Users") }, @"PS C:\Users"));   // only the spaces differ
        }

        [Fact]
        public void Click_OnlyFewestErrors_Wait_CountsEveryLine()
        {
            var lines = new[] { L(0, "Administrator"), L(20, "Adninistrator"), L(40, "Adninistratox") };
            var click = McpOcr.Find(lines, "Administrator");
            Assert.Single(click);   // there is an exact match: the nearly equal ones do not count
            Assert.Equal(3, McpOcr.Find(lines, "Administrator", all: true).Count);
            Assert.Single(McpOcr.Find(lines.Skip(1).ToArray(), "Administrator"));   // among the nearly equal ones, the one with the fewest errors
        }
    }
}
