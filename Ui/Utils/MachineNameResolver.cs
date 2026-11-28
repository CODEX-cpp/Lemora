using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Lemora.Utils
{
    /// <summary>
    /// Name of the connected PC, only for display in the session bar.
    /// RDP: the certificate the server presents contains the machine name (a connection is opened, nothing is sent).
    /// RDP only: 1) certificate; 2) if the entry has the domain, reverse DNS. Kept in memory for 5 minutes, never on disk.
    /// </summary>
    public static class MachineNameResolver
    {
        private static readonly ConcurrentDictionary<string, (DateTime At, string? Name)> Cache = new();

        public static async Task<string?> ResolveAsync(string address, int port, bool rdp, string? domain = null)
        {
            // a name instead of the address already is the machine name
            if (!IPAddress.TryParse(address, out var ip)) return null;
            var key = $"{address}:{port}:{rdp}:{domain}";
            if (Cache.TryGetValue(key, out var hit) && DateTime.Now - hit.At < TimeSpan.FromMinutes(5)) return hit.Name;

            // RDP only: it is the port already open towards the customer. Reverse DNS and NetBIOS (UDP) over VPN do not pass or lack the name
            string? name = null;
            try { if (rdp) name = ShortName(await FromRdpCertificate(ip, port <= 0 ? 3389 : port)); } catch { /* server unreachable or without TLS */ }
            // second method, only if the entry has the domain field: reverse lookup of the IP (needs the reverse record on the customer's DNS)
            if (name == null && rdp && !string.IsNullOrWhiteSpace(domain))
            {
                try
                {
                    var dns = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(dns, Task.Delay(3000)) == dns) name = ShortName(dns.Result.HostName);
                }
                catch { /* no reverse record */ }
            }
            Cache[key] = (DateTime.Now, name);
            return name;
        }

        /// <summary>only the machine name, uppercase, without domain: "srv-app01.acme.local" becomes "SRV-APP01"; an IP is not a name</summary>
        public static string? ShortName(string? full)
        {
            var n = (full ?? "").Trim().Split('.')[0];
            return n.Length == 0 || IPAddress.TryParse(full!.Trim(), out _) ? null : n.ToUpperInvariant();
        }

        private static async Task<string?> FromRdpCertificate(IPAddress ip, int port)
        {
            using var cts = new System.Threading.CancellationTokenSource(4000);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, port, cts.Token);
            var s = tcp.GetStream();
            // X.224 Connection Request with RDP_NEG_REQ: TLS (1) and NLA (2); customer servers often accept only NLA.
            // With NLA the certificate still arrives in the TLS handshake, before any credential, and we stop there
            byte[] req = { 0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0, 0, 0, 0, 0, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00 };
            await s.WriteAsync(req, cts.Token);
            var resp = new byte[19];
            var n = 0;
            while (n < resp.Length)
            {
                var r = await s.ReadAsync(resp.AsMemory(n), cts.Token);
                if (r == 0) return null;
                n += r;
                if (n >= 4 && resp[0] == 0x03 && resp[3] < resp.Length && n >= resp[3]) break;
            }
            if (n < 19 || resp[11] != 0x02) return null;   // the server did not choose TLS
            string? cn = null;
            using var ssl = new SslStream(s, false, (_, cert, _, _) =>
            {
                if (cert != null) cn = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert).GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                return true;   // only the name is read, the certificate is not trusted
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ip.ToString() }, cts.Token);
            return cn;
        }
    }
}
