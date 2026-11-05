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
    /// Nome del PC a cui si e' collegati, solo da mostrare nella barra sessione.
    /// RDP: il certificato che il server presenta contiene il nome della macchina (si apre una connessione, non si invia nulla).
    /// Solo RDP: 1) certificato; 2) se la voce ha il dominio, DNS inverso. Memoria per 5 minuti, mai su disco.
    /// </summary>
    public static class MachineNameResolver
    {
        private static readonly ConcurrentDictionary<string, (DateTime At, string? Name)> Cache = new();

        public static async Task<string?> ResolveAsync(string address, int port, bool rdp, string? domain = null)
        {
            // un nome al posto dell'indirizzo e' gia' il nome della macchina
            if (!IPAddress.TryParse(address, out var ip)) return null;
            var key = $"{address}:{port}:{rdp}:{domain}";
            if (Cache.TryGetValue(key, out var hit) && DateTime.Now - hit.At < TimeSpan.FromMinutes(5)) return hit.Name;

            // solo RDP: e' la porta gia' aperta verso il cliente. DNS inverso e NetBIOS (UDP) via VPN non passano o non hanno il nome
            string? name = null;
            try { if (rdp) name = ShortName(await FromRdpCertificate(ip, port <= 0 ? 3389 : port)); } catch { /* server non raggiungibile o senza TLS */ }
            // secondo metodo, solo se la voce ha il campo dominio: ricerca inversa dell'IP (serve il record inverso sul DNS del cliente)
            if (name == null && rdp && !string.IsNullOrWhiteSpace(domain))
            {
                try
                {
                    var dns = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(dns, Task.Delay(3000)) == dns) name = ShortName(dns.Result.HostName);
                }
                catch { /* nessun record inverso */ }
            }
            Cache[key] = (DateTime.Now, name);
            return name;
        }

        /// <summary>solo il nome della macchina, in maiuscolo, senza dominio: "srvdmn.omg.local" diventa "SRVDMN"; un IP non e' un nome</summary>
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
            // X.224 Connection Request con RDP_NEG_REQ: TLS (1) e NLA (2); i server dei clienti spesso accettano solo NLA.
            // Con NLA il certificato arriva comunque nella stretta di mano TLS, prima di qualsiasi credenziale, e ci si ferma li'
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
            if (n < 19 || resp[11] != 0x02) return null;   // il server non ha scelto TLS
            string? cn = null;
            using var ssl = new SslStream(s, false, (_, cert, _, _) =>
            {
                if (cert != null) cn = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert).GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                return true;   // si legge solo il nome, non si fida del certificato
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ip.ToString() }, cts.Token);
            return cn;
        }
    }
}
