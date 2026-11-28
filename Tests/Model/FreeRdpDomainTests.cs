using Lemora.Model.Protocol;
using Lemora.Model.ProtocolRunner;
using Xunit;

namespace Tests.Model
{
    /// <summary>
    /// FreeRDP with /d keeps "DOMAIN\user" whole as the user name and the server says "wrong password":
    /// the domain written in the user wins over the Domain field, like mstsc
    /// </summary>
    public class FreeRdpDomainTests
    {
        private static System.Collections.Generic.List<string> Args(string user, string domain) =>
            FreeRdpRunner.BuildArguments(new RDP { DisplayName = "pc", Address = "10.0.0.5", Port = "3389", UserName = user, Domain = domain, Password = "x" }, false, "");

        [Theory]
        [InlineData(@"domain\administrator")]
        [InlineData("administrator@domain.local")]
        public void DomainInUser_NoDomainParameter(string user)
        {
            var a = Args(user, "domain");
            Assert.Contains("/u:" + user, a);
            Assert.DoesNotContain(a, x => x.StartsWith("/d:"));
        }

        [Fact]
        public void PlainUser_DomainField()
        {
            var a = Args("administrator", "domain");
            Assert.Contains("/u:administrator", a);
            Assert.Contains("/d:domain", a);
        }

        [Fact]
        public void KeyboardLayoutOfThisPc_IsSent()
        {
            // without it FreeRDP on Windows falls back to the US layout (LANG missing) and the server types other symbols
            var kbd = Assert.Single(Args("administrator", ""), x => x.StartsWith("/kbd:layout:0x"));
            Assert.Matches("^/kbd:layout:0x[0-9A-F]{8}$", kbd);
            Assert.NotEqual("/kbd:layout:0x00000000", kbd);
        }
    }
}
