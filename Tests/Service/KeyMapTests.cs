using System.Collections.Generic;
using Lemora.Model.Protocol;
using Lemora.Service;
using Lemora.Utils;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// Key mapping: value of a key in a connection = its override if there is one, otherwise the default of its protocol;
    /// RDP and VNC defaults are separate; the connection keeps only the keys changed by hand
    /// </summary>
    [Collection("KeyMap")]
    public class KeyMapTests
    {
        private static KeyMap.Data Use(KeyMap.Data d) { KeyMap.TestData = () => d; return d; }

        [Fact]
        public void FactoryDefaults_AllToRemote_ExceptCtrlAltDel()
        {
            Use(new KeyMap.Data());
            try
            {
                foreach (var k in KeyMap.All)
                    Assert.Equal(!k.Fixed, KeyMap.ToRemote(new RDP(), k.Id));
                Assert.False(KeyMap.ToRemote(new VNC(), "cad"));
            }
            finally { KeyMap.TestData = null; }
        }

        [Fact]
        public void RdpAndVnc_Separate()
        {
            var d = Use(new KeyMap.Data());
            try
            {
                d.Rdp.Keys["altTab"] = false;
                Assert.False(KeyMap.ToRemote(new RDP(), "altTab"));
                Assert.True(KeyMap.ToRemote(new VNC(), "altTab"));
            }
            finally { KeyMap.TestData = null; }
        }

        [Fact]
        public void Override_WinsOverDefault_AndFollowsOtherDefaults()
        {
            var d = Use(new KeyMap.Data());
            try
            {
                var rdp = new RDP { KeyOverrides = "win=0" };
                Assert.False(KeyMap.ToRemote(rdp, "win"));
                Assert.True(KeyMap.ToRemote(rdp, "winR"));
                d.Rdp.Keys["winR"] = false;   // a default changed later reaches the connection
                Assert.False(KeyMap.ToRemote(rdp, "winR"));
                d.Rdp.Keys["win"] = false;
                rdp.KeyOverrides = "win=1";
                Assert.True(KeyMap.ToRemote(rdp, "win"));
            }
            finally { KeyMap.TestData = null; }
        }

        [Theory]
        [InlineData("win=0;altTab=1", "altTab=1;win=0")]   // page order
        [InlineData("win=0;doesnotexist=1;winR=x", "win=0")]  // unknown ids and values dropped
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Overrides_ParseAndFormat(string? text, string expected) => Assert.Equal(expected, KeyMap.FormatOverrides(KeyMap.ParseOverrides(text)));

        [Theory]
        [InlineData('R', false, "winR")]
        [InlineData('L', false, "winL")]
        [InlineData('S', true, "winShiftS")]
        [InlineData('S', false, "win")]     // Win+S is not on the page: it follows "win"
        [InlineData(0xBE, false, "winDot")]
        [InlineData(0x09, false, "winTab")]
        [InlineData('1', false, "win")]
        public void WinCombos(int vk, bool shift, string id) => Assert.Equal(id, KeyMap.WinCombo(vk, shift));

        [Theory]
        [InlineData(0x09, false, true, false, "altTab")]
        [InlineData(0x1B, false, true, false, "altEsc")]
        [InlineData(0x73, false, true, false, "altF4")]
        [InlineData(0x1B, true, false, false, "ctrlEsc")]
        [InlineData(0x1B, true, false, true, "ctrlShiftEsc")]
        [InlineData('W', true, false, false, "ctrlW")]
        [InlineData('W', true, false, true, null)]   // Ctrl+Shift+W is another combination
        [InlineData(0x09, true, false, false, "ctrlTab")]
        [InlineData(0x09, true, false, true, "ctrlTab")] // Ctrl+Shift+Tab: previous tab, same rule
        [InlineData('A', true, false, false, null)]
        [InlineData(0x1B, false, false, false, null)]
        public void ModCombos(int vk, bool ctrl, bool alt, bool shift, string? id) => Assert.Equal(id, KeyMap.ModCombo(vk, ctrl, alt, shift));

        [Fact]
        public void NewRdp_TakesScreenDefaults()
        {
            var d = Use(new KeyMap.Data());
            try
            {
                d.RdpScreen.Full = ERdpFullScreenFlag.Disable; d.RdpScreen.FsBar = false; d.RdpScreen.Res = ERdpWindowResizeMode.Fixed;
                d.RdpScreen.SysScale = false; d.RdpScreen.Scale = 150; d.RdpScreen.MonFit = true;
                var r = new RDP();
                KeyMap.ApplyNewConnectionDefaults(r);
                Assert.Equal(ERdpFullScreenFlag.Disable, r.RdpFullScreenFlag);
                Assert.False(r.IsFullScreenWithConnectionBar);
                Assert.Equal(ERdpWindowResizeMode.Fixed, r.RdpWindowResizeMode);
                Assert.False(r.IsScaleFactorFollowSystem);
                Assert.Equal(150u, r.ScaleFactorCustomValue);
                Assert.True(r.RdpFullScreenFollowMonitors);
            }
            finally { KeyMap.TestData = null; }
        }

        [Fact]
        public void Overrides_SavedWithTheConnection()
        {
            var r = new RDP { KeyOverrides = "win=0" };
            var back = (RDP)new RDP().CreateFromJsonString(r.ToJsonString())!;
            Assert.Equal("win=0", back.KeyOverrides);
            var v = new VNC { KeyOverrides = "altTab=0" };
            Assert.Equal("altTab=0", ((VNC)new VNC().CreateFromJsonString(v.ToJsonString())!).KeyOverrides);
        }

        [Fact]
        public void SessionMenu_SameAsDefault_NoOverride()
        {
            var d = Use(new KeyMap.Data());
            try
            {
                var rdp = new RDP();
                Lemora.View.Host.SessionKeysMenu.Set(rdp, "altTab", false);
                Assert.Equal("altTab=0", rdp.KeyOverrides);
                Lemora.View.Host.SessionKeysMenu.Set(rdp, "altTab", true);   // back to the default: the connection follows the page again
                Assert.Equal("", rdp.KeyOverrides);
                d.Rdp.Keys["win"] = false;
                Lemora.View.Host.SessionKeysMenu.Set(rdp, "win", true);      // different from a default that is off
                Assert.Equal("win=1", rdp.KeyOverrides);
            }
            finally { KeyMap.TestData = null; }
        }

        [Fact]
        public void FreeRdp_NeverOwnKeyboardGrab()
        {
            var a = Lemora.Model.ProtocolRunner.FreeRdpRunner.BuildArguments(new RDP { DisplayName = "pc", Address = "10.0.0.5", Port = "3389", EnableKeyCombinations = true }, false, "");
            Assert.Contains("-grab-keyboard", a);
            Assert.DoesNotContain("+grab-keyboard", a);
        }
    }
}
