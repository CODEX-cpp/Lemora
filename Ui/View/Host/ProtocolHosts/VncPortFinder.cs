using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// Trova la porta di un server VNC quando l'utente scrive solo l'indirizzo: la 5900 (standard) e poi 5901..5910 (schermi :1..:10 dei server Linux
    /// e di chi ne apre piu' di uno). Una porta vale solo se risponde con il banner del protocollo ("RFB "): un'altra applicazione non viene scambiata per VNC.
    /// </summary>
    public static class VncPortFinder
    {
        public static readonly int[] DefaultPorts = Enumerable.Range(5900, 11).ToArray();

        /// <returns>la porta del server VNC (la 5900 se risponde, altrimenti la piu' bassa); null se nessuna risponde</returns>
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
                return false;   // chiusa, non risponde o non e' VNC
            }
        }
    }
}
