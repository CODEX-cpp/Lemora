using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lemora.Service.Mcp;
using Lemora.Setup;
using Xunit;
using Assert = Xunit.Assert;

namespace Tests.Utils
{
    /// <summary>security fixes: protected startup with administrator permissions (launcher) and MCP requests made by a browser</summary>
    public class SecurityElevationMcpTests
    {
        private static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "lemora-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static string RepoFile(string rel)
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, rel))) return Path.Combine(d.FullName, rel);
            throw new FileNotFoundException(rel);
        }

        [Fact]
        public void IncludedLauncher_KnowsTheProtectedElevation()
        {
            var bytes = File.ReadAllBytes(RepoFile(@"Ui\Resources\Launcher\Lemora-launcher.bin"));
            Assert.True(SetupEngine.HasElevatedMarker(bytes), "the included launcher lacks the protected start: rebuild with Installer\\launcher\\build-launcher.ps1");
        }

        [Fact]
        public void ElevatedLauncher_OnlyANewSmallLauncher()
        {
            var root = TempDir();
            try
            {
                var exe = Path.Combine(root, SetupEngine.ExeName);
                Assert.Null(SetupEngine.ElevatedLauncher(root));                      // missing
                Assert.Null(SetupEngine.ElevatedLauncher(null));
                File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A, 1, 2, 3 });
                Assert.Null(SetupEngine.ElevatedLauncher(root));                      // old launcher: no marker
                File.WriteAllBytes(exe, System.Text.Encoding.ASCII.GetBytes("MZ....LEMORA-ELEVATED-V1...."));
                Assert.Equal(exe, SetupEngine.ElevatedLauncher(root));
                var big = new byte[6 * 1024 * 1024];
                System.Text.Encoding.ASCII.GetBytes("LEMORA-ELEVATED-V1").CopyTo(big, 100);
                File.WriteAllBytes(exe, big);
                Assert.Null(SetupEngine.ElevatedLauncher(root));                      // it is the whole app, not a launcher
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void RefreshLauncher_LeavesTheAppAloneWithoutVersionsFolder()
        {
            var root = TempDir();
            try
            {
                var exe = Path.Combine(root, SetupEngine.ExeName);
                File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
                SetupEngine.RefreshLauncher(root);   // without versions\ it is not a launcher installation: leave it alone
                Assert.Equal(2, new FileInfo(exe).Length);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void BrowserHeaders_Refused_ClaudeCodeHeaders_Accepted()
        {
            var claude = new NameValueCollection { ["Authorization"] = "Bearer x", ["Content-Type"] = "application/json", ["Accept"] = "application/json, text/event-stream", ["User-Agent"] = "claude-code/2.1", ["mcp-protocol-version"] = "2025-06-18" };
            Assert.Null(McpServer.BrowserRequest(claude, 0));
            Assert.Null(McpServer.BrowserRequest(claude, Environment.ProcessId));   // the test process is not a browser
            // Node's fetch (MCP clients written in Node) sends "sec-fetch-mode: cors" on its own: it is not a browser
            Assert.Null(McpServer.BrowserRequest(new NameValueCollection(claude) { ["sec-fetch-mode"] = "cors", ["User-Agent"] = "node" }, 0));
            foreach (var h in new[] { "Sec-Fetch-Site", "sec-fetch-dest", "Sec-Fetch-User", "Sec-CH-UA-Platform" })
            {
                var b = new NameValueCollection(claude) { [h] = "x" };
                Assert.NotNull(McpServer.BrowserRequest(b, 0));
            }
        }

        /// <summary>real TCP connection made by a program called chrome.exe (renamed curl): recognized from the process, even without headers</summary>
        [Fact]
        public async Task ConnectionFromABrowserProcess_IsRecognized()
        {
            var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (!File.Exists(curl)) return;
            var dir = TempDir();
            var chrome = Path.Combine(dir, "chrome.exe");
            File.Copy(curl, chrome);
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            using var p = Process.Start(new ProcessStartInfo(chrome, $"-s -m 10 http://127.0.0.1:{port}/mcp") { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                using var c = await l.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var cpid = PortOwner.ClientPidExact((IPEndPoint)c.Client.RemoteEndPoint!, (IPEndPoint)c.Client.LocalEndPoint!);
                Assert.Equal(p.Id, cpid);
                var why = McpServer.BrowserRequest(new NameValueCollection(), cpid);
                Assert.NotNull(why);
                Assert.Contains("chrome", why);

                // same check for a connection from this process (like Claude Code): no refusal
                using var mine = new TcpClient();
                await mine.ConnectAsync(IPAddress.Loopback, port);
                using var acc = await l.AcceptTcpClientAsync();
                var mpid = PortOwner.ClientPidExact((IPEndPoint)acc.Client.RemoteEndPoint!, (IPEndPoint)acc.Client.LocalEndPoint!);
                Assert.Equal(Environment.ProcessId, mpid);
                Assert.Null(McpServer.BrowserRequest(new NameValueCollection(), mpid));
                // same port but different address/family: no match
                Assert.Equal(0, PortOwner.ClientPidExact(new IPEndPoint(IPAddress.IPv6Loopback, ((IPEndPoint)acc.Client.RemoteEndPoint!).Port), new IPEndPoint(IPAddress.IPv6Loopback, port)));
            }
            finally
            {
                l.Stop();
                try { p.Kill(); p.WaitForExit(5000); } catch { /* already finished */ }
                try { Directory.Delete(dir, true); } catch { /* stays in temp */ }
            }
        }
    }
}
