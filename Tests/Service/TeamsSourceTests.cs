using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Service;
using Lemora.Service.DataSource.Model;
using Xunit;

namespace Tests.Service
{
    /// <summary>Teams data source: read-only, disabled entries excluded, locked after 7 days without contact with the server</summary>
    public class TeamsSourceTests
    {
        private static List<TeamsItem> Items() => new()
        {
            new TeamsItem { Id = "a", Name = "One", Protocol = "rdp", Host = "10.0.0.1", Port = 3389, Folder = "A/B", Tags = "x,y" },
            new TeamsItem { Id = "b", Name = "Two",Protocol = "ssh", Host = "10.0.0.2", Port = 22, Disabled = true },
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
        public void Local_users_of_a_shared_item_arrive_with_their_password_ready_to_connect()
        {
            Lemora.Utils.DataVault.SetKey(Enumerable.Repeat((byte)7, 32).ToArray());   // like the other tests: the vault is locked without a key
            var db = new TeamsDatabase();
            db.Set(new List<TeamsItem> { new TeamsItem { Id = "u", Name = "Node",Protocol = "ssh", Host = "nd1", Port = 22, User = "",
                LUsers = new List<TeamsLocalUser> { new TeamsLocalUser { User = "operator", Password = "pw-op" } } } }, DateTime.Now);
            var p = Assert.Single(db.GetServers().Items);
            var c = (Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd)p.Clone();
            c.DecryptToConnectLevel();
            var cand = Assert.Single(Lemora.Service.UserResolver.GetCandidates(c));
            Assert.Equal("operator", cand.UserName);
            Assert.Equal("pw-op", cand.Password);   // without a password the connection asks for credentials
            Assert.NotEqual(true, c.AskPasswordWhenConnect);
        }

        [Fact]
        public void Saved_ask_password_flag_does_not_survive_once_the_item_has_users()
        {
            var p = new Lemora.Model.Protocol.SSH { Address = "nd1", Port = "22" };
            p.Users.Add(new Lemora.Model.Protocol.Base.UserEntry { UserName = "operator", Password = "x" });
            TeamsOverlay.Apply(p, "{\"AskPasswordWhenConnect\":true}");
            Assert.NotEqual(true, p.AskPasswordWhenConnect);
            var empty = new Lemora.Model.Protocol.SSH { Address = "nd1", Port = "22" };
            TeamsOverlay.Apply(empty, "{\"AskPasswordWhenConnect\":true}");
            Assert.Equal(true, empty.AskPasswordWhenConnect);   // without users the prompt stays
        }

        [Fact]
        public void Rdp_display_performance_survives_save_and_apply()
        {
            var a = new Lemora.Model.Protocol.RDP { Address = "h", Port = "3389", DisplayPerformance = Lemora.Model.Protocol.EDisplayPerformance.Low };
            var json = a.ToJsonString();
            Assert.Contains("DisplayPerformance", json);
            var b = new Lemora.Model.Protocol.RDP { Address = "h", Port = "3389" };
            TeamsOverlay.Apply(b, json);
            Assert.Equal(Lemora.Model.Protocol.EDisplayPerformance.Low, b.DisplayPerformance);
            var c = (Lemora.Model.Protocol.RDP?)new Lemora.Model.Protocol.RDP().CreateFromJsonString(json);   // as the database reads it back
            Assert.Equal(Lemora.Model.Protocol.EDisplayPerformance.Low, c?.DisplayPerformance);
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
