using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// Finds the port of a VNC server when the user types only the address: 5900 (standard) and then 5901..5910 (displays :1..:10 of Linux servers
    /// and of anyone who opens more than one). A port counts only if it answers with the protocol banner ("RFB "): another application is not mistaken for VNC.
    /// </summary>
    public static class VncPortFinder
    {
        public static readonly int[] DefaultPorts = Enumerable.Range(5900, 11).ToArray();

        /// <returns>the VNC server port (5900 if it answers, otherwise the lowest one); null if none answers</returns>
        public static async Task<int?> FindAsync(string address, int[]? ports = null, int timeoutMs = 1500)
        {
            ports ??= DefaultPorts;
            var results = await Task.WhenAll(ports.Select(p => IsVncAsync(address, p, timeoutMs)));
            for (var i = 0; i < ports.Length; i++)
                if (results[i]) return ports[i];
            return null;
        }

        private static async Task<bool> IsVncAsync(string address, int port, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var client = new TcpClient();
                await client.ConnectAsync(address, port, cts.Token);
                var stream = client.GetStream();
                var buf = new byte[4];
                var n = 0;
                while (n < 4)
                {
                    var r = await stream.ReadAsync(buf, n, 4 - n, cts.Token);
                    if (r == 0) return false;
                    n += r;
                }
                return Encoding.ASCII.GetString(buf) == "RFB ";
            }
            catch
            {
                return false;   // closed, not answering or not VNC
            }
        }
    }
}
