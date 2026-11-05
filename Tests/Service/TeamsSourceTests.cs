using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Service.DataSource.Model;
using Xunit;

namespace Tests.Service
{
    /// <summary>origine dati Teams: sola lettura, voci disabilitate escluse, blocco dopo 7 giorni senza contatto col server</summary>
    public class TeamsSourceTests
    {
        private static List<TeamsItem> Items() => new()
        {
            new TeamsItem { Id = "a", Name = "Uno", Protocol = "rdp", Host = "10.0.0.1", Port = 3389, Folder = "A/B", Tags = "x,y" },
            new TeamsItem { Id = "b", Name = "Due", Protocol = "ssh", Host = "10.0.0.2", Port = 22, Disabled = true },
        };

        [Fact]
        public void Recent_copy_lists_enabled_items_only_with_folder_and_tags()
        {
            var db = new TeamsDatabase();
            db.Set(Items(), DateTime.Now);
            var r = db.GetServers();
            Assert.True(r.IsSuccess);
            var p = Assert.Single(r.Items);
            Assert.Equal("teams-a", p.Id);
            Assert.Equal(new[] { "A", "B" }, p.TreeNodes);
            Assert.Equal(new[] { "x", "y" }, p.Tags);
        }

        [Fact]
        public void Copy_older_than_seven_days_shows_nothing_and_comes_back_after_contact()
        {
            var db = new TeamsDatabase();
            db.Set(Items(), DateTime.Now.AddDays(-TeamsSource.MaxOfflineDays).AddMinutes(-1));
            Assert.Empty(db.GetServers().Items);
            db.Set(Items(), DateTime.Now);
            Assert.Single(db.GetServers().Items);
        }

        [Fact]
        public void Every_write_fails()
        {
            var db = new TeamsDatabase();
            Assert.False(db.SetConfig("k", "v").IsSuccess);
            Assert.False(db.DeleteServer(new[] { "teams-a" }).IsSuccess);
            Assert.False(new TeamsSource().IsWritable);
        }
    }
}
