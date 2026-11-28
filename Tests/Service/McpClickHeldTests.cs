using Lemora.Service.Mcp;
using Xunit;

namespace Tests.Service
{
    /// <summary>clic tieni=...: which keys are held down during the click</summary>
    public class McpClickHeldTests
    {
        [Theory]
        [InlineData(null, new uint[0])]
        [InlineData("", new uint[0])]
        [InlineData("ctrl", new uint[] { 0xffe3 })]
        [InlineData("Maiusc", new uint[] { 0xffe1 })]
        [InlineData("shift", new uint[] { 0xffe1 })]
        [InlineData("ctrl+maiusc", new uint[] { 0xffe3, 0xffe1 })]
        [InlineData("ctrl + ctrl", new uint[] { 0xffe3 })]
        public void HeldKeys_Recognized(string? held, uint[] expected)
        {
            Assert.Equal(expected, McpTools.HeldKeys(held, out var error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("alt")]
        [InlineData("ctrl+win")]
        public void HeldKeys_Other_Refused(string held)
        {
            Assert.Null(McpTools.HeldKeys(held, out var error));
            Assert.Contains("not recognized", error);
        }
    }
}
