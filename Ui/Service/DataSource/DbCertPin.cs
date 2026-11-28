using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service.DataSource
{
    /// <summary>
    /// Certificate of the data sources' MySQL/PostgreSQL server (security hardening):
    /// the connection was encrypted but the certificate was not checked, so a man in the middle could pose as the server and read the password.
    /// As for SSH and RDP: on the first connection the certificate's SHA-256 fingerprint is remembered (encrypted database, key "db.certpins");
    /// afterwards a different certificate blocks the connection and Lemora asks (certificate renewal or someone in the middle).
    /// Servers with a home-made (self-signed) certificate are fine: what matters is that it is always the same.
    /// PostgreSQL: check inside the real connection (UserCertificateValidationCallback).
    /// MySQL (MySql.Data has no customizable check): the certificate is read with a test connection just before.
    /// shortcut: for MySQL the window between the probe and the real connection remains; closing it requires another client library (MySqlConnector)
    /// </summary>
    public static class DbCertPin
    {
        private const string DbKey = "db.certpins";
        private static Dictionary<string, string>? _map;
        private static readonly object Gate = new();

        public static string Key(string kind, string host, int port) => $"{kind}|{host.Trim().TrimEnd('.').ToLowerInvariant()}:{port}";

        private static Dictionary<string, string> Map()
        {
            if (_map != null) return _map;
            try
            {
                if (AppDbStore.TryGet(DbKey, out var json))
                    return _map = string.IsNullOrEmpty(json) ? new() : JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
            }
            catch (Exception e) { SimpleLogHelper.Warning("Data sources: certificate fingerprints unreadable. " + e.Message); }
            return new();
        }

        public static string? Get(string kind, string host, int port) { lock (Gate) return Map().TryGetValue(Key(kind, host, port), out var f) ? f : null; }

        public static void Set(string kind, string host, int port, string fingerprint)
        {
            lock (Gate)
            {
                var m = Map();
                m[Key(kind, host, port)] = fingerprint;
                try { if (AppDbStore.Set(DbKey, JsonConvert.SerializeObject(m))) _map = m; }
                catch (Exception e) { SimpleLogHelper.Error(e); }
            }
        }

        public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();

        /// <summary>
        /// result of the comparison: true = remembered certificate (or first connection, which remembers it); false = different from the remembered one.
        /// Asks nothing: it is also called inside the TLS handshake. Confirm asks the question, outside
        /// </summary>
        public static bool Accept(string kind, string host, int port, string fingerprint)
        {
            var pinned = Get(kind, host, port);
            if (pinned == null)
            {
                Set(kind, host, port, fingerprint);
                SimpleLogHelper.Warning($"Data source {kind}: server certificate remembered at the first connection ({fingerprint[..16]}...)");
                return true;
            }
            return string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>certificate changed: ask the user (default No). If accepted, the new one becomes the remembered one</summary>
        public static bool ConfirmChanged(string kind, string host, int port, string newFingerprint)
        {
            var old = Get(kind, host, port) ?? "";
            static string Short(string f) => f.Length >= 32 ? f[..32] + "..." : f;
            var ok = Lemora.Utils.MessageBoxHelper.Confirm(
                string.Format(IoC.Translate("db_cert_changed_text"), host, port, kind, Short(old), Short(newFingerprint)),
                IoC.Translate("db_cert_accept_new"), IoC.Translate("db_target_dont_connect"), IoC.Translate("db_cert_changed_title"), defaultNo: true);
            if (ok) { Set(kind, host, port, newFingerprint); SimpleLogHelper.Warning($"Data source {kind} {host}:{port}: new certificate accepted by the user"); }
            else SimpleLogHelper.Warning($"Data source {kind} {host}:{port}: certificate changed, connection refused");
            return ok;
        }

        /// <summary>
        /// TLS certificate of a MySQL server: server greeting, SSL request (as the client does) and TLS handshake; sends no credentials.
        /// null if the server does not respond or does not offer SSL
        /// </summary>
        public static string? ProbeMysql(string host, int port, int timeoutMs = 5000)
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs)) throw new TimeoutException($"{host}:{port} does not respond");
            var ns = tcp.GetStream();
            ns.ReadTimeout = timeoutMs; ns.WriteTimeout = timeoutMs;
            var head = ReadExact(ns, 4);
            int len = head[0] | head[1] << 8 | head[2] << 16;
            if (len <= 0 || len > 64 * 1024) throw new IOException("invalid MySQL server greeting");
            var hello = ReadExact(ns, len);
            if (hello[0] == 0xFF) throw new IOException("the MySQL server refused the connection");
            if (hello[0] != 10) throw new IOException("unsupported MySQL protocol");
            int i = 1;
            while (i < hello.Length && hello[i] != 0) i++;   // server version
            i += 1 + 4 + 8 + 1;                               // string terminator, connection id, first 8 bytes of salt, filler
            if (i + 2 > hello.Length) throw new IOException("MySQL server greeting too short");
            int caps = hello[i] | hello[i + 1] << 8;
            if ((caps & 0x0800) == 0) return null;            // CLIENT_SSL not offered
            // SSLRequest: capabilities (PROTOCOL_41, SSL, SECURE_CONNECTION, LONG_PASSWORD, PLUGIN_AUTH), max packet size, character set, 23 zeros
            var req = new byte[4 + 32];
            req[0] = 32; req[3] = 1;
            BitConverter.GetBytes(0x0200 | 0x0800 | 0x8000 | 0x0001 | 0x80000).CopyTo(req, 4);
            BitConverter.GetBytes(16 * 1024 * 1024).CopyTo(req, 8);
            req[12] = 45;
            ns.Write(req, 0, req.Length);
            string? fp = null;
            using var ssl = new SslStream(ns, false, (_, cert, _, _) => { if (cert != null) fp = Fingerprint(cert); return true; });
            ssl.AuthenticateAsClient(new SslClientAuthenticationOptions { TargetHost = host });
            return fp;
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n]; int got = 0;
            while (got < n) { var r = s.Read(b, got, n - got); if (r <= 0) throw new IOException("connection closed by the server"); got += r; }
            return b;
        }
    }
}
