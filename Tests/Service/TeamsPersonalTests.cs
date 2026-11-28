using System.Collections.Generic;
using System.Text.Json;
using Lemora.Setup;
using Xunit;
using Item = Lemora.Setup.TeamsServerClient.PersonalItem;
using St = Lemora.Setup.TeamsServerClient.PersonalStatus;

namespace Tests.Service
{
    /// <summary>the PC's personal connections sent to the server: what gets resent, state of the sent entries, toggle on by default</summary>
    public class TeamsPersonalTests
    {
        private static Item A(string host = "10.0.0.1", string pw = "x") => new("a", "Off", "One", "rdp", host, 3389, "u", pw, "");
        private static Item B() => new("b", "", "Two","ssh", "10.0.0.2", 22, "root", "y", "");

        [Fact]
        public void Diff_SendsOnlyNewOrChanged_AndRemoved()
        {
            var sent = new Dictionary<string, string> { ["a"] = TeamsSync.Fingerprint(A()), ["gone"] = "h" };
            var (changed, removed) = TeamsSync.PersonalDiff(new List<Item> { A(), B() }, sent);
            Assert.Equal(new[] { "b" }, changed.ConvertAll(x => x.LocalId));
            Assert.Equal(new[] { "gone" }, removed);
        }

        [Fact]
        public void Diff_PasswordChangeIsAChange()
        {
            var sent = new Dictionary<string, string> { ["a"] = TeamsSync.Fingerprint(A()) };
            var (changed, _) = TeamsSync.PersonalDiff(new List<Item> { A(pw: "new") }, sent);
            Assert.Single(changed);
        }

        [Fact]
        public void Outcomes_TrackStates()
        {
            var known = new Dictionary<string, string>();
            Assert.True(TeamsSync.ApplyPersonalOutcomes(known, new List<St> { new("a", "pending", ""), new("b", "published", "") }, new Dictionary<string, string>()));
            Assert.False(TeamsSync.ApplyPersonalOutcomes(known, new List<St> { new("a", "pending", ""), new("b", "published", "") }, new Dictionary<string, string>()));
            Assert.True(TeamsSync.ApplyPersonalOutcomes(known, new List<St> { new("a", "rejected", "no") }, new Dictionary<string, string> { ["a"] = "One" }));
            Assert.Equal("rejected", known["a"]);
        }

        [Fact]
        public void ShareToggle_OnByDefault_AlsoForOldSavedLinks()
        {
            Assert.True(new TeamsLink().SharePersonal);
            Assert.True(JsonSerializer.Deserialize<TeamsLink>("{\"State\":\"approved\",\"Permission\":\"write\"}")!.SharePersonal);
        }
    }
}
