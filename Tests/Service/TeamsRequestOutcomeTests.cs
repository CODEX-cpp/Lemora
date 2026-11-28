using System.Collections.Generic;
using Lemora.Setup;
using Xunit;
using Req = Lemora.Setup.TeamsServerClient.ReqStatus;

namespace Tests.Service
{
    /// <summary>outcome of requests ("Requests" permission) received at sync: which ones close and which stay pending</summary>
    public class TeamsRequestOutcomeTests
    {
        private static List<TeamsSync.PendingRequest> Pending() => new()
        {
            new() { Id = "a", Kind = "update", ConnId = "c1", Name = "One" },
            new() { Id = "b", Kind = "delete", ConnId = "c2", Name = "Two" },
            new() { Id = "c", Kind = "create", Name = "Three" },
            new() { Id = "d", Kind = "update", ConnId = "c4", Name = "Four" },
        };

        [Fact]
        public void ApprovedAndRejectedClose_PendingAndStaleStay()
        {
            var p = Pending();
            var closed = TeamsSync.ApplyRequestOutcomes(p, new List<Req> { new("a", "approved", ""), new("b", "rejected", "no"), new("c", "pending", ""), new("d", "stale", "") });
            Assert.True(closed);
            Assert.Equal(new[] { "c", "d" }, p.ConvertAll(x => x.Id));
        }

        [Fact]
        public void EmptyServerList_KeepsEverything()
        {
            // old server without "requests" in the sync: the pending label must not be lost
            var p = Pending();
            Assert.False(TeamsSync.ApplyRequestOutcomes(p, new List<Req>()));
            Assert.Equal(4, p.Count);
        }

        [Fact]
        public void ApprovedCreate_ProvisionalCopyIsNotReportedAsGone()
        {
            // approved new entry: the provisional "req-" copy gives way to the real one, it is not "deleted by the administrator"
            var before = new List<Lemora.Service.DataSource.Model.TeamsItem> { new() { Id = "req-r1", Name = "PC02 Carla", Protocol = "vnc" }, new() { Id = "k1", Name = "Other" } };
            var after = new List<Lemora.Service.DataSource.Model.TeamsItem> { new() { Id = "s9", Name = "PC02 Carla", Protocol = "vnc" } };
            Assert.Equal(new[] { "Other" }, TeamsSync.GoneNames(before, after));
        }

        [Fact]
        public void OfflineCreateRequest_ShownRightAway_ThenTakesTheRequestId()
        {
            // "Requests" permission, server unreachable: the new entry must show up at once (before: nothing appeared and the save looked lost)
            var items = new List<Lemora.Service.DataSource.Model.TeamsItem> { new() { Id = "k1", Name = "Other" } };
            var queue = new List<TeamsSync.QueuedWrite>();
            var q = TeamsSync.QueueRequest(items, queue, "", new Lemora.Service.DataSource.Model.TeamsItem { Name = "New PC", Protocol = "rdp", Version = 3 }, "New PC");
            var prov = Assert.Single(items, x => x.Name == "New PC");
            Assert.StartsWith(TeamsSync.ProvisionalPrefix, prov.Id);
            Assert.Equal(0, prov.Version);
            Assert.Equal("", Assert.Single(queue).Id);   // still sent as "create"
            // back online: the request leaves, the copy takes the request id and closes like an online one when the administrator decides
            var connId = TeamsSync.RequestSent(items, q, "r7");
            Assert.Equal("req-r7", connId);
            Assert.Equal("req-r7", prov.Id);
            var pending = new List<TeamsSync.PendingRequest> { new() { Id = "r7", Kind = "create", ConnId = connId, Name = "New PC" } };
            Assert.True(TeamsSync.ApplyRequestOutcomes(pending, new List<Req> { new("r7", "approved", "") }, items));
            Assert.Equal(new[] { "k1" }, items.ConvertAll(x => x.Id));
        }

        [Fact]
        public void OfflineUpdateRequest_NoProvisionalCopy()
        {
            // a change to an existing entry stays as it is until approved (the clock comes from the queued id)
            var items = new List<Lemora.Service.DataSource.Model.TeamsItem> { new() { Id = "k1", Name = "Other" } };
            var queue = new List<TeamsSync.QueuedWrite>();
            var q = TeamsSync.QueueRequest(items, queue, "k1", new Lemora.Service.DataSource.Model.TeamsItem { Id = "k1", Name = "New name" }, "New name");
            Assert.Single(items);
            Assert.Equal("", q.ProvId);
            Assert.Equal("k1", TeamsSync.RequestSent(items, q, "r8"));
        }

        [Fact]
        public void PendingNewEntry_EditedThenDeleted()
        {
            // a new entry waiting for the administrator can be changed and deleted (before: refused until the decision)
            var items = new List<Lemora.Service.DataSource.Model.TeamsItem> { new() { Id = "req-r1", Name = "New PC", Protocol = "rdp" }, new() { Id = "k1", Name = "Other" } };
            var pending = new List<TeamsSync.PendingRequest> { new() { Id = "r1", Kind = "create", ConnId = "req-r1", Name = "New PC" } };
            TeamsSync.ApplyPendingEdit(items, pending, "req-r1", new Lemora.Service.DataSource.Model.TeamsItem { Id = "req-r1", Name = "Renamed PC", Protocol = "rdp", Version = 5, User = "second" });
            var e = Assert.Single(items, x => x.Id == "req-r1");
            Assert.Equal("Renamed PC", e.Name);
            Assert.Equal("second", e.User);
            Assert.Equal(0, e.Version);
            Assert.Equal("Renamed PC", Assert.Single(pending).Name);   // the notices use the new name
            TeamsSync.ApplyPendingEdit(items, pending, "req-r1", null);
            Assert.Equal(new[] { "k1" }, items.ConvertAll(x => x.Id));
            Assert.Empty(pending);   // withdrawn: no longer waiting
        }

        [Fact]
        public void WithdrawnOnServer_Closes()
        {
            var p = Pending();
            Assert.True(TeamsSync.ApplyRequestOutcomes(p, new List<Req> { new("c", "withdrawn", ""), new("a", "pending", ""), new("b", "pending", ""), new("d", "pending", "") }));
            Assert.Equal(new[] { "a", "b", "d" }, p.ConvertAll(x => x.Id));
        }

        [Fact]
        public void MissingFromNonEmptyList_IsDropped()
        {
            // decided over 30 days ago: the server no longer sends it
            var p = Pending();
            Assert.True(TeamsSync.ApplyRequestOutcomes(p, new List<Req> { new("a", "pending", "") }));
            Assert.Equal(new[] { "a" }, p.ConvertAll(x => x.Id));
        }
    }
}
