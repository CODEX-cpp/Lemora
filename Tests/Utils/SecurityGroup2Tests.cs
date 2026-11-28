using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Setup;
using Lemora.Utils;
using Xunit;
using Assert = Xunit.Assert;

namespace Tests.Utils
{
    /// <summary>security fixes: update version, PuTTY password, import (gateway, PuTTY sessions), installation folder</summary>
    public class SecurityGroup2Tests
    {
        private static string LemoraDll => typeof(UpdateApplier).Assembly.Location;
        private static Version DllVersion => Version.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(LemoraDll).FileVersion!);

        [Fact]
        public void Update_NewerAnnouncedVersion_Accepted()
        {
            var v = DllVersion;
            Assert.Null(UpdateApplier.DowngradeOrMismatch(LemoraDll, v, new Version(v.Major, v.Minor, v.Build, Math.Max(0, v.Revision - 1)).ToString()));
        }

        [Fact]
        public void Update_SameOrOlderVersion_Refused()
        {
            var v = DllVersion;
            Assert.NotNull(UpdateApplier.DowngradeOrMismatch(LemoraDll, v, v.ToString()));                                          // same
            Assert.NotNull(UpdateApplier.DowngradeOrMismatch(LemoraDll, v, new Version(v.Major + 1, 0, 0, 0).ToString()));         // the one in use is newer
        }

        [Fact]
        public void Update_AnnouncedVersionDifferentFromPackage_Refused()
        {
            var v = DllVersion;
            Assert.NotNull(UpdateApplier.DowngradeOrMismatch(LemoraDll, new Version(v.Major + 5, 0, 0, 0), "0.0.0.1"));
        }

        [Fact]
        public void PasswordFile_HoldsPasswordAndIsDeleted()
        {
            var f = PasswordFile.Create("pw-test-putty", deleteAfterSeconds: 3600);
            try
            {
                Assert.StartsWith(Path.GetTempPath(), f, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("pw-test-putty", File.ReadAllText(f));
            }
            finally { PasswordFile.Delete(f); }
            Assert.False(File.Exists(f));
        }

        [Fact]
        public void PuttyIncluded_SupportsPwFile()
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "PuTTY", "putty.exe");
            if (!File.Exists(exe)) exe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Ui", "Resources", "PuTTY", "putty.exe");
            Assert.True(File.Exists(exe), exe);
            Assert.True(Lemora.Model.ProtocolRunner.Default.PuttyRunner.SupportsPwFile(exe));
        }

        [Fact]
        public void ImportGuard_ShowsAndStripsGatewayAndPuttySessionFiles()
        {
            var rdp = new RDP { DisplayName = "srv", Address = "srv01", GatewayMode = EGatewayMode.UseTheseGatewayServerSettings, GatewayHostName = "gw.intruder.test" };
            var ssh = new SSH { DisplayName = "linux", Address = "lnx", ExternalSessionConfigPath = @"\\intruder\share\sess.ini" };
            var plain = new RDP { DisplayName = "ok", Address = "srv02" };
            var list = new List<ProtocolBase> { rdp, ssh, plain };
            var lines = ImportGuard.Describe(list);
            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, l => l.Contains("gw.intruder.test"));
            Assert.Contains(lines, l => l.Contains(@"\\intruder\share\sess.ini"));
            ImportGuard.Strip(list);
            Assert.Equal(EGatewayMode.DoNotUseGateway, rdp.GatewayMode);
            Assert.Equal("", rdp.GatewayHostName);
            Assert.Equal("", ssh.ExternalSessionConfigPath);
            Assert.Empty(ImportGuard.Describe(list));
        }

        [Fact]
        public void PuttySessionFile_LocalProxyCommandIgnored()
        {
            var ini = Path.Combine(Path.GetTempPath(), "lemora-sess-" + Guid.NewGuid().ToString("N") + ".ini");
            File.WriteAllLines(ini, new[] { @"ProxyMethod\5\", @"ProxyTelnetCommand\calc.exe\", @"FontHeight\12\" });
            try
            {
                var cfg = new Lemora.Utils.PuTTY.Model.PuttyConfig(Lemora.Assert.APP_NAME + "_test");
                cfg.ApplyOverwriteSession(ini);
                Assert.DoesNotContain(cfg.Options, o => o.Key == "ProxyTelnetCommand" && o.Value?.ToString() == "calc.exe");
                Assert.DoesNotContain(cfg.Options, o => o.Key == "ProxyMethod" && o.Value?.ToString() == "5");
                Assert.Contains(cfg.Options, o => o.Key == "FontHeight" && o.Value?.ToString() == "12");   // the rest of the file applies
            }
            finally { File.Delete(ini); }
        }
    }
}
