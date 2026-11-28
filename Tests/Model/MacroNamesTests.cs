using Lemora.Model.Protocol.Base;
using Xunit;

namespace Tests.Model
{
    /// <summary>commands saved with the old variables (another prefix or none) become %LM_...%; the correct ones stay the same</summary>
    public class MacroNamesTests
    {
        [Theory]
        [InlineData("-ssh %XYZ_HOSTNAME% -P %XYZ_PORT% -l %XYZ_USERNAME% -pw %XYZ_PASSWORD%", "-ssh %LM_HOSTNAME% -P %LM_PORT% -l %LM_USERNAME% -pw %LM_PASSWORD%")]
        [InlineData("-i %SSH_PRIVATE_KEY_PATH%", "-i %LM_PRIVATE_KEY_PATH%")]
        [InlineData("%HOSTNAME%:%PORT%", "%LM_HOSTNAME%:%LM_PORT%")]
        [InlineData("--title \"%LM_HOSTNAME%\" -%SSH_VERSION% %STARTUP_AUTO_COMMAND%", "--title \"%LM_HOSTNAME%\" -%SSH_VERSION% %STARTUP_AUTO_COMMAND%")]
        [InlineData("", "")]
        public void OldMacrosBecomeCurrent(string saved, string expected) => Assert.Equal(expected, ProtocolBaseWithAddressPort.NormalizeMacros(saved));
    }
}
