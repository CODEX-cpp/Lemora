using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Utils;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security fixes (medium findings):"lv1:" ciphertexts from outside, macros hidden in ImportGuard, network paths</summary>
    public class SecurityMediumsTests
    {
        [Fact]
        public void FromOutside_DropsVaultTexts_KeepsPlain()
        {
            Assert.Equal("", DataVault.FromOutside("lv1:QUJDREVGR0g="));
            Assert.Equal("pw-real", DataVault.FromOutside("pw-real"));
            Assert.Equal("", DataVault.FromOutside(null));
        }

        [Fact]
        public void Import_VaultTextFromAnotherDb_NotDecryptedWithLocalKey()
        {
            DataVault.SetKey(Enumerable.Repeat((byte)7, 32).ToArray());
            var stolen = DataVault.SimpleEncrypt("manager-secret");   // a ciphertext taken from a copy of the victim's database
            var ssh = new SSH { Address = "attacker", Password = stolen, PrivateKey = stolen };
            ssh.Users.Add(new UserEntry { UserName = "u", Password = stolen });
            var rdp = new RDP { Address = "x", Password = "in-plain", GatewayPassword = stolen };
            ssh.DropVaultTextsFromOutside();
            rdp.DropVaultTextsFromOutside();
            Assert.Equal("", ssh.Password);
            Assert.Equal("", ssh.PrivateKey);
            Assert.Equal("", ssh.Users[0].Password);
            Assert.Equal("", rdp.GatewayPassword);
            Assert.Equal("in-plain", rdp.Password);
        }

        [Fact]
        public void ImportGuard_ShowsProgramBehindMacros()
        {
            // fake "local App" that looks like a template: the real program is in Address, the command in User
            var app = new LocalApp { DisplayName = "Launcher", ExePath = "%LM_HOSTNAME%", Address = @"C:\Windows\System32\cmd.exe", UserName = "/c whoami" };
            app.ArgumentList.Add(new AppArgument { Type = AppArgumentType.Normal, Key = "", Value = "%LM_USERNAME%" });
            var lines = ImportGuard.Describe(new List<ProtocolBase> { app });
            Assert.Contains(lines, l => l.Contains(@"C:\Windows\System32\cmd.exe") && l.Contains("/c whoami"));
        }

        [Fact]
        public void ImportGuard_CommandMacrosExpanded()
        {
            var rdp = new RDP { DisplayName = "r", Address = "calc.exe", CommandBeforeConnected = "%LM_HOSTNAME%" };
            Assert.Contains(ImportGuard.Describe(new List<ProtocolBase> { rdp }), l => l.Contains("calc.exe"));
        }

        [Fact]
        public void ImportGuard_NetworkPrivateKey_ShownAndStripped()
        {
            var ssh = new SSH { DisplayName = "s", Address = "10.0.0.2", PrivateKey = @"\\attacker\k\id_rsa" };
            var list = new List<ProtocolBase> { ssh };
            Assert.Contains(ImportGuard.Describe(list), l => l.Contains(@"\\attacker\k\id_rsa"));
            ImportGuard.Strip(list);
            Assert.Equal("", ssh.PrivateKey);
            Assert.Empty(ImportGuard.Describe(list));
        }

        [Fact]
        public void LocalApp_SubTitle_DoesNotTouchNetworkPath()
        {
            var app = new LocalApp { ExePath = @"\\10.255.255.1\s\a.exe" };   // address that does not respond: with File.Exists the UI would wait
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.StartsWith("a.exe", app.SubTitle);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
        }
    }
}
