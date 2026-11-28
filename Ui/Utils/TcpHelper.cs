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
        /// Resolves a name and tries ALL its addresses in parallel: the first one that answers wins.
        /// Needed because a name can first give a link-local IPv6 that does not answer (fe80::...): trying the addresses
        /// one after the other used up the whole timeout by itself. No result is cached: the name is resolved at every connection.
        /// </summary>
        public static async Task<IPAddress?> FindReachableAddressAsync(string host, int port, CancellationToken token, int connectTimeoutMs = 0)
        {
            // the log hides PC names and IPs: also those not saved yet (quick connect, Claude's tests) before writing them
            // (security hardening)
            LogRedactor.Remember(host);
            IPAddress[] ips;
            if (IPAddress.TryParse(host, out var literal)) ips = new[] { literal };
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { ips = await Dns.GetHostAddressesAsync(host, token); LogRedactor.Remember(ips.Select(i => i.ToString()).ToArray()); }
                catch (Exception e) { SimpleLogHelper.Warning($"TcpHelper: name '{host}' not resolved after {sw.ElapsedMilliseconds} ms ({e.GetType().Name})"); return null; }
                // slow resolution (empty cache: the name goes through DNS, then LLMNR/NetBIOS): it is the time "lost" before the connection starts
                if (sw.ElapsedMilliseconds > 1000) SimpleLogHelper.Warning($"TcpHelper: slow resolution of name '{host}': {sw.ElapsedMilliseconds} ms ->{string.Join(", ", ips.Select(i => i.ToString()))}");
            }
            if (ips.Length == 0) return null;

            // the connect timeout starts AFTER name resolution: with an empty cache (mDNS/LLMNR names) resolution can take
            // more than 3 seconds and, counted in the same limit, it made the first connection fail (the second found the name in cache)
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
        /// Diagnostics for the log when a connection fails: which addresses the name resolves to and how long it takes, and for each address
        /// whether the port answers (or the error). Called only after a failure, never to decide.
        /// </summary>
        public static async Task<string> DescribeAsync(string host, int port)
        {
            var sb = new System.Text.StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IPAddress[] ips;
            if (IPAddress.TryParse(host, out var literal)) ips = new[] { literal };
            else
            {
                try { ips = await Dns.GetHostAddressesAsync(host); sb.Append($"name resolved in {sw.ElapsedMilliseconds} ms: {string.Join(", ", ips.Select(i => i.ToString()))}"); }
                catch (Exception e) { return $"name NOT resolved after {sw.ElapsedMilliseconds} ms ({e.GetType().Name}: {e.Message})"; }
            }
            foreach (var ip in ips)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var c = new TcpClient(ip.AddressFamily);
                    using var cts = new CancellationTokenSource(3000);
                    await c.ConnectAsync(ip, port, cts.Token);
                    sb.Append($" | {ip}: port {port} open in {t.ElapsedMilliseconds} ms");
                }
                catch (Exception e) { sb.Append($" | {ip}: {e.GetType().Name} after {t.ElapsedMilliseconds} ms ({e.Message})"); }
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
            LogRedactor.Remember(address);
            // name (not an IP address): all addresses are tried in parallel
            if (!IPAddress.TryParse(address, out _))
            {
                var outer = cancellationToken ?? CancellationToken.None;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
                var connectTimeout = timeOutMillisecond <= 0 ? 30 * 1000 : timeOutMillisecond;
                linked.CancelAfter(15 * 1000 + connectTimeout);   // at most 15 s to resolve the name, then the requested connect timeout
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
            LogRedactor.Remember(address);
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
