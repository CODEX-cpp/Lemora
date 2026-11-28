using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Utils.Tracing;
using Lemora.View;
using Lemora.View.Launcher;
using Newtonsoft.Json;
using Shawn.Utils;

namespace Lemora.Service.Locality
{
    public class LocalityConnectRecorderSettings
    {
        /// <summary>
        /// keep the last connect time of each server, in order to sort the server list
        /// </summary>
        public ConcurrentDictionary<string, long> ConnectRecodes = new ConcurrentDictionary<string, long>();
        public List<QuickConnectionItem> QuickConnectionHistory = new List<QuickConnectionItem>();
        public ConcurrentDictionary<string, RdpLocalSetting> RdpCaches = new ConcurrentDictionary<string, RdpLocalSetting>();
    }

    public static class LocalityConnectRecorder
    {
        /// <summary>old local file: only used to import the data into the database the first time</summary>
        public static string JsonPath => Path.Combine(AppPathHelper.Instance.LocalityDirPath, ".connection_records.json");
        private const string DbKey = "connection_records";
        private static LocalityConnectRecorderSettings _settings = new LocalityConnectRecorderSettings();
        private static bool _isLoaded = false;

        /// <summary>
        /// Loads the data from the database (encrypted). If the database is not ready yet it retries on the next call.
        /// On first start it imports the old local file and deletes it after verifying the write to the database.
        /// </summary>
        private static void Load()
        {
            lock (_settings)
            {
                if (_isLoaded) return;
                if (!AppDbStore.TryGet(DbKey, out var json)) return;
                _isLoaded = true;
                try
                {
                    if (!string.IsNullOrEmpty(json))
                    {
                        _settings = JsonConvert.DeserializeObject<LocalityConnectRecorderSettings>(json) ?? new LocalityConnectRecorderSettings();
                    }
                    else if (File.Exists(JsonPath))
                    {
                        var tmp = JsonConvert.DeserializeObject<LocalityConnectRecorderSettings>(File.ReadAllText(JsonPath));
                        if (tmp != null) _settings = tmp;
                        var text = JsonConvert.SerializeObject(_settings);
                        if (AppDbStore.Set(DbKey, text) && AppDbStore.TryGet(DbKey, out var back) && back == text)
                            File.Delete(JsonPath);
                    }
                }
                catch
                {
                    _settings = new LocalityConnectRecorderSettings();
                }
            }
        }

        /// <summary>raised when connection dates or the quick connection history change (the home page refreshes)</summary>
        public static event Action? Changed;
        public static void RaiseChanged() => Changed?.Invoke();

        public static bool CanSave { get; private set; } = true;
        private static void Save()
        {
            if (!CanSave || !_isLoaded) return;   // if the database has not been read, do not overwrite it
            lock (_settings)
            {
                if (!CanSave) return;
                CanSave = false;
                try
                {
                    AppDbStore.Set(DbKey, JsonConvert.SerializeObject(_settings));
                }
                catch (Exception exception)
                {
                    UnifyTracing.Error(exception);
                }
                CanSave = true;
            }
        }


        #region ConnectTime
        /// <summary>
        /// Update the connect time of id
        /// </summary>
        public static void ConnectTimeAddOrUpdate(this ProtocolBaseViewModel vmServer, long time = 0)
        {
            Load();
            //var serverId = $"{server.Id}_From{server.DataSourceName}";
            if (time == 0)
                time = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            _settings.ConnectRecodes.AddOrUpdate(vmServer.Id, time, (s, l) => time);
            Save();
            vmServer.LastConnectTime = ConnectTimeGet(vmServer.Server);
            RaiseChanged();
        }

        public static DateTime ConnectTimeGet(ProtocolBase server)
        {
            Load();
            if (_settings.ConnectRecodes.TryGetValue(server.Id, out var ut))
            {
                DateTimeOffset dto = DateTimeOffset.FromUnixTimeMilliseconds(ut);
                return dto.LocalDateTime;
            }
            return DateTime.MinValue;
        }
        public static void ConnectTimeCleanup(IEnumerable<string>? existedIds = null)
        {
            Load();
            if (existedIds != null)
            {
                var junks = _settings.ConnectRecodes.Where(x => existedIds.All(y => y != x.Key));
                foreach (var junk in junks)
                {
                    _settings.ConnectRecodes.TryRemove(junk.Key, out _);
                }
            }
            if (_settings.ConnectRecodes.Count > 100)
            {
                var ordered = _settings.ConnectRecodes.OrderByDescending(x => x.Value).ToList().Skip(100);
                foreach (var junk in ordered)
                {
                    _settings.ConnectRecodes.TryRemove(junk.Key, out _);
                }
            }
        }
        #endregion


        #region QuickConnectionHistory

        public static List<QuickConnectionItem> QuickConnectionHistoryGetAll() { Load(); return new List<QuickConnectionItem>(_settings.QuickConnectionHistory); }
        public static void QuickConnectionHistoryAddOrUpdate(string host, string protocol, string username, string password, string privateKeyPath)
        {
            var item = new QuickConnectionItem()
            {
                Host = host,
                Protocol = protocol,
                LastTime = DateTime.Now,
            };
            item.SetUserPassword(username, password, privateKeyPath);
            Load();
            var old = _settings.QuickConnectionHistory.FirstOrDefault(x => x.Host == item.Host && x.Protocol == item.Protocol);
            if (old != null)
            {
                item.IsFavorite = old.IsFavorite;   // a favorite stays a favorite even when reconnecting
                _settings.QuickConnectionHistory.Remove(old);
            }
            _settings.QuickConnectionHistory.Insert(0, item);
            TrimQuickHistory();

            Save();
            RaiseChanged();
        }
        /// <summary>
        /// Removes the saved user and password from quick connections whose address starts with the prefix (e.g. "192.0.2."): the entries stay
        /// in history (and favorites), only the credentials change. Returns how many entries had credentials to remove.
        /// </summary>
        public static int QuickConnectionHistoryClearCredentials(string hostPrefix)
        {
            Load();
            var n = 0;
            foreach (var x in _settings.QuickConnectionHistory.Where(x => x.Host.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var (u, p, k) = x.GetUserPassword();
                if (string.IsNullOrEmpty(u) && string.IsNullOrEmpty(p) && string.IsNullOrEmpty(k)) continue;
                x.SetUserPassword("", "", "");
                n++;
            }
            if (n > 0) { Save(); RaiseChanged(); }
            return n;
        }

        public static void QuickConnectionHistoryRemove(QuickConnectionItem item)
        {
            Load();
            var old = _settings.QuickConnectionHistory.FirstOrDefault(x => x.Host == item.Host && x.Protocol == item.Protocol);
            if (old != null)
                _settings.QuickConnectionHistory.Remove(old);
            TrimQuickHistory();
            Save();
        }
        public static void QuickConnectionHistoryRemoveAll()
        {
            Load();
            _settings.QuickConnectionHistory.RemoveAll(x => !x.IsFavorite);   // favorites stay
            Save();
        }

        /// <summary>adds a quick connection to favorites or removes it</summary>
        public static void QuickConnectionSetFavorite(QuickConnectionItem item, bool value)
        {
            Load();
            var stored = _settings.QuickConnectionHistory.FirstOrDefault(x => x.Host == item.Host && x.Protocol == item.Protocol);
            if (stored == null || stored.IsFavorite == value) return;
            stored.IsFavorite = value;
            item.IsFavorite = value;
            Save();
            RaiseChanged();
        }

        // at most 500 entries: the oldest non-favorite ones are dropped
        private static void TrimQuickHistory()
        {
            while (_settings.QuickConnectionHistory.Count > 500)
            {
                var last = _settings.QuickConnectionHistory.LastOrDefault(x => !x.IsFavorite);
                if (last == null) break;
                _settings.QuickConnectionHistory.Remove(last);
            }
        }
        #endregion


        #region RDP caches for last session is full screen

        public static RdpLocalSetting? RdpCacheGet(string key)
        {
            Load();
            return _settings.RdpCaches.TryGetValue(key, out var v) ? v : null;
        }


        public static void RdpCacheUpdate(string id, bool isFullScreen, int fullScreenIndex = -1)
        {
            if(ProtocolBase.IsTmpSession(id)) return;
            Load();
            var value = new RdpLocalSetting()
            {
                LastUpdateTime = DateTime.Now,
                FullScreenLastSessionIsFullScreen = isFullScreen,
                FullScreenLastSessionScreenIndex = isFullScreen ? fullScreenIndex : -1,
            };
            _settings.RdpCaches.AddOrUpdate(id, value, (s, setting) => value);
            var obsoletes = _settings.RdpCaches.Where(x => x.Value.LastUpdateTime < DateTime.Now.AddDays(-30)).Select(x => x.Key).ToArray();
            foreach (var obsolete in obsoletes)
            {
                _settings.RdpCaches.TryRemove(obsolete, out _);
            }

            Save();
        }

        #endregion
    }
}
