using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Service.Locality;
using Lemora.Utils;
using Newtonsoft.Json;

namespace Lemora.Service
{
    /// <summary>a user of a device (NAS, switch, DVR...) with its password</summary>
    public class DeviceUser
    {
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>
    /// Entry of the folder password manager: a device with name, address and one or more user/password pairs.
    /// It has no protocol or type. It belongs to a folder (<see cref="Folder"/>) and also applies to its subfolders.
    /// </summary>
    public class DeviceEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>folder path, with " / " between levels; empty = outside any folder</summary>
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>main address</summary>
        public string Ip { get; set; } = "";
        /// <summary>other addresses of the same device (e.g. a NAS with two network cards)</summary>
        public List<string> MoreIps { get; set; } = new();
        public List<DeviceUser> Users { get; set; } = new();

        /// <summary>every address: the main one first, then the others (no empty ones, no repeats)</summary>
        public List<string> AllIps() => new[] { Ip }.Concat(MoreIps).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        /// <summary>increases at every change (needed for synchronization with the Teams server)</summary>
        public int Version { get; set; } = 1;
        /// <summary>true = the entry is also on the Teams server (arrived from there or sent); false = only on this PC</summary>
        public bool OnServer { get; set; }

        public DeviceEntry Clone() => new()
        {
            Id = Id, Folder = Folder, Name = Name, Ip = Ip, MoreIps = MoreIps.ToList(), Version = Version, OnServer = OnServer,
            Users = Users.Select(u => new DeviceUser { User = u.User, Password = u.Password }).ToList(),
        };
    }

    /// <summary>position of the drawer tab on the edge of the remote screen: edge (left/right/top/bottom) and point 0..1 along the edge</summary>
    public class DrawerPlace
    {
        public string Edge { get; set; } = "right";
        public double T { get; set; } = 0.5;
    }

    /// <summary>
    /// Folder password manager (drawer tab on the screen of saved RDP and VNC sessions).
    /// The entries live in the app's encrypted database (key/value store, like the folders): nothing outside the database.
    /// </summary>
    public static class DevicePasswordService
    {
        /// <summary>master switch: false = no drawer tab. Always on for now; where it gets turned off is decided later (currently a single spot in the code).</summary>
        public const bool Enabled = true;

        /// <summary>
        /// true = the drawer tab appears only if the server's folder has at least one entry (and the popup opens with only "+ New entry" if empty).
        /// Now false: the drawer tab is always there, because the first entry is added from here. To be changed when the insertion section is ready.
        /// </summary>
        public const bool ShowOnlyIfFolderHasEntries = false;

        public const int ClipboardClearSeconds = 30;

        private const string DbKey = "device_passwords";
        private const string PlaceKey = "device_passwords_place";

        private static List<DeviceEntry> _items = new();
        private static bool _loaded;
        private static readonly object Lock = new();

        /// <summary>raised when the entries change (local edit or arrival from the server)</summary>
        public static event Action? Changed;

        private static bool Load()
        {
            lock (Lock)
            {
                if (_loaded) return true;
                if (!DataVault.IsUnlocked) return false;   // without the key the content cannot be read: it must not be mistaken for "empty" and then overwritten
                if (!AppDbStore.TryGet(DbKey, out var json)) return false;
                try { _items = string.IsNullOrEmpty(json) ? new() : JsonConvert.DeserializeObject<List<DeviceEntry>>(json) ?? new(); }
                catch { _items = new(); }
                _loaded = true;
                return true;
            }
        }

        private static void Save()
        {
            lock (Lock)
            {
                if (!_loaded) return;
                AppDbStore.Set(DbKey, JsonConvert.SerializeObject(_items));
            }
            Changed?.Invoke();
        }

        /// <summary>all entries (copies: the receiver can modify them without touching the store)</summary>
        public static List<DeviceEntry> All() { lock (Lock) { return Load() ? _items.Select(x => x.Clone()).ToList() : new(); } }

        /// <summary>replaces all entries (arrival from the server: the local copy becomes the server's)</summary>
        public static void ReplaceAll(IEnumerable<DeviceEntry> items)
        {
            lock (Lock) { if (!Load()) return; _items = items.Select(x => x.Clone()).ToList(); }
            Save();
        }

        /// <summary>true if the entry's folder path is the server's folder or one of its parent folders</summary>
        public static bool AppliesTo(DeviceEntry e, IReadOnlyList<string> serverNodes) => FolderService.IsIn(serverNodes, e.Folder);

        /// <summary>the entries that apply to a server: those of its folder and of the folders above</summary>
        public static List<DeviceEntry> ForServer(IReadOnlyList<string> serverNodes) =>
            All().Where(x => AppliesTo(x, serverNodes))
                 .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        /// <summary>
        /// Prepares saving an entry: cleans up names and users. A new entry with the same address as one already in the same folder
        /// does not create a duplicate: its credentials are added to that device (merged = true, and the result is the existing entry to UPDATE).
        /// Writes nothing: the caller saves it locally (<see cref="Save(DeviceEntry)"/>) or sends it to the Teams server.
        /// </summary>
        public static DeviceEntry? Prepare(DeviceEntry e, bool isNew, out bool merged)
        {
            merged = false;
            e = e.Clone();
            e.Name = (e.Name ?? "").Trim();
            // main address = the first one written; the others without repeats
            var ips = e.AllIps();
            SetIps(e, ips);
            e.Users =e.Users.Where(u => !string.IsNullOrWhiteSpace(u.User)).Select(u => new DeviceUser { User = u.User.Trim(), Password = u.Password ?? "" }).ToList();
            if (!isNew) return e;
            lock (Lock)
            {
                if (!Load()) return null;
                // same device = same folder and at least one address in common
                var same = ips.Count > 0 ? _items.FirstOrDefault(x => string.Equals(x.Folder, e.Folder, StringComparison.CurrentCultureIgnoreCase) && x.AllIps().Intersect(ips, StringComparer.OrdinalIgnoreCase).Any()) : null;
                if (same == null) return e;
                var target = same.Clone();
                target.MoreIps = target.AllIps().Concat(ips).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).ToList();   // the new entry's other addresses join too
                foreach (var u in e.Users)
                {
                    var have = target.Users.FirstOrDefault(x => string.Equals(x.User, u.User, StringComparison.CurrentCultureIgnoreCase));
                    if (have == null) target.Users.Add(u); else have.Password = u.Password;
                }
                merged = true;
                return target;
            }
        }

        /// <summary>saves locally: adds the entry or replaces the one with the same id (version + 1)</summary>
        public static bool Save(DeviceEntry e)
        {
            lock (Lock)
            {
                if (!Load()) return false;
                var i = _items.FindIndex(x => x.Id == e.Id);
                if (i < 0) _items.Add(e);
                else { e.Version = _items[i].Version + 1; _items[i] = e; }
            }
            Save();
            return true;
        }

        public static bool Delete(string id)
        {
            lock (Lock)
            {
                if (!Load()) return false;
                if (_items.RemoveAll(x => x.Id == id) == 0) return false;
            }
            Save();
            return true;
        }

        /// <summary>
        /// Arrival from the Teams server: adds or replaces entries (by id, if the version is not older) and removes the deleted ones.
        /// The arrived entries are "on the server". true if something changed.
        /// </summary>
        public static bool ApplyRemote(IEnumerable<DeviceEntry> upserts, IEnumerable<string> deletedIds)
        {
            bool changed = false;
            lock (Lock)
            {
                if (!Load()) return false;
                foreach (var d in deletedIds) changed |= _items.RemoveAll(x => x.Id == d) > 0;
                foreach (var u in upserts)
                {
                    var c = u.Clone(); c.OnServer = true;
                    var i = _items.FindIndex(x => x.Id == c.Id);
                    if (i < 0) { _items.Add(c); changed = true; }
                    else if (_items[i].Version <= c.Version) { _items[i] = c; changed = true; }
                }
            }
            if (changed) Save();
            return changed;
        }

        /// <summary>the server restarted from scratch: all entries must be uploaded again</summary>
        public static void MarkAllLocal()
        {
            lock (Lock) { if (!Load()) return; foreach (var x in _items) x.OnServer = false; }
            Save();
        }

        /// <summary>PC disconnected from the server: the entries that came from there are deleted (those made only on this PC remain)</summary>
        public static void RemoveServerCopies()
        {
            int n;
            lock (Lock) { if (!Load()) return; n = _items.RemoveAll(x => x.OnServer); }
            if (n > 0) Save();
        }

        /// <summary>entries only on this PC (not yet on the server): uploaded to the server when the PC can write</summary>
        public static List<DeviceEntry> NotOnServer() => All().Where(x => !x.OnServer).ToList();

        /// <summary>saved position of the drawer tab (right edge, halfway, if none)</summary>
        public static DrawerPlace GetPlace()
        {
            try
            {
                if (AppDbStore.TryGet(PlaceKey, out var json) && !string.IsNullOrEmpty(json))
                {
                    var p = JsonConvert.DeserializeObject<DrawerPlace>(json);
                    if (p != null && p.Edge is "left" or "right" or "top" or "bottom") { p.T = Math.Clamp(p.T, 0, 1); return p; }
                }
            }
            catch { /* unreadable value: fall back to the default */ }
            return new DrawerPlace();
        }

        public static void SetPlace(DrawerPlace p) => AppDbStore.Set(PlaceKey, JsonConvert.SerializeObject(p));

        /// <summary>
        /// IP prefix suggested for a new entry: the first three numbers of the address of the connected server (10.0.4.21 -> "10.0.4.").
        /// For addresses that are not IPv4 (names, IPv6) there is no prefix. Networks other than /24 are still to be decided.
        /// </summary>
        public static string IpPrefix(string? serverAddress)
        {
            var parts = (serverAddress ?? "").Trim().Split('.');
            if (parts.Length != 4 || parts.Any(p => !byte.TryParse(p, out _))) return "";
            return string.Join(".", parts.Take(3)) + ".";
        }

        /// <summary>addresses typed in one field, separated by commas, semicolons or spaces (an address never contains them)</summary>
        public static List<string> SplitIps(string? text) =>
            (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>sets main and other addresses from the list (the first one is the main one)</summary>
        public static void SetIps(DeviceEntry e, IEnumerable<string> ips)
        {
            var l = ips.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            e.Ip = l.FirstOrDefault() ?? "";
            e.MoreIps = l.Skip(1).ToList();
        }

        /// <summary>search filter: name, address or user name (case-insensitive)</summary>
        public static bool Matches(DeviceEntry e, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return true;
            var cmp = StringComparison.CurrentCultureIgnoreCase;
            return e.Name.Contains(query, cmp) || e.AllIps().Any(x => x.Contains(query, cmp)) || e.Users.Any(u => u.User.Contains(query, cmp));
        }
    }
}
