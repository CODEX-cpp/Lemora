using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;

namespace Lemora.Service.Browser
{
    /// <summary>
    /// Lemora.exe launched by the browser for the extension (native messaging): no windows, just a bridge between the browser's standard input/output
    /// and the pipe of the running Lemora instance. Chrome/Edge pass the origin "chrome-extension://id/", Firefox the manifest and the extension id.
    /// </summary>
    internal static class BrowserHost
    {
        public static bool IsHostLaunch(string[] args) =>
            args.Any(a => a.StartsWith("chrome-extension://" + BrowserProtocol.ChromeExtensionId, StringComparison.OrdinalIgnoreCase))
            || args.Any(a => string.Equals(a, BrowserProtocol.FirefoxExtensionId, StringComparison.OrdinalIgnoreCase));

        /// <returns>exit code</returns>
        public static int Run()
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            NamedPipeClientStream? pipe = null;
            try
            {
                while (true)
                {
                    var msg = BrowserProtocol.Read(stdin);
                    if (msg == null) return 0;   // the browser closed
                    string reply;
                    try
                    {
                        pipe ??= Connect();
                        if (pipe == null)
                        {
                            // Lemora is not running: "Open Lemora" starts it (same exe, no arguments; ShellExecute: does not inherit the browser's handles)
                            if (msg.Contains("\"type\":\"open\"")) StartLemora();
                            reply = "{\"ok\":false,\"error\":\"closed\"}";
                        }
                        else
                        {
                            BrowserProtocol.Write(pipe, msg);
                            reply = BrowserProtocol.Read(pipe) ?? throw new IOException("pipe closed");
                        }
                    }
                    catch (IOException)
                    {
                        pipe?.Dispose(); pipe = null;   // Lemora closed midway: retry on the next message
                        reply = "{\"ok\":false,\"error\":\"closed\"}";
                    }
                    BrowserProtocol.Write(stdout, reply);
                }
            }
            finally { pipe?.Dispose(); }
        }

        private static void StartLemora()
        {
            try { if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch { /* the "Lemora is not running" message stays */ }
        }

        private static NamedPipeClientStream? Connect()
        {
            // Identification: the other side can know who we are but cannot act on our behalf (impersonation)
            var p = new NamedPipeClientStream(".", BrowserProtocol.PipeName, PipeDirection.InOut, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
            try { p.Connect(1500); }
            catch (Exception) { p.Dispose(); return null; }
            // pipe created by another user with the same name (before Lemora started): send it nothing
            if (!BrowserProtocol.ServerIsMine(p)) { p.Dispose(); return null; }
            return p;
        }
    }
}
