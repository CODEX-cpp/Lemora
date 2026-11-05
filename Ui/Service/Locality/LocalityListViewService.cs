using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Shawn.Utils;
using Lemora.Service.DataSource;
using Lemora.Utils.Tracing;
using Lemora.View;

namespace Lemora.Service.Locality
{
    public class LocalityListViewSettings
    {
        public EnumServerOrderBy ServerOrderBy = EnumServerOrderBy.IdAsc;
        public Dictionary<string, int> ServerCustomOrder = new Dictionary<string, int>();
        public Dictionary<string, int> GroupedOrder = new Dictionary<string, int>();
        public Dictionary<string, bool> GroupedIsExpanded = new Dictionary<string, bool>();
        public double ServerListNameWidth = 300;
        public double ServerListNoteWidth = 100;
    }

    public static class LocalityListViewService
    {
        public static string JsonPath => Path.Combine(AppPathHelper.Instance.LocalityDirPath, ".list_view.json");
        private static LocalityListViewSettings? _settings;
        public static LocalityListViewSettings Settings
        {
            get
            {
                if (_settings == null || !_dbLoaded)
                {
                    Load();
                }
                return _settings!;
            }
            private set => _settings = value;
        }


        private const string DbKey = "list_view";
        private static bool _dbLoaded = false;

        public static void Load()
        {
            FlushWidths();   // larghezze ancora da salvare: prima si scrivono, poi si rilegge (altrimenti andrebbero perse)
            var json = AppDbStore.ReadOrMigrate(DbKey, JsonPath, out var ready);
            _dbLoaded = ready;   // se il database non e' pronto si lavora in memoria e si riprova alla prossima lettura
            try
            {
                var tmp = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<LocalityListViewSettings>(json);
                tmp ??= new LocalityListViewSettings();
                _settings = tmp;
            }
            catch
            {
                _settings = new LocalityListViewSettings();
            }
        }

        public static void Save()
        {
            if (!_dbLoaded) return;   // se il database non e' stato letto non lo si sovrascrive
            RetryHelper.Try(() => { AppDbStore.Set(DbKey, JsonConvert.SerializeObject(Settings)); }, actionOnError: exception => UnifyTracing.Error(exception));
        }

        public static void ServerOrderBySet(EnumServerOrderBy value)
        {
            if (Settings.ServerOrderBy == value) return;
            Settings.ServerOrderBy = value;
            Save();
        }




        public static void ServerCustomOrderSave(IEnumerable<ProtocolBaseViewModel> servers)
        {
            int i = 0;
            Settings.ServerCustomOrder.Clear();
            foreach (var server in servers)
            {
                Settings.ServerCustomOrder.Add(server.Id, i);
                server.CustomOrder = i;
                ++i;
            }
            Save();
        }




        public static int GroupedOrderGet(string dataSourceName)
        {
            return Settings.GroupedOrder.GetValueOrDefault(dataSourceName, int.MaxValue);
        }

        public static void GroupedOrderSave(IEnumerable<string> dataSourceNames)
        {
            int i = 0;
            Settings.GroupedOrder.Clear();
            foreach (var str in dataSourceNames.Distinct())
            {
                Settings.GroupedOrder.Add(str, i);
                ++i;
            }
            Save();
        }


        public static bool GroupedIsExpandedGet(string dataSourceName)
        {
            return Settings.GroupedIsExpanded.GetValueOrDefault(dataSourceName, true);
        }
        public static void GroupedIsExpandedSet(string dataSourceName, bool isExpanded)
        {
            Load();
            try
            {
                Settings.GroupedIsExpanded[dataSourceName] = isExpanded;
                var ds = IoC.TryGet<DataSourceService>();
                if (ds != null)
                {
                    foreach (var key in Settings.GroupedIsExpanded.Keys.ToArray())
                    {
                        if (ds.LocalDataSource?.Name != key && ds.AdditionalSources.All(x => x.Key != key))
                        {
                            Settings.GroupedIsExpanded.Remove(key);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                UnifyTracing.Error(e);
                Settings.GroupedIsExpanded = new Dictionary<string, bool>();
            }
            Save();
        }

        public static void ServerListNameWidthSet(double value)
        {
            if (Math.Abs(Settings.ServerListNameWidth - value) < 0.1) return;
            Settings.ServerListNameWidth = value;
            SaveWidthsSoon();
        }

        public static void ServerListNoteWidthSet(double value)
        {
            if (Math.Abs(Settings.ServerListNoteWidth - value) < 0.1) return;
            Settings.ServerListNoteWidth = value;
            SaveWidthsSoon();
        }

        // le larghezze delle colonne cambiano a ogni passo quando si ridimensiona la finestra: salvarle ogni volta (cifratura + database)
        // la faceva andare a scatti. Si salvano una volta sola, mezzo secondo dopo l'ultima modifica, e alla chiusura dell'app (FlushWidths).
        private static System.Threading.Timer? _widthTimer;
        private static bool _widthPending;
        private static readonly object WidthLock = new();

        private static void SaveWidthsSoon()
        {
            lock (WidthLock)
            {
                _widthPending = true;
                _widthTimer ??= new System.Threading.Timer(_ => FlushWidths());
                _widthTimer.Change(500, System.Threading.Timeout.Infinite);
            }
        }

        public static void FlushWidths()
        {
            lock (WidthLock)
            {
                if (!_widthPending) return;
                _widthPending = false;
            }
            Save();
        }
    }
}
