using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lemora.View.Host.ProtocolHosts;
using Xunit;

namespace Tests.Model
{
    /// <summary>automatic VNC reconnection: the check must tell a name that does not resolve apart from a PC that is off</summary>
    public class VncProbeTests
    {
        [Fact]
        public async Task NameThatDoesNotResolve_IsNameNotFound()
        {
            // .invalid never resolves (RFC 6761)
            var r = await VncHost.ProbeAsync("lemora-pc-inesistente.invalid", 5900, CancellationToken.None);
            Assert.Equal(VncHost.Probe.NameNotFound, r);
        }

        [Fact]
        public async Task ClosedPort_IsServiceOff_OpenPort_IsOpen()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            Assert.Equal(VncHost.Probe.Open, await VncHost.ProbeAsync("127.0.0.1", port, CancellationToken.None));
            l.Stop();
            Assert.Equal(VncHost.Probe.ServiceOff, await VncHost.ProbeAsync("127.0.0.1", port, CancellationToken.None));
        }
    }
}
