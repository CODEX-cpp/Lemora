using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Service;

namespace Lemora.Setup
{
    /// <summary>
    /// Things shared by the Teams server besides connections: device passwords (folder password manager), folders and folder credentials.
    /// They arrive with the same sync as the connections (TeamsSync.RunAsync) and are written to the server immediately (like connections with "write" permission).
    /// Permissions: read only = view only; "requests" = folders directly, folder credentials and devices only as a request to the administrator;
    /// write = everything directly. The server applies the same rules (403 to whoever is not allowed).
    /// </summary>
    internal static class TeamsShared
    {
        public static bool Linked => TeamsLink.Load().State == "approved" && TeamsLink.Load().Key.Length > 0;

        /// <summary>read-only Teams PC: sees folder credentials, devices and folders but creates, edits or deletes nothing</summary>
        public static bool ReadOnlyTeam => Linked && TeamsLink.Load().Permission == "read";

        /// <summary>Teams PC with "requests": creates, edits and deletes folder credentials and devices only as a request to the administrator; folders it handles directly</summary>
        public static bool RequestsTeam => Linked && TeamsLink.Load().Permission == "requests";

        /// <summary>device passwords and folder credentials: everyone except read only ("requests" goes through approval); always without a server</summary>
        public static bool CanEditDevices => !ReadOnlyTeam;

        /// <summary>deleting: same as editing (with "requests" it becomes a deletion request)</summary>
        public static bool CanDeleteDevices => !ReadOnlyTeam;

        /// <summary>server id of the folder credential (folder + user); null if the server does not know it (made only on this PC)</summary>
        public static string? ServerCredId(string folder, string user) =>
            LoadCredMap().FirstOrDefault(kv => string.Equals(kv.Value.Folder, folder, StringComparison.CurrentCultureIgnoreCase) && string.Equals(kv.Value.User, user, StringComparison.CurrentCultureIgnoreCase)).Key;

        /// <summary>folder credentials the server knows (local folder + user, lowercase): the others belong only to this PC</summary>
        public static HashSet<(string folder, string user)> KnownCreds() =>
            LoadCredMap().Values.Select(v => (v.Folder.ToLowerInvariant(), v.User.ToLowerInvariant())).ToHashSet();

        /// <summary>PC linked to Teams and folder credential the server does not know (it exists only on this PC); <paramref name="known"/> from <see cref="KnownCreds"/></summary>
        public static bool IsLocalOnly(string folderPath, string user, HashSet<(string folder, string user)>? known) =>
            known != null && !known.Contains((FromServerPath(ToServerPath(folderPath)).ToLowerInvariant(), (user ?? "").ToLowerInvariant()));

        /// <summary>app folder path ("Customers / Site") -> server path ("Customers/Site")</summary>
        public static string ToServerPath(string path) => string.Join("/", FolderService.ToNodes(path));

        /// <summary>server path ("Customers/Site") -> app path ("Customers / Site")</summary>
        public static string FromServerPath(string path) => FolderService.ToPath(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));

        private static DeviceEntry ToEntry(TeamsServerClient.SharedDevice d) => new()
        {
            Id = d.Id, Version = d.Version, Folder = FromServerPath(d.Folder), Name = d.Name, Ip = d.Ip, MoreIps = d.MoreIps?.ToList() ?? new(), OnServer = true,
            Users = d.Users.Select(u => new DeviceUser { User = u.User, Password = u.Password }).ToList(),
        };

        private static TeamsServerClient.SharedDevice ToShared(DeviceEntry e) => new(e.Id, e.Version, ToServerPath(e.Folder), e.Name, e.Ip, e.Users, false, e.MoreIps);

        /// <summary>
        /// Writes a password manager entry to the server and stores it locally as it came back (version included). null = done, otherwise the error text. Blocks for at most 15 s.
        /// Read only: nothing is written. "Requests": the request to the administrator is sent, <paramref name="saved"/> stays null and the entry here does not change
        /// (once approved it arrives with the sync; an entry only on this PC proposes its own id, so it is not duplicated later).
        /// </summary>
        public static string? WriteDevice(DeviceEntry e, bool create, out DeviceEntry? saved)
        {
            saved = null;
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return TeamsSync.ErrorText("revoked");
            if (link.Permission == "read") return TeamsSync.ErrorText("permission");
            if (link.Permission == "requests")
            {
                var err = TeamsSync.RequestShared(create ? "device-create" : "device-update", e.Id, e.Folder, e.Name, e.Ip, null, null, e.Users, e.Name, e.MoreIps);
                if (err != null) return err;
                // new password usable right away on this PC (before: it appeared only after the approval). The request proposes this same id:
                // once approved the shared entry takes its place, if refused it stays only on this PC
                if (create && DevicePasswordService.Save(e)) saved = e;
                return null;
            }
            try
            {
                if (TeamsSync.QuickOffline(link)) throw new TeamsException("unreachable", "The server does not respond.");
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
            catch (TeamsException ex) when (create && ex.Kind is "unreachable" or "server")
            {
                // server unreachable: the new password stays on this PC and is uploaded at the next synchronization (UploadLocalAsync)
                if (!DevicePasswordService.Save(e)) return SetupText.T("dp_save_fail");
                saved = e;
                Lemora.Service.ToastService.Show(SetupText.T("tm_queued"), Lemora.Service.ToastKind.Warning);
                return null;
            }
            catch (TeamsException ex) { return TeamsSync.ErrorText(ex.Kind); }
        }

        /// <summary>Deletes an entry from the server and from here. null = done, otherwise the error text.</summary>
        public static string? DeleteDevice(string id)
        {
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return TeamsSync.ErrorText("revoked");
            if (link.Permission == "read") return TeamsSync.ErrorText("permission");   // read only does not delete
            if (link.Permission == "requests")
            {
                // "requests": deletion is asked of the administrator; the entry here stays until approved
                var d = DevicePasswordService.All().FirstOrDefault(x => x.Id == id);
                return TeamsSync.RequestShared("device-delete", id, d?.Folder ?? "", null, null, null, null, null, d?.Name ?? id);
            }
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
            catch (TeamsException ex) when (ex.Kind == "notfound") { DevicePasswordService.Delete(id); return null; }   // already removed by another PC
            catch (TeamsException ex) { return TeamsSync.ErrorText(ex.Kind); }
        }

        /// <summary>Saves a password manager entry from the folder page: cleans up, merges the same IP, writes to the server if linked or only here. null = done, otherwise the error text.</summary>
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

        /// <summary>Deletes an entry (from the server too if it is there). null = done.</summary>
        public static string? RemoveDevice(DeviceEntry e) =>
            e.OnServer && Linked ? DeleteDevice(e.Id) : (DevicePasswordService.Delete(e.Id) ? null : SetupText.T("dp_save_fail"));

        /// <summary>"credential copied" in the server log (never the password). Does not block and raises no errors: it is only a log</summary>
        public static void AuditCopy(string what, string device, string user)
        {
            var link = TeamsLink.Load();
            if (link.State != "approved" || link.Key.Length == 0) return;
            _ = Task.Run(async () =>
            {
                try { using var c = new TeamsServerClient(link.Server, link.Fingerprint); await c.AuditCopyAsync(link.Key, what, device, user); }
                catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Debug("Teams: copy log not sent: " + ex.Message); }
            });
        }

        /// <summary>Applies the changes that arrived with the sync. true if something changed locally.</summary>
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

        // ---- folders: those created, renamed, moved or deleted here reach everyone. The operations go into a queue saved in the database and are sent with
        // the sync (right after the change, and again at every round if the server does not answer). Read only does not do them (FolderService blocks it earlier).
        private class FolderOp
        {
            public string Op { get; set; } = "";     // upsert | delete | rename | cupsert | cdelete (the last two: folder credential)
            public string Path { get; set; } = "";
            public string To { get; set; } = "";
            public string Domain { get; set; } = "";
            public string Ip { get; set; } = "";
            public string User { get; set; } = "";  // folder credentials
            public string Pw { get; set; } = "";
        }

        /// <summary>folder credentials: each server id with folder, user and version (the identity for the app is folder + user)</summary>
        private class CredMapItem
        {
            public string Folder { get; set; } = "";
            public string User { get; set; } = "";
            public int Version { get; set; }
            /// <summary>
            /// id of the copy in this database: finds it even if the folder was moved on the server or the user got the domain
            /// (previously it was looked up only by folder + user and the copy with the password stayed after revocation). Empty in old maps
            /// </summary>
            public string LocalId { get; set; } = "";
        }

        /// <summary>the local copy of a server credential: by id, then (old maps) by folder + user</summary>
        private static Lemora.Model.Protocol.Base.Credential? LocalOf(CredMapItem m) =>
            (string.IsNullOrEmpty(m.LocalId) ? null : FolderCredentialService.GetById(m.LocalId)) ?? FolderCredentialService.Find(m.Folder, m.User);

        private const string CredMapKey = "teams.fcredmap";
        private const string CredsUploadedKey = "teams.fcreds_up";

        private static Dictionary<string, CredMapItem> LoadCredMap()
        {
            try { if (Lemora.Service.Locality.AppDbStore.TryGet(CredMapKey, out var j) && !string.IsNullOrEmpty(j)) return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CredMapItem>>(j!) ?? new(); }
            catch { /* unreadable map: rebuilt with the next full sync */ }
            return new();
        }

        private static void SaveCredMap(Dictionary<string, CredMapItem> m) => Lemora.Service.Locality.AppDbStore.Set(CredMapKey, m.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(m));

        public static void CredUpsert(string folder, string user, string password) => Enqueue(new FolderOp { Op = "cupsert", Path = ToServerPath(folder), User = user, Pw = password });
        public static void CredDelete(string folder, string user) => Enqueue(new FolderOp { Op = "cdelete", Path = ToServerPath(folder), User = user });

        /// <summary>folder credentials arrived from the server: added, changed or removed locally (without sending them back)</summary>
        private static bool ApplyCreds(List<TeamsServerClient.SharedCred> creds)
        {
            var map = LoadCredMap();
            bool changed = false;
            // deleted here but with the deletion still queued: the sync started by a change just before sees them still alive on the server and
            // would recreate them, and once the deletion goes out they would remain only here (password change then immediate Delete)
            HashSet<(string, string)> deleting;
            lock (QueueLock) deleting = LoadQueue().Where(o => o.Op == "cdelete").Select(o => (FromServerPath(o.Path).ToLowerInvariant(), o.User.ToLowerInvariant())).ToHashSet();
            foreach (var c in creds)
            {
                if (c.Deleted)
                {
                    if (map.Remove(c.Id, out var m) && LocalOf(m) is { } gone) { FolderCredentialService.Delete(gone, remote: true); changed = true; }
                    continue;
                }
                var folder = FromServerPath(c.Folder);
                // without a folder it would become a normal Vault credential, outside every Teams rule and revocation (security hardening)
                if (folder.Trim().Length == 0) { Shawn.Utils.SimpleLogHelper.Warning("Teams: folder credential without a folder from the server, ignored"); continue; }
                map.TryGetValue(c.Id, out var prev);
                if (deleting.Contains((folder.ToLowerInvariant(), c.User.ToLowerInvariant()))) { map[c.Id] = new CredMapItem { Folder = folder, User = c.User, Version = c.Version, LocalId = prev?.LocalId ?? "" }; continue; }   // the id is needed by the queued deletion
                if (prev != null && prev.Version > c.Version) continue;
                var local = FolderCredentialService.Find(folder, c.User);
                // same server credential with changed folder or user (rename on the server, approved request): the local one is moved/renamed,
                // so the servers using it stay linked and no old copy with the password is left behind
                if (local == null && prev != null && LocalOf(prev) is { } mine)
                {
                    if (!string.Equals(mine.FolderPath, folder, StringComparison.CurrentCultureIgnoreCase)) mine = FolderCredentialService.MoveRemote(mine, folder) ?? mine;
                    if (FolderCredentialService.Update(mine, c.User, c.Password, remote: true).IsSuccess) changed = true;
                    map[c.Id] = new CredMapItem { Folder = folder, User = c.User, Version = c.Version, LocalId = mine.DatabaseId };
                    continue;
                }
                // server credential never seen before with the same name as a LOCAL-ONLY one on this PC (read only or "requests": theirs stay here):
                // it is neither adopted nor overwritten, otherwise a colleague with write access would pick its password and then delete it (security hardening).
                // With "write" the local copy is the one uploaded by this PC: it is adopted
                // ...and the same if an already known credential is moved/renamed on the server onto a local-only one of this PC
                // Same password as the local one (e.g. the credential this PC asked for and kept here while waiting): adopting it reveals nothing, it is linked
                if (local != null && (ReadOnlyTeam || RequestsTeam) && (prev == null || LocalOf(prev) is not { } mineNow || mineNow.DatabaseId != local.DatabaseId)
                    && FolderCredentialService.GetDecryptedById(local.DatabaseId)?.Password != c.Password)
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"Teams: server credential with the same name as a local-only one ({folder} / {c.User}): the local one stays as it is");
                    continue;
                }
                string localId;
                if (local == null)
                {
                    if (FolderCredentialService.Add(folder, c.User, c.Password, out var added, remote: true).IsSuccess) changed = true;
                    localId = added?.DatabaseId ?? "";
                }
                else
                {
                    if (FolderCredentialService.GetDecryptedById(local.DatabaseId)?.Password != c.Password
                        && FolderCredentialService.Update(local, local.UserName, c.Password, remote: true).IsSuccess) changed = true;
                    localId = local.DatabaseId;
                }
                map[c.Id] = new CredMapItem { Folder = folder, User = c.User, Version = c.Version, LocalId = localId };
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
            catch { /* unreadable queue: restart empty, folders are uploaded again at the next link */ }
            return new();
        }

        private static void SaveQueue(List<FolderOp> q) => Lemora.Service.Locality.AppDbStore.Set(QueueKey, q.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(q));

        private static void Enqueue(FolderOp op)
        {
            if (!Linked) return;
            lock (QueueLock) { var q = LoadQueue(); q.Add(op); SaveQueue(q); }
            _ = Task.Run(async () => { try { await TeamsSync.RunAsync(); } catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Debug("Teams: sync after a folder change failed: " + ex.Message); } });
        }

        public static void FolderUpsert(string path, string domain, string ipPrefix) => Enqueue(new FolderOp { Op = "upsert", Path = ToServerPath(path), Domain = domain, Ip = ipPrefix });
        public static void FolderRename(string from, string to) => Enqueue(new FolderOp { Op = "rename", Path = ToServerPath(from), To = ToServerPath(to) });
        public static void FolderDelete(string path) => Enqueue(new FolderOp { Op = "delete", Path = ToServerPath(path) });

        /// <summary>
        /// Revocation or unlinking: the folder credentials present on the server (all those in the map, including those uploaded by this PC,
        /// as for device passwords) go away with the link (security hardening). They stay on the server.
        /// </summary>
        public static void RemoveServerCreds()
        {
            var map = LoadCredMap();
            int n = 0;
            foreach (var m in map.Values)
                if (LocalOf(m) is { } c && FolderCredentialService.Delete(c, remote: true).IsSuccess) n++;
            Lemora.Service.Locality.AppDbStore.Set(CredMapKey, null);
            Lemora.Service.Locality.AppDbStore.Set(CredsUploadedKey, null);
            lock (QueueLock) SaveQueue(new List<FolderOp>());   // changes not yet sent: without a link they will never go out
            if (n > 0) Shawn.Utils.SimpleLogHelper.Warning($"Teams: removed {n} folder credentials that came from the server (link closed)");
        }

        /// <summary>the server restarted from scratch: this PC's folders must be uploaded again</summary>
        public static void ResetUploaded()
        {
            Lemora.Service.Locality.AppDbStore.Set(UploadedKey, null);
            Lemora.Service.Locality.AppDbStore.Set(CredsUploadedKey, null);
            Lemora.Service.Locality.AppDbStore.Set(CredMapKey, null);   // the server ids are no longer valid
        }

        /// <summary>Sends the queued folder operations to the server. First time after linking: also uploads all folders already present here.</summary>
        public static async Task FlushFoldersAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            // read only: uploads nothing (the server would reject with 403); "requests" uploads folders (it handles them directly) but not credentials and
            // devices made before linking: they stay only on this PC (to share them, ask the administrator by editing them)
            if (link.Permission != "read" && Lemora.Service.Locality.AppDbStore.TryGet(UploadedKey, out var up) && up != link.Id)
            {
                lock (QueueLock)
                {
                    var q0 = LoadQueue();
                    q0.InsertRange(0, FolderService.Records().Select(r => new FolderOp { Op = "upsert", Path = ToServerPath(r.Path), Domain = r.Domain, Ip = r.IpPrefix }));
                    SaveQueue(q0);
                }
                Lemora.Service.Locality.AppDbStore.Set(UploadedKey, link.Id);
            }
            // folder credentials made before linking: uploaded once, only with "write"
            if (link.Permission == "write" && Lemora.Service.Locality.AppDbStore.TryGet(CredsUploadedKey, out var cu) && cu != link.Id)
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
                            if (res != null)
                            {
                                var mineId = FolderCredentialService.Find(FromServerPath(res.Folder), res.User)?.DatabaseId ?? cur.Value?.LocalId ?? "";
                                map[res.Id] = new CredMapItem { Folder = FromServerPath(res.Folder), User = res.User, Version = res.Version, LocalId = mineId };
                                SaveCredMap(map);
                            }
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
                catch (TeamsException ex) when (ex.Kind is "unreachable" or "certificate" or "server" or "revoked") { break; }   // retried at the next round, in order
                catch (TeamsException ex)
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"Teams: operation on folder '{op.Path}' ({op.Op}) rejected by the server ({ex.Kind}): dropped");   // e.g. folder not empty or already existing: retrying is pointless
                    done++;
                }
            }
            if (done > 0) lock (QueueLock) { var cur = LoadQueue(); cur.RemoveRange(0, Math.Min(done, cur.Count)); SaveQueue(cur); }
        }

        /// <summary>Entries created on this PC before linking (or after a server restarted from scratch) are uploaded to the server, only with "write"
        /// (read only and "requests" do not write directly: the entries stay on this PC).</summary>
        public static async Task UploadLocalAsync(TeamsServerClient c, TeamsLink link, CancellationToken ct)
        {
            if (link.Permission != "write") return;
            foreach (var e in DevicePasswordService.NotOnServer())
            {
                try
                {
                    var res = await c.DeviceWriteAsync(link.Key, ToShared(e), create: true, ct);
                    if (res != null) DevicePasswordService.ApplyRemote(new[] { ToEntry(res) }, Array.Empty<string>());
                }
                catch (TeamsException ex) when (ex.Kind is "input" or "conflict" or "exists")
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"Teams: password manager entry not uploaded ({ex.Kind}): {e.Name}");   // no endless retries
                    DevicePasswordService.ApplyRemote(new[] { new DeviceEntry { Id = e.Id, Version = e.Version, Folder = e.Folder, Name = e.Name, Ip = e.Ip, MoreIps = e.MoreIps, Users = e.Users } }, Array.Empty<string>());
                }
            }
        }
    }
}
