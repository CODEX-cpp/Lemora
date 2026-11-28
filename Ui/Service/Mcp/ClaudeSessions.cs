using System;
using System.Collections.Generic;
using System.Linq;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// Which sessions are Claude's (AI tab, "Claude is using this session" panel, yellow border): the SESSION Claude opened or used, not the server.
    /// Keyed by server, the marks stayed after Claude's session ended without chiudi_sessione, and the user's own session on that server hours later
    /// got them. Not thread safe: the caller locks. T = the session (HostBase; a plain class in the tests).
    /// </summary>
    internal sealed class ClaudeSessions<T> where T : class
    {
        public static readonly TimeSpan OpeningFor = TimeSpan.FromMinutes(10);   // the user may need time to type the password
        public static readonly TimeSpan ReconnectFor = TimeSpan.FromMinutes(3);  // back within this = automatic reconnection, still Claude's

        private readonly Func<T, string> _serverId;
        private readonly Dictionary<string, DateTime> _opening = new();         // servers Claude is opening: their next session to connect becomes Claude's
        private readonly Dictionary<T, DateTime?> _owned = new(ReferenceEqualityComparer.Instance as IEqualityComparer<T>);   // value = when it stopped being connected

        public ClaudeSessions(Func<T, string> serverId) => _serverId = serverId;

        /// <summary>Claude asked to open this server</summary>
        public void Opening(string serverId, DateTime now) => _opening[serverId] = now;

        /// <summary>Claude acts on this session: it is Claude's. True if it was not already</summary>
        public bool Own(T session)
        {
            var added = !_owned.TryGetValue(session, out var dropped) || dropped != null;
            _owned[session] = null;
            return added;
        }

        /// <summary>closed by Claude</summary>
        public void Release(T session) { _owned.Remove(session); _opening.Remove(_serverId(session)); }

        /// <summary>the attempt to open the server failed or was cancelled: a later session of the user is not Claude's</summary>
        public void OpeningEnded(string serverId) => _opening.Remove(serverId);

        /// <summary>a session of the server connected: if Claude was opening it, that one (preferably <paramref name="preferred"/>) becomes Claude's</summary>
        public void Connected(string serverId, IEnumerable<T> sessionsOfServer, T? preferred)
        {
            if (!_opening.Remove(serverId)) return;
            var list = sessionsOfServer.ToList();
            var s = preferred != null && list.Contains(preferred) ? preferred : list.FirstOrDefault(x => !_owned.ContainsKey(x));
            if (s != null) _owned[s] = null;
        }

        /// <summary>a server Claude is interested in: its events are news for Claude</summary>
        public bool Follows(string serverId) => _opening.ContainsKey(serverId) || _owned.Keys.Any(h => _serverId(h) == serverId);

        /// <summary>Claude's sessions that are connected now; forgets closed sessions and sessions the user reconnected long after they dropped</summary>
        public List<T> Current(IReadOnlyCollection<T> open, Func<T, bool> isConnected, DateTime now)
        {
            foreach (var id in _opening.Where(o => now - o.Value > OpeningFor).Select(o => o.Key).ToList()) _opening.Remove(id);
            foreach (var h in _owned.Keys.ToList())
            {
                if (!open.Contains(h)) { _owned.Remove(h); continue; }   // tab closed
                if (!isConnected(h)) _owned[h] ??= now;
                else if (_owned[h] is { } dropped) { if (now - dropped > ReconnectFor) _owned.Remove(h); else _owned[h] = null; }
            }
            return _owned.Keys.Where(isConnected).ToList();
        }
    }
}
