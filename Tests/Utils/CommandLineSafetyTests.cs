using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.ProtocolRunner;
using Lemora.Service;
using Lemora.Utils;
using Xunit;

namespace Tests.Utils
{
    /// <summary>values of an entry (from .mora, import, Teams) in command lines and in the pipe between instances</summary>
    public class CommandLineSafetyTests
    {
        // same rules as CommandLineToArgvW: each value must come back as a single identical argument
        [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
        private static extern System.IntPtr CommandLineToArgvW([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string cmd, out int n);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern System.IntPtr LocalFree(System.IntPtr p);

        private static string[] Split(string cmd)
        {
            var p = CommandLineToArgvW(cmd, out var n);
            try { return Enumerable.Range(0, n).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(p, i * System.IntPtr.Size))!).ToArray(); }
            finally { LocalFree(p); }
        }

        [Theory]
        [InlineData("root")]
        [InlineData("root -proxycmd \"calc.exe\"")]
        [InlineData("a\\\"b")]
        [InlineData("fine\\")]
        [InlineData("c:\\folder with spaces\\")]
        [InlineData("")]
        public void QuoteGivesOneIdenticalArgument(string value)
        {
            var args = Split("putty.exe -l " + CmdArg.Quote(value) + " -pw x");
            Assert.Equal(new[] { "putty.exe", "-l", value, "-pw", "x" }, args);
        }

        private const string Plink = "plink -ssh %LM_HOSTNAME% -l %LM_USERNAME% -pw \"%LM_PASSWORD%\"";

        [Theory]
        [InlineData("root", "secret with spaces", null)]
        [InlineData("root -proxycmd x", "p", "%LM_USERNAME%")]
        [InlineData("root;calc", "p", "%LM_USERNAME%")]
        [InlineData("root", "a\"b", "%LM_PASSWORD%")]
        [InlineData("root", "line\nnew", "%LM_PASSWORD%")]
        public void UnsafeMacroValuesAreRefused(string user, string pwd, string? expected)
        {
            var ssh = new SSH { Address = "10.0.0.1", UserName = user, Password = pwd };
            Assert.Equal(expected, CmdArg.UnsafeMacro(ssh, Plink, @"C:\x\plink.exe"));
        }

        [Fact]
        public void WindowsTerminalRefusesSemicolonEvenQuoted()
        {
            var ssh = new SSH { Address = "10.0.0.1", UserName = "root", Password = "a;new-tab cmd" };
            Assert.Equal("%LM_PASSWORD%", CmdArg.UnsafeMacro(ssh, Plink, "wt.exe"));
        }

        [Fact]
        public void FreeRdpArgumentsLoseControlCharacters()
        {
            Assert.Equal("/u:admin/drive:c,C:\\", FreeRdpRunner.StripControl("/u:admin\n/drive:c,C:\\"));
            Assert.Equal("/v:hostOPENSSL_CONF=x", FreeRdpRunner.StripControl("/v:host\0OPENSSL_CONF=x"));
        }

        [Fact]
        public void PipeMessageKeepsFileNameWhole()
        {
            var name = @"C:\Download\a+++!+!+++#test+++!+!+++uninstall.mora";
            var sent = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { name });
            Assert.Equal(new[] { name }, AppStartupHelper.ParsePipeMessage(sent).ToArray());
            // a non-JSON message is a single argument
            Assert.Single(AppStartupHelper.ParsePipeMessage("a+++!+!+++uninstall"));
        }
    }
}
