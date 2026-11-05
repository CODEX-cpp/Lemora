using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Service.Locality;
using Lemora.Utils;
using Newtonsoft.Json;

namespace Lemora.Service
{
    /// <summary>un utente di un dispositivo (NAS, switch, DVR...) con la sua password</summary>
    public class DeviceUser
    {
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>
    /// Voce del gestore password della cartella: un dispositivo con nome, indirizzo e una o piu' coppie utente/password.
    /// Non ha protocollo ne' tipo. Appartiene a una cartella (<see cref="Folder"/>) e vale anche per le sue sottocartelle.
    /// </summary>
    public class DeviceEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>percorso della cartella, con " / " tra i livelli; vuoto = fuori da ogni cartella</summary>
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public List<DeviceUser> Users { get; set; } = new();
        /// <summary>aumenta a ogni modifica (serve alla sincronizzazione col server Teams)</summary>
        public int Version { get; set; } = 1;
        /// <summary>true = la voce c'e' anche sul server Teams (arrivata da li' o inviata); false = solo su questo PC</summary>
        public bool OnServer { get; set; }

        public DeviceEntry Clone() => new()
        {
            Id = Id, Folder = Folder, Name = Name, Ip = Ip, Version = Version, OnServer = OnServer,
            Users = Users.Select(u => new DeviceUser { User = u.User, Password = u.Password }).ToList(),
        };
    }

    /// <summary>posizione della linguetta sul bordo dello schermo remoto: bordo (left/right/top/bottom) e punto 0..1 lungo il bordo</summary>
    public class DrawerPlace
    {
        public string Edge { get; set; } = "right";
        public double T { get; set; } = 0.5;
    }

    /// <summary>
    /// Gestore password della cartella (linguetta sullo schermo delle sessioni RDP e VNC salvate).
    /// Le voci stanno nel database cifrato dell'app (archivio chiave/valore, come le cartelle): niente fuori dal database.
    /// </summary>
    public static class DevicePasswordService
    {
        /// <summary>interruttore generale: false = nessuna linguetta. Per ora sempre accesa; dove si spegne si decide dopo (ora un solo punto nel codice).</summary>
        public const bool Enabled = true;

        /// <summary>
        /// true = la linguetta compare solo se la cartella del server ha almeno una voce (e il popup si apre con il solo "+ Nuova voce" se vuota).
        /// Ora false: la linguetta c'e' sempre, perche' la prima voce si inserisce da qui. Si cambia quando la sezione di inserimento sara' pronta.
        /// </summary>
        public const bool ShowOnlyIfFolderHasEntries = false;

        public const int ClipboardClearSeconds = 30;

        private const string DbKey = "device_passwords";
        private const string PlaceKey = "device_passwords_place";

        private static List<DeviceEntry> _items = new();
        private static bool _loaded;
        private static readonly object Lock = new();

        /// <summary>sollevato quando cambiano le voci (modifica locale o arrivo dal server)</summary>
        public static event Action? Changed;

        private static bool Load()
        {
            lock (Lock)
            {
                if (_loaded) return true;
                if (!DataVault.IsUnlocked) return false;   // senza chiave il contenuto non si legge: non si deve scambiare per "vuoto" e poi sovrascrivere
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

        /// <summary>tutte le voci (copie: chi le riceve puo' modificarle senza toccare l'archivio)</summary>
        public static List<DeviceEntry> All() { lock (Lock) { return Load() ? _items.Select(x => x.Clone()).ToList() : new(); } }

        /// <summary>sostituisce tutte le voci (arrivo dal server: la copia locale diventa quella del server)</summary>
        public static void ReplaceAll(IEnumerable<DeviceEntry> items)
        {
            lock (Lock) { if (!Load()) return; _items = items.Select(x => x.Clone()).ToList(); }
            Save();
        }

        /// <summary>true se il percorso della cartella della voce e' la cartella del server o una sua cartella genitore</summary>
        public static bool AppliesTo(DeviceEntry e, IReadOnlyList<string> serverNodes) => FolderService.IsIn(serverNodes, e.Folder);

        /// <summary>le voci che valgono per un server: quelle della sua cartella e delle cartelle sopra</summary>
        public static List<DeviceEntry> ForServer(IReadOnlyList<string> serverNodes) =>
            All().Where(x => AppliesTo(x, serverNodes))
                 .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        /// <summary>
        /// Prepara il salvataggio di una voce: pulisce nomi e utenti. Una voce nuova con lo stesso indirizzo di una gia' presente nella stessa cartella
        /// non crea un doppione: le sue credenziali si aggiungono a quel dispositivo (merged = true, e il risultato e' la voce esistente da AGGIORNARE).
        /// Non scrive nulla: chi chiama la salva in locale (<see cref="Save(DeviceEntry)"/>) o la manda al server Teams.
        /// </summary>
        public static DeviceEntry? Prepare(DeviceEntry e, bool isNew, out bool merged)
        {
            merged = false;
            e = e.Clone();
            e.Name = (e.Name ?? "").Trim();
            e.Ip = (e.Ip ?? "").Trim();
            e.Users = e.Users.Where(u => !string.IsNullOrWhiteSpace(u.User)).Select(u => new DeviceUser { User = u.User.Trim(), Password = u.Password ?? "" }).ToList();
            if (!isNew) return e;
            lock (Lock)
            {
                if (!Load()) return null;
                var same = e.Ip.Length > 0 ? _items.FirstOrDefault(x => string.Equals(x.Folder, e.Folder, StringComparison.CurrentCultureIgnoreCase) && string.Equals(x.Ip, e.Ip, StringComparison.OrdinalIgnoreCase)) : null;
                if (same == null) return e;
                var target = same.Clone();
                foreach (var u in e.Users)
                {
                    var have = target.Users.FirstOrDefault(x => string.Equals(x.User, u.User, StringComparison.CurrentCultureIgnoreCase));
                    if (have == null) target.Users.Add(u); else have.Password = u.Password;
                }
                merged = true;
                return target;
            }
        }

        /// <summary>salva in locale: aggiunge la voce o sostituisce quella con lo stesso id (versione + 1)</summary>
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
        /// Arrivo dal server Teams: aggiunge o sostituisce le voci (per id, se la versione non e' piu' vecchia) e toglie quelle cancellate.
        /// Le voci arrivate sono "sul server". true se qualcosa e' cambiato.
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

        /// <summary>il server e' ripartito da zero: tutte le voci vanno ricaricate</summary>
        public static void MarkAllLocal()
        {
            lock (Lock) { if (!Load()) return; foreach (var x in _items) x.OnServer = false; }
            Save();
        }

        /// <summary>PC scollegato dal server: le voci che venivano da li' si cancellano (restano quelle fatte solo su questo PC)</summary>
        public static void RemoveServerCopies()
        {
            int n;
            lock (Lock) { if (!Load()) return; n = _items.RemoveAll(x => x.OnServer); }
            if (n > 0) Save();
        }

        /// <summary>voci solo di questo PC (non ancora sul server): si caricano sul server quando il PC puo' scrivere</summary>
        public static List<DeviceEntry> NotOnServer() => All().Where(x => !x.OnServer).ToList();

        /// <summary>posizione salvata della linguetta (bordo destro, a meta', se non c'e')</summary>
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
            catch { /* valore illeggibile: si riparte dal predefinito */ }
            return new DrawerPlace();
        }

        public static void SetPlace(DrawerPlace p) => AppDbStore.Set(PlaceKey, JsonConvert.SerializeObject(p));

        /// <summary>
        /// Prefisso IP proposto per una voce nuova: i primi tre numeri dell'indirizzo del server a cui si e' collegati (10.0.4.21 -> "10.0.4.").
        /// Per indirizzi che non sono IPv4 (nomi, IPv6) non c'e' prefisso. Le reti diverse da /24 sono da decidere.
        /// </summary>
        public static string IpPrefix(string? serverAddress)
        {
            var parts = (serverAddress ?? "").Trim().Split('.');
            if (parts.Length != 4 || parts.Any(p => !byte.TryParse(p, out _))) return "";
            return string.Join(".", parts.Take(3)) + ".";
        }

        /// <summary>filtro della ricerca: nome, indirizzo o nome utente (senza badare alle maiuscole)</summary>
        public static bool Matches(DeviceEntry e, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return true;
            var cmp = StringComparison.CurrentCultureIgnoreCase;
            return e.Name.Contains(query, cmp) || e.Ip.Contains(query, cmp) || e.Users.Any(u => u.User.Contains(query, cmp));
        }
    }
}
