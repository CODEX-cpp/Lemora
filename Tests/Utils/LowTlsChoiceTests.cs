using Lemora.View.Host.ProtocolHosts;
using Xunit;

namespace Tests.Utils
{
    /// <summary>
    /// FreeRDP with old servers (code 143): the reduced TLS chosen "always" is valid only with the same PC certificate.
    /// Before, it was saved without a certificate and a man in the middle could lower the protection for good
    /// </summary>
    public class LowTlsChoiceTests
    {
        private const string Fp = "ab12cd34";

        [Theory]
        [InlineData("1|ab12cd34", 1)]
        [InlineData("0|AB12CD34", 0)]   // fingerprint in a different case: same certificate
        public void SameCertificate_LevelKept(string stored, int level) => Assert.Equal(level, IntegrateHost.LowTlsChoices.Parse(stored, Fp));

        [Theory]
        [InlineData("1|ffff0000")]   // certificate changed: ask again
        [InlineData("1")]            // old format without certificate
        [InlineData("5|ab12cd34")]   // level that does not exist
        [InlineData("x|ab12cd34")]
        [InlineData("")]
        [InlineData(null)]
        public void OtherCases_AskAgain(string? stored) => Assert.Null(IntegrateHost.LowTlsChoices.Parse(stored, Fp));

        [Fact]
        public void NoCertificate_NeverSaved() => Assert.Null(IntegrateHost.LowTlsChoices.Parse("1|", null));
    }
}
