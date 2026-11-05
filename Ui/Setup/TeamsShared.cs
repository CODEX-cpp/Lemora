using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Service;

namespace Lemora.Setup
{
    /// <summary>
    /// Cose condivise dal server Teams oltre alle connessioni: password dei dispositivi (gestore password della cartella), cartelle e credenziali di cartella.
    /// Arrivano con la stessa sincronizzazione delle connessioni (TeamsSync.RunAsync) e si scrivono subito sul server (come le connessioni con permesso "scrittura").
    /// Permessi: password dei dispositivi = come le connessioni (scrittura); le cartelle le possono creare, rinominare ed eliminare tutti.
    /// </summary>
    internal static class TeamsShared
    {
        public static bool Linked => TeamsLink.Load().State == "approved" && TeamsLink.Load().Key.Length > 0;

        /// <summary>password dei dispositivi e credenziali di cartella: tutti i permessi (anche "con approvazione", senza richieste) creano e modificano (decisione del 05/11)</summary>
        public static bool CanEditDevices => true;

        /// <summary>eliminare e' vietato solo alla sola lettura; senza server si puo' sempre</summary>
        public static bool CanDeleteDevices => !Linked || TeamsLink.Load().Permission != "read";

        /// <summary>percorso di cartella dell'app ("Clienti / Sede") -> percorso del server ("Clienti/Sede")</summary>
        public static string ToServerPath(string path) => string.Join("/", FolderService.ToNodes(path));

        /// <summary>percorso del server ("Clienti/Sede") -> percorso dell'app ("Clienti / Sede")</summary>
        public static string FromServerPath(string path) => FolderService.ToPath(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));

        private static DeviceEntry ToEntry(TeamsServerClient.SharedDevice d) => new()
        {
            Id = d.Id, Version = d.Version, Folder = FromServerPath(d.Folder), Name = d.Name, Ip = d.Ip, OnServer = true,
            Users = d.Users.Select(u => new DeviceUser { User = u.User, Password = u.Password }).ToList(),
        };

        private static TeamsServerClient.SharedDevice ToShared(DeviceEntry e) => new(e.Id, e.Version, ToServerPath(e.Folder), e.Name, e.Ip, e.Users, false);

        /// <summary>Scrive sul server una voce del gestore password e la rimette in locale com'e' tornata (versione compresa). null = fatto, altrimenti il testo dell'errore. Blocca al massimo 15 s.</summary>
        public static string? WriteDevice(DeviceEntry e, bool create, out DeviceEntry? saved)
        {
            saved = null;
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return TeamsSync.ErrorText("revoked");
            try
            {
                var res = Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    return await c.DeviceWriteAsync(link.Key, ToShared(e), create);
                }).GetAwaiter().GetResult();
                if (res == null) return TeamsSync.ErrorText("server");
                saved = ToEntry(res);
                DevicePasswordService.ApplyRemote(new[] { saved }, Array.Empty<string>());
                return null;
            }
            catch (TeamsException ex) { return TeamsSync.ErrorText(ex.Kind); }
        }

        /// <summary>Cancella una voce dal server e da qui. null = fatto, altrimenti il testo dell'errore.</summary>
        public static string? DeleteDevice(string id)
        {
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return TeamsSync.ErrorText("revoked");
            if (link.Permission == "read") return TeamsSync.ErrorText("permission");   // la sola lettura non elimina
            try
            {
                Task.Run(async () =>
                {
                    using var c = new TeamsServerClient(link.Server, link.Fingerprint);
                    await c.DeviceDeleteAsync(link.Key, id);
                }).GetAwaiter().GetResult();
                DevicePasswordService.Delete(id);
                return null;
            }
            catch (TeamsException ex) when (ex.Kind == "notfound") { DevicePasswordService.Delete(id); return null; }   // gia' tolta da un altro PC
            catch (TeamsException ex) { return TeamsSync.ErrorText(ex.Kind); }
        }

        /// <summary>Salva una voce del gestore password dalla pagina cartella: pulisce, unisce lo stesso IP, scrive sul server se collegato o solo qui. null = fatto, altrimenti testo dell'errore.</summary>
        public static string? SaveDevice(DeviceEntry e, bool isNew, out DeviceEntry? saved)
        {
            saved = null;
            var prepared = DevicePasswordService.Prepare(e, isNew, out _);
            if (prepared == null) return SetupText.T("dp_save_fail");
            if (Linked) return WriteDevice(prepared, create: !prepared.OnServer, out saved);
            if (!DevicePasswordService.Save(prepared)) return SetupText.T("dp_save_fail");
            saved = prepared;
            return null;
        }

        /// <summary>Elimina una voce (dal server se c'e' anche li'). null = fatto.</summary>
        public static string? RemoveDevice(DeviceEntry e) =>
            e.OnServer && Linked ? DeleteDevice(e.Id) : (DevicePasswordService.Delete(e.Id) ? null : SetupText.T("dp_save_fail"));

        /// <summary>"credenziale copiata" nel registro del server (mai la password). Non blocca e non da' errori: e' solo un registro</summary>
        public static void AuditCopy(string what, string device, string user)
        {
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return;
            _ = Task.Run(async () =>
            {
                try { using var c = new TeamsServerClient(link.Server, link.Fingerprint); await c.AuditCopyAsync(link.Key, what, device, user); }
                catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Debug("Teams: registro della copia non inviato: " + ex.Message); }
            });
        }

        /// <summary>Applica le novita' arrivate con la sincronizzazione. true se qualcosa e' cambiato in locale.</summary>
        public static bool Apply(TeamsServerClient.SharedBatch b)
        {
            if (!b.Present) return false;
            bool changed = false;
            if (b.Devices.Count > 0)
                changed |= DevicePasswordService.ApplyRemote(b.Devices.Where(d => !d.Deleted).Select(ToEntry), b.Devices.Where(d => d.Deleted).Select(d => d.Id));
            if (b.Folders.Count > 0)
                changed |= FolderService.ApplyRemote(b.Folders.Select(f => (FromServerPath(f.Path), f.Domain, f.IpPrefix, f.Deleted)));
            if (b.Creds.Count > 0) changed |= ApplyCreds(b.Creds);
            return changed;
        }

        // ---- cartelle: create, rinominate, spostate, eliminate qui arrivano a tutti. Le operazioni si mettono in una coda salvata nel database e partono con
        // la sincronizzazione (subito dopo la modifica, e di nuovo a ogni giro se il server non risponde). Nessun permesso: vale anche in sola lettura.
        private class FolderOp
        {
            public string Op { get; set; } = "";     // upsert | delete | rename | cupsert | cdelete (le ultime due: credenziale di cartella)
            public string Path { get; set; } = "";
            public string To { get; set; } = "";
            public string Domain { get; set; } = "";
            public string Ip { get; set; } = "";
            public string User { get; set; } = "";  // credenziali di cartella
            public string Pw { get; set; } = "";
        }

        /// <summary>credenziali di cartella: ogni id del server con cartella, utente e versione (l'identita' per l'app e' cartella + utente)</summary>
        private class CredMapItem
        {
            public string Folder { get; set; } = "";
            public string User { get; set; } = "";
            public int Version { get; set; }
        }

        private const string CredMapKey = "teams.fcredmap";
        private const string CredsUploadedKey = "teams.fcreds_up";

        private static Dictionary<string, CredMapItem> LoadCredMap()
        {
            try { if (Lemora.Service.Locality.AppDbStore.TryGet(CredMapKey, out var j) && !string.IsNullOrEmpty(j)) return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CredMapItem>>(j!) ?? new(); }
            catch { /* mappa illeggibile: si ricostruisce con la prossima sincronizzazione completa */ }
            return new();
        }

        private static void SaveCredMap(Dictionary<string, CredMapItem> m) => Lemora.Service.Locality.AppDbStore.Set(CredMapKey, m.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(m));

        public static void CredUpsert(string folder, string user, string password) => Enqueue(new FolderOp { Op = "cupsert", Path = ToServerPath(folder), User = user, Pw = password });
        public static void CredDelete(string folder, string user) => Enqueue(new FolderOp { Op = "cdelete", Path = ToServerPath(folder), User = user });

        /// <summary>credenziali di cartella arrivate dal server: si aggiungono, cambiano o si tolgono in locale (senza rimandarle indietro)</summary>
        private static bool ApplyCreds(List<TeamsServerClient.SharedCred> creds)
        {
            var map = LoadCredMap();
            bool changed = false;
            foreach (var c in creds)
            {
                if (c.Deleted)
                {
                    if (map.Remove(c.Id, out var m) && FolderCredentialService.Find(m.Folder, m.User) is { } gone) { FolderCredentialService.Delete(gone, remote: true); changed = true; }
                    continue;
                }
                var folder = FromServerPath(c.Folder);
                if (map.TryGetValue(c.Id, out var old) && old.Version > c.Version) continue;
                var local = FolderCredentialService.Find(folder, c.User);
                if (local == null)
                {
                    if (FolderCredentialService.Add(folder, c.User, c.Password, out _, remote: true).IsSuccess) changed = true;
                }
                else if (FolderCredentialService.GetDecryptedById(local.DatabaseId)?.Password != c.Password)
                {
                    if (FolderCredentialService.Update(local, local.UserName, c.Password, remote: true).IsSuccess) changed = true;
                }
                map[c.Id] = new CredMapItem { Folder = folder, User = c.User, Version = c.Version };
            }
            SaveCredMap(map);
            return changed;
        }

        private const string QueueKey = "teams.folderq";
        private const string UploadedKey = "teams.folders_up";
        private static readonly object QueueLock = new();

        private static List<FolderOp> LoadQueue()
        {
            try { if (Lemora.Service.Locality.AppDbStore.TryGet(QueueKey, out var j) && !string.IsNullOrEmpty(j)) return System.Text.Json.JsonSerializer.Deserialize<List<FolderOp>>(j!) ?? new(); }
            catch { /* coda illeggibile: si riparte vuota, le cartelle si ricaricano al prossimo collegamento */ }
            return new();
        }

        private static void SaveQueue(List<FolderOp> q) => Lemora.Service.Locality.AppDbStore.Set(QueueKey, q.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(q));

        private static void Enqueue(FolderOp op)
        {
            if (!Linked) return;
            lock (QueueLock) { var q = LoadQueue(); q.Add(op); SaveQueue(q); }
            _ = Task.Run(async () => { try { await TeamsSync.RunAsync(); } catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Debug("Teams: sincronizzazione dopo la modifica di una cartella non riuscita: " + ex.Message); } });
        }

        public static void FolderUpsert(string path, string domain, string ipPrefix) => Enqueue(new FolderOp { Op = "upsert", Path = ToServerPath(path), Domain = domain, Ip = ipPrefix });
        public static void FolderRename(string from, string to) => Enqueue(new FolderOp { Op = "rename", Path = ToServerPath(from), To = ToServerPath(to) });
        public static void FolderDelete(string path) => Enqueue(new FolderOp { Op = "delete", Path = ToServerPath(path) });

        /// <summary>il server e' ripartito da zero: le cartelle di questo PC vanno ricaricate</summary>
        public static void ResetUploaded()
        {
            Lemora.Service.Locality.AppDbStore.Set(UploadedKey, null);
            Lemora.Service.Locality.AppDbStore.Set(CredsUploadedKey, null);
            Lemora.Service.Locality.AppDbStore.Set(CredMapKey, null);   // gli id del server non valgono piu'
        }

        /// <summary>Manda al server le operazioni sulle cartelle in coda. Prima volta dopo il collegamento: carica anche tutte le cartelle gia' presenti qui.</summary>
        public static async Task FlushFoldersAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            if (Lemora.Service.Locality.AppDbStore.TryGet(UploadedKey, out var up) && up != link.Id)
            {
                lock (QueueLock)
                {
                    var q0 = LoadQueue();
                    q0.InsertRange(0, FolderService.Records().Select(r => new FolderOp { Op = "upsert", Path = ToServerPath(r.Path), Domain = r.Domain, Ip = r.IpPrefix }));
                    SaveQueue(q0);
                }
                Lemora.Service.Locality.AppDbStore.Set(UploadedKey, link.Id);
            }
            // credenziali di cartella fatte prima del collegamento: si caricano una volta, con qualsiasi permesso
            if (Lemora.Service.Locality.AppDbStore.TryGet(CredsUploadedKey, out var cu) && cu != link.Id)
            {
                var existing = LoadCredMap().Values.Select(v => (v.Folder.ToLowerInvariant(), v.User.ToLowerInvariant())).ToHashSet();
                lock (QueueLock)
                {
                    var q1 = LoadQueue();
                    foreach (var cr in FolderCredentialService.Snapshot())
                    {
                        if (existing.Contains((FromServerPath(ToServerPath(cr.FolderPath)).ToLowerInvariant(), cr.UserName.ToLowerInvariant()))) continue;
                        q1.Add(new FolderOp { Op = "cupsert", Path = ToServerPath(cr.FolderPath), User = cr.UserName, Pw = FolderCredentialService.GetDecryptedById(cr.DatabaseId)?.Password ?? "" });
                    }
                    SaveQueue(q1);
                }
                Lemora.Service.Locality.AppDbStore.Set(CredsUploadedKey, link.Id);
            }
            List<FolderOp> q;
            lock (QueueLock) q = LoadQueue();
            int done = 0;
            foreach (var op in q)
            {
                try
                {
                    switch (op.Op)
                    {
                        case "upsert": await c.FolderUpsertAsync(link.Key, op.Path, op.Domain, op.Ip, ct); break;
                        case "rename": await c.FolderRenameAsync(link.Key, op.Path, op.To, ct); break;
                        case "delete": await c.FolderDeleteAsync(link.Key, op.Path, ct); break;
                        case "cupsert":
                        {
                            var map = LoadCredMap();
                            var cur = map.FirstOrDefault(kv => string.Equals(kv.Value.Folder, FromServerPath(op.Path), StringComparison.CurrentCultureIgnoreCase) && string.Equals(kv.Value.User, op.User, StringComparison.CurrentCultureIgnoreCase));
                            bool create = cur.Key == null;
                            var res = await c.CredWriteAsync(link.Key, new TeamsServerClient.SharedCred(cur.Key ?? "", cur.Value?.Version ?? 0, op.Path, op.User, "", false), create, op.Pw, ct);
                            if (res != null) { map[res.Id] = new CredMapItem { Folder = FromServerPath(res.Folder), User = res.User, Version = res.Version }; SaveCredMap(map); }
                            break;
                        }
                        case "cdelete":
                        {
                            var map = LoadCredMap();
                            var cur = map.FirstOrDefault(kv => string.Equals(kv.Value.Folder, FromServerPath(op.Path), StringComparison.CurrentCultureIgnoreCase) && string.Equals(kv.Value.User, op.User, StringComparison.CurrentCultureIgnoreCase));
                            if (cur.Key != null) { await c.CredDeleteAsync(link.Key, cur.Key, ct); map.Remove(cur.Key); SaveCredMap(map); }
                            break;
                        }
                    }
                    done++;
                }
                catch (TeamsException ex) when (ex.Kind is "unreachable" or "certificate" or "server" or "revoked") { break; }   // si riprova al prossimo giro, in ordine
                catch (TeamsException ex)
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"Teams: operazione sulla cartella '{op.Path}' ({op.Op}) rifiutata dal server ({ex.Kind}): lasciata cadere");   // es. cartella non vuota o gia' esistente: riprovare non serve
                    done++;
                }
            }
            if (done > 0) lock (QueueLock) { var cur = LoadQueue(); cur.RemoveRange(0, Math.Min(done, cur.Count)); SaveQueue(cur); }
        }

        /// <summary>Le voci create su questo PC prima del collegamento (o dopo un server ripartito da zero) si caricano sul server (con qualsiasi permesso: creare e' sempre permesso).</summary>
        public static async Task UploadLocalAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            foreach (var e in DevicePasswordService.NotOnServer())
            {
                try
                {
                    var res = await c.DeviceWriteAsync(link.Key, ToShared(e), create: true, ct);
                    if (res != null) DevicePasswordService.ApplyRemote(new[] { ToEntry(res) }, Array.Empty<string>());
                }
                catch (TeamsException ex) when (ex.Kind is "input" or "conflict" or "exists")
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"Teams: voce del gestore password non caricata ({ex.Kind}): {e.Name}");   // non si riprova all'infinito
                    DevicePasswordService.ApplyRemote(new[] { new DeviceEntry { Id = e.Id, Version = e.Version, Folder = e.Folder, Name = e.Name, Ip = e.Ip, Users = e.Users } }, Array.Empty<string>());
                }
            }
        }
    }
}
