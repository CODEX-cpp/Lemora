using Lemora.Service.Browser;
using Xunit;

namespace Tests.Utils
{
    /// <summary>browser extension: host and port of the entry and of the page must match</summary>
    public class BrowserBridgeTests
    {
        [Theory]
        [InlineData("127.0.0.1:8099", "127.0.0.1", 8099)]
        [InlineData("http://127.0.0.1:8099/index.html", "127.0.0.1", 8099)]
        [InlineData("10.0.0.5", "10.0.0.5", 0)]
        [InlineData("https://nas.local/login", "nas.local", 0)]
        [InlineData("[fe80::1]:8443", "fe80::1", 8443)]
        [InlineData("router.lan.", "router.lan", 0)]
        public void HostPort_Reads(string s, string host, int port)
        {
            Assert.Equal((host, port), BrowserBridge.HostPort(s));
        }

        [Theory]
        // without scheme: same host, port if given
        [InlineData("127.0.0.1:8099", "http://127.0.0.1:8099/index.html", true)]
        [InlineData("127.0.0.1:8099", "http://127.0.0.1:8100/", false)]
        [InlineData("10.0.0.5", "https://10.0.0.5:8443/", true)]
        [InlineData("NAS.local.", "http://nas.local/", true)]
        // with scheme: same scheme and port (default if not given)
        [InlineData("https://nas.local", "https://nas.local/login", true)]
        [InlineData("https://nas.local", "http://nas.local/login", false)]
        [InlineData("https://nas.local", "https://nas.local:8443/", false)]
        [InlineData("http://10.0.0.1:8080", "http://10.0.0.1:8080/x", true)]
        [InlineData("http://10.0.0.1:8080", "http://10.0.0.1/", false)]
        [InlineData("ssh://10.0.0.1", "http://10.0.0.1/", false)]
        // other hosts, userinfo, IPv6, IDN, non-web pages
        [InlineData("nas.local", "http://nas.local.evil.com/", false)]
        [InlineData("nas.local", "http://nas.local@evil.com/", false)]
        [InlineData("[fe80::1]:8443", "https://[fe80::1]:8443/", true)]
        [InlineData("bücher.example", "https://xn--bcher-kva.example/", true)]
        [InlineData("10.0.0.5", "file://10.0.0.5/c$/x", false)]
        [InlineData("", "http://nas.local/", false)]
        public void EntryMatches_Page(string entry, string page, bool expected)
        {
            Assert.Equal(expected, BrowserBridge.EntryMatches(entry, page));
        }

        // extension older than the one inside Lemora: before 1.0.7 it did not send its version (null = older)
        [Theory]
        [InlineData(null, "1.0.7", true)]
        [InlineData("1.0.3", "1.0.7", true)]
        [InlineData("1.0.7", "1.0.7", false)]
        [InlineData("1.0.10", "1.0.7", false)]
        [InlineData("1.0.3", null, false)]          // no file inside Lemora: nothing to offer
        public void ExtensionOlder(string? installed, string? bundled, bool expected) =>
            Assert.Equal(expected, BrowserExtensionSetup.IsOlder(installed, bundled));

        [Fact]
        public void BundledVersion_ReadFromEmbeddedFile() => Assert.Matches(@"^\d+\.\d+\.\d+$", BrowserExtensionSetup.BundledVersion() ?? "");
    }
}
