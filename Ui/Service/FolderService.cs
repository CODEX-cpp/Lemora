using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>folder with the settings shared by the servers it contains (saved in the database)</summary>
    public class FolderRecord
    {
        /// <summary>full path, with " / " between levels (e.g. "Acme / Production")</summary>
        public string Path { get; set; } = "";
        /// <summary>shared domain (RDP); empty = not set</summary>
        public string Domain { get; set; } = "";
        /// <summary>IP prefix of the site (e.g. "192.0.2."); empty = not set</summary>
        public string IpPrefix { get; set; } = "";
    }

    /// <summary>
    /// List of folders. A folder exists if it is in the list saved in the database (even if empty)
    /// or if at least one server uses it in its path (<see cref="Lemora.Model.Protocol.Base.ProtocolBase.TreeNodes"/>).
    /// </summary>
    public static class FolderService
    {
        public const string Separator = " / ";
        private const string DbKey = "folders";
        private const string TagsMigratedDbKey = "tags_to_folders_done";

        private static List<FolderRecord> _records = new List<FolderRecord>();
        private static bool _isLoaded;
        private static readonly object Lock = new object();

        /// <summary>raised when the folders or their settings change</summary>
        public static event Action? Changed;

        private static bool Load()
        {
            lock (Lock)
            {
                if (_isLoaded) return true;
                if (!AppDbStore.TryGet(DbKey, out var json)) return false;   // database not ready yet
                _isLoaded = true;
                try
                {
                    if (!string.IsNullOrEmpty(json))
                        _records = JsonConvert.DeserializeObject<List<FolderRecord>>(json) ?? new List<FolderRecord>();
                }
                catch
                {
                    _records = new List<FolderRecord>();
                }
                return true;
            }
        }

        private static void Save()
        {
            lock (Lock)
            {
                if (!_isLoaded) return;   // if the database was not read, do not overwrite it
                AppDbStore.Set(DbKey, JsonConvert.SerializeObject(_records));
            }
            Changed?.Invoke();
        }

        public static List<string> ToNodes(string path) => path.Split(new[] { Separator.Trim() }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        public static string ToPath(IEnumerable<string> nodes) => string.Join(Separator, nodes);
        public static string Leaf(string path) => ToNodes(path).LastOrDefault() ?? "";
        public static string Parent(string path) { var n = ToNodes(path); return n.Count <= 1 ? "" : ToPath(n.Take(n.Count - 1)); }
        public static int Level(string path) => Math.Max(0, ToNodes(path).Count - 1);

        /// <summary>true if the server's path is inside the folder (or in one of its subfolders)</summary>
        public static bool IsIn(IReadOnlyList<string> serverNodes, string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return true;
            var p = ToNodes(folderPath);
            if (serverNodes.Count < p.Count) return false;
            for (var i = 0; i < p.Count; i++)
                if (!string.Equals(serverNodes[i], p[i], StringComparison.CurrentCultureIgnoreCase)) return false;
            return true;
        }

        private static IEnumerable<string> Prefixes(IReadOnlyList<string> nodes)
        {
            for (var n = 1; n <= nodes.Count; n++) yield return ToPath(nodes.Take(n));
        }

        /// <summary>all folder paths (parents before children, in alphabetical order)</summary>
        public static List<string> AllPaths()
        {
            Load();
            MigrateTagsOnce();
            var set = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            void Add(string p) { if (!string.IsNullOrEmpty(p) && !set.ContainsKey(p)) set[p] = p; }
            lock (Lock)
                foreach (var r in _records) foreach (var p in Prefixes(ToNodes(r.Path))) Add(p);
            foreach (var vm in IoC.Get<GlobalData>().VmItemList)
                foreach (var p in Prefixes(vm.Server.TreeNodes)) Add(p);
            return set.Values.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>fake path of the "No folder" item: connections saved outside any folder (it is never the name of a real folder)</summary>
        public const string NoFolder = "\u0001no-folder";

        /// <summary>the server sits in this very folder (not in a subfolder); empty path = all; NoFolder = those without a folder</summary>
        public static bool IsDirectlyIn(IReadOnlyList<string> serverNodes, string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return true;
            if (folderPath == NoFolder) return serverNodes.Count == 0;
            var p = ToNodes(folderPath);
            return serverNodes.Count == p.Count && IsIn(serverNodes, folderPath);
        }

        /// <summary>servers placed in this very folder (those of subfolders are seen by opening the subfolder)</summary>
        public static int CountDirectServers(string path) => IoC.Get<GlobalData>().VmItemList.Count(vm => IsDirectlyIn(vm.Server.TreeNodes, path));

        public static int CountServers(string path) => IoC.Get<GlobalData>().VmItemList.Count(vm => IsIn(vm.Server.TreeNodes, path));

        public static bool Exists(string path) => AllPaths().Any(x => string.Equals(x, path, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>error if the name is not valid (empty or contains the separator), otherwise empty string</summary>
        public static string ValidateName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return IoC.Translate(LanguageService.CAN_NOT_BE_EMPTY);
            if (name.Contains("/")) return IoC.Translate("folder_name_invalid");
            return "";
        }

        /// <summary>read-only Teams PC: folders are visible but cannot be created, renamed, moved or deleted (the server would reject it with 403)</summary>
        private static bool ReadOnlyTeam(out string error)
        {
            error = Lemora.Setup.TeamsShared.ReadOnlyTeam ? Lemora.Setup.TeamsSync.ErrorText("permission") : "";
            return error.Length > 0;
        }

        public static bool Create(string parentPath, string name, out string error)
        {
            if (ReadOnlyTeam(out error)) return false;
            error = ValidateName(name);
            if (error.Length > 0) return false;
            var path = string.IsNullOrEmpty(parentPath) ? name.Trim() : ToPath(ToNodes(parentPath).Append(name.Trim()));
            if (Exists(path)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, name.Trim()); return false; }
            if (!Load()) { error = "database"; return false; }
            lock (Lock) _records.Add(new FolderRecord { Path = path });
            Save();
            Lemora.Setup.TeamsShared.FolderUpsert(path, "", "");   // with the Teams server the folder also reaches the other PCs
            return true;
        }

        /// <summary>
        /// The folder contains Teams entries and this PC has no "write" permission: renaming or moving it would move them (the Teams server rejects it, 403),
        /// and here they would be left half moved. Applies to every path (folder page, tree, drag and drop, folder window).
        /// </summary>
        public static bool HasUnmovableShared(string path) =>
            Lemora.Setup.TeamsLink.Load().Permission != "write"
            && (IoC.Get<GlobalData>().VmItemList.Any(vm => vm.Server.DataSource is Lemora.Service.DataSource.Model.TeamsSource && IsIn(vm.Server.TreeNodes, path))
                // also the entries not visible here (disabled, or hidden after too many days without the server): the server would reject it anyway
                || (IoC.TryGet<Lemora.Service.DataSource.DataSourceService>()?.GetDataSource(Lemora.Service.DataSource.Model.TeamsSource.SourceName) is Lemora.Service.DataSource.Model.TeamsSource ts
                    && ts.AllFolders().Any(f => IsIn(f, path))));

        /// <summary>
        /// Folder with shared entries on a "Richieste" PC: renaming or moving it would move entries of everyone, so it is asked of the administrator
        /// (before: simply refused). Here nothing changes until the approval, then the sync brings the new path.
        /// Returns false; empty <paramref name="error"/> = request sent (or queued without server): the callers do not treat it as an error
        /// </summary>
        private static bool RequestRename(string path, string newPath, out string error)
        {
            if (Lemora.Setup.TeamsLink.Load().Permission != "requests") { error = Lemora.Setup.SetupText.T("folder_shared_no_perm"); return false; }
            if (Exists(newPath) && !string.Equals(newPath, path, StringComparison.CurrentCultureIgnoreCase)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, Leaf(newPath)); return false; }
            error =Lemora.Setup.TeamsSync.RequestShared("folder-rename", null, path, Lemora.Setup.TeamsShared.ToServerPath(newPath), null, null, null, null, $"{path} → {newPath}") ?? "";
            return false;
        }

        /// <summary>Teams read-only: deleting the folder would take away the shared credentials and passwords inside it (deleting them is forbidden)</summary>
        private static bool ReadOnlyWithSharedSecrets(string path) =>
            Lemora.Setup.TeamsShared.Linked && Lemora.Setup.TeamsLink.Load().Permission is "read" or "requests"
            && (FolderCredentialService.Snapshot().Any(c => IsIn(ToNodes(c.FolderPath), path))
                || DevicePasswordService.All().Any(d => IsIn(ToNodes(d.Folder), path)));

        /// <summary>renames the last level of the path; updates the servers and settings inside it</summary>
        public static bool Rename(string path, string newName, out string error)
        {
            if (ReadOnlyTeam(out error)) return false;
            error = ValidateName(newName);
            if (error.Length > 0) return false;
            newName = newName.Trim();
            var parent = Parent(path);
            var newPath = string.IsNullOrEmpty(parent) ? newName : ToPath(ToNodes(parent).Append(newName));
            if (string.Equals(newPath, path, StringComparison.CurrentCulture)) return true;
            if (HasUnmovableShared(path)) return RequestRename(path, newPath, out error);
            if (Exists(newPath) && !string.Equals(newPath, path, StringComparison.CurrentCultureIgnoreCase)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, newName); return false; }
            if (!Load()) { error = "database"; return false; }

            var oldNodes = ToNodes(path);
            var newNodes = ToNodes(newPath);
            // server
            // Teams server entries change folder through the operation on the server (and come back via sync): they are not written from here, even in read-only
            var servers = IoC.Get<GlobalData>().VmItemList.Select(x => x.Server).Where(s => IsIn(s.TreeNodes, path) && !(s.DataSource is Lemora.Service.DataSource.Model.TeamsSource)).ToList();
            foreach (var s in servers)
            {
                var nodes = new List<string>(s.TreeNodes);
                for (var i = 0; i < newNodes.Count; i++) nodes[i] = newNodes[i];
                s.TreeNodes = nodes;
            }
            if (servers.Count > 0)
            {
                var ret = IoC.Get<GlobalData>().UpdateServer(servers);
                if (!ret.IsSuccess) { error = ret.ErrorInfo; return false; }
            }
            // saved list and settings
            lock (Lock)
            {
                foreach (var r in _records.Where(r => IsIn(ToNodes(r.Path), path)).ToList())
                {
                    var nodes = ToNodes(r.Path);
                    for (var i = 0; i < newNodes.Count; i++) nodes[i] = newNodes[i];
                    r.Path = ToPath(nodes);
                }
            }
            FolderCredentialService.OnFolderRenamed(path, newPath);
            Renamed?.Invoke(path, newPath);
            Save();
            Lemora.Setup.TeamsShared.FolderRename(path, newPath);
            return true;
        }

        /// <summary>
        /// moves the folder (with everything it contains) into <paramref name="newParentPath"/> ("" = root);
        /// servers, settings and shared credentials follow the new path
        /// </summary>
        public static bool Move(string path, string newParentPath, out string error)
        {
            if (ReadOnlyTeam(out error)) return false;
            newParentPath = newParentPath ?? "";
            var leaf = Leaf(path);
            var newPath = string.IsNullOrEmpty(newParentPath) ? leaf : ToPath(ToNodes(newParentPath).Append(leaf));
            if (string.Equals(newPath, path, StringComparison.CurrentCulture)) return true;
            if (IsIn(ToNodes(newParentPath), path)) { error = IoC.Translate("folder_move_into_itself"); return false; }
            if (HasUnmovableShared(path)) return RequestRename(path, newPath, out error);
            if (Exists(newPath)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, leaf); return false; }
            if (!Load()) { error = "database"; return false; }

            var oldNodes = ToNodes(path);
            var newNodes = ToNodes(newPath);
            List<string> Remap(IEnumerable<string> nodes) => newNodes.Concat(nodes.Skip(oldNodes.Count)).ToList();

            var servers = IoC.Get<GlobalData>().VmItemList.Select(x => x.Server).Where(sv => IsIn(sv.TreeNodes, path) && !(sv.DataSource is Lemora.Service.DataSource.Model.TeamsSource)).ToList();   // Teams server ones change through the operation on the server
            foreach (var sv in servers) sv.TreeNodes = Remap(sv.TreeNodes);
            if (servers.Count > 0)
            {
                var ret = IoC.Get<GlobalData>().UpdateServer(servers);
                if (!ret.IsSuccess) { error = ret.ErrorInfo; return false; }
            }
            lock (Lock)
            {
                foreach (var r in _records.Where(r => IsIn(ToNodes(r.Path), path)).ToList())
                    r.Path = ToPath(Remap(ToNodes(r.Path)));
            }
            FolderCredentialService.OnFolderRenamed(path, newPath);
            Renamed?.Invoke(path, newPath);
            Save();
            Lemora.Setup.TeamsShared.FolderRename(path, newPath);
            return true;
        }

        /// <summary>raised after the rename (old path, new path): whoever has data tied to the folder updates it</summary>
        public static event Action<string, string>? Renamed;

        /// <summary>deletes the folder only if it contains no servers (subfolders included)</summary>
        public static bool Delete(string path, out string error)
        {
            if (ReadOnlyTeam(out error)) return false;
            if (CountServers(path) > 0) { error = IoC.Translate("folder_delete_not_empty"); return false; }
            if (ReadOnlyWithSharedSecrets(path)) { error = Lemora.Setup.SetupText.T("folder_secrets_no_perm"); return false; }   // the Teams server rejects it anyway (409)
            if (!Load()) { error = "database"; return false; }
            lock (Lock) _records.RemoveAll(r => IsIn(ToNodes(r.Path), path));
            FolderCredentialService.OnFolderDeleted(path);
            Deleted?.Invoke(path);
            Save();
            Lemora.Setup.TeamsShared.FolderDelete(path);
            return true;
        }

        public static event Action<string>? Deleted;

        /// <summary>
        /// Removes the folder from the saved list (only that path, not the subfolders) if it is left as just a name:
        /// no settings (domain, prefix) and no shared credentials. Used for folders of Teams entries emptied by the server.
        /// </summary>
        public static void ForgetIfBare(string path)
        {
            if (!Load()) return;
            if (FolderCredentialService.ForFolder(path).Count > 0) return;
            int removed;
            lock (Lock) removed = _records.RemoveAll(r => string.Equals(r.Path, path, StringComparison.CurrentCultureIgnoreCase) && r.Domain.Length == 0 && r.IpPrefix.Length == 0);
            if (removed > 0) Save();
        }

        // ---- computed suggestions and effective values
        /// <summary>suggested domain: the customer name (first level) in uppercase</summary>
        public static string SuggestDomain(string path) => ToNodes(path).FirstOrDefault()?.ToUpperInvariant() ?? "";

        /// <summary>
        /// common network of the folder's servers (and subfolders): the first three numbers of the most frequent IP, e.g. "192.0.2.".
        /// Empty if the folder has no server with an IP.
        /// </summary>
        public static string SuggestIpPrefix(string path)
        {
            var prefixes = new List<string>();
            foreach (var vm in IoC.Get<GlobalData>().VmItemList.Where(x => IsIn(x.Server.TreeNodes, path)))
            {
                if (vm.Server is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPort ap) continue;
                var parts = (ap.Address ?? "").Trim().Split('.');
                if (parts.Length == 4 && parts.All(x => int.TryParse(x, out var n) && n >= 0 && n <= 255))
                    prefixes.Add($"{parts[0]}.{parts[1]}.{parts[2]}.");
            }
            return prefixes.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        }

        /// <summary>value set in the folder or, if missing, in the first parent folder that has it</summary>
        private static string Inherited(string path, Func<FolderRecord, string> pick)
        {
            Load();
            for (var p = path; p.Length > 0; p = Parent(p))
            {
                var r = GetSettings(p);
                var v = pick(r);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }

        /// <summary>domain SET in the folder (or in the first parent folder that has it); empty if no folder chose one</summary>
        public static string ConfiguredDomain(string path) => Inherited(path, r => r.Domain);

        /// <summary>
        /// domain to use for the folder's servers: ONLY the one set in the folder (or in a parent).
        /// The suggested one (first level name in uppercase) is not applied on its own: it used to fill the Domain of every entry
        /// with the name of ordinary folders (e.g. DATEAMS); it stays as a suggestion in the folder panel.
        /// </summary>
        public static string EffectiveDomain(string path) => ConfiguredDomain(path);

        /// <summary>
        /// DNS suffix of the folder's servers, computed from the set domain (ACME -> acme.local; a domain that already has a dot, like valkoria.lan, stays as is).
        /// Empty if the folder has no domain.
        /// </summary>
        public static string DnsSuffix(string path)
        {
            var d = ConfiguredDomain(path).Trim().Trim('.').ToLowerInvariant();
            return d.Length == 0 ? "" : d.Contains('.') ? d : d + ".local";
        }

        /// <summary>"pc39" in a folder with a domain becomes "pc39.acme.local"; an address that already has a dot, is an IP or has a port stays as is</summary>
        public static string WithDnsSuffix(string path, string address)
        {
            var a = (address ?? "").Trim();
            var suffix = DnsSuffix(path);
            if (suffix.Length == 0 || a.Length == 0 || a.IndexOfAny(new[] { '.', ':', '/', '\\', ' ' }) >= 0 || System.Net.IPAddress.TryParse(a, out _)) return address ?? "";
            return a + "." + suffix;
        }

        /// <summary>IP prefix to use for the folder's servers: the one set, otherwise the one computed from the servers</summary>
        public static string EffectiveIpPrefix(string path) { var v = Inherited(path, r => r.IpPrefix); return v.Length > 0 ? v : SuggestIpPrefix(path); }

        // ---- shared settings (domain and IP prefix)
        public static FolderRecord GetSettings(string path)
        {
            Load();
            lock (Lock)
                return _records.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.CurrentCultureIgnoreCase))
                       ?? new FolderRecord { Path = path };
        }

        /// <returns>false = not allowed: read-only Teams PC that would change domain or prefix (true if nothing changes)</returns>
        public static bool SetSettings(string path, string domain, string ipPrefix)
        {
            if (Lemora.Setup.TeamsShared.ReadOnlyTeam)
            {
                var cur = GetSettings(path);
                return cur.Domain == (domain ?? "").Trim() && cur.IpPrefix == (ipPrefix ?? "").Trim();
            }
            if (!Load()) return false;
            // domain of the folder credentials (this folder and the subfolders that inherit it) before the change: they must follow the new one
            var credsBefore = FolderCredentialService.Snapshot().Where(c => IsIn(ToNodes(c.FolderPath), path)).Select(c => (c, ConfiguredDomain(c.FolderPath))).ToList();
            lock (Lock)
            {
                var r = _records.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.CurrentCultureIgnoreCase));
                if (r == null) _records.Add(r = new FolderRecord { Path = path });
                r.Domain = (domain ?? "").Trim();
                r.IpPrefix = (ipPrefix ?? "").Trim();
            }
            Save();
            Lemora.Setup.TeamsShared.FolderUpsert(path, (domain ?? "").Trim(), (ipPrefix ?? "").Trim());
            FolderCredentialService.FollowDomain(credsBefore);
            return true;
        }

        /// <summary>folders of the saved list (even empty) with their settings: these are the ones to upload to the Teams server the first time</summary>
        public static List<FolderRecord> Records() { Load(); lock (Lock) return _records.Select(r => new FolderRecord { Path = r.Path, Domain = r.Domain, IpPrefix = r.IpPrefix }).ToList(); }

        /// <summary>
        /// Arrival from the Teams server: adds the folders (even empty) with domain and IP prefix, removes the deleted ones from the list.
        /// Does not touch the servers and sends nothing back to the server. true if the list changed.
        /// </summary>
        public static bool ApplyRemote(IEnumerable<(string path, string domain, string ipPrefix, bool deleted)> items)
        {
            if (!Load()) return false;
            bool changed = false;
            lock (Lock)
            {
                foreach (var (path, domain, ipPrefix, deleted) in items)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    var r = _records.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.CurrentCultureIgnoreCase));
                    if (deleted) { if (r != null) { _records.Remove(r); changed = true; } continue; }
                    if (r == null) { _records.Add(new FolderRecord { Path = path, Domain = domain ?? "", IpPrefix = ipPrefix ?? "" }); changed = true; }
                    else if (r.Path != path || r.Domain != (domain ?? "") || r.IpPrefix != (ipPrefix ?? "")) { r.Path = path; r.Domain = domain ?? ""; r.IpPrefix = ipPrefix ?? ""; changed = true; }
                }
            }
            if (changed) Save();
            return changed;
        }

        /// <summary>
        /// Only once: each tag becomes a folder (the server's first tag) for servers that do not have a folder yet.
        /// Tags are not touched. It is marked as done in the database.
        /// </summary>
        private static void MigrateTagsOnce()
        {
            if (!AppDbStore.TryGet(TagsMigratedDbKey, out var done) || !string.IsNullOrEmpty(done)) return;
            var all = IoC.Get<GlobalData>().VmItemList;
            if (all.Count == 0) return;   // data not loaded yet: retry later
            // only entries of this PC: Teams ones belong to the team (with read-only the write would fail at every launch, with the others moves nobody asked for would start)
            var toUpdate = all.Select(x => x.Server).Where(s => s.TreeNodes.Count == 0 && s.Tags.Count > 0 && s.DataSource?.IsWritable == true
                                                                && s.DataSource is not Lemora.Service.DataSource.Model.TeamsSource).ToList();
            foreach (var s in toUpdate) s.TreeNodes = new List<string> { s.Tags[0].Trim() };
            if (toUpdate.Count > 0)
            {
                var ret = IoC.Get<GlobalData>().UpdateServer(toUpdate);
                if (!ret.IsSuccess) return;   // do not mark as done if the write failed
            }
            AppDbStore.Set(TagsMigratedDbKey, "1");
        }
    }
}
