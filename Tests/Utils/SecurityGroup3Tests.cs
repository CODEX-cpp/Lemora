using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Lemora.Service;
using Lemora.Service.Mcp;
using Xunit;
using Assert = Xunit.Assert;

namespace Tests.Utils
{
    /// <summary>security fixes: identity of RDP PCs (certificate read before FreeRDP) and MCP port taken by another program</summary>
    public class SecurityGroup3Tests
    {
        /// <summary>fake RDP PC: answers the X.224 request choosing TLS (or refusing it) and then does TLS with a self-signed certificate</summary>
        private static (int port, Task server, X509Certificate2 cert) FakeRdp(bool tls)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=pc-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = X509CertificateLoader.LoadPkcs12(req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)).Export(X509ContentType.Pfx), null);
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                try
                {
                    using var c = await l.AcceptTcpClientAsync();
                    var s = c.GetStream();
                    var reqBytes = new byte[19]; int n = 0; while (n < 19) n += await s.ReadAsync(reqBytes.AsMemory(n));
                    // TPKT + X.224 CC + RDP_NEG_RSP (type 2, chosen protocol 1 = SSL) or RDP_NEG_FAILURE (type 3)
                    var cc = new byte[] { 0x03, 0x00, 0x00, 0x13, 0x0E, 0xD0, 0x00, 0x00, 0x12, 0x34, 0x00, (byte)(tls ? 0x02 : 0x03), 0x00, 0x08, 0x00, (byte)(tls ? 0x01 : 0x02), 0x00, 0x00, 0x00 };
                    await s.WriteAsync(cc);
                    if (!tls) return;
                    using var ssl = new SslStream(s);
                    await ssl.AuthenticateAsServerAsync(cert);
                }
                catch { /* the client closes after reading the certificate */ }
                finally { l.Stop(); }
            });
            return (port, server, cert);
        }

        [Fact]
        public async Task RdpCert_FingerprintReadFromServer()
        {
            var (port, server, cert) = FakeRdp(tls: true);
            var p = RdpCertPin.Fetch("127.0.0.1", port);
            await Task.WhenAny(server, Task.Delay(5000));
            Assert.Equal(RdpCertPin.Kind.Tls, p.Kind);
            Assert.Equal(RdpCertPin.Fingerprint(cert.RawData), p.Fingerprint);
            Assert.Equal(64, p.Fingerprint!.Length);
        }

        [Fact]
        public async Task RdpCert_ServerWithoutTls_Recognized()
        {
            var (port, server, _) = FakeRdp(tls: false);
            var p = RdpCertPin.Fetch("127.0.0.1", port);
            await Task.WhenAny(server, Task.Delay(5000));
            Assert.Equal(RdpCertPin.Kind.NoTls, p.Kind);
        }

        [Fact]
        public void RdpCert_NobodyListening_Unreachable()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Assert.Equal(RdpCertPin.Kind.Unreachable, RdpCertPin.Fetch("127.0.0.1", port, 2000).Kind);
        }

        [Fact]
        public void McpPort_OwnerFound()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            try
            {
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                Assert.Contains(Environment.ProcessId, PortOwner.ListenerPids(port));
                Assert.Contains($"pid {Environment.ProcessId}", PortOwner.Describe(Environment.ProcessId));
            }
            finally { l.Stop(); }
        }
    }
}
