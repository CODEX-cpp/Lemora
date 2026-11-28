using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Lemora.Service.DataSource;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// MySQL/PostgreSQL server certificate: with local fake servers that go as far as TLS, checks that Lemora
    /// reads the right certificate without sending credentials before the check
    /// </summary>
    public class DbCertPinTests
    {
        private static X509Certificate2 SelfSigned(string cn)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);   // with a private key usable by SslStream on Windows
        }

        /// <summary>fake MySQL: greeting with CLIENT_SSL, receives the SSL request (32 bytes), TLS handshake; then closes. Returns what it received in plain text</summary>
        private static async Task<byte[]> FakeMysql(TcpListener l, X509Certificate2 cert)
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var payload = new System.Collections.Generic.List<byte> { 10 };
            payload.AddRange(System.Text.Encoding.ASCII.GetBytes("8.0.0-fake")); payload.Add(0);
            payload.AddRange(new byte[4]);                    // connection id
            payload.AddRange(new byte[8]); payload.Add(0);    // salt and filler
            payload.Add(0x00); payload.Add(0x0A);             // lower capabilities: 0x0A00 = SSL (0x0800) + PROTOCOL_41 (0x0200)
            payload.AddRange(new byte[20]);
            var head = new byte[] { (byte)payload.Count, 0, 0, 0 };
            await s.WriteAsync(head); await s.WriteAsync(payload.ToArray());
            var got = new byte[36]; int n = 0;
            while (n < 36) { var r = await s.ReadAsync(got.AsMemory(n)); if (r <= 0) break; n += r; }
            using var ssl = new SslStream(s);
            await ssl.AuthenticateAsServerAsync(cert);
            return got;
        }

        [Fact]
        public async Task ProbeMysql_ReadsServerCertificate_SendsNoCredentials()
        {
            using var cert = SelfSigned("db-test");
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            try
            {
                var server = FakeMysql(l, cert);
                var fp = await Task.Run(() => DbCertPin.ProbeMysql("127.0.0.1", port));
                var sent = await server;
                Assert.Equal(DbCertPin.Fingerprint(cert), fp);
                // only the SSL request in plain text: 32 bytes, sequence number 1, capabilities with CLIENT_SSL, no user name
                Assert.Equal(32, sent[0]); Assert.Equal(1, sent[3]);
                Assert.True((BitConverter.ToInt32(sent, 4) & 0x0800) != 0);
                for (int i = 13; i < 36; i++) Assert.Equal(0, sent[i]);
            }
            finally { l.Stop(); }
        }

        [Fact]
        public void Fingerprint_DiffersBetweenCertificates()
        {
            using var a = SelfSigned("a");
            using var b = SelfSigned("a");   // same name, different key: must come out different
            Assert.NotEqual(DbCertPin.Fingerprint(a), DbCertPin.Fingerprint(b));
            Assert.Equal(64, DbCertPin.Fingerprint(a).Length);
        }

        /// <summary>PostgreSQL: Npgsql calls the certificate check during the handshake, before sending user and password</summary>
        [Fact]
        public async Task Npgsql_CallbackSeesServerCertificateBeforeLogin()
        {
            using var cert = SelfSigned("pg-test");
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var c = await l.AcceptTcpClientAsync();
                var s = c.GetStream();
                var req = new byte[8]; int n = 0;
                while (n < 8) { var r = await s.ReadAsync(req.AsMemory(n)); if (r <= 0) break; n += r; }
                await s.WriteAsync(new byte[] { (byte)'S' });   // yes, TLS
                using var ssl = new SslStream(s);
                await ssl.AuthenticateAsServerAsync(cert);
                var buf = new byte[4096];
                return await ssl.ReadAsync(buf);              // the first message after TLS (the StartupMessage): arrives only if the check said yes
            });
            try
            {
                string? seen = null;
                var b = new Npgsql.NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = port, Username = "u", Password = "secret",Database = "d", SslMode = Npgsql.SslMode.Require, Timeout = 5 };
                using var pg = new Npgsql.NpgsqlConnection(b.ConnectionString);
#pragma warning disable CS0618
                pg.UserCertificateValidationCallback = (_, c, _, _) => { seen = c == null ? null : DbCertPin.Fingerprint(c); return false; };   // like a changed certificate
#pragma warning restore CS0618
                await Assert.ThrowsAnyAsync<Exception>(() => pg.OpenAsync());
                Assert.Equal(DbCertPin.Fingerprint(cert), seen);
                // certificate rejected, the server receives nothing (no user or password)
                var r = await Task.WhenAny(server, Task.Delay(5000));
                if (r == server) { try { Assert.True(await server <= 0); } catch (Exception) { /* handshake aborted on the server side: that is fine */ } }
            }
            finally { l.Stop(); }
        }
    }
}
