using System.Collections.Generic;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource.Model;

namespace Lemora.Service.Mcp
{
    /// <summary>
    /// The Teams administrator's rule for Claude, received from the Teams server (responses of /api/client/me and of the sync, "claude" field).
    /// Enabled = false: Claude off for this user (total block). AllServers = true: all shared entries; otherwise only those in Servers (Teams server ids).
    /// The user's personal connections do not depend on this rule, only on their own settings (except for the total block).
    /// </summary>
    public class TeamsClaudePolicy
    {
        public bool Enabled { get; set; }
        public bool AllServers { get; set; }
        public List<string> Servers { get; set; } = new();

        public const string BlockedAllReason = "the Teams administrator has turned Claude off for this user";
        private const string TeamsIdPrefix = "teams-";

        /// <summary>the rule read only once for a whole list (TeamsLink lives in the database)</summary>
        public sealed class Snapshot
        {
            /// <summary>PC linked to a Teams server (approved)</summary>
            public bool Linked { get; init; }
            /// <summary>null = the Teams server sent no rule (old version or not set yet)</summary>
            public TeamsClaudePolicy? Policy { get; init; }

            public bool BlocksAll => Linked && Policy is { Enabled: false };

            /// <summary>
            /// entry shared by Teams that is not allowed. Without a rule from the server (Policy null: the Teams server does not handle it yet) the user's
            /// checkboxes decide; as soon as the server sends the rule, the administrator decides.
            /// </summary>
            public bool Blocks(ProtocolBase server)
            {
                if (!IsTeamsEntry(server)) return false;
                if (Policy == null) return false;
                if (Policy is not { Enabled: true } p) return true;
                return !p.AllServers && !p.Servers.Contains(server.Id.Substring(TeamsIdPrefix.Length));
            }
        }

        public static bool IsTeamsEntry(ProtocolBase server) => server.DataSource is TeamsSource || server.Id.StartsWith(TeamsIdPrefix);

        public static Snapshot Current()
        {
            try
            {
                var link = Lemora.Setup.TeamsLink.Load();
                return new Snapshot { Linked = link.State == "approved", Policy = link.Claude };
            }
            catch { return new Snapshot(); }
        }
    }
}
