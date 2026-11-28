using System;
using System.IO;
using Lemora.Setup;
using Lemora.Utils;
using Lemora.Utils.PuTTY.Model;
using Lemora.Utils.RdpFile;
using Lemora.View.Host.ProtocolHosts;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security fixes that can be verified without the UI</summary>
    public class SecurityFixes3Tests
    {
        [Theory]
        [InlineData("\u001b[2\u001b[201~01~rm -rf /\r")]
        [InlineData("\u001b[2\u009b201~01~x")]
        [InlineData("\u001b[201~")]
        public void PastedText_NoEscapeSurvives(string pasted)
        {
            var s = TerminalHost.StripPasteMarkers(pasted);
            Assert.DoesNotContain('\u001b', s);
            Assert.DoesNotContain('\u009b', s);
        }

        [Theory]
        [InlineData("AuthPlugin", true)]
        [InlineData("SSHManualHostKeys", true)]
        [InlineData("HostName", true)]
        [InlineData("Printer", true)]
        [InlineData("BellWaveFile", true)]
        [InlineData("FontHeight", false)]
        [InlineData("Colour0", false)]
        [InlineData("TermWidth", false)]
        [InlineData("ScrollbackLines", false)]
        public void PuttySessionFile_OnlyAppearanceKeysAllowed(string key, bool blocked) => Assert.Equal(blocked, PuttyConfig.IsLocalEffectKey(key));

        [Theory]
        [InlineData("authentication level:i:0:a:")]
        [InlineData("Authentication  Level : i : 0")]
        [InlineData("full address:s:[2001:db8::a:5]")]
        [InlineData("enablecredsspsupport:i:0")]
        public void RdpFile_ProtectedKeys_NotOverridden(string extra)
        {
            var cfg = new RdpConfig("entry","10.0.0.1:3389", "admin", "", extra).ToString();
            Assert.Contains("authentication level:i:2", cfg);
            Assert.DoesNotContain("2001:db8", cfg);
            Assert.DoesNotContain("enablecredsspsupport:i:0", cfg);
            Assert.DoesNotContain(":a:", cfg);
        }

        [Fact]
        public void RdpFile_OtherKeysStillApplied()
        {
            var cfg = new RdpConfig("entry","10.0.0.1:3389", "admin", "", "smart sizing:i:1\r\nSMART SIZING:i:0").ToString();
            Assert.Contains("smart sizing:i:0", cfg, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InstallDir_ParentsChecked()
        {
            Assert.True(SetupEngine.ParentsSafe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lemora")));
            Assert.False(SetupEngine.ParentsSafe(Path.Combine(Path.GetTempPath(), "Lemora")));   // %TEMP% belongs to the user
            var mine = Path.Combine(Path.GetTempPath(), "lemora-owner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mine);
            try { Assert.False(SetupEngine.OwnedByAdmins(mine)); }
            finally { Directory.Delete(mine); }
        }
    }
}
