using System.Collections.Generic;
using Lemora.Setup;
using Xunit;
using Req = Lemora.Setup.TeamsServerClient.ReqStatus;

namespace Tests.Service
{
    /// <summary>esito delle richieste (permesso "Richieste") ricevuto al sync: quali si chiudono e quali restano in attesa</summary>
    public class TeamsRequestOutcomeTests
    {
        private static List<TeamsSync.PendingRequest> Pending() => new()
        {
            new() { Id = "a", Kind = "update", ConnId = "c1", Name = "Uno" },
            new() { Id = "b", Kind = "delete", ConnId = "c2", Name = "Due" },
            new() { Id = "c", Kind = "create", Name = "Tre" },
            new() { Id = "d", Kind = "update", ConnId = "c4", Name = "Quattro" },
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
            // server vecchio senza "requests" nel sync: non si deve perdere l'etichetta in attesa
            var p = Pending();
            Assert.False(TeamsSync.ApplyRequestOutcomes(p, new List<Req>()));
            Assert.Equal(4, p.Count);
        }

        [Fact]
        public void MissingFromNonEmptyList_IsDropped()
        {
            // decisa da oltre 30 giorni: il server non la manda piu'
            var p = Pending();
            Assert.True(TeamsSync.ApplyRequestOutcomes(p, new List<Req> { new("a", "pending", "") }));
            Assert.Equal(new[] { "a" }, p.ConvertAll(x => x.Id));
        }
    }
}
