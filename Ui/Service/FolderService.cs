using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model;
using Lemora.Service.Locality;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>cartella con le impostazioni condivise dai server che contiene (salvate nel database)</summary>
    public class FolderRecord
    {
        /// <summary>percorso completo, con " / " tra i livelli (es. "Acme / Produzione")</summary>
        public string Path { get; set; } = "";
        /// <summary>dominio condiviso (RDP); vuoto = non impostato</summary>
        public string Domain { get; set; } = "";
        /// <summary>prefisso IP della sede (es. "192.168.100."); vuoto = non impostato</summary>
        public string IpPrefix { get; set; } = "";
    }

    /// <summary>
    /// Elenco delle cartelle. Una cartella esiste se e' nell'elenco salvato nel database (anche vuota)
    /// oppure se almeno un server la usa nel suo percorso (<see cref="Lemora.Model.Protocol.Base.ProtocolBase.TreeNodes"/>).
    /// </summary>
    public static class FolderService
    {
        public const string Separator = " / ";
        private const string DbKey = "folders";
        private const string TagsMigratedDbKey = "tags_to_folders_done";

        private static List<FolderRecord> _records = new List<FolderRecord>();
        private static bool _isLoaded;
        private static readonly object Lock = new object();

        /// <summary>sollevato quando cambiano le cartelle o le loro impostazioni</summary>
        public static event Action? Changed;

        private static bool Load()
        {
            lock (Lock)
            {
                if (_isLoaded) return true;
                if (!AppDbStore.TryGet(DbKey, out var json)) return false;   // database non ancora pronto
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
                if (!_isLoaded) return;   // se il database non e' stato letto non lo si sovrascrive
                AppDbStore.Set(DbKey, JsonConvert.SerializeObject(_records));
            }
            Changed?.Invoke();
        }

        public static List<string> ToNodes(string path) => path.Split(new[] { Separator.Trim() }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        public static string ToPath(IEnumerable<string> nodes) => string.Join(Separator, nodes);
        public static string Leaf(string path) => ToNodes(path).LastOrDefault() ?? "";
        public static string Parent(string path) { var n = ToNodes(path); return n.Count <= 1 ? "" : ToPath(n.Take(n.Count - 1)); }
        public static int Level(string path) => Math.Max(0, ToNodes(path).Count - 1);

        /// <summary>true se il percorso del server e' dentro la cartella (o in una sua sottocartella)</summary>
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

        /// <summary>tutti i percorsi delle cartelle (genitori prima dei figli, in ordine alfabetico)</summary>
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

        public static int CountServers(string path) => IoC.Get<GlobalData>().VmItemList.Count(vm => IsIn(vm.Server.TreeNodes, path));

        public static bool Exists(string path) => AllPaths().Any(x => string.Equals(x, path, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>errore se il nome non e' valido (vuoto o contiene il separatore), altrimenti stringa vuota</summary>
        public static string ValidateName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return IoC.Translate(LanguageService.CAN_NOT_BE_EMPTY);
            if (name.Contains("/")) return IoC.Translate("folder_name_invalid");
            return "";
        }

        public static bool Create(string parentPath, string name, out string error)
        {
            error = ValidateName(name);
            if (error.Length > 0) return false;
            var path = string.IsNullOrEmpty(parentPath) ? name.Trim() : ToPath(ToNodes(parentPath).Append(name.Trim()));
            if (Exists(path)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, name.Trim()); return false; }
            if (!Load()) { error = "database"; return false; }
            lock (Lock) _records.Add(new FolderRecord { Path = path });
            Save();
            Lemora.Setup.TeamsShared.FolderUpsert(path, "", "");   // col server Teams la cartella arriva anche agli altri PC
            return true;
        }

        /// <summary>rinomina l'ultimo livello del percorso; aggiorna server e impostazioni che stanno dentro</summary>
        public static bool Rename(string path, string newName, out string error)
        {
            error = ValidateName(newName);
            if (error.Length > 0) return false;
            newName = newName.Trim();
            var parent = Parent(path);
            var newPath = string.IsNullOrEmpty(parent) ? newName : ToPath(ToNodes(parent).Append(newName));
            if (string.Equals(newPath, path, StringComparison.CurrentCulture)) return true;
            if (Exists(newPath) && !string.Equals(newPath, path, StringComparison.CurrentCultureIgnoreCase)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, newName); return false; }
            if (!Load()) { error = "database"; return false; }

            var oldNodes = ToNodes(path);
            var newNodes = ToNodes(newPath);
            // server
            // le voci del server Teams cambiano cartella con l'operazione sul server (e tornano con la sincronizzazione): da qui non si scrivono, anche in sola lettura
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
            // elenco salvato e impostazioni
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
        /// sposta la cartella (con tutto cio' che contiene) dentro <paramref name="newParentPath"/> ("" = radice);
        /// server, impostazioni e credenziali condivise seguono il nuovo percorso
        /// </summary>
        public static bool Move(string path, string newParentPath, out string error)
        {
            error = "";
            newParentPath = newParentPath ?? "";
            var leaf = Leaf(path);
            var newPath = string.IsNullOrEmpty(newParentPath) ? leaf : ToPath(ToNodes(newParentPath).Append(leaf));
            if (string.Equals(newPath, path, StringComparison.CurrentCulture)) return true;
            if (IsIn(ToNodes(newParentPath), path)) { error = IoC.Translate("folder_move_into_itself"); return false; }
            if (Exists(newPath)) { error = IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, leaf); return false; }
            if (!Load()) { error = "database"; return false; }

            var oldNodes = ToNodes(path);
            var newNodes = ToNodes(newPath);
            List<string> Remap(IEnumerable<string> nodes) => newNodes.Concat(nodes.Skip(oldNodes.Count)).ToList();

            var servers = IoC.Get<GlobalData>().VmItemList.Select(x => x.Server).Where(sv => IsIn(sv.TreeNodes, path) && !(sv.DataSource is Lemora.Service.DataSource.Model.TeamsSource)).ToList();   // quelle del server Teams cambiano con l'operazione sul server
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

        /// <summary>sollevato dopo la rinomina (vecchio percorso, nuovo percorso): chi ha dati legati alla cartella li aggiorna</summary>
        public static event Action<string, string>? Renamed;

        /// <summary>elimina la cartella solo se non contiene server (anche nelle sottocartelle)</summary>
        public static bool Delete(string path, out string error)
        {
            error = "";
            if (CountServers(path) > 0) { error = IoC.Translate("folder_delete_not_empty"); return false; }
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
        /// Toglie dall'elenco salvato la cartella (solo quel percorso, non le sottocartelle) se e' rimasta solo un nome:
        /// nessuna impostazione (dominio, prefisso) e nessuna credenziale condivisa. Serve alle cartelle delle voci Teams svuotate dal server.
        /// </summary>
        public static void ForgetIfBare(string path)
        {
            if (!Load()) return;
            if (FolderCredentialService.ForFolder(path).Count > 0) return;
            int removed;
            lock (Lock) removed = _records.RemoveAll(r => string.Equals(r.Path, path, StringComparison.CurrentCultureIgnoreCase) && r.Domain.Length == 0 && r.IpPrefix.Length == 0);
            if (removed > 0) Save();
        }

        // ---- suggerimenti calcolati e valori effettivi
        /// <summary>dominio suggerito: il nome del cliente (primo livello) in maiuscolo</summary>
        public static string SuggestDomain(string path) => ToNodes(path).FirstOrDefault()?.ToUpperInvariant() ?? "";

        /// <summary>
        /// rete comune dei server della cartella (e sottocartelle): i primi tre numeri dell'IP piu' frequente, es. "192.168.100.".
        /// Vuoto se la cartella non ha server con un IP.
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

        /// <summary>valore impostato nella cartella o, in mancanza, nella prima cartella genitore che lo ha</summary>
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

        /// <summary>dominio IMPOSTATO nella cartella (o nella prima cartella genitore che lo ha); vuoto se nessuna cartella lo ha scelto</summary>
        public static string ConfiguredDomain(string path) => Inherited(path, r => r.Domain);

        /// <summary>
        /// dominio da usare per i server della cartella: SOLO quello impostato nella cartella (o in un genitore).
        /// Il suggerito (nome del primo livello in maiuscolo) non si applica da solo: riempiva il Dominio di ogni voce
        /// con il nome di cartelle normali (es. DATEAMS); resta come proposta nel pannello della cartella.
        /// </summary>
        public static string EffectiveDomain(string path) => ConfiguredDomain(path);

        /// <summary>prefisso IP da usare per i server della cartella: quello impostato, altrimenti quello calcolato dai server</summary>
        public static string EffectiveIpPrefix(string path) { var v = Inherited(path, r => r.IpPrefix); return v.Length > 0 ? v : SuggestIpPrefix(path); }

        // ---- impostazioni condivise (dominio e prefisso IP)
        public static FolderRecord GetSettings(string path)
        {
            Load();
            lock (Lock)
                return _records.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.CurrentCultureIgnoreCase))
                       ?? new FolderRecord { Path = path };
        }

        public static void SetSettings(string path, string domain, string ipPrefix)
        {
            if (!Load()) return;
            lock (Lock)
            {
                var r = _records.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.CurrentCultureIgnoreCase));
                if (r == null) _records.Add(r = new FolderRecord { Path = path });
                r.Domain = (domain ?? "").Trim();
                r.IpPrefix = (ipPrefix ?? "").Trim();
            }
            Save();
            Lemora.Setup.TeamsShared.FolderUpsert(path, (domain ?? "").Trim(), (ipPrefix ?? "").Trim());
        }

        /// <summary>cartelle dell'elenco salvato (anche vuote) con le loro impostazioni: sono quelle da caricare sul server Teams la prima volta</summary>
        public static List<FolderRecord> Records() { Load(); lock (Lock) return _records.Select(r => new FolderRecord { Path = r.Path, Domain = r.Domain, IpPrefix = r.IpPrefix }).ToList(); }

        /// <summary>
        /// Arrivo dal server Teams: aggiunge le cartelle (anche vuote) con dominio e prefisso IP, toglie dall'elenco quelle cancellate.
        /// Non tocca i server e non rimanda niente al server. true se l'elenco e' cambiato.
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
        /// Una sola volta: ogni tag diventa una cartella (il primo tag del server) per i server che non hanno ancora una cartella.
        /// I tag non vengono toccati. Si segna come fatto nel database.
        /// </summary>
        private static void MigrateTagsOnce()
        {
            if (!AppDbStore.TryGet(TagsMigratedDbKey, out var done) || !string.IsNullOrEmpty(done)) return;
            var all = IoC.Get<GlobalData>().VmItemList;
            if (all.Count == 0) return;   // dati non ancora caricati: si riprova
            var toUpdate = all.Select(x => x.Server).Where(s => s.TreeNodes.Count == 0 && s.Tags.Count > 0).ToList();
            foreach (var s in toUpdate) s.TreeNodes = new List<string> { s.Tags[0].Trim() };
            if (toUpdate.Count > 0)
            {
                var ret = IoC.Get<GlobalData>().UpdateServer(toUpdate);
                if (!ret.IsSuccess) return;   // non segnare come fatto se la scrittura e' fallita
            }
            AppDbStore.Set(TagsMigratedDbKey, "1");
        }
    }
}
