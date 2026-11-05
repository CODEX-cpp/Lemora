namespace Lemora.Model.ProtocolRunner.Default
{
    /// <summary>
    /// terminale SSH integrato (xterm.js in una WebView2 + SSH.NET): nessun programma esterno
    /// </summary>
    public class TerminalRunner(string ownerProtocolName) : InternalRunner(ownerProtocolName)
    {
        public const string RunnerName = "Terminale integrato";
    }
}
