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
using Lemora.Service.Mcp;
using Lemora.Setup;
using Xunit;

namespace Tests.Service
{
    /// <summary>the real Teams server program, started in a hidden process with throwaway data; the administrator is driven over HTTP</summary>
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
            Assert.True(File.Exists(exe), $"server not built: {exe}");
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
            Assert.True((await Post("/api/setup/complete", new { dbDir = Path.Combine(_work, "db"), user = "admin", password = "Test-password-123", port = Port, address = "all" })).IsSuccessStatusCode);
            Assert.True((await Post("/api/login", new { user = "admin", password = "Test-password-123" })).IsSuccessStatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            try { _proc?.Kill(true); _proc?.WaitForExit(5000); } catch { /* already closed */ }
            _admin?.Dispose();
            await Task.Delay(300);
            try { Directory.Delete(_work, true); } catch { /* the system retries with its temp file cleanup */ }
        }

        private Task<HttpResponseMessage> Post(string path, object body) =>
            _admin.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        public async Task<string> NewCodeAsync()
        {
            var r = await Post("/api/pairing/new", new { });
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("code").GetString()!;
        }

        public async Task<bool> DecideAsync(string id, string action, string? permission = null, bool noCopy = false, bool? mcp = null)
        {
            var r = mcp == null ? await Post($"/api/pcs/{id}/{action}", new { permission, noCopy }) : await Post($"/api/pcs/{id}/{action}", new { permission, noCopy, mcp });
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

            // with the right fingerprint the connection succeeds (the server answers, even if for an id it does not know)
            using var good = NewClient(req.Fingerprint);
            var notFound = await Assert.ThrowsAsync<TeamsException>(() => good.StatusAsync("0123456789abcdef0123456789abcdef", "x", false, ct));
            Assert.Equal("server", notFound.Kind);
            // with a different fingerprint (as if someone were in the middle) it refuses before saying anything
            using var bad = NewClient(new string('0', 64));
            var ex = await Assert.ThrowsAsync<TeamsException>(() => bad.StatusAsync(req.Id, req.Token, false, ct));
            Assert.Equal("certificate", ex.Kind);
        }

        [Fact]
        public async Task McpServerPermission_OnByDefault_TurnedOffAndOnByTheAdministrator()
        {
            var ct = TestContext.Current.CancellationToken;
            var link = new TeamsLink { State = "pending" };
            using var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), "PCMCP", "User02", "1.0", ct);

            // approved without saying anything about MCP: allowed, for all shared entries
            Assert.True(await _srv.DecideAsync(req.Id, "approve", "read"));
            Assert.True(TeamsLinkService.Apply(link, await c.StatusAsync(req.Id, req.Token, false, ct)));
            Assert.Equal((true, true), (link.Claude?.Enabled, link.Claude?.AllServers));
            Assert.False(new TeamsClaudePolicy.Snapshot { Linked = true, Policy = link.Claude }.BlocksAll);

            // turned off: the client receives it and the MCP server is blocked
            Assert.True(await _srv.DecideAsync(req.Id, "update", "read", mcp: false));
            Assert.True(TeamsLinkService.Apply(link, await c.MeAsync(link.Key, ct)));
            Assert.False(link.Claude?.Enabled);
            Assert.True(new TeamsClaudePolicy.Snapshot { Linked = true, Policy = link.Claude }.BlocksAll);

            // the other permissions keep working; turned on again
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", mcp: true));
            Assert.True(TeamsLinkService.Apply(link, await c.MeAsync(link.Key, ct)));
            Assert.Equal(("write", true), (link.Permission, link.Claude?.Enabled));
        }

        [Fact]
        public async Task FullPairing_FromRequestToRevocation()
        {
            var ct = TestContext.Current.CancellationToken;
            var link = new TeamsLink { State = "pending" };
            using var c = NewClient();
            var code = await _srv.NewCodeAsync();
            var req = await c.RequestAsync(code, "PC10", "User01", "1.0", ct);

            // pending: no key, the link does not change
            var s = await c.StatusAsync(req.Id, req.Token, false, ct);
            Assert.Equal("pending", s.State);
            Assert.False(TeamsLinkService.Apply(link, s));
            Assert.Equal("pending", link.State);

            // the administrator approves with write and "do not copy"
            Assert.True(await _srv.DecideAsync(req.Id, "approve", "write", noCopy: true));
            s = await c.StatusAsync(req.Id, req.Token, false, ct);
            Assert.Equal("approved", s.State);
            Assert.NotNull(s.Key);
            Assert.True(TeamsLinkService.Apply(link, s));
            Assert.Equal(("approved", "write", true), (link.State, link.Permission, link.NoCopy));
            var key = link.Key;

            // the key does not stay in plain text on the server (security review point 4): a second pickup before confirmation creates a new one; after confirmation none
            var key2 = (await c.StatusAsync(req.Id, req.Token, false, ct)).Key;
            Assert.False(string.IsNullOrEmpty(key2));
            Assert.NotEqual(key, key2);
            Assert.Null((await c.StatusAsync(req.Id, req.Token, true, ct)).Key);

            // periodic check with the key; the permission change reaches the client
            var me = await c.MeAsync(key, ct);
            Assert.Equal(("approved", "write"), (me.State, me.Permission));
            Assert.False(TeamsLinkService.Apply(link, me));
            Assert.True(await _srv.DecideAsync(req.Id, "update", "read", noCopy: false));
            me = await c.MeAsync(key, ct);
            Assert.True(TeamsLinkService.Apply(link, me));
            Assert.Equal(("read", false), (link.Permission, link.NoCopy));

            // unlinked by the administrator: the client knows it and forgets the key
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
            await c.StatusAsync(req.Id, req.Token, true, ct); // confirm: from here on the server does not send it again
            var item = new TeamsItem { Name = "T1", Protocol = "rdp", Host = "10.0.0.9", Port = 3389, Password = "p1" };
            async Task<string> Kind(Func<Task> f) => (await Assert.ThrowsAsync<TeamsException>(f)).Kind;

            // read-only: no writing
            Assert.Equal("permission", await Kind(() => c.WriteAsync(key, "", item, ct)));

            // write + "do not copy": cannot add
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", noCopy: true));
            Assert.Equal("nocopy", await Kind(() => c.WriteAsync(key, "", item, ct)));

            // unrestricted write: adds, edits (with the version seen), refuses the old version, deletes
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", noCopy: false));
            var added = (await c.WriteAsync(key, "", item, ct)).Item!;
            Assert.Equal((1, "p1"), (added.Version, added.Password));
            added.Name = "T2";
            var updated = (await c.WriteAsync(key, added.Id, added, ct)).Item!;
            Assert.Equal((2, "T2"), (updated.Version, updated.Name));
            Assert.Equal("conflict", await Kind(() => c.WriteAsync(key, added.Id, added, ct))); // added.Version is still 1
            Assert.Equal("input", await Kind(() => c.WriteAsync(key, "", new TeamsItem { Name = "X", Protocol = "rdp", Host = "h", Port = 0 }, ct)));

            // "do not copy" does not prevent editing or deleting
            Assert.True(await _srv.DecideAsync(req.Id, "update", "write", noCopy: true));
            updated.Name = "T3";
            Assert.Equal(3, (await c.WriteAsync(key, updated.Id, updated, ct)).Item!.Version);
            Assert.Null((await c.WriteAsync(key, updated.Id, null, ct)).Item);
            Assert.Equal("notfound", await Kind(() => c.WriteAsync(key, updated.Id, null, ct)));
        }

        [Fact]
        public async Task CreationSentAgainWithTheSameKey_DoesNotDuplicate()
        {
            // slow network: the server creates the entry but the reply is lost, the PC sends the same creation again from the queue
            var ct = TestContext.Current.CancellationToken;
            using var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), "PC16", "User07", "1.0", ct);
            Assert.True(await _srv.DecideAsync(req.Id, "approve", "write"));
            var key = (await c.StatusAsync(req.Id, req.Token, false, ct)).Key!;
            await c.StatusAsync(req.Id, req.Token, true, ct);
            var item = new TeamsItem { Name = "SRVDUP", Protocol = "rdp", Host = "10.0.0.77", Port = 3389, Password = "p1" };
            var first = (await c.WriteAsync(key, "", item, ct, createKey: "local-abc123")).Item!;
            var again = (await c.WriteAsync(key, "", item, ct, createKey: "local-abc123")).Item!;
            Assert.Equal(first.Id, again.Id);
            // another key (another entry with the same data, made on purpose) is a new entry
            var other = (await c.WriteAsync(key, "", item, ct, createKey: "local-def456")).Item!;
            Assert.NotEqual(first.Id, other.Id);
            var (_, _, items) = await c.SyncAsync(key, 0, ct);
            Assert.Equal(2, items.Count(x => x.Name == "SRVDUP" && !x.Deleted));
        }

        [Fact]
        public async Task CancelRequest_RemovesItFromTheServer()
        {
            var ct = TestContext.Current.CancellationToken;
            using var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), "PC13", "User04", "1.0", ct);
            Assert.Equal("pending", (await c.StatusAsync(req.Id, req.Token, false, ct)).State);
            await c.CancelAsync(req.Id, req.Token, ct);
            await Assert.ThrowsAsync<TeamsException>(() => c.StatusAsync(req.Id, req.Token, false, ct)); // the server no longer knows it (404)
            await c.CancelAsync(req.Id, req.Token, ct); // cancelling again is not an error
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
        [InlineData("pending", "approved", true, "approved")]   // approved with the key
        [InlineData("pending", "approved", false, "revoked")]   // approved but the key was already handed out and this PC does not have it: a new pairing is needed
        [InlineData("pending", "rejected", false, "rejected")]
        [InlineData("pending", "revoked", false, "revoked")]
        [InlineData("pending", "pending", false, "pending")]
        [InlineData("approved", "revoked", false, "revoked")]
        [InlineData("approved", "approved", false, "approved")]
        [InlineData("rejected", "approved", true, "rejected")]  // a closed request does not reopen by itself
        public void Apply_DecidesTheNewStateFromTheServerAnswer(string from, string server, bool withKey, string expected)
        {
            var link = new TeamsLink { State = from, Key = from == "approved" ? "id.key" : "" };
            TeamsLinkService.Apply(link, new TeamsServerClient.PairStatus(server, withKey ? "id.key" : null, "read", false));
            Assert.Equal(expected, link.State);
        }
    }
}
