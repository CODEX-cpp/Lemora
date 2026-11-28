using System;
using System.Collections.Generic;
using Lemora.Service.Mcp;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// Claude's marks (AI tab, "Claude is controlling this session" panel, yellow border) belong to Claude's session, not to the server:
    /// once the user's own session on server X, hours after Claude's had ended, still showed them
    /// </summary>
    public class ClaudeSessionsTests
    {
        private sealed class S { public string Server = "x"; public bool Up = true; }

        private static readonly DateTime T0 = new(2026, 11, 18, 9, 0, 0);
        private static ClaudeSessions<S> New() => new(s => s.Server);
        private static List<S> Now(ClaudeSessions<S> c, DateTime at, params S[] open) => c.Current(open, s => s.Up, at);

        [Fact]
        public void ClaudeSessionDropped_TabClosed_UserSessionLater_NotClaude()
        {
            var c = New();
            var claude = new S();
            c.Opening("x", T0);
            c.Connected("x", new[] { claude }, claude);
            Assert.Single(Now(c, T0, claude));
            claude.Up = false;                                    // Claude's session ends from inside Windows (no chiudi_sessione)
            Assert.Empty(Now(c, T0.AddMinutes(1), claude));
            Assert.Empty(Now(c, T0.AddMinutes(2)));               // its tab is closed
            var user = new S();                                    // 3 hours later the user opens server X
            Assert.Empty(Now(c, T0.AddHours(3), user));
            Assert.False(c.Follows("x"));
        }

        [Fact]
        public void SameTabReconnectedByUserHoursLater_NotClaude()
        {
            var c = New();
            var s = new S();
            c.Own(s);
            s.Up = false;
            Assert.Empty(Now(c, T0, s));
            s.Up = true;
            Assert.Empty(Now(c, T0.AddHours(3), s));
        }

        [Fact]
        public void AutomaticReconnection_StaysClaude()
        {
            var c = New();
            var s = new S();
            c.Own(s);
            s.Up = false;
            Now(c, T0, s);
            s.Up = true;
            Assert.Single(Now(c, T0.AddSeconds(30), s));
        }

        [Fact]
        public void ClosedByClaude_UserSessionAfter_NotClaude()
        {
            var c = New();
            var claude = new S();
            c.Opening("x", T0);
            c.Connected("x", new[] { claude }, claude);
            c.Release(claude);
            var user = new S();
            c.Connected("x", new[] { user }, user);
            Assert.Empty(Now(c, T0.AddMinutes(5), user));
        }

        [Fact]
        public void OpeningFailed_UserSessionAfter_NotClaude()
        {
            var c = New();
            c.Opening("x", T0);
            c.OpeningEnded("x");
            var user = new S();
            c.Connected("x", new[] { user }, user);
            Assert.Empty(Now(c, T0.AddMinutes(1), user));
        }

        [Fact]
        public void OpeningForgottenAfterTenMinutes()
        {
            var c = New();
            c.Opening("x", T0);
            Now(c, T0.AddMinutes(11));
            var user = new S();
            c.Connected("x", new[] { user }, user);
            Assert.Empty(Now(c, T0.AddMinutes(12), user));
        }

        [Fact]
        public void UserSessionAlreadyOpen_ClaudeOpensAnother_OnlyClaudesIsMarked()
        {
            var c = New();
            var user = new S();
            var claude = new S();
            c.Opening("x", T0);
            c.Connected("x", new[] { user, claude }, claude);
            var mine = Now(c, T0, user, claude);
            Assert.Single(mine);
            Assert.Same(claude, mine[0]);
        }
    }
}
