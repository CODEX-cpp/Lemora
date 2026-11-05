using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource.DAO;
using Lemora.Setup;
using Newtonsoft.Json;

namespace Lemora.Service.DataSource.Model
{
    /// <summary>una voce condivisa dal server Teams, cosi' come la manda /api/client/sync</summary>
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
        /// <summary>voce nata da una connessione personale di un PC (id del PC sul server e id locale della voce su quel PC); vuoti per le voci dell'amministratore</summary>
        public string OriginPc { get; set; } = "";
        public string OriginLocalId { get; set; } = "";
    }

    /// <summary>
    /// Voci condivise dal server Teams: origine dati in SOLA LETTURA, tenuta in memoria.
    /// Non c'e' un file: la copia sta nel database cifrato dell'app (AppDbStore "teams.items"), la riempie TeamsSync.
    /// </summary>
    public sealed class TeamsSource : DataSourceBase
    {
        public const string SourceName = "Teams";
        public const int MaxOfflineDays = 7; // dopo tanti giorni senza contatto col server le voci non si aprono piu'

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

        // niente file da aprire: la sorgente e' sempre pronta
        public override DatabaseStatus Database_SelfCheck(int connectTimeOutSeconds = 5)
        {
            Status = EnumDatabaseStatus.OK;
            return DatabaseStatus.New(Status);
        }

        /// <summary>scrivibile solo con il permesso "scrittura" dato dal server (cambia quando l'amministratore lo cambia)</summary>
        internal void SetWritable(bool writable)
        {
            if (IsWritable == writable) return;
            IsWritable = writable;
            // le voci gia' mostrate (riga selezionata, pannello dettagli, barra di selezione, barra cartelle) si aggiornano subito
            Stylet.Execute.OnUIThread(() =>
            {
                var data = IoC.TryGet<GlobalData>();
                if (data == null) return;
                foreach (var vm in data.VmItemList.Where(x => ReferenceEquals(x.Server.DataSource, this))) vm.RefreshWritable();
                data.ReloadTagsFromServers();   // solleva ServersReloaded: la barra delle cartelle ricalcola "sola lettura"
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();   // pulsanti Modifica/Elimina (CanExecute)
            });
        }

        internal void SetItems(List<TeamsItem> items, DateTime lastContact)
        {
            _db.Set(items, lastContact);
            ClearReadTimestamp(); // si rilegge al prossimo giro
            Status = EnumDatabaseStatus.OK;
        }
    }

    /// <summary>"database" in memoria: sa solo leggere le voci; ogni scrittura fallisce</summary>
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

        public override ResultSelects<ProtocolBase> GetServers()
        {
            List<TeamsItem> items;
            lock (this)
            {
                // senza contatto col server da troppo tempo: nessuna voce (il blocco si toglie da solo al primo giro riuscito)
                items = DateTime.Now - _lastContact > TimeSpan.FromDays(TeamsSource.MaxOfflineDays) ? new List<TeamsItem>() : _items.Where(x => !x.Disabled).ToList();
            }
            var ret = new List<ProtocolBase>(items.Count);
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
                p.EncryptToDatabaseLevel(); // come le legge un database vero: la password esce in chiaro solo al momento di collegarsi
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

        // ---- scrittura: ogni modifica va subito al server (che controlla il permesso) e solo se lui accetta cambia la copia locale ----

        private const string IdPrefix = "teams-";
        private static string ServerId(string id) => id.StartsWith(IdPrefix) ? id.Substring(IdPrefix.Length) : "";

        /// <summary>da voce dell'app a voce condivisa; null + errore se il tipo non e' condivisibile</summary>
        private TeamsItem? ToItem(ProtocolBase p, string serverId, out string? error)
        {
            error = null;
            var clone = (ProtocolBase)p.Clone();
            clone.DecryptToConnectLevel(); // l'editor consegna la password cifrata come in un database: al server va in chiaro (il server la cifra a sua volta)
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
            };
        }

        private Result WriteOne(ProtocolBase p, bool add)
        {
            var sid = add ? "" : ServerId(p.Id);
            if (!add && sid.Length == 0) return Result.Fail("id");
            var item = ToItem(p, sid, out var err);
            if (item == null) return Result.Fail(err!);
            err = TeamsSync.Write(sid, item, out var newId);
            if (err != null) return Result.Fail(err);
            if (add) p.Id = IdPrefix + newId;
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
            foreach (var p in servers)
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
