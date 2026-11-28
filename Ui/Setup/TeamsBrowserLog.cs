using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Service;
using Lemora.Service.Locality;

namespace Lemora.Setup
{
    /// <summary>
    /// Usage log of device passwords from the browser extension, Teams only: filled, copied, created, changed, saved from a login,
    /// deleted. Each event goes into a queue in the encrypted database (AppDbStore) and is sent to the server right away; if the server does not respond it stays queued and is sent again
    /// with the synchronization. Without Teams nothing is logged. NEVER the password: only entry, folder, entry user, page origin and browser.
    /// </summary>
    internal static class TeamsBrowserLog
    {
        internal record Item(string Id, string At, string Kind, string DeviceId, string Device, string Folder, string Account, string Origin, string Browser);

        private const string DbKey = "teams.browserlog";
        private const int MaxQueue = 5000;   // server unreachable for a long time: the most recent ones are kept
        private static readonly object Gate = new();
        private static readonly SemaphoreSlim SendGate = new(1, 1);

        private static List<Item> Load()
        {
            try { if (AppDbStore.TryGet(DbKey, out var j) && !string.IsNullOrEmpty(j)) return JsonSerializer.Deserialize<List<Item>>(j!) ?? new(); }
            catch { /* unreadable queue: start again empty */ }
            return new();
        }

        private static void Save(List<Item> q) => AppDbStore.Set(DbKey, q.Count == 0 ? null : JsonSerializer.Serialize(q));

        /// <summary>page origin ("https://host[:port]") or empty: no path, parameters or user in the address</summary>
        internal static string Origin(string? s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 300 || !Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return "";
            return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }

        /// <summary>logs an event (does not block, does not throw). kind: fill | copy | create | update | save | delete</summary>
        public static void Note(string kind, DeviceEntry e, string account, string? origin, string browser)
        {
            try
            {
                var link = TeamsLink.Load();
                if (link.State != "approved" || link.Key.Length == 0) return;
                // "do not copy": entries only on this PC do not go to the server, not even by name (like the connection duration)
                if (!e.OnServer && link.NoCopy) return;
                var item = new Item(Guid.NewGuid().ToString("N"), DateTime.UtcNow.ToString("o"), kind, e.Id, e.Name, e.Folder, account, Origin(origin), browser);
                lock (Gate)
                {
                    var q = Load();
                    q.Add(item);
                    if (q.Count > MaxQueue) q.RemoveRange(0, q.Count - MaxQueue);
                    Save(q);
                }
                _ = Task.Run(async () =>
                {
                    try { using var c = new TeamsServerClient(link.Server, link.Fingerprint); await FlushAsync(c, link, CancellationToken.None); }
                    // Warning and not Debug: with the normal log level (Base) the reason was previously not written anywhere
                    catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: extension log not sent, kept in the queue: " + ex.Message); }
                });
            }
            catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Warning("Teams: extension log not written: " + ex.Message); }
        }

        /// <summary>sends the queue to the server (in blocks of 500) and removes what it accepted. One send at a time. Errors: the queue stays and is retried on the next round</summary>
        internal static async Task FlushAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            // another send is in progress (usually the synchronization): before, it simply gave up, but if that send had already read the queue
            // the event just added stayed stuck until the next round. Now an "again" flag is left and the sender does one more round before leaving;
            // the check after Release covers the case where the flag arrives right while the other one is leaving
            Volatile.Write(ref _again, 1);
            while (Volatile.Read(ref _again) == 1 && await SendGate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                try
                {
                    while (Interlocked.Exchange(ref _again, 0) == 1)
                        if (!await SendQueueAsync(c, link, ct).ConfigureAwait(false)) return;   // server refusing: the queue stays, the synchronization retries
                }
                finally { SendGate.Release(); }
            }
        }
        private static int _again;

        private static async Task<bool> SendQueueAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            while (true)
            {
                List<Item> batch;
                lock (Gate) batch = Load().Take(500).ToList();
                if (batch.Count == 0) return true;
                if (!await c.BrowserLogAsync(link.Key, batch, ct).ConfigureAwait(false)) return false;
                var sent = batch.Select(x => x.Id).ToHashSet();
                lock (Gate) { var q = Load(); q.RemoveAll(x => sent.Contains(x.Id)); Save(q); }
                if (batch.Count < 500) return true;
            }
        }

        /// <summary>link closed: events not yet sent will never be sent (a new server must not receive the old one's)</summary>
        public static void Clear() { lock (Gate) Save(new()); }
    }
}
