using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shawn.Utils;
using System.Net.NetworkInformation;

namespace Lemora.Utils
{
    public static class TcpHelper
    {
        public static (bool, IPAddress?) IsIPv6(string address)
        {
            if (IPAddress.TryParse(address, out var ipAddress))
            {
                return (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6, ipAddress);
            }
            return (false, null);
        }

        /// <summary>
        /// Risolve un nome e prova TUTTI i suoi indirizzi in parallelo: vince il primo che risponde.
        /// Serve perche' un nome puo' dare prima un IPv6 link-local che non risponde (fe80::...): provare gli indirizzi
        /// uno dopo l'altro consumava da solo tutto il tempo massimo. Nessun risultato viene memorizzato: si risolve a ogni connessione.
        /// </summary>
        public static async Task<IPAddress?> FindReachableAddressAsync(string host, int port, CancellationToken token, int connectTimeoutMs = 0)
        {
            IPAddress[] ips;
            if (IPAddress.TryParse(host, out var literal)) ips = new[] { literal };
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { ips = await Dns.GetHostAddressesAsync(host, token); }
                catch (Exception e) { SimpleLogHelper.Warning($"TcpHelper: nome '{host}' non risolto dopo {sw.ElapsedMilliseconds} ms ({e.GetType().Name})"); return null; }
                // risoluzione lenta (cache vuota: il nome passa da DNS, poi LLMNR/NetBIOS): e' il tempo "perso" prima che la connessione parta
                if (sw.ElapsedMilliseconds > 1000) SimpleLogHelper.Warning($"TcpHelper: risoluzione del nome '{host}' lenta: {sw.ElapsedMilliseconds} ms -> {string.Join(", ", ips.Select(i => i.ToString()))}");
            }
            if (ips.Length == 0) return null;

            // il tempo di connessione parte DOPO la risoluzione del nome: a cache vuota (nomi mDNS/LLMNR) la risoluzione puo' impiegare
            // anche piu' di 3 secondi e, contata nello stesso limite, faceva fallire la prima connessione (la seconda trovava il nome in cache)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (connectTimeoutMs > 0) cts.CancelAfter(connectTimeoutMs);
            var tasks = ips.Select(ip => TryConnectAsync(ip, port, cts.Token)).ToList();
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                var found = await done;
                if (found != null)
                {
                    cts.Cancel();
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Diagnosi per il registro quando un collegamento non riesce: in quali indirizzi si risolve il nome e in quanto tempo, e per ogni indirizzo
        /// se la porta risponde (o l'errore). Si chiama solo dopo un fallimento, mai per decidere.
        /// </summary>
        public static async Task<string> DescribeAsync(string host, int port)
        {
            var sb = new System.Text.StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IPAddress[] ips;
            if (IPAddress.TryParse(host, out var literal)) ips = new[] { literal };
            else
            {
                try { ips = await Dns.GetHostAddressesAsync(host); sb.Append($"nome risolto in {sw.ElapsedMilliseconds} ms: {string.Join(", ", ips.Select(i => i.ToString()))}"); }
                catch (Exception e) { return $"nome NON risolto dopo {sw.ElapsedMilliseconds} ms ({e.GetType().Name}: {e.Message})"; }
            }
            foreach (var ip in ips)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var c = new TcpClient(ip.AddressFamily);
                    using var cts = new CancellationTokenSource(3000);
                    await c.ConnectAsync(ip, port, cts.Token);
                    sb.Append($" | {ip}: porta {port} aperta in {t.ElapsedMilliseconds} ms");
                }
                catch (Exception e) { sb.Append($" | {ip}: {e.GetType().Name} dopo {t.ElapsedMilliseconds} ms ({e.Message})"); }
            }
            return sb.ToString();
        }

        private static async Task<IPAddress?> TryConnectAsync(IPAddress ip, int port, CancellationToken token)
        {
            using var client = new TcpClient(ip.AddressFamily);
            try
            {
                await client.ConnectAsync(ip, port, token);
                return ip;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// return true if connected, false if not connected, null if timeout or cancelled.
        /// </summary>
        public static async Task<bool?> TestConnectionAsync(string address, int port, CancellationToken? cancellationToken = null, int timeOutMillisecond = 0)
        {
            // nome (non un indirizzo IP): si provano tutti gli indirizzi in parallelo
            if (!IPAddress.TryParse(address, out _))
            {
                var outer = cancellationToken ?? CancellationToken.None;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
                var connectTimeout = timeOutMillisecond <= 0 ? 30 * 1000 : timeOutMillisecond;
                linked.CancelAfter(15 * 1000 + connectTimeout);   // 15 s al massimo per risolvere il nome, poi il tempo di connessione richiesto
                var found = await FindReachableAddressAsync(address, port, linked.Token, connectTimeout);
                if (found != null)
                {
                    SimpleLogHelper.Debug($"TcpHelper: Connected to {address}({found}):{port}");
                    return true;
                }
                return linked.IsCancellationRequested ? null : false;
            }

            var (isIPv6, iPv6) = IsIPv6(address);
            using var client = isIPv6 ? new TcpClient(iPv6!.AddressFamily) : new TcpClient();
            try
            {
                var cts = new CancellationTokenSource();
                cancellationToken ??= cts.Token;
#if NETCOREAPP
                var connectTask = client.ConnectAsync(address, port, (CancellationToken)cancellationToken).AsTask();
#else
                var connectTask = isIPv6 ? client.ConnectAsync(iPv6, port) : client.ConnectAsync(address, port);
#endif
                if (timeOutMillisecond <= 0)
                    timeOutMillisecond = 30 * 1000;
                var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(timeOutMillisecond), (CancellationToken)cancellationToken);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                if (completedTask == timeoutTask && completedTask.IsCanceled != true)
                {
                    SimpleLogHelper.Debug("TcpHelper: timeoutTask is completed, cancel connectTask.");
                    cts.Cancel();
                    return null;
                }

                if (completedTask.IsCanceled)
                {
                    SimpleLogHelper.Debug("TcpHelper: Connection cancelled.");
                    cts.Cancel();
                    return null;
                }

                await connectTask;

                SimpleLogHelper.Debug($"TcpHelper: Connected to {address}:{port}");
                return true;
            }
            catch (OperationCanceledException)
            {
                SimpleLogHelper.Debug("TcpHelper: Connection cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Debug($"TcpHelper: Error connecting to {address}:{port}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// try access the address and port, if connect success return true, cancel return null
        /// </summary>
        public static async Task<bool?> TestConnectionAsync(string address, string portStr, CancellationToken? cancellationToken = null, int timeOutMillisecond = 0)
        {
            using var client = new TcpClient();
            try
            {
                var port = int.Parse(portStr);
                return await TestConnectionAsync(address, port, cancellationToken, timeOutMillisecond);
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Debug("TcpHelper: Error connecting to {0}:{1}: {2}", address, portStr, ex.Message);
                return false;
            }
        }
    }
}
