using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lemora.Service.DataSource.Model;
using Lemora.Setup;
using Xunit;

namespace Tests.Service
{
    /// <summary>programma del server Teams vero, avviato in un processo nascosto con dati usa e getta; l'amministratore e' pilotato via HTTP</summary>
    public sealed class TeamsServerFixture : IAsyncLifetime
    {
        public int Port { get; private set; }
        private Process? _proc;
        private string _work = "";
        private HttpClient _admin = null!;

        private static string ExePath => typeof(TeamsServerFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ServerExe").Value!;

        public async ValueTask InitializeAsync()
        {
            var exe = Path.GetFullPath(ExePath);
            Assert.True(File.Exists(exe), $"server non compilato: {exe}");
            _work = Path.Combine(Path.GetTempPath(), "teams-client-test-" + Guid.NewGuid().ToString("N"));
            var data = Path.Combine(_work, "data");
            Directory.CreateDirectory(data);
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

            _proc = Process.Start(new ProcessStartInfo(exe, $"--foreground --data \"{data}\" --port {Port}") { CreateNoWindow = true, UseShellExecute = false })!;
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, CookieContainer = new CookieContainer(), UseCookies = true };
            _admin = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{Port}") };
            _admin.DefaultRequestHeaders.Add("X-Lemora", "1");

            for (int i = 0; i < 60; i++)
            {
                try { if ((await _admin.GetAsync("/api/state", TestContext.Current.CancellationToken)).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
            var setupCode = (await File.ReadAllTextAsync(Path.Combine(data, "setup-code.txt"), TestContext.Current.CancellationToken)).Trim();
            Assert.True((await Post("/api/setup/verify", new { code = setupCode })).IsSuccessStatusCode);
            Assert.True((await Post("/api/setup/complete", new { dbDir = Path.Combine(_work, "db"), user = "admin", password = "Password-di-prova-123", port = Port, address = "all" })).IsSuccessStatusCode);
            Assert.True((await Post("/api/login", new { user = "admin", password = "Password-di-prova-123" })).IsSuccessStatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            try { _proc?.Kill(true); _proc?.WaitForExit(5000); } catch { /* gia' chiuso */ }
            _admin?.Dispose();
            await Task.Delay(300);
            try { Directory.Delete(_work, true); } catch { /* ci riprova il sistema con la pulizia dei temporanei */ }
        }

        private Task<HttpResponseMessage> Post(string path, object body) =>
            _admin.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        public async Task<string> NewCodeAsync()
        {
            var r = await Post("/api/pairing/new", new { });
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("code").GetString()!;
        }

        public async Task<bool> DecideAsync(string id, string action, string? permission = null, bool noCopy = false)
        {
            var r = await Post($"/api/pcs/{id}/{action}", new { permission, noCopy });
            return r.IsSuccessStatusCode;
        }
    }

    public class TeamsClientTests : IClassFixture<TeamsServerFixture>
    {
        private readonly TeamsServerFixture _srv;
        public TeamsClientTests(TeamsServerFixture srv) => _srv = srv;
        private TeamsServerClient NewClient(string? pinned = null) => new($"127.0.0.1:{_srv.Port}", pinned);

        [Theory]
        [InlineData("192.168.1.10", "192.168.1.10", 25353)]
        [InlineData("192.168.1.10:9000", "192.168.1.10", 9000)]
        [InlineData("https://srv.local:8443/", "srv.local", 8443)]
        [InlineData("  10.0.0.5  ", "10.0.0.5", 25353)]
        [InlineData("[fe80::1]:8443", "fe80::1", 8443)]
        [InlineData("[fe80::1]", "fe80::1", 25353)]
        [InlineData("::1", "::1", 25353)]
        public void ParseServer_AcceptsTheUsualWaysOfWritingAnAddress(string text, string host, int port)
        {
            Assert.Equal((host, port), TeamsServerClient.ParseServer(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://192.168.1.10")]
        [InlineData("host:abc")]
        [InlineData("host:70000")]
        [InlineData("host:0")]
        [InlineData("[::1")]
        public void ParseServer_RejectsWhatIsNotAnAddress(string text)
        {
            Assert.Throws<TeamsException>(() => TeamsServerClient.ParseServer(text));
        }

        [Theory]
        [InlineData("10.1.2.3", true)] [InlineData("172.16.0.1", true)] [InlineData("172.31.255.255", true)] [InlineData("192.168.0.5", true)]
        [InlineData("169.254.1.1", true)] [InlineData("127.0.0.1", true)] [InlineData("::1", true)] [InlineData("fd00::1", true)]
        [InlineData("fe80::1", true)] [InlineData("::ffff:192.168.1.1", true)]
        [InlineData("8.8.8.8", false)] [InlineData("172.32.0.1", false)] [InlineData("172.15.0.1", false)] [InlineData("11.0.0.1", false)]
        [InlineData("192.169.0.1", false)] [InlineData("2001:db8::1", false)] [InlineData("::ffff:8.8.8.8", false)] [InlineData("100.64.0.1", false)]
        public void IsLocalAddress_OnlyPrivateAndLoopbackRanges(string ip, bool expected)
        {
            Assert.Equal(expected, TeamsServerClient.IsLocalAddress(IPAddress.Parse(ip)));
        }

        [Fact]
        public async Task PublicAddress_IsRefusedBeforeAnyConnection()
        {
            using var c = new TeamsServerClient("8.8.8.8:8443");
            var ex = await Assert.ThrowsAsync<TeamsException>(() => c.RequestAsync("ABCDEF", "PC", "user", "1.0", TestContext.Current.CancellationToken));
            Assert.Equal("address", ex.Kind);
        }

        [Fact]
        public async Task ServerNotRunning_IsReportedAsUnreachable()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int closed = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            using var c = new TeamsServerClient($"127.0.0.1:{closed}");
            var ex = await Assert.ThrowsAsync<TeamsException>(() => c.RequestAsync("ABCDEF", "PC", "user", "1.0", TestContext.Current.CancellationToken));
            Assert.Equal("unreachable", ex.Kind);
        }

        [Fact]
        public async Task WrongCode_ReportsAttemptsLeft()
        {
            using var c = NewClient();
            var ex = await Assert.ThrowsAsync<TeamsException>(() => c.RequestAsync("ZZZZZZ", "PC", "user", "1.0", TestContext.Current.CancellationToken));
            Assert.Equal("code", ex.Kind);
            Assert.InRange(ex.Remaining, 1, 4);
        }

        [Fact]
        public async Task Certificate_IsRememberedOnFirstUse_AndMustBeTheSameAfter()
        {
            var ct = TestContext.Current.CancellationToken;
            using var first = NewClient();
            var code = await _srv.NewCodeAsync();
            var req = await first.RequestAsync(code, "PCFP", "user", "1.0", ct);
            Assert.Equal(64, req.Fingerprint.Length);
            Assert.Equal(req.Fingerprint, first.SeenFingerprint);

            // con l'impronta giusta il collegamento riesce (il server risponde, anche se per un id che non conosce)
            using var good = NewClient(req.Fingerprint);
            var notFound = await Assert.ThrowsAsync<TeamsException>(() => good.StatusAsync("0123456789abcdef0123456789abcdef", "x", false, ct));
            Assert.Equal("server", notFound.Kind);
            // con un'impronta diversa (come se qualcuno si mettesse in mezzo) si rifiuta prima di dire qualsiasi cosa
            using var bad = NewClient(new string('0', 64));
            var ex = await Assert.ThrowsAsync<TeamsException>(() => bad.StatusAsync(req.Id, req.Token, false, ct));
            Assert.Equal("certificate", ex.Kind);
        }

        [Fact]
        public async Task FullPairing_FromRequestToRevocation()
        {
            var ct = TestContext.Current.CancellationToken;
            var link = new TeamsLink { State = "pending" };
            using var c = NewClient();
            var code = await _srv.NewCodeAsync();
            var req = await c.RequestAsync(code, "PC10", "User01", "1.0", ct);

            // in attesa: nessuna chiave, il collegamento non cambia
            var s = await c.StatusAsync(req.Id, req.Token, false, ct);
            Assert.Equal("pending", s.State);
            Assert.False(TeamsLinkService.Apply(link, s));
            Assert.Equal("pending", link.State);

            // l'amministratore approva con scrittura e "non copiare"
            Assert.True(await _srv.DecideAsync(req.Id, "approve", "write", noCopy: true));
            s = await c.StatusAsync(req.Id, req.Token, false, ct);
            Assert.Equal("approved", s.State);
            Assert.NotNull(s.Key);
            Assert.True(TeamsLinkService.Apply(link, s));
            Assert.Equal(("approved", "write", true), (link.State, link.Permission, link.NoCopy));
            var key = link.Key;

            // la chiave arriva ancora finche' non si conferma; dopo la conferma non piu'
            Assert.Equal(key, (await c.StatusAsync(req.Id, req.Token, false, ct)).Key);
            Assert.Null((await c.StatusAsync(req.Id, req.Token, true, ct)).Key);

            // controllo periodico con la chiave; il cambio di permesso arriva al client
            var me = await c.MeAsync(key, ct);
            Assert.Equal(("approved", "write"), (me.State, me.Permission));
            Assert.False(TeamsLinkService.Apply(link, me));
            Assert.True(await _srv.DecideAsync(req.Id, "update", "read", noCopy: false));
            me = await c.MeAsync(key, ct);
            Assert.True(TeamsLinkService.Apply(link, me));
            Assert.Equal(("read", false), (link.Permission, link.NoCopy));

            // scollegato dall'amministratore: il client lo sa e dimentica la chiave
            Assert.True(await _srv.DecideAsync(req.Id, "revoke"));
            me = await c.MeAsync(key, ct);
            Assert.Equal("revoked", me.State);
            Assert.True(TeamsLinkService.Apply(link, me));
            Assert.Equal(("revoked", ""), (link.State, link.Key));
        }

        [Fact]
        public async Task WritingSharedConnections_FollowsPermissionNoCopyAndVersion()
        {
            var ct = TestContext.Current.CancellationToken;
            using var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), "PC12", "User03", "1.0", ct);
            Assert.True(await _srv.DecideAsync(req.Id, "approve", "read"));
            var key = (await c.StatusAsync(req.Id, req.Token, false, ct)).Key!;
            await c.StatusAsync(req.Id, req.Token, true, ct); // conferma: da qui il server non la rimanda
            var item = new TeamsItem { Name = "T1", Protocol = "rdp", Host = "10.0.0.9", Port = 3389, Password = "p1" };
            async Task<string> Kind(Func<Task> f) => (await Assert.ThrowsAsync<TeamsException>(f)).Kind;

            // sola lettura: niente scrittura
            Assert.Equal("permission", await Kind(() => c.WriteAsync(key, "", item, ct)));

            // scrittura + "non copiare": non si aggiunge
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", noCopy: true));
            Assert.Equal("nocopy", await Kind(() => c.WriteAsync(key, "", item, ct)));

            // scrittura libera: aggiunge, modifica (con la versione vista), rifiuta la versione vecchia, cancella
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", noCopy: false));
            var added = (await c.WriteAsync(key, "", item, ct)).Item!;
            Assert.Equal((1, "p1"), (added.Version, added.Password));
            added.Name = "T2";
            var updated = (await c.WriteAsync(key, added.Id, added, ct)).Item!;
            Assert.Equal((2, "T2"), (updated.Version, updated.Name));
            Assert.Equal("conflict", await Kind(() => c.WriteAsync(key, added.Id, added, ct))); // added.Version e' ancora 1
            Assert.Equal("input", await Kind(() => c.WriteAsync(key, "", new TeamsItem { Name = "X", Protocol = "rdp", Host = "h", Port = 0 }, ct)));

            // "non copiare" non impedisce di modificare o cancellare
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", noCopy: true));
            updated.Name = "T3";
            Assert.Equal(3, (await c.WriteAsync(key, updated.Id, updated, ct)).Item!.Version);
            Assert.Null((await c.WriteAsync(key, updated.Id, null, ct)).Item);
            Assert.Equal("notfound", await Kind(() => c.WriteAsync(key, updated.Id, null, ct)));
        }

        [Fact]
        public async Task CancelRequest_RemovesItFromTheServer()
        {
            var ct = TestContext.Current.CancellationToken;
            using var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), "PC13", "User04", "1.0", ct);
            Assert.Equal("pending", (await c.StatusAsync(req.Id, req.Token, false, ct)).State);
            await c.CancelAsync(req.Id, req.Token, ct);
            await Assert.ThrowsAsync<TeamsException>(() => c.StatusAsync(req.Id, req.Token, false, ct)); // il server non la conosce piu' (404)
            await c.CancelAsync(req.Id, req.Token, ct); // annullare di nuovo non e' un errore
        }

        [Fact]
        public async Task RejectedRequest_IsReportedToTheClient()
        {
            var ct = TestContext.Current.CancellationToken;
            var link = new TeamsLink { State = "pending" };
            using var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), "PC11", "User02", "1.0", ct);
            Assert.True(await _srv.DecideAsync(req.Id, "reject"));
            var s = await c.StatusAsync(req.Id, req.Token, false, ct);
            Assert.True(TeamsLinkService.Apply(link, s));
            Assert.Equal("rejected", link.State);
        }

        [Theory]
        [InlineData("pending", "approved", true, "approved")]   // approvata con la chiave
        [InlineData("pending", "approved", false, "revoked")]   // approvata ma la chiave e' gia' stata consegnata e questo PC non l'ha: serve un nuovo abbinamento
        [InlineData("pending", "rejected", false, "rejected")]
        [InlineData("pending", "revoked", false, "revoked")]
        [InlineData("pending", "pending", false, "pending")]
        [InlineData("approved", "revoked", false, "revoked")]
        [InlineData("approved", "approved", false, "approved")]
        [InlineData("rejected", "approved", true, "rejected")]  // una richiesta chiusa non si riapre da sola
        public void Apply_DecidesTheNewStateFromTheServerAnswer(string from, string server, bool withKey, string expected)
        {
            var link = new TeamsLink { State = from, Key = from == "approved" ? "id.key" : "" };
            TeamsLinkService.Apply(link, new TeamsServerClient.PairStatus(server, withKey ? "id.key" : null, "read", false));
            Assert.Equal(expected, link.State);
        }
    }
}
