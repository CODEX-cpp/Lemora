using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace Lemora.Service.Mcp
{
    /// <summary>who is already listening on a TCP port of this PC (process and, if readable, program name)</summary>
    internal static class PortOwner
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3, TCP_TABLE_OWNER_PID_CONNECTIONS = 4;

        /// <summary>pid of the process listening on the port (IPv4 or IPv6), 0 if none</summary>
        public static int ListenerPid(int port) => ListenerPids(port).FirstOrDefault();

        /// <summary>all processes listening on the port (the Windows http server, which serves Lemora, shows up as pid 4)</summary>
        public static System.Collections.Generic.List<int> ListenerPids(int port) =>
            Rows(TCP_TABLE_OWNER_PID_LISTENER).Where(r => r.local == port).Select(r => r.pid).ToList();

        /// <summary>
        /// pid of the program that opened THIS connection: same family, same addresses and ports at both ends, established connections only,
        /// and exactly one matching row; otherwise 0. With ports alone another user connected from [::1] with the same port as one of the victim's
        /// IPv4 connections was mistaken for the victim (security hardening)
        /// </summary>
        public static int ClientPidExact(IPEndPoint client, IPEndPoint server)
        {
            const int ESTABLISHED = 5;
            var v6 = client.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (v6 && client.Address.IsIPv4MappedToIPv6) { client = new IPEndPoint(client.Address.MapToIPv4(), client.Port); server = new IPEndPoint(server.Address.MapToIPv4(), server.Port); v6 = false; }
            int af = v6 ? AF_INET6 : AF_INET, size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return 0;
                int n = Marshal.ReadInt32(buf), rowSize = v6 ? 56 : 24, found = 0, pid = 0;
                int Port(IntPtr at) => (Marshal.ReadByte(at) << 8) | Marshal.ReadByte(at + 1);
                IPAddress Addr(IntPtr at) { var b = new byte[v6 ? 16 : 4]; Marshal.Copy(at, b, 0, b.Length); return new IPAddress(b); }
                for (int i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    // IPv4: state 0, local ip 4, port 8, remote ip 12, port 16, pid 20; IPv6: ip 0, port 20, remote ip 24, port 44, state 48, pid 52
                    int state = Marshal.ReadInt32(row + (v6 ? 48 : 0));
                    if (state != ESTABLISHED) continue;
                    if (Port(row + (v6 ? 20 : 8)) != client.Port || Port(row + (v6 ? 44 : 16)) != server.Port) continue;
                    if (!Addr(row + (v6 ? 0 : 4)).Equals(client.Address) || !Addr(row + (v6 ? 24 : 12)).Equals(server.Address)) continue;
                    found++; pid = Marshal.ReadInt32(row + (v6 ? 52 : 20));
                }
                return found == 1 ? pid : 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>rows of the Windows TCP table (IPv4 and IPv6): local port, remote port, pid</summary>
        private static System.Collections.Generic.List<(int local, int remote, int pid)> Rows(int tblClass)
        {
            var all = new System.Collections.Generic.List<(int, int, int)>();
            foreach (var af in new[] { AF_INET, AF_INET6 })
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, tblClass, 0);
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, af, tblClass, 0) != 0) continue;
                    int n = Marshal.ReadInt32(buf);
                    // MIB_TCPROW_OWNER_PID: 6 x 4 bytes (state, local ip, local port, remote ip, remote port, pid)
                    // MIB_TCP6ROW_OWNER_PID: 16 ip + 4 scope + 4 port + 16 ip + 4 scope + 4 port + 4 state + 4 pid = 56 bytes
                    int rowSize = af == AF_INET ? 24 : 56, portOff = af == AF_INET ? 8 : 20, rportOff = af == AF_INET ? 16 : 44, pidOff = af == AF_INET ? 20 : 52;
                    int Port(IntPtr at) => (Marshal.ReadByte(at) << 8) | Marshal.ReadByte(at + 1);   // port in network byte order
                    for (int i = 0; i < n; i++)
                    {
                        var row = buf + 4 + i * rowSize;
                        all.Add((Port(row + portOff), Port(row + rportOff), Marshal.ReadInt32(row + pidOff)));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return all;
        }

        /// <summary>description for the user: "name (pid N)", or just the pid if the process belongs to another user or is protected</summary>
        public static string Describe(int pid)
        {
            if (pid <= 0) return "";
            try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} (pid {pid})"; }
            catch { return $"pid {pid}"; }
        }
    }
}
