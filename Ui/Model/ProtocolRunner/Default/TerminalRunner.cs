namespace Lemora.Model.ProtocolRunner.Default
{
    /// <summary>
    /// built-in SSH terminal (xterm.js in a WebView2 + SSH.NET): no external program
    /// </summary>
    public class TerminalRunner(string ownerProtocolName) : InternalRunner(ownerProtocolName)
    {
        public const string RunnerName = "Terminale integrato";
    }
}
