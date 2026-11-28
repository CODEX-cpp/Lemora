using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource.Model;
using Lemora.Service.Locality;

namespace Lemora.Setup
{
    /// <summary>
    /// Connection duration (Installer/TEAMS-DESIGN.md section 6): for every session opened to a SHARED entry (TeamsSource origin) start and end
    /// are recorded and uploaded at sync, together per PC and per entry. ONLY Teams entries, NEVER the password. Lightweight logging: one write to the encrypted DB at opening
    /// and one at closing. Several sessions at once: each has its own local id. App closed with a session open: at restart the session counts as ended (see Load).
    /// </summary>
    internal static class TeamsSessionLog
    {
        internal class Rec
        {
            public string Id { get; set; } = "";       // local session id (guid): unique per PC
            public string ConnId { get; set; } = "";    // id of the entry on the server (Teams)
            public string Protocol { get; set; } = "";  // rdp | ssh | vnc
            public DateTime Start { get; set; }          // UTC
            public DateTime? End { get; set; }           // UTC, null = still in progress
            /// <summary>only for NON-shared entries (saved on this PC or ad hoc IP): name, address and folder to show them in the console; empty for Teams entries (the server knows them)</summary>
            public string Name { get; set; } = "";
            public string Host { get; set; } = "";
            public string Folder { get; set; } = "";
            /// <summary>true = ad hoc connection (search with an unsaved IP)</summary>
            public bool Adhoc { get; set; }
        }

        private class Store
        {
            public List<Rec> Items { get; set; } = new List<Rec>();
            /// <summary>id -> was it ended at the last accepted upload: only what changes is resent (new opening, closing)</summary>
            public Dictionary<string, bool> Sent { get; set; } = new Dictionary<string, bool>();
        }

        private const string DbKey = "teams.sessions";
        private const string Prefix = "teams-";
        private const int MaxRecords = 500;      // local cap: the oldest ones are dropped
        private static readonly object _gate = new object();
        private static bool _orphansClosed;

        /// <summary>host connectionId -> local session id, to find the row again at closing (memory only, lasts as long as the app)</summary>
        private static readonly Dictionary<string, string> _open = new Dictionary<string, string>();

        private static Store Load()
        {
            Store s;
            try { s = AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json) ? JsonSerializer.Deserialize<Store>(json!) ?? new Store() : new Store(); }
            catch { s = new Store(); }
            if (!_orphansClosed)
            {
                _orphansClosed = true;
                // the app has restarted: sessions left "open" ended with the old process. They are closed at startup (then resent as ended).
                // ponytail: the real end is unknown; the startup time is used, which overestimates by the downtime (usually short). Upgrade: per-session heartbeat.
                var now = DateTime.UtcNow; bool changed = false;
                foreach (var r in s.Items.Where(x => x.End == null)) { r.End = now; changed = true; }
                if (changed) Save(s);
            }
            return s;
        }

        private static void Save(Store s) => AppDbStore.Set(DbKey, JsonSerializer.Serialize(s));

        private static string? ServerId(ProtocolBase server) =>
            server.DataSource is TeamsSource && server.Id.StartsWith(Prefix, StringComparison.Ordinal) ? server.Id.Substring(Prefix.Length) : null;

        /// <summary>start of a connection to a Teams entry; for local entries it does nothing (no record, no cost)</summary>
        public static void NoteOpen(ProtocolBase server, string connectionId)
        {
            try
            {
                if (string.IsNullOrEmpty(connectionId)) return;
                // only RDP, SSH and VNC (like shared entries); other protocols are not logged
                if (server is not (RDP or SSH or VNC)) return;
                var sid = ServerId(server);
                var protocol = server is SSH ? "ssh" : server is VNC ? "vnc" : "rdp";
                var rec = new Rec { Protocol = protocol, Start = DateTime.UtcNow };
                if (sid != null && sid.Length > 0) rec.ConnId = sid;
                else
                {
                    // entry saved on this PC or ad hoc connection: not on the server, so send what the console needs (never the password)
                    var u = server as ProtocolBaseWithAddressPort;
                    var address = (u?.Address ?? "").Trim();
                    rec.Adhoc = server.IsTmpSession();
                    rec.ConnId = rec.Adhoc ? "adhoc:" + address + ":" + (u?.Port ?? "") : "pc:" + server.Id;
                    rec.Name = rec.Adhoc ? "" : server.DisplayName ?? "";
                    rec.Host = string.IsNullOrEmpty(u?.Port) ? address : address + ":" + u!.Port;
                    rec.Folder = rec.Adhoc ? "" : string.Join("/", server.TreeNodes);
                }
                lock (_gate)
                {
                    var s = Load();
                    var id = Guid.NewGuid().ToString("N");
                    rec.Id = id;
                    s.Items.Add(rec);
                    _open[connectionId] = id;
                    Save(s);
                }
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("Teams: recording the connection opening failed: " + e.Message); }
        }

        /// <summary>end of the connection (session closed); ignored if it was not a Teams entry</summary>
        public static void NoteClose(string connectionId)
        {
            try
            {
                lock (_gate)
                {
                    if (!_open.TryGetValue(connectionId, out var id)) return;
                    _open.Remove(connectionId);
                    var s = Load();
                    var r = s.Items.FirstOrDefault(x => x.Id == id && x.End == null);
                    if (r == null) return;
                    r.End = DateTime.UtcNow;
                    Save(s);
                }
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("Teams: recording the connection closing failed: " + e.Message); }
        }

        private static string Iso(DateTime t) => t.ToUniversalTime().ToString("o");

        /// <summary>the rows to send: never sent, or sent as "in progress" and now ended</summary>
        internal static List<TeamsServerClient.SessionItem> Changed()
        {
            lock (_gate)
            {
                var s = Load();
                var send = new List<TeamsServerClient.SessionItem>();
                // "do not copy": non-shared entries (saved here or ad hoc) do not go to the server
                bool shareLocal = !TeamsLink.Load().NoCopy;
                foreach (var r in s.Items)
                {
                    bool local = !r.ConnId.Equals("") && (r.ConnId.StartsWith("pc:", StringComparison.Ordinal) || r.ConnId.StartsWith("adhoc:", StringComparison.Ordinal));
                    if (local && !shareLocal) continue;
                    bool known = s.Sent.TryGetValue(r.Id, out var wasEnded);
                    if (!known || (!wasEnded && r.End != null))
                        send.Add(new TeamsServerClient.SessionItem(r.Id, r.ConnId, r.Protocol, Iso(r.Start), r.End == null ? null : Iso(r.End.Value),
                            local ? r.Name : null, local ? r.Host : null, local ? r.Folder : null, local ? r.Adhoc : null));
                }
                return send;
            }
        }

        /// <summary>after a successful upload: marks what the server accepted and trims the history</summary>
        internal static void MarkSent(List<TeamsServerClient.SessionItem> sent)
        {
            lock (_gate)
            {
                var s = Load();
                foreach (var item in sent)
                {
                    var r = s.Items.FirstOrDefault(x => x.Id == item.Id);
                    if (r != null) s.Sent[r.Id] = r.End != null;
                }
                Prune(s);
                Save(s);
            }
        }

        private static void Prune(Store s)
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            // ended, already sent as such and old: remove
            s.Items.RemoveAll(r => r.End != null && r.End < cutoff && s.Sent.TryGetValue(r.Id, out var sent) && sent);
            if (s.Items.Count > MaxRecords)
                foreach (var r in s.Items.OrderBy(x => x.Start).Take(s.Items.Count - MaxRecords).ToList()) s.Items.Remove(r);
            var ids = s.Items.Select(x => x.Id).ToHashSet();
            foreach (var k in s.Sent.Keys.Where(k => !ids.Contains(k)).ToList()) s.Sent.Remove(k);
        }
    }
}
