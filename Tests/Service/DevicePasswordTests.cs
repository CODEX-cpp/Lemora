using System.Collections.Generic;
using Lemora.Service;
using Xunit;

namespace Tests.Service
{
    /// <summary>folder password manager: which entries apply to a server, search and IP prefix (the parts without a database)</summary>
    public class DevicePasswordTests
    {
        private static DeviceEntry E(string folder, string name = "x", string ip = "", params string[] users)
        {
            var e = new DeviceEntry { Folder = folder, Name = name, Ip = ip };
            foreach (var u in users) e.Users.Add(new DeviceUser { User = u, Password = "p" });
            return e;
        }

        [Theory]
        [InlineData("ACME", true)]                          // the same folder
        [InlineData("ACME / Site", true)]
        [InlineData("", true)]                              // no folder: applies to everyone
        [InlineData("ACME / Site / Room", false)]           // a subfolder further down does not apply
        [InlineData("Other customer", false)]
        [InlineData("ACME / Other site", false)]
        public void EntryAppliesToFolderAndAbove(string entryFolder, bool expected) =>
            Assert.Equal(expected, DevicePasswordService.AppliesTo(E(entryFolder), new List<string> { "ACME", "Site" }));

        [Theory]
        [InlineData("10.0.4.21", "10.0.4.")]
        [InlineData(" 192.0.2.5 ", "192.0.2.")]
        [InlineData("srv-web", "")]
        [InlineData("10.0.4", "")]
        [InlineData("10.0.4.300", "")]
        [InlineData("fe80::1", "")]
        [InlineData(null, "")]
        public void IpPrefixIsFirstThreeNumbers(string? address, string expected) => Assert.Equal(expected, DevicePasswordService.IpPrefix(address));

        [Theory]
        [InlineData("syno", true)]            // name
        [InlineData("10.0.4.4", true)]        // address
        [InlineData("BIANC", true)]           // user, case-insensitive
        [InlineData("zzz", false)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        public void SearchMatchesNameIpAndUser(string query, bool expected) =>
            Assert.Equal(expected, DevicePasswordService.Matches(E("ACME", "Synology DS920", "10.0.4.40", "Administrator", "Bianchi"), query));

        // a NAS with two network cards: both addresses belong to the same entry
        [Fact]
        public void MoreAddresses_SplitSetAndSearch()
        {
            Assert.Equal(new[] { "192.0.2.40", "https://192.0.2.41:5001", "nas" }, DevicePasswordService.SplitIps(" 192.0.2.40, https://192.0.2.41:5001;nas  192.0.2.40 "));
            var e = E("ACME", "NAS");
            DevicePasswordService.SetIps(e, new[] { "192.0.2.40", "", "192.0.2.41", "192.0.2.40" });
            Assert.Equal("192.0.2.40", e.Ip);
            Assert.Equal(new[] { "192.0.2.41" }, e.MoreIps);
            Assert.Equal(new[] { "192.0.2.40", "192.0.2.41" }, e.AllIps());
            Assert.True(DevicePasswordService.Matches(e, "2.41"));
            Assert.Equal(new[] { "192.0.2.41" }, e.Clone().MoreIps);
            Assert.NotSame(e.MoreIps, e.Clone().MoreIps);
        }
    }
}
