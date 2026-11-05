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
    /// Durata dei collegamenti (Installer/TEAMS-DESIGN.md sezione 6): per ogni sessione aperta verso una voce CONDIVISA (origine TeamsSource) si registra inizio
    /// e fine e li si carica al sync, insieme per PC e per voce. SOLO voci Teams, MAI la password. Registrazione leggera: una scrittura nel DB cifrato all'apertura
    /// e una alla chiusura. Piu' sessioni insieme: ognuna ha il suo id locale. App chiusa con sessione aperta: alla ripartenza la sessione risulta finita (vedi Load).
    /// </summary>
    internal static class TeamsSessionLog
    {
        internal class Rec
        {
            public string Id { get; set; } = "";       // id locale della sessione (guid): univoco per PC
            public string ConnId { get; set; } = "";    // id della voce sul server (Teams)
            public string Protocol { get; set; } = "";  // rdp | ssh | vnc
            public DateTime Start { get; set; }          // UTC
            public DateTime? End { get; set; }           // UTC, null = ancora in corso
        }

        private class Store
        {
            public List<Rec> Items { get; set; } = new List<Rec>();
            /// <summary>id -> era conclusa all'ultimo invio accettato: si rimanda solo cio' che cambia (apertura nuova, chiusura)</summary>
            public Dictionary<string, bool> Sent { get; set; } = new Dictionary<string, bool>();
        }

        private const string DbKey = "teams.sessions";
        private const string Prefix = "teams-";
        private const int MaxRecords = 500;      // tetto locale: le piu' vecchie si scartano
        private static readonly object _gate = new object();
        private static bool _orphansClosed;

        /// <summary>connectionId del host -> id locale della sessione, per ritrovare la riga alla chiusura (solo in memoria, dura quanto l'app)</summary>
        private static readonly Dictionary<string, string> _open = new Dictionary<string, string>();

        private static Store Load()
        {
            Store s;
            try { s = AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json) ? JsonSerializer.Deserialize<Store>(json!) ?? new Store() : new Store(); }
            catch { s = new Store(); }
            if (!_orphansClosed)
            {
                _orphansClosed = true;
                // l'app e' ripartita: le sessioni rimaste "aperte" sono finite col vecchio processo. Si chiudono all'avvio (poi vengono rimandate come concluse).
                // ponytail: la fine vera non si conosce; si usa l'ora d'avvio, che sovrastima del tempo di spegnimento (di norma breve). Upgrade: heartbeat per sessione.
                var now = DateTime.UtcNow; bool changed = false;
                foreach (var r in s.Items.Where(x => x.End == null)) { r.End = now; changed = true; }
                if (changed) Save(s);
            }
            return s;
        }

        private static void Save(Store s) => AppDbStore.Set(DbKey, JsonSerializer.Serialize(s));

        private static string? ServerId(ProtocolBase server) =>
            server.DataSource is TeamsSource && server.Id.StartsWith(Prefix, StringComparison.Ordinal) ? server.Id.Substring(Prefix.Length) : null;

        /// <summary>inizio di un collegamento a una voce Teams; per le voci locali non fa nulla (nessun record, nessun peso)</summary>
        public static void NoteOpen(ProtocolBase server, string connectionId)
        {
            try
            {
                var sid = ServerId(server);
                if (sid == null || sid.Length == 0 || string.IsNullOrEmpty(connectionId)) return;
                var protocol = server is SSH ? "ssh" : server is VNC ? "vnc" : "rdp";
                lock (_gate)
                {
                    var s = Load();
                    var id = Guid.NewGuid().ToString("N");
                    s.Items.Add(new Rec { Id = id, ConnId = sid, Protocol = protocol, Start = DateTime.UtcNow });
                    _open[connectionId] = id;
                    Save(s);
                }
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("Teams: registrazione apertura collegamento non riuscita: " + e.Message); }
        }

        /// <summary>fine del collegamento (chiusura della sessione); ignorata se non era una voce Teams</summary>
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
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("Teams: registrazione chiusura collegamento non riuscita: " + e.Message); }
        }

        private static string Iso(DateTime t) => t.ToUniversalTime().ToString("o");

        /// <summary>le righe da mandare: mai inviate, oppure inviate "in corso" e ora concluse</summary>
        internal static List<TeamsServerClient.SessionItem> Changed()
        {
            lock (_gate)
            {
                var s = Load();
                var send = new List<TeamsServerClient.SessionItem>();
                foreach (var r in s.Items)
                {
                    bool known = s.Sent.TryGetValue(r.Id, out var wasEnded);
                    if (!known || (!wasEnded && r.End != null))
                        send.Add(new TeamsServerClient.SessionItem(r.Id, r.ConnId, r.Protocol, Iso(r.Start), r.End == null ? null : Iso(r.End.Value)));
                }
                return send;
            }
        }

        /// <summary>dopo l'invio riuscito: segna cio' che il server ha accettato e sfoltisce lo storico</summary>
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
            // concluse, gia' inviate come tali e vecchie: via
            s.Items.RemoveAll(r => r.End != null && r.End < cutoff && s.Sent.TryGetValue(r.Id, out var sent) && sent);
            if (s.Items.Count > MaxRecords)
                foreach (var r in s.Items.OrderBy(x => x.Start).Take(s.Items.Count - MaxRecords).ToList()) s.Items.Remove(r);
            var ids = s.Items.Select(x => x.Id).ToHashSet();
            foreach (var k in s.Sent.Keys.Where(k => !ids.Contains(k)).ToList()) s.Sent.Remove(k);
        }
    }
}
