using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.Locality;
using Lemora.Setup;
using Newtonsoft.Json;

namespace Lemora.Service.DataSource.Model
{
    /// <summary>an entry shared by the Teams server, as /api/client/sync sends it</summary>
    internal class TeamsItem
    {
        public string Id { get; set; } = "";
        public int Version { get; set; }
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string User { get; set; } = "";
        public string? Password { get; set; }
        public string Tags { get; set; } = "";
        public bool Disabled { get; set; }
        public bool Deleted { get; set; }
        /// <summary>entry created from a PC's personal connection (the PC's id on the server and the entry's local id on that PC); empty for the administrator's entries</summary>
        public string OriginPc { get; set; } = "";
        public string OriginLocalId { get; set; } = "";
        /// <summary>link between the entry and folder credentials, in JSON ({"c":[{"f":folder,"u":user}],"d":{"f","u"} default,"a":always ask}); empty = none</summary>
        public string Ucfg { get; set; } = "";
        /// <summary>all LOCAL users of this entry (only for this server, not domain users) with their password: encrypted on the server, every PC receives all of them</summary>
        public List<TeamsLocalUser> LUsers { get; set; } = new List<TeamsLocalUser>();
    }

    internal class TeamsLocalUser
    {
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>server - folder credentials link: sent to the server (local ids -> folder and user) and rebuilt on arrival (folder and user -> local ids)</summary>
    internal static class TeamsUserConfig
    {
        internal record Ref(string f, string u);
        private record Cfg(List<Ref>? c, Ref? d = null, bool a = false);   // d and a: old values, no longer sent (personal settings)

        internal static Ref? ToRef(Lemora.Model.Protocol.Base.Credential? c) => c == null ? null : new Ref(Lemora.Setup.TeamsShared.ToServerPath(c.FolderPath), c.UserName);
        internal static string? Find(Ref? r, IReadOnlyList<Lemora.Model.Protocol.Base.Credential> all) => r == null ? null : all.FirstOrDefault(c => string.Equals(c.FolderPath, Lemora.Setup.TeamsShared.FromServerPath(r.f), StringComparison.OrdinalIgnoreCase)
                                                                         && string.Equals(c.UserName, r.u, StringComparison.OrdinalIgnoreCase))?.DatabaseId;

        /// <summary>ONLY the shared link: which folder credentials are active on this server. "Always ask" and the default user are personal settings of each PC (TeamsUserPrefs), they do not go to the server and need no approval</summary>
        public static string Build(ProtocolBaseWithAddressPortUserPwd p)
        {
            if (p.ActiveFolderCredentialIds.Count == 0) return "";
            var all = Lemora.Service.FolderCredentialService.Snapshot();
            var list = p.ActiveFolderCredentialIds.Select(id => ToRef(all.FirstOrDefault(c => c.DatabaseId == id))).Where(r => r != null).Select(r => r!).ToList();
            return list.Count == 0 ? "" : System.Text.Json.JsonSerializer.Serialize(new Cfg(list));
        }

        /// <summary>puts the active folder credentials on the entry; credentials not yet received are skipped (retried on the next read). Default and "always ask" are set by TeamsUserPrefs.Apply</summary>
        /// <param name="onlyKnown">false = the reference was written by this PC (<see cref="TeamsUserPrefs"/>): its own credentials are accepted too</param>
        public static void Apply(ProtocolBaseWithAddressPortUserPwd p, string ucfg, IReadOnlyList<Lemora.Model.Protocol.Base.Credential> all, bool onlyKnown = true)
        {
            Cfg? cfg;
            try { cfg = System.Text.Json.JsonSerializer.Deserialize<Cfg>(ucfg, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (System.Text.Json.JsonException) { return; }
            if (cfg == null) return;
            // the reference was written by another PC (possibly a colleague with "write" access who chooses the host): bind ONLY to credentials the server
            // knows, never to one that exists only on this PC (read-only/"requests": those created before connecting). Security hardening.
            var known = Lemora.Setup.TeamsShared.KnownCreds();
            var shared = onlyKnown ? all.Where(c => known.Contains((c.FolderPath.ToLowerInvariant(), c.UserName.ToLowerInvariant()))).ToList() : all.ToList();
            p.ActiveFolderCredentialIds = (cfg.c ?? new List<Ref>()).Select(r => Find(r, shared)).Where(x => x != null).Select(x => x!).Distinct().ToList();
        }
    }

    /// <summary>
    /// All other settings of a Teams entry that the server does not keep (chosen client, RDP/VNC settings, note, color...): they stay on THIS PC, in the app's encrypted
    /// database, without requests to the administrator. The entry is saved as it is in the editor (without password) and on read it is laid over the server's entry;
    /// the shared fields (name, address, port, user, password, folder, tags) and those handled separately always come from the server.
    /// </summary>
    internal static class TeamsOverlay
    {
        private const string DbKey = "teams.overlay";
        private static readonly object Gate = new object();
        private static readonly JsonSerializerSettings Merge = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace, NullValueHandling = NullValueHandling.Ignore };

        private static Dictionary<string, string> Load()
        {
            try { return AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(json!) ?? new() : new(); }
            catch { return new Dictionary<string, string>(); }
        }

        public static Dictionary<string, string> LoadAll() => Load();

        /// <summary>saves the entry's settings (id on the server); true if they changed</summary>
        public static bool Save(string itemId, ProtocolBase p)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            var c = p.Clone();
            if (c is ProtocolBaseWithAddressPortUserPwd u)
            {
                u.Password = "";   // the password lives on the server, never here
                // local users with a password live on the server (all PCs receive them); only those with a private key stay here, they are not shared
                u.Users = new System.Collections.ObjectModel.ObservableCollection<UserEntry>(u.Users.Where(x => x.UsePrivateKey).Select(x => (UserEntry)x.Clone()));
            }
            var json = c.ToJsonString();
            lock (Gate)
            {
                var d = Load();
                if (d.TryGetValue(itemId, out var old) && old == json) return false;
                d[itemId] = json;
                AppDbStore.Set(DbKey, JsonConvert.SerializeObject(d));
                return true;
            }
        }

        /// <summary>puts the saved settings back on the entry, then the fields that always come from the server</summary>
        public static void Apply(ProtocolBaseWithAddressPortUserPwd p, string json)
        {
            var id = p.Id; var name = p.DisplayName; var nodes = p.TreeNodes; var tags = p.Tags;
            var address = p.Address; var port = p.Port; var user = p.UserName; var pwd = p.Password;
            var active = p.ActiveFolderCredentialIds; var users = p.Users; var def = p.DefaultUserKey; var ask = p.AskWhichUser;
            try { JsonConvert.PopulateObject(json, p, Merge); }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning("Teams: personal settings of an entry not applied: " + e.Message); }
            var keyUsers = p.Users.Where(x => x.UsePrivateKey).ToList();   // users with a private key: only on this PC
            p.Id = id; p.DisplayName = name; p.TreeNodes = nodes; p.Tags = tags;
            p.Address = address; p.Port = port; p.UserName = user; p.Password = pwd;
            p.ActiveFolderCredentialIds = active; p.DefaultUserKey = def; p.AskWhichUser = ask;
            p.Users = new System.Collections.ObjectModel.ObservableCollection<UserEntry>(users.Concat(keyUsers));   // server users + those with a key
            // with users or folder credentials the password prompt is decided at connection time, for the chosen user (as in the editor): an "always ask" left
            // in the saved settings from when the entry had no users yet made the prompt appear on every connection
            if (p.Users.Count > 0 || p.ActiveFolderCredentialIds.Count > 0) p.AskPasswordWhenConnect = false;
        }
    }

    /// <summary>
    /// PERSONAL settings of this PC for Teams entries: "Always ask which user to use" and default user. They live in the app's encrypted database (not on the server):
    /// everyone changes them whenever they like, without requests to the administrator. No setting: "always ask" on.
    /// </summary>
    internal static class TeamsUserPrefs
    {
        /// <param name="Ucfg">active folder credentials chosen in the editor on this PC. Valid while the server has, for this entry, the same address
        /// (<see cref="Host"/>) and the credentials it had then (<see cref="Base"/>) or the chosen ones (approved): if the server changes them, the server wins.
        /// Case seen on a real folder: the server already linked the credentials but this PC discarded them (not "known": local password different from the
        /// server's), and enabling them again saved nothing; with "requests" or the server unreachable the choice was also lost until approval</param>
        internal class Pref { public bool Ask { get; set; } = true; public TeamsUserConfig.Ref? Def { get; set; } public string? Ucfg { get; set; } public string? Base { get; set; } public string? Host { get; set; } }
        private const string DbKey = "teams.userprefs";
        private static readonly object Gate = new object();

        public static Dictionary<string, Pref> Load()
        {
            try { return AppDbStore.TryGet(DbKey, out var json) && !string.IsNullOrEmpty(json) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Pref>>(json!) ?? new() : new(); }
            catch { return new Dictionary<string, Pref>(); }
        }

        /// <summary>saves "always ask", the default and the active credentials chosen in the editor (against the server's <paramref name="serverUcfg"/> and <paramref name="serverHost"/>) of a Teams entry (id on the server); true if something changed</summary>
        public static bool Save(string itemId, ProtocolBaseWithAddressPortUserPwd p, string? serverUcfg = null, string? serverHost = null)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            var all = Lemora.Service.FolderCredentialService.Snapshot();
            var def = p.DefaultUserKey.StartsWith("F:") ? TeamsUserConfig.ToRef(all.FirstOrDefault(c => c.DatabaseId == p.DefaultUserKey.Substring(2))) : null;
            var mine = serverUcfg != null;
            var now = new Pref { Ask = p.AskWhichUser == true, Def = def, Ucfg = mine ? TeamsUserConfig.Build(p) : null, Base = mine ? serverUcfg : null, Host = mine ? serverHost : null };
            lock (Gate)
            {
                var d = Load();
                var had = d.TryGetValue(itemId, out var old);
                if (had ? (old!.Ask == now.Ask && old.Def == now.Def && old.Ucfg == now.Ucfg && old.Base == now.Base && old.Host == now.Host) : (now.Ask && now.Def == null && now.Ucfg == null)) return false;
                d[itemId] = now;
                AppDbStore.Set(DbKey, System.Text.Json.JsonSerializer.Serialize(d));
                return true;
            }
        }

        /// <summary>puts the personal settings on the entry (or the initial values: always ask on, default = first active credential)</summary>
        public static void Apply(ProtocolBaseWithAddressPortUserPwd p, string itemId, Dictionary<string, Pref> prefs, IReadOnlyList<Lemora.Model.Protocol.Base.Credential> all, string serverUcfg, string serverHost)
        {
            prefs.TryGetValue(itemId, out var pref);
            // credentials chosen here, while the server still has the same address and what it had then (or the chosen ones): they are this PC's own
            // choice (also credentials only on this PC). Another address = the entry now points elsewhere: the choice no longer applies (password not sent there)
            if (pref?.Ucfg != null && pref.Host == serverHost && (pref.Base == serverUcfg || pref.Ucfg == serverUcfg))
            {
                if (pref.Ucfg.Length == 0) p.ActiveFolderCredentialIds = new List<string>();
                else TeamsUserConfig.Apply(p, pref.Ucfg, all, onlyKnown: false);
            }
            p.AskWhichUser = pref?.Ask ?? true;
            var d = TeamsUserConfig.Find(pref?.Def, all);
            p.DefaultUserKey = d != null && p.ActiveFolderCredentialIds.Contains(d) ? Lemora.Service.UserResolver.FolderKey(d)
                : p.ActiveFolderCredentialIds.Count > 0 ? Lemora.Service.UserResolver.FolderKey(p.ActiveFolderCredentialIds[0]) : "";
        }
    }

    /// <summary>
    /// Entries shared by the Teams server: READ-ONLY data source, kept in memory.
    /// There is no file: the copy lives in the app's encrypted database (AppDbStore "teams.items"), filled by TeamsSync.
    /// </summary>
    public sealed class TeamsSource : DataSourceBase
    {
        public const string SourceName = "Teams";
        public const int MaxOfflineDays = 7; // after this many days without contact with the server the entries can no longer be opened

        private readonly TeamsDatabase _db = new TeamsDatabase();

        public TeamsSource()
        {
            DataSourceName = SourceName;
            IsWritable = false;
        }

        public override string GetConnectionString(int connectTimeOutSeconds = 5) => "";
        [JsonIgnore] public override DatabaseType DatabaseType => DatabaseType.Sqlite;
        [JsonIgnore] public override string Description => "Teams";
        public override IDatabase GetDataBase() => _db;

        // no file to open: the source is always ready
        public override DatabaseStatus Database_SelfCheck(int connectTimeOutSeconds = 5)
        {
            Status = EnumDatabaseStatus.OK;
            return DatabaseStatus.New(Status);
        }

        /// <summary>writable only with the "write" permission given by the server (changes when the administrator changes it)</summary>
        internal void SetWritable(bool writable)
        {
            if (IsWritable == writable) return;
            IsWritable = writable;
            // entries already shown (selected row, details panel, selection bar, folder bar) update right away
            Stylet.Execute.OnUIThread(() =>
            {
                var data = IoC.TryGet<GlobalData>();
                if (data == null) return;
                foreach (var vm in data.VmItemList.Where(x => ReferenceEquals(x.Server.DataSource, this))) vm.RefreshWritable();
                data.ReloadTagsFromServers();   // raises ServersReloaded: the folder bar recomputes "read-only"
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();   // Edit/Delete buttons (CanExecute)
            });
        }

        internal void SetItems(List<TeamsItem> items, DateTime lastContact)
        {
            _db.Set(items, lastContact);
            ClearReadTimestamp(); // reread on the next round
            Status = EnumDatabaseStatus.OK;
        }

        /// <summary>folders (nodes) of ALL shared entries, including disabled ones or those hidden after too many days offline: to know what is really on the server</summary>
        internal List<List<string>> AllFolders() => _db.AllFolders();
    }

    /// <summary>in-memory "database": it can only read the entries; every write fails</summary>
    internal sealed class TeamsDatabase : IDatabase
    {
        private List<TeamsItem> _items = new List<TeamsItem>();
        private DateTime _lastContact = DateTime.MinValue;
        private long _stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        public TeamsDatabase() : base(TeamsSource.SourceName, DatabaseType.Sqlite) { }

        public void Set(List<TeamsItem> items, DateTime lastContact)
        {
            lock (this)
            {
                _items = items;
                _lastContact = lastContact;
                _stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            }
        }

        private static Result ReadOnly() => Result.Fail("readonly");

        public List<List<string>> AllFolders() { lock (this) return _items.Select(x => x.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList()).ToList(); }

        public override ResultSelects<ProtocolBase> GetServers()
        {
            List<TeamsItem> items;
            lock (this)
            {
                // no contact with the server for too long: no entries (the block lifts by itself on the first successful round)
                items = DateTime.Now - _lastContact > TimeSpan.FromDays(TeamsSource.MaxOfflineDays) ? new List<TeamsItem>() : _items.Where(x => !x.Disabled).ToList();
            }
            var ret = new List<ProtocolBase>(items.Count);
            IReadOnlyList<Lemora.Model.Protocol.Base.Credential>? folderCreds = null;
            Dictionary<string, TeamsUserPrefs.Pref>? prefs = null;
            Dictionary<string, string>? overlays = null;
            foreach (var i in items)
            {
                ProtocolBaseWithAddressPortUserPwd p = i.Protocol switch { "ssh" => new SSH(), "vnc" => new VNC(), _ => new RDP() };
                p.Id = "teams-" + i.Id;
                p.DisplayName = i.Name;
                p.Address = i.Host;
                p.Port = i.Port.ToString();
                p.UserName = i.User;
                p.Password = i.Password ?? "";
                p.TreeNodes = i.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                p.Tags = i.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (i.LUsers.Count > 0 && i.Protocol != "vnc")   // local users of this server, all of them, as the server has them
                    p.Users = new System.Collections.ObjectModel.ObservableCollection<UserEntry>(i.LUsers.Select(x => new UserEntry { UserName = x.User, Password = x.Password ?? "" }));
                if (i.Protocol != "vnc")
                {
                    // in-memory copy if available (never the database from here: GetServers runs during list reloads, even while the UI is busy); read once only if it is empty
                    folderCreds ??= Lemora.Service.FolderCredentialService.Cached.Count > 0 ? Lemora.Service.FolderCredentialService.Cached : Lemora.Service.FolderCredentialService.Snapshot();
                    prefs ??= TeamsUserPrefs.Load();
                    if (i.Ucfg.Length > 0) TeamsUserConfig.Apply(p, i.Ucfg, folderCreds);   // active credentials: shared
                    TeamsUserPrefs.Apply(p, i.Id, prefs, folderCreds, i.Ucfg ?? "", HostKey(i));   // "always ask", default and credentials chosen on this PC
                    // no local user, no active folder credential and no password: the entry's user is just a reflection of a domain credential
                    // (left on the server from before): it is not a user of this server, so the entry stays empty
                    if (p.Users.Count == 0 && p.ActiveFolderCredentialIds.Count == 0 && string.IsNullOrEmpty(i.Password) && p.UserName.Length > 0
                        && folderCreds.Any(c => FolderService.IsIn(p.TreeNodes, c.FolderPath) && string.Equals(c.UserName, p.UserName, StringComparison.OrdinalIgnoreCase)))
                        p.UserName = "";
                }
                overlays ??= TeamsOverlay.LoadAll();
                if (overlays.TryGetValue(i.Id, out var ov)) TeamsOverlay.Apply(p, ov);   // the other settings (client, RDP, note...) stay on this PC, VNC included
                p.EncryptToDatabaseLevel(); // as a real database reads them: the password comes out in plaintext only when connecting
                ret.Add(p);
            }
            return ResultSelects<ProtocolBase>.Success(ret);
        }

        public override ResultSelects<Credential> GetCredentials(bool closeInEnd = true) => ResultSelects<Credential>.Success(new List<Credential>());
        public override ResultLong GetTableUpdateTimestamp(string tableName, bool closeInEnd = true) { lock (this) return ResultLong.Success(_stamp); }
        public override ResultString GetConfig(string key, bool closeInEnd = true) => ResultString.Success(null);

        public override void CloseConnection() { }
        public override Result OpenNewConnection(string newConnectionString) => Result.Success();
        public override bool IsConnected() => true;
        public override Result InitTables() => Result.Success();
        public override Result TableExists(string tableName) => Result.Success();

        // ---- writing: every change goes straight to the server (which checks the permission) and the local copy changes only if it accepts ----

        private const string IdPrefix = "teams-";
        private static string ServerId(string id) => id.StartsWith(IdPrefix) ? id.Substring(IdPrefix.Length) : "";

        /// <summary>from app entry to shared entry; null + error if the type cannot be shared</summary>
        private TeamsItem? ToItem(ProtocolBase p, string serverId, out string? error)
        {
            error = null;
            var clone = (ProtocolBase)p.Clone();
            clone.DecryptToConnectLevel(); // the editor hands over the password encrypted as in a database: it goes to the server in plaintext (the server encrypts it in turn)
            if (clone is not ProtocolBaseWithAddressPortUserPwd u || clone is not (RDP or SSH or VNC) ||!int.TryParse(u.Port, out var port))
            {
                error = TeamsSync.ErrorText("protocol");
                return null;
            }
            int version;
            lock (this) version = _items.FirstOrDefault(x => x.Id == serverId)?.Version ?? 0;
            return new TeamsItem
            {
                Id = serverId, Version = version, Name = clone.DisplayName, Protocol = clone switch { SSH => "ssh", VNC => "vnc", _ => "rdp" }, Host = u.Address, Port = port, User = clone is VNC ? "" : u.UserName,
                Password = u.Password, Folder = string.Join("/", clone.TreeNodes), Tags = string.Join(",", clone.Tags),
                Ucfg = clone is VNC ? "" : TeamsUserConfig.Build(u),
                LUsers = clone is VNC ? new List<TeamsLocalUser>() : u.Users.Where(x => !x.UsePrivateKey && !string.IsNullOrWhiteSpace(x.UserName))
                    .Select(x => new TeamsLocalUser { User = x.UserName.Trim(), Password = x.Password ?? "" }).ToList(),
            };
        }

        private static string HostKey(TeamsItem i) => (i.Host ?? "").Trim().ToLowerInvariant() + ":" + i.Port;

        /// <summary>the entry has the same shared fields as the existing one (only color, notes, RDP settings... changed: Teams does not keep them)</summary>
        private bool Unchanged(TeamsItem n)
        {
            TeamsItem? o;
            lock (this) o = _items.FirstOrDefault(x => x.Id == n.Id);
            return o != null && o.Name == n.Name && o.Protocol == n.Protocol && string.Equals(o.Host, n.Host, StringComparison.OrdinalIgnoreCase) && o.Port == n.Port
                   && (o.User ?? "") == (n.User ?? "") && (o.Password ?? "") == (n.Password ?? "") && o.Folder == n.Folder && (o.Tags ?? "") == (n.Tags ?? "") && (o.Ucfg ?? "") == (n.Ucfg ?? "")
                   && (o.LUsers ?? new List<TeamsLocalUser>()).Select(x => x.User + "\t" + x.Password).SequenceEqual(n.LUsers.Select(x => x.User + "\t" + x.Password));
        }

        private Result WriteOne(ProtocolBase p, bool add)
        {
            var sid = add ? "" : ServerId(p.Id);
            if (!add && sid.Length == 0) return Result.Fail("id");
            var item = ToItem(p, sid, out var err);
            if (item == null) return Result.Fail(err!);
            // nothing to send: with "write" the change would vanish on reread, with "with approval" it would be an empty request
            // personal settings ("always ask", default): they stay on this PC, without requests to the administrator, under any permission
            if (!add && p is ProtocolBaseWithAddressPortUserPwd pu)
            {
                TeamsItem? cur;
                lock (this) cur = _items.FirstOrDefault(x => x.Id == sid);
                TeamsUserPrefs.Save(sid, pu, p is VNC || cur == null ? null : cur.Ucfg ?? "", cur == null ? null : HostKey(cur));
            }
            if (!add) TeamsOverlay.Save(sid, p);   // chosen client, RDP/VNC settings, note, color...: they stay on this PC
            // nothing changed for the server: saving again (e.g. credentials already active, approved a moment earlier) just closes the editor.
            // Before, an error said "only name, address... are shared": wrong (active credentials are shared too) and the other settings stay on this PC (TeamsOverlay)
            if (!add && Unchanged(item)) return Result.Success();
            err = TeamsSync.Write(sid, item, out var newId);
            if (err != null) return Result.Fail(err);
            if (add) { p.Id = IdPrefix + newId; if (p is ProtocolBaseWithAddressPortUserPwd pn) TeamsUserPrefs.Save(newId, pn); TeamsOverlay.Save(newId, p); }
            return Result.Success();
        }

        public override Result AddServer(ref ProtocolBase protocolBase) => WriteOne(protocolBase, add: true);

        public override Result AddServer(IEnumerable<ProtocolBase> protocolBases)
        {
            foreach (var p in protocolBases)
            {
                var r = WriteOne(p, add: true);
                if (!r.IsSuccess) return r;
            }
            return Result.Success();
        }

        public override Result UpdateServer(ProtocolBase server) => WriteOne(server, add: false);

        public override Result UpdateServer(IEnumerable<ProtocolBase> servers)
        {
            var list = servers.ToList();
            foreach (var p in list)
            {
                var r = WriteOne(p, add: false);
                if (!r.IsSuccess) return r;
            }
            return Result.Success();
        }

        public override Result DeleteServer(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                var sid = ServerId(id);
                var err = sid.Length == 0 ? "id" : TeamsSync.Write(sid, null, out _);
                if (err != null) return Result.Fail(err);
            }
            return Result.Success();
        }
        public override Result AddCredential(ref Credential credential) => ReadOnly();
        public override Result UpdateCredential(Credential credential, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null) => ReadOnly();
        public override Result DeleteCredential(IEnumerable<string> names, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null) => ReadOnly();
        public override Result SetConfig(string key, string? value, bool closeInEnd = true) => ReadOnly();
        public override Result SetTableUpdateTimestamp(string tableName, long time = -1, bool closeInEnd = true) => ReadOnly();
    }
}
