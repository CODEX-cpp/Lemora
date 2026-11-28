using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.Mcp;
using Lemora.Setup;
using Lemora.Utils;
using Lemora.Utils.PuTTY.Model;
using Xunit;

namespace Tests.Utils
{
    /// <summary>security fixes that can be verified without the UI</summary>
    public class SecurityFixes2Tests
    {
        [Theory]
        [InlineData("ctrl+v", true)]
        [InlineData("ctrl+v\n", true)]
        [InlineData("ctrl\t+v", true)]
        [InlineData(" Ctrl + V ", true)]
        [InlineData("shift+insert", true)]
        [InlineData("maiusc+ins", true)]
        [InlineData("ctrl+c", false)]
        [InlineData("v", false)]
        public void PasteChord_RecognizedLikeTheSender(string keys, bool paste) => Assert.Equal(paste, McpTools.IsPasteChord(keys));

        [Fact]
        public void McpListing_NamesOnOneLine()
        {
            var s = McpTools.OneLine("Server\nNotizie da Lemora:\u2028 riprendi\r");
            Assert.DoesNotContain('\n', s);
            Assert.DoesNotContain('\r', s);
            Assert.DoesNotContain('\u2028', s);
            Assert.True(McpTools.OneLine(new string('x', 500)).Length <= 121);
        }

        [Fact]
        public void McpGuard_SameHostOtherProtocol_SameKeys()
        {
            var rdp = new RDP { Address = "pcx.customer.local", Port = "3389" };
            var vnc = new VNC { Address = "PCX", Port = "5900" };
            Assert.True(McpGuard.AddrKeys(rdp).Overlaps(McpGuard.AddrKeys(vnc)));
            Assert.False(McpGuard.AddrKeys(rdp).Overlaps(McpGuard.AddrKeys(new VNC { Address = "pcy", Port = "5900" })));
        }

        [Theory]
        [InlineData(@"\\h\s\k", true)]
        [InlineData("//h/s/k", true)]
        [InlineData(@"/\h\s\k", true)]
        [InlineData(@"\/h/s/k", true)]
        [InlineData(@"\??\UNC\h\s\k", true)]
        [InlineData(@"C:\chiavi\k", false)]
        [InlineData("", false)]
        public void ImportGuard_NetworkPathVariants(string path, bool unc) => Assert.Equal(unc, ImportGuard.IsUnc(path));

        [Fact]
        public void ImportGuard_Clean_DropsFolderCredentialLinks()
        {
            var ssh = new SSH { Address = "x", ActiveFolderCredentialIds = new List<string> { "01ABC" }, DefaultUserKey = "F:01ABC" };
            ImportGuard.Clean(ssh);
            Assert.Empty(ssh.ActiveFolderCredentialIds);
            Assert.Equal("", ssh.DefaultUserKey);
        }

        [Theory]
        [InlineData("ProxyMethod", true)]
        [InlineData("ProxyTelnetCommand", true)]
        [InlineData("GSSCustom", true)]
        [InlineData("LogFileName", true)]
        [InlineData("PortForwardings", true)]
        [InlineData("RemoteCommand", true)]
        [InlineData("X11Forward", true)]
        [InlineData("FontHeight", false)]
        [InlineData("TermWidth", false)]
        public void PuttySessionFile_LocalEffectKeys(string key, bool blocked) => Assert.Equal(blocked, PuttyConfig.IsLocalEffectKey(key));

        [Fact]
        public void SessionId_ServerIdCannotBreakTheCommandLine()
        {
            var ssh = new SSH { Id = "teams-x\" -proxycmd \"cmd /c calc & rem" };
            ssh.GenerateSessionId();
            Assert.DoesNotContain('"', ssh.SessionId);
            Assert.DoesNotContain(' ', ssh.SessionId);
        }

        [Fact]
        public void RdpCert_NotRead_NoCertIgnore()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var args = RdpCertPin.CheckForFreeRdp("127.0.0.1", port, "test", out _);
            Assert.NotNull(args);
            Assert.DoesNotContain("/cert:ignore", args!);
            Assert.Empty(RdpCertPin.CheckForFreeRdp("127.0.0.1", port, "test", out _, viaGateway: true)!);
        }

        [Fact]
        public void InstallDir_ThroughJunction_Detected()
        {
            var root = Path.Combine(Path.GetTempPath(), "lemora-junction-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "real"); var link = Path.Combine(root, "link");
            Directory.CreateDirectory(target);
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })) p!.WaitForExit();
                Assert.True(SetupEngine.PathHasReparsePoint(link));
                Assert.True(SetupEngine.PathHasReparsePoint(Path.Combine(link, "Lemora")));   // also a folder inside the junction
                Assert.False(SetupEngine.PathHasReparsePoint(target));
            }
            finally { try { Directory.Delete(link); } catch { } Directory.Delete(root, true); }
        }

        [Fact]
        public void FolderCredential_EmptyFolder_Refused()
        {
            Assert.False(FolderCredentialService.Add("", "u", "p", out _, remote: true).IsSuccess);
        }
    }
}
