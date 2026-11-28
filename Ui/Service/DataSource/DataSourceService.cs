using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.DataSource.DAO.Dapper;
using Lemora.Service.DataSource.Model;
using Lemora.Utils.Tracing;
using Lemora.View;
using Newtonsoft.Json;
using Shawn.Utils;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.Service.DataSource
{
    public class DataSourceService : NotifyPropertyChangedBase
    {
        public const int CHECK_UPDATE_PERIOD = 5;
        public const string LOCAL_DATA_SOURCE_NAME = "Local";

        public DataSourceService()
        {
        }

        public SqliteSource? LocalDataSource { get; private set; } = null;

        public readonly ConcurrentDictionary<string, DataSourceBase> AdditionalSources = new ConcurrentDictionary<string, DataSourceBase>();
        public List<DataSourceBase> AllSources
        {
            get
            {
                var ret = new List<DataSourceBase>(AdditionalSources.Count + 1);
                if (LocalDataSource != null)
                    ret.Add(LocalDataSource);
                ret.AddRange(AdditionalSources.Values);
                return ret;
            }
        }

        public List<ProtocolBaseViewModel> GetServers(bool force)
        {
            lock (this)
            {
                var ret = new List<ProtocolBaseViewModel>(100);
                if (LocalDataSource != null)
                    ret.AddRange(LocalDataSource.GetServers(force).Where(x => !Lemora.Setup.TeamsSync.IsReplaced(x.Id))); // already published as a Teams entry: show that one (no duplicates)
                foreach (var dataSource in AdditionalSources)
                {
                    try
                    {
                        var pbs = dataSource.Value.GetServers(force);
                        foreach (var pb in pbs)
                        {
                            pb.DataSourceNameForLauncher = AdditionalSources.Any() ? pb.DataSourceName : "";
                        }
                        ret.AddRange(pbs);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.DebugWarning(e);
                    }
                }
                return ret;
            }
        }

        public List<Credential> GetCredentials(bool force)
        {
            lock (this)
            {
                var ret = new List<Credential>(100);
                if (LocalDataSource != null)
                    ret.AddRange(LocalDataSource.GetCredentials(force));
                foreach (var dataSource in AdditionalSources)
                {
                    try
                    {
                        var cs = dataSource.Value.GetCredentials(force);
                        ret.AddRange(cs);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.DebugWarning(e);
                    }
                }
                return ret;
            }
        }


        public List<(DataSourceBase, Credential)> GetSourceCredentials(bool force)
        {
            lock (this)
            {
                var ret = new List<(DataSourceBase, Credential)>(100);
                if (LocalDataSource != null)
                {
                    ret.AddRange(LocalDataSource.GetCredentials().Select(credential => (LocalDataSource, credential)).Select(dummy => ((DataSourceBase, Credential)) dummy));
                }
                foreach (var dataSource in AdditionalSources)
                {
                    try
                    {
                        var cs = dataSource.Value.GetCredentials(force);
                        ret.AddRange(cs.Select(credential => (dataSource.Value, credential)).Select(dummy => ((DataSourceBase, Credential))dummy));
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.DebugWarning(e);
                    }
                }
                return ret;
            }
        }


        public DataSourceBase? GetDataSource(string sourceId = LOCAL_DATA_SOURCE_NAME)
        {
            if (string.IsNullOrEmpty(sourceId) || sourceId == LOCAL_DATA_SOURCE_NAME)
                return LocalDataSource;
            if (AdditionalSources.ContainsKey(sourceId))
                return AdditionalSources[sourceId];
            return null;
        }


        /// <summary>
        /// init db connection to a sqlite db. Do make sure sqlitePath is writable!.
        /// </summary>
        public DatabaseStatus InitLocalDataSource(SqliteSource sqliteConfig)
        {
            // database chosen while the program is running: it must not already be open in another copy of Lemora (otherwise stay on the previous one)
            if (!DbInstanceLock.TryAcquire(sqliteConfig.Path, out var owner))
                return DatabaseStatus.New(EnumDatabaseStatus.AccessDenied, string.Format(IoC.Translate("db_already_open_other_copy"), sqliteConfig.Path, owner));
            LocalDataSource?.Database_CloseConnection();
            sqliteConfig.DataSourceName = LOCAL_DATA_SOURCE_NAME;
            sqliteConfig.ClearReadTimestamp();
            if (!Lemora.Utils.DbFileAccess.HasWritePermissionShared(sqliteConfig.Path))
            {
                LocalDataSource = null;
                return DatabaseStatus.New(EnumDatabaseStatus.AccessDenied, $"write permission denied for `{sqliteConfig.Path}`");
            }
            var ret = sqliteConfig.Database_SelfCheck();
            LocalDataSource = sqliteConfig;
            RaisePropertyChanged(nameof(LocalDataSource));
            return ret;
        }

        // ---- MySQL/PostgreSQL servers confirmed by the user (security hardening)
        // Lemora.dataSources.json lives outside the encrypted database: whoever writes it could change host and port (or put a ciphertext taken from the DB as the
        // password) and Lemora would send the decrypted password to the server of their choice. The encrypted DB remembers where the user decided to connect:
        // a different destination read from the file must be confirmed before connecting.
        private const string TrustedKey = "datasources.trusted";

        private static string? Target(DataSourceBase ds) => ds switch
        {
            MysqlSource m => $"mysql|{m.Host.Trim().ToLowerInvariant()}|{m.Port}|{m.DatabaseName.Trim().ToLowerInvariant()}|{m.UserName.Trim().ToLowerInvariant()}|{Hash(m.Password)}",
            PgsqlSource p => $"pgsql|{p.Host.Trim().ToLowerInvariant()}|{p.Port}|{p.DatabaseName.Trim().ToLowerInvariant()}|{p.UserName.Trim().ToLowerInvariant()}|{Hash(p.Password)}",
            _ => null,   // SQLite: no password to send
        };

        private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s ?? "")))[..16];

        private static HashSet<string> LoadTrusted()
        {
            try { if (Lemora.Service.Locality.AppDbStore.TryGet(TrustedKey, out var j) && !string.IsNullOrEmpty(j)) return JsonConvert.DeserializeObject<HashSet<string>>(j!) ?? new(); }
            catch { /* unreadable: ask for confirmation again */ }
            return new();
        }

        private static bool TargetConfirmed(DataSourceBase ds, bool fromProfile)
        {
            if (Target(ds) is not { } t) return true;
            var trusted = LoadTrusted();
            if (trusted.Contains(t)) return true;
            if (fromProfile)
            {
                var (kind, host, port, db, user) = ds is MysqlSource m ? ("MySQL", m.Host, m.Port, m.DatabaseName, m.UserName) : ds is PgsqlSource p ? ("PostgreSQL", p.Host, p.Port, p.DatabaseName, p.UserName) : ("", "", 0, "", "");
                bool ok = false;
                Stylet.Execute.OnUIThreadSync(() => ok = Lemora.Utils.MessageBoxHelper.Confirm(
                    string.Format(IoC.Translate("db_target_confirm_text"), ds.DataSourceName, kind, host, port, db, user),
                    IoC.Translate("db_target_connect"), IoC.Translate("db_target_dont_connect"), IoC.Translate("db_target_confirm_title")));
                if (!ok) { SimpleLogHelper.Warning($"Data source '{ds.DataSourceName}': destination not confirmed, not connected"); return false; }
            }
            trusted.Add(t);
            Lemora.Service.Locality.AppDbStore.Set(TrustedKey, JsonConvert.SerializeObject(trusted));
            return true;
        }

        /// <summary>
        /// Adds and enables a new data source (updates it if it already exists), returns the data source connection status
        /// </summary>
        /// <param name="fromProfile">
        /// true (default) = destination not just entered by the user: if it is not already confirmed, ask. Only Save in the
        /// Add/Edit dialogs passes false. Previously the "reconnect" dot in Settings made it trusted without asking (security hardening)
        /// </param>
        public DatabaseStatus AddOrUpdateDataSource(DataSourceBase newDataSource, int connectTimeOutSeconds = 2, bool doReload = true, bool fromProfile = true)
        {
            if (!TargetConfirmed(newDataSource, fromProfile))
                return DatabaseStatus.New(EnumDatabaseStatus.OtherError, IoC.Translate("db_connection_not_confirmed"));
            try
            {
                newDataSource.ClearReadTimestamp(); // reload database
                if (newDataSource is SqliteSource { DataSourceName: LOCAL_DATA_SOURCE_NAME } localConfig)
                {
                    return InitLocalDataSource(localConfig);
                }

                // remove the old one
                {
                    var olds = AdditionalSources.Where(x => x.Value == newDataSource);
                    foreach (var pair in olds)
                    {
                        AdditionalSources.TryRemove(pair.Key, out var tmp);
                        tmp?.Database_CloseConnection();
                    }

                    {
                        AdditionalSources.TryRemove(newDataSource.DataSourceName, out var tmp);
                        tmp?.Database_CloseConnection();
                    }
                }


                newDataSource.Database_CloseConnection();
                var ret = DatabaseStatus.New(EnumDatabaseStatus.NotConnectedYet);
                if (connectTimeOutSeconds > 0)
                    ret = newDataSource.Database_SelfCheck(connectTimeOutSeconds);
                AdditionalSources.AddOrUpdate(newDataSource.DataSourceName, newDataSource, (name, source) => newDataSource);
                return ret;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning(e);
                UnifyTracing.Error(e);
                var ret = DatabaseStatus.New(EnumDatabaseStatus.OtherError, e.Message);
                return ret;
            }
            finally
            {
                if (doReload)
                    IoC.Get<GlobalData>().ReloadAll(); // reload server list after adding a new data source
            }
        }

        public void RemoveDataSource(string name)
        {
            if (name == LOCAL_DATA_SOURCE_NAME)
                return;
            else if (AdditionalSources.ContainsKey(name))
            {
                AdditionalSources[name].Database_CloseConnection();
                if (AdditionalSources.TryRemove(name, out _))
                {
                    IoC.Get<GlobalData>().ReloadAll(true); // reload server list after removing a data source
                }
            }
        }

        public static List<DataSourceBase> AdditionalSourcesLoadFromProfile(string path)
        {
            var ads = new List<DataSourceBase>();
            if (File.Exists(path))
            {
                var tmp = JsonConvert.DeserializeObject<List<DataSourceBase>>(File.ReadAllText(AppPathHelper.Instance.ProfileAdditionalDataSourceJsonPath));
                if (tmp != null)
                    ads = tmp;
            }
            return ads;
        }


        public static void AdditionalSourcesSaveToProfile(string path, List<DataSourceBase> sources)
        {
            try
            {
                if (sources.Count == 0)
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists)
                        fi.Delete();
                }
                else
                {
                    var fi = new FileInfo(path);
                    try
                    {
                        if (fi?.Directory?.Exists == false)
                            fi.Directory.Create();
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Error(e);
                        return;
                    }
                    if (IoPermissionHelper.HasWritePermissionOnFile(path))
                        RetryHelper.Try(() =>
                        {
                            File.WriteAllText(path, JsonConvert.SerializeObject(sources, Formatting.Indented), Encoding.UTF8);
                        }, actionOnError: exception => UnifyTracing.Error(exception));
                }
            }
            finally
            {

            }
        }
    }

    //public static class DataSourceServiceExtend
    //{
    //    public static DataSourceBase? GetDataSource(this ProtocolBase protocol)
    //    {
    //        return protocol.DataSource;
    //        //return IoC.Get<DataSourceService>().GetDataSource(protocol.DataSource);
    //    }
    //    public static DataSourceBase? GetDataSource(this ProtocolBaseViewModel protocol)
    //    {
    //        return protocol.DataSource;
    //        //return IoC.Get<DataSourceService>().GetDataSource(protocol.Server.DataSource);
    //    }
    //}
}