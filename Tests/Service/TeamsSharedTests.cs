using System;
using System.Linq;
using System.Threading.Tasks;
using Lemora.Service;
using Lemora.Service.DataSource.Model;
using Lemora.Setup;
using Xunit;

namespace Tests.Service
{
    /// <summary>shared device passwords, folders and folder credentials: real client against the real server</summary>
    public class TeamsSharedTests : IClassFixture<TeamsServerFixture>
    {
        private readonly TeamsServerFixture _srv;
        public TeamsSharedTests(TeamsServerFixture srv) => _srv = srv;
        private TeamsServerClient NewClient() => new($"127.0.0.1:{_srv.Port}");

        private async Task<(TeamsServerClient c, string key, string id)> PairAsync(string pc, string permission)
        {
            var ct = TestContext.Current.CancellationToken;
            var c = NewClient();
            var req = await c.RequestAsync(await _srv.NewCodeAsync(), pc, "User", "1.0", ct);
            Assert.True(await _srv.DecideAsync(req.Id, "approve", permission));
            var key = (await c.StatusAsync(req.Id, req.Token, false, ct)).Key!;
            await c.StatusAsync(req.Id, req.Token, true, ct);
            return (c, key, req.Id);
        }

        private static string Kind(Func<Task> f) => Assert.ThrowsAsync<TeamsException>(f).GetAwaiter().GetResult().Kind;

        // Rule: read-only = view only; "requests" = credentials and devices only as a request to the administrator
        // (direct write refused), folders directly; write = everything directly

        [Fact]
        public async Task Devices_OnlyWriteWritesDirectly_ReadAndRequestsAreRefused_AndReachOtherPcsWithPasswords()
        {
            var ct = TestContext.Current.CancellationToken;
            var (writer, wkey, _) = await PairAsync("PCW", "write");
            var (reader, rkey, _) = await PairAsync("PCR", "read");
            var (asker, akey, _) = await PairAsync("PCQ", "requests");
            using var _w = writer; using var _r = reader; using var _q = asker;
            var id = Guid.NewGuid().ToString("N");
            var dev = new TeamsServerClient.SharedDevice(id, 0, "ACME/Site", "Synology", "10.0.4.40", new() { new DeviceUser { User = "admin", Password = "Secret-1" } }, false);

            Assert.Equal("permission", Kind(() => reader.DeviceWriteAsync(rkey, dev, true, ct)));   // read-only: cannot create
            Assert.Equal("permission", Kind(() => asker.DeviceWriteAsync(akey, dev, true, ct)));    // requests: only as a request
            var created = (await writer.DeviceWriteAsync(wkey, dev, true, ct))!;
            Assert.Equal((id, 1), (created.Id, created.Version));
            Assert.Equal("permission", Kind(() => reader.DeviceWriteAsync(rkey, created with { Name = "X" }, false, ct)));   // and cannot modify

            var (_, rev, _) = await reader.SyncAsync(rkey, 0, ct);
            var seen = reader.LastShared.Devices.Single(d => d.Id == id);
            Assert.Equal(("ACME/Site", "10.0.4.40", "admin", "Secret-1"), (seen.Folder, seen.Ip, seen.Users[0].User, seen.Users[0].Password));

            // edit with the version seen; with an old version the server refuses
            var changed = created with { Name = "Synology DS920", Version = created.Version };
            var updated = (await writer.DeviceWriteAsync(wkey, changed, false, ct))!;
            Assert.Equal(2, updated.Version);
            Assert.Equal("conflict", Kind(() => writer.DeviceWriteAsync(wkey, changed, false, ct)));

            await reader.SyncAsync(rkey, rev, ct);
            Assert.Equal("Synology DS920", reader.LastShared.Devices.Single(d => d.Id == id).Name);

            // deletion: read-only cannot delete; a writer can. It arrives as "deleted" to those who already had the entry; from scratch it no longer arrives
            Assert.Equal("permission", Kind(() => reader.DeviceDeleteAsync(rkey, id, ct)));
            await writer.DeviceDeleteAsync(wkey, id, ct);
            var (_, rev2, _) = await reader.SyncAsync(rkey, rev, ct);
            Assert.True(reader.LastShared.Devices.Single(d => d.Id == id).Deleted);
            await reader.SyncAsync(rkey, 0, ct);
            Assert.DoesNotContain(reader.LastShared.Devices, d => d.Id == id);
            Assert.True(rev2 > rev);
        }

        [Fact]
        public async Task Folders_RequestsAndWriteCreateRenameDelete_ReadOnlyCannot_NotNonEmptyOnes()
        {
            var ct = TestContext.Current.CancellationToken;
            var (ro, rokey, _) = await PairAsync("PCR2", "read");
            var (reader, rkey, _) = await PairAsync("PCQ2", "requests");   // "requests": creates folders directly
            var (writer, wkey, _) = await PairAsync("PCW2", "write");
            using var _ro = ro; using var _r = reader; using var _w = writer;

            Assert.Equal("permission", Kind(() => ro.FolderUpsertAsync(rokey, "Acme/Site", "ACME", "10.1.1.", ct)));   // read-only: no folders
            await reader.FolderUpsertAsync(rkey, "Acme/Site", "ACME", "10.1.1.", ct);
            var (_, rev, _) = await writer.SyncAsync(wkey, 0, ct);
            var f = writer.LastShared.Folders.Single(x => x.Path == "Acme/Site" && !x.Deleted);
            Assert.Equal(("ACME", "10.1.1."), (f.Domain, f.IpPrefix));

            await reader.FolderRenameAsync(rkey, "Acme/Site", "Acme/New", ct);
            await writer.SyncAsync(wkey, rev, ct);
            Assert.Contains(writer.LastShared.Folders, x => x.Path == "Acme/Site" && x.Deleted);
            Assert.Contains(writer.LastShared.Folders, x => x.Path == "Acme/New" && !x.Deleted);
            Assert.Equal("into_itself", Kind(() => reader.FolderRenameAsync(rkey, "Acme/New", "Acme/New/Inside", ct)));   // into itself

            // a folder containing a shared connection cannot be deleted, not even by a writer; an empty one can
            var conn = (await writer.WriteAsync(wkey, "", new TeamsItem { Name = "T1", Folder = "Acme/New", Protocol = "rdp", Host = "10.1.1.5", Port = 3389, Password = "p" }, ct)).Item!;
            Assert.Equal("notempty", Kind(() => reader.FolderDeleteAsync(rkey, "Acme/New", ct)));
            await writer.WriteAsync(wkey, conn.Id, null, ct);
            await reader.FolderDeleteAsync(rkey, "Acme/New", ct);
            var (_, rev3, _) = await writer.SyncAsync(wkey, 0, ct);
            Assert.DoesNotContain(writer.LastShared.Folders, x => x.Path == "Acme/New");
            Assert.True(rev3 > rev);
        }

        [Fact]
        public async Task FolderCredentials_OnlyWriteWritesDirectly_AndKeepThePasswordWhenNotSent()
        {
            var ct = TestContext.Current.CancellationToken;
            var (reader, rkey, _) = await PairAsync("PCR3", "read");
            var (writer, wkey, _) = await PairAsync("PCW3", "write");
            var (asker, akey, _) = await PairAsync("PCQ3", "requests");
            using var _r = reader; using var _w = writer; using var _q = asker;
            var cred = new TeamsServerClient.SharedCred("", 0, "Beta", "BETA\\admin", "", false);

            Assert.Equal("permission", Kind(() => reader.CredWriteAsync(rkey, cred, true, "First-1", ct)));   // read-only: cannot create
            Assert.Equal("permission", Kind(() => asker.CredWriteAsync(akey, cred, true, "First-1", ct)));    // requests: only as a request
            var created = (await writer.CredWriteAsync(wkey, cred, true, "First-1", ct))!;
            Assert.Equal("permission", Kind(() => reader.CredWriteAsync(rkey, created, false, "Other-2", ct)));   // and cannot modify
            Assert.Equal("exists", Kind(() => writer.CredWriteAsync(wkey, cred, true, "other", ct)));   // same folder and same user

            await reader.SyncAsync(rkey, 0, ct);
            Assert.Equal("First-1", reader.LastShared.Creds.Single(x => x.Id == created.Id).Password);

            var kept = (await writer.CredWriteAsync(wkey, created, false, null, ct))!;   // password not sent: the existing one is kept
            Assert.Equal(2, kept.Version);
            await reader.SyncAsync(rkey, 0, ct);
            Assert.Equal("First-1", reader.LastShared.Creds.Single(x => x.Id == created.Id).Password);

            Assert.Equal("permission", Kind(() => reader.CredDeleteAsync(rkey, created.Id, ct)));   // read-only cannot delete
            await writer.CredDeleteAsync(wkey, created.Id, ct);
            await reader.SyncAsync(rkey, 0, ct);
            Assert.DoesNotContain(reader.LastShared.Creds, x => x.Id == created.Id);
        }

        [Theory]
        [InlineData("Customers / Site", "Customers/Site")]
        [InlineData("One", "One")]
        [InlineData("", "")]
        public void FolderPaths_TravelWithSlashes(string appPath, string serverPath)
        {
            Assert.Equal(serverPath, TeamsShared.ToServerPath(appPath));
            Assert.Equal(appPath, TeamsShared.FromServerPath(serverPath));
        }
    }
}
