using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.Mcp;
using Lemora.Utils;
using Xunit;

namespace Tests.Utils
{
    /// <summary>completed security fixes: address keys for Claude (alternate, short name) and log</summary>
    [Collection("vault-key")]
    public class SecurityIncompleteFixTests
    {
        private static RDP Saved()
        {
            var r = new RDP { Address = "srv01.customer.local", Port = "3389" };
            r.AlternateCredentials.Add(new Credential { Address = "10.20.30.40", Port = "3390" });
            return r;
        }

        [Theory]
        [InlineData("srv01.customer.local", "3389", true)]
        [InlineData("SRV01.customer.local.", "3389", true)]   // uppercase and trailing dot
        [InlineData("srv01", "3389", true)]                  // short name
        [InlineData("10.20.30.40", "3390", true)]            // alternate address
        [InlineData("10.20.30.40", "3389", true)]            // same PC, different port: the rules apply to the whole PC
        [InlineData("10.20.30.40", "5900", true)]            // ...also with another protocol or port (VNC)
        [InlineData("srv02.customer.local", "3389", false)]
        public void TemporarySessionMatchesSavedEntryAddresses(string addr, string port, bool same)
        {
            var tmp = new RDP { Address = addr, Port = port };
            Assert.Equal(same, McpGuard.AddrKeys(tmp).Overlaps(McpGuard.AddrKeys(Saved())));
        }

        [Fact]
        public void LogHidesAlternateAndUnsavedAddressesAndConnectionId()
        {
            DataVault.SetKey(Enumerable.Repeat((byte)9, 32).ToArray());
            LogRedactor.Remember("192.168.77.88", "quick-operator");
            var line = LogRedactor.Redact("quick connection to 192.168.77.88 user quick-operator, id abc_192.168.77.88:3389");
            Assert.DoesNotContain("192.168.77.88", line);
            Assert.DoesNotContain("quick-operator", line);
            Assert.Contains("[#", line);
        }
    }
}
