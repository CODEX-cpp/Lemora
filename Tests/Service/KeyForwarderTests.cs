using System.Collections.Generic;
using System.Drawing;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.Mcp;
using Lemora.Utils;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// keyboard hook of the sessions with the Key mapping rules: what goes to the session, what is typed again on this PC.
    /// A fake FreeRDP session records the keys it receives; no real keyboard is used
    /// </summary>
    [Collection("KeyMap")]
    public class KeyForwarderTests
    {
        private sealed class FakeSession : IMcpScreen
        {
            public ProtocolBase ProtocolServer { get; init; } = new RDP();
            public readonly List<string> Keys = new();
            public Bitmap? McpCapture() => null;
            public Size? McpScreenSize() => null;
            public void McpPointer(byte mask, int x, int y) { }
            public Point? McpToScreen(int x, int y) => null;
            public void SendKey(uint keysym, bool pressed) => Keys.Add($"{keysym:X}{(pressed ? "+" : "-")}");
            public void SendChord(uint[] modifiers, uint key) { }
        }

        private const int Win = 0x5B, Tab = 0x09, AltDown = 0x20;
        private const string SuperDown = "FFEB+", SuperUp = "FFEB-";

        private static (FakeSession s, List<(int, bool)[]> injected) Setup(string overrides = "")
        {
            KeyMap.TestData = () => new KeyMap.Data();
            var s = new FakeSession { ProtocolServer = new RDP { KeyOverrides = overrides } };
            var injected = new List<(int, bool)[]>();
            VncKeyForwarder.GetFocusedHost = () => null;
            VncKeyForwarder.GetActiveVncHost = () => null;
            VncKeyForwarder.GetFocusedRdp = () => s;
            VncKeyForwarder.InjectLocal = k => injected.Add(k);
            return (s, injected);
        }

        private static void Done()
        {
            KeyMap.TestData = null;
            VncKeyForwarder.GetFocusedRdp = () => null;
            VncKeyForwarder.InjectLocal = _ => { };
        }

        [Fact]
        public void WinR_ToRemote()
        {
            var (s, inj) = Setup();
            try
            {
                Assert.True(VncKeyForwarder.TryForward(Win, 0, false));   // held back
                Assert.Empty(s.Keys);
                Assert.False(VncKeyForwarder.TryForward('R', 0, false));  // R goes on to the session, after Win
                Assert.Equal(new[] { SuperDown }, s.Keys);
                Assert.False(VncKeyForwarder.TryForward('R', 0, true));
                Assert.True(VncKeyForwarder.TryForward(Win, 0, true));
                Assert.Equal(new[] { SuperDown, SuperUp }, s.Keys);
                Assert.Empty(inj);
            }
            finally { Done(); }
        }

        [Fact]
        public void WinR_OnThisPc_WhenOverridden()
        {
            var (s, inj) = Setup("winR=0");
            try
            {
                Assert.True(VncKeyForwarder.TryForward(Win, 0, false));
                Assert.True(VncKeyForwarder.TryForward('R', 0, false));   // typed again here: Win+R of this PC
                Assert.Equal(new (int, bool)[] { (Win, false), ('R', false), ('R', true) }, inj[0]);
                Assert.True(VncKeyForwarder.TryForward('R', 0, true));    // its release does not reach the session
                Assert.True(VncKeyForwarder.TryForward(Win, 0, true));
                Assert.Equal(new (int, bool)[] { (Win, true) }, inj[1]);
                Assert.Empty(s.Keys);                                     // nothing to the remote PC
            }
            finally { Done(); }
        }

        [Fact]
        public void WinAlone_FollowsWinRule()
        {
            var (s, inj) = Setup();
            try
            {
                Assert.True(VncKeyForwarder.TryForward(Win, 0, false));
                Assert.True(VncKeyForwarder.TryForward(Win, 0, false));   // auto-repeat
                Assert.True(VncKeyForwarder.TryForward(Win, 0, true));
                Assert.Equal(new[] { SuperDown, SuperUp }, s.Keys);
                Assert.Empty(inj);
            }
            finally { Done(); }
            (s, inj) = Setup("win=0");
            try
            {
                VncKeyForwarder.TryForward(Win, 0, false);
                VncKeyForwarder.TryForward(Win, 0, true);
                Assert.Empty(s.Keys);
                Assert.Equal(new (int, bool)[] { (Win, false), (Win, true) }, inj[0]);   // Start menu of this PC
            }
            finally { Done(); }
        }

        [Fact]
        public void NoSession_WinGoesToWindows()
        {
            var (s, inj) = Setup();
            VncKeyForwarder.GetFocusedRdp = () => null;
            try
            {
                Assert.False(VncKeyForwarder.TryForward(Win, 0, false));
                Assert.False(VncKeyForwarder.TryForward(Win, 0, true));
                Assert.Empty(s.Keys); Assert.Empty(inj);
            }
            finally { Done(); }
        }

        [Fact]
        public void AltTab_ToRemote_OrToWindows()
        {
            var (s, _) = Setup();
            try
            {
                Assert.True(VncKeyForwarder.TryForward(Tab, AltDown, false));
                Assert.True(VncKeyForwarder.TryForward(Tab, AltDown, true));
                Assert.Equal(new[] { "FF09+", "FF09-" }, s.Keys);
            }
            finally { Done(); }
            (s, _) = Setup("altTab=0");
            try
            {
                Assert.False(VncKeyForwarder.TryForward(Tab, AltDown, false));   // Windows shows its window switcher
                Assert.False(VncKeyForwarder.TryForward(Tab, AltDown, true));
                Assert.Empty(s.Keys);
            }
            finally { Done(); }
        }

        [Fact]
        public void PrintScreen_ToRemote_OrToWindows()
        {
            const int PrtSc = 0x2C;
            var (s, _) = Setup();
            try
            {
                Assert.True(VncKeyForwarder.TryForward(PrtSc, 0, false));
                Assert.True(VncKeyForwarder.TryForward(PrtSc, 0, true));
                Assert.Equal(new[] { "FF61+", "FF61-" }, s.Keys);
            }
            finally { Done(); }
            (s, _) = Setup("prtSc=0");
            try
            {
                Assert.False(VncKeyForwarder.TryForward(PrtSc, 0, false));   // Windows captures this PC's screen
                Assert.False(VncKeyForwarder.TryForward(PrtSc, 0, true));
                Assert.Empty(s.Keys);
            }
            finally { Done(); }
        }

        [Fact]
        public void InjectedKeys_NeverTouched()
        {
            var (s, inj) = Setup();
            try
            {
                Assert.False(VncKeyForwarder.TryForward(Win, 0x10, false));
                Assert.False(VncKeyForwarder.TryForward(Win, 0x10, true));
                Assert.Empty(s.Keys); Assert.Empty(inj);
            }
            finally { Done(); }
        }
    }
}
