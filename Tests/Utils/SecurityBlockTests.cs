using System.Linq;
using Lemora.Controls.NoteDisplay;
using Lemora.Service;
using Lemora.Utils.RdpFile;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils.Wpf.Controls;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security fixes: Markdown notes, links, additional .rdp settings, paste into the terminal, "Keep unlocked"</summary>
    public class SecurityBlockTests
    {
        [Theory]
        [InlineData("https://example.com/a", true)]
        [InlineData("http://example.com/a", true)]
        [InlineData("mailto:a@b.it", true)]
        [InlineData("file://server/share/x.exe", false)]
        [InlineData(@"\\server\share\x.exe", false)]
        [InlineData(@"C:\Windows\System32\calc.exe", false)]
        [InlineData("ms-settings:privacy", false)]
        [InlineData("search-ms:query=x&crumb=location:\\\\server", false)]
        public void OnlyWebAndMailLinksOpen(string url, bool expected) => Assert.Equal(expected, HyperlinkHelper.IsSafeToOpen(url));

        [Fact]
        public void NoteImagesFromFileOrHttpAreNotLoaded()
        {
            var md = "Text ![logo](file://10.0.0.5/c$/x.png) and ![b](http://spy.example/p.gif) and ![ok](https://example.com/i.png)\n\n![r][ref]\n\n[ref]: \\\\10.0.0.5\\share\\y.png\n";
            var safe = NoteMarkdown.SafeForDisplay(md);
            Assert.DoesNotContain("file://", safe);
            Assert.DoesNotContain("http://spy", safe);
            Assert.DoesNotContain("10.0.0.5", safe);
            Assert.Contains("https://example.com/i.png", safe);
            Assert.Contains("Text", safe);
            Assert.Equal("no images", NoteMarkdown.SafeForDisplay("no images"));
        }

        [Fact]
        public void RdpAdditionalSettingsCannotChangeAddressOrCredentials()
        {
            var cfg = new RdpConfig("entry", "10.0.0.1:3389", "admin", "", "full address:s:attacker.example\r\nusername:s:otheruser\r\ngatewayhostname:s:gw.example\r\nsmart sizing:i:1");
            var text = cfg.ToString();
            Assert.Contains("full address:s:10.0.0.1:3389", text);
            Assert.DoesNotContain("attacker", text);
            Assert.DoesNotContain("otheruser", text);
            Assert.DoesNotContain("gw.example", text);
            Assert.Contains("smart sizing:i:1", text);   // the other additional settings still apply
        }

        [Theory]
        [InlineData("ls", 0)]
        [InlineData("ls\n", 1)]
        [InlineData("ls\r", 1)]
        [InlineData("ls\r\nrm -rf x\r\n", 2)]
        [InlineData("a\rb\rc", 2)]
        public void EveryEnterIsCounted(string text, int enters) => Assert.Equal(enters, TerminalHost.CountEnters(text));

        [Fact]
        public void PasteEndMarkerIsRemoved()
        {
            var t = TerminalHost.StripPasteMarkers("echo ok\u001b[201~rm -rf ~\n");
            Assert.Equal("echo okrm -rf ~\n", t);
            Assert.False(t.Contains('\u001b'));   // char comparison: xUnit's culture-aware one ignores control characters
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(15, 15)]
        [InlineData(480, 480)]
        [InlineData(10, 0)]
        [InlineData(99999, 0)]
        [InlineData(-5, 0)]
        public void KeepUnlockedOnlyMenuValues(int minutes, int expected) => Assert.Equal(expected, VaultUnlock.AllowedKeep(minutes));

        [Fact]
        public void KeepUnlockedMacDependsOnKeyAndMinutes()
        {
            var k1 = Enumerable.Repeat((byte)1, 32).ToArray();
            var k2 = Enumerable.Repeat((byte)2, 32).ToArray();
            Assert.Equal(VaultUnlock.KeepMac(60, k1), VaultUnlock.KeepMac(60, k1));
            Assert.NotEqual(VaultUnlock.KeepMac(60, k1), VaultUnlock.KeepMac(480, k1));
            Assert.NotEqual(VaultUnlock.KeepMac(60, k1), VaultUnlock.KeepMac(60, k2));
        }
    }
}
