using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Lemora.Utils;

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>
    /// Controllo veloce del server VNC prima di usare il client integrato (che e' bloccante):
    /// legge il banner RFB e i tipi di autenticazione offerti, con tempi massimi.
    /// </summary>
    public static class VncPreflight
    {
        private const int TimeoutMs = 6000;

        /// <summary>failure null = si puo' procedere; types = tipi di autenticazione offerti (1 = nessuna, 2 = password VNC)</summary>
        public static (string? failure, int[] types) Check(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, port);
                if (!connect.Wait(TimeoutMs)) return (IoC.Translate("vnc_server_unreachable"), Array.Empty<int>());
                client.ReceiveTimeout = TimeoutMs;
                client.SendTimeout = TimeoutMs;
                var stream = client.GetStream();

                var banner = ReadExactly(stream, 12);
                var text = Encoding.ASCII.GetString(banner);
                if (!text.StartsWith("RFB ")) return (IoC.Translate("vnc_not_a_vnc_server"), Array.Empty<int>());
                int.TryParse(text.Substring(8, 3), out var minor);

                // si risponde con la versione piu' alta che il client capisce
                var reply = minor >= 8 ? "RFB 003.008\n" : minor >= 7 ? "RFB 003.007\n" : "RFB 003.003\n";
                stream.Write(Encoding.ASCII.GetBytes(reply), 0, 12);

                var types = new List<int>();
                if (minor >= 7)
                {
                    var n = ReadExactly(stream, 1)[0];
                    if (n == 0) return (IoC.Translate("vnc_server_refused"), Array.Empty<int>());
                    types.AddRange(ReadExactly(stream, n).Select(x => (int)x));
                }
                else
                {
                    var b = ReadExactly(stream, 4);
                    var t = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
                    if (t == 0) return (IoC.Translate("vnc_server_refused"), Array.Empty<int>());
                    types.Add(t);
                }

                if (!types.Contains(1) && !types.Contains(2))
                    return (string.Format(IoC.Translate("vnc_auth_not_supported"), string.Join(", ", types)), types.ToArray());
                return (null, types.ToArray());
            }
            catch (AggregateException e) when (e.InnerException is SocketException se)
            {
                return (se.Message, Array.Empty<int>());
            }
            catch (System.IO.IOException)
            {
                // timeout in lettura: il server accetta la connessione ma non parla il protocollo
                return (IoC.Translate("vnc_server_silent"), Array.Empty<int>());
            }
            catch (Exception e)
            {
                return (e.Message, Array.Empty<int>());
            }
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buf, read, count - read);
                if (n <= 0) throw new System.IO.IOException("closed");
                read += n;
            }
            return buf;
        }
    }
}
