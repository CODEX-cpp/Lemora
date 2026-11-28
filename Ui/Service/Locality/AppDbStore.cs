using System;
using Lemora.Service.DataSource;
using Lemora.Utils;
using Shawn.Utils;

namespace Lemora.Service.Locality
{
    /// <summary>
    /// Key/value store inside the app database (Configs table).
    /// Values are JSON encrypted with the same cipher as the passwords: nothing the app writes stays outside the database.
    /// </summary>
    public static class AppDbStore
    {
        private const string Prefix = "lm.";

        private static DataSource.DAO.IDatabase? Db() => IoC.TryGet<DataSourceService>()?.LocalDataSource?.GetDataBase();

        /// <summary>suffix of the backup copy of a row that cannot be decrypted</summary>
        public const string UnreadableSuffix = ".illeggibile";

        /// <summary>
        /// true if the database answered. json is null when the key does not exist yet.
        /// false if the database is not ready yet, or if the row exists but the vault is locked: the caller must retry later and write nothing.
        /// Row present but unreadable with the vault unlocked (different key, corrupted data): before answering "empty" a backup copy is made
        /// (key + ".illeggibile"), so the first save does not lose it forever (security hardening).
        /// At most ONE copy per key: if one already exists it is kept (it is the oldest, the one with the original data), so the database does not fill up.
        /// </summary>
        public static bool TryGet(string key, out string? json)
        {
            json = null;
            var db = Db();
            if (db == null) return false;
            var r = db.GetConfig(Prefix + key);
            if (!r.IsSuccess) return false;
            if (string.IsNullOrEmpty(r.Result)) return true;
            if (!DataVault.IsUnlocked) return false;   // without the key we do not know what is there: it is not "empty"
            json = DataVault.SimpleDecrypt(r.Result!);
            if (json == null && !key.EndsWith(UnreadableSuffix))
            {
                var backupKey = Prefix + key + UnreadableSuffix;
                var old = db.GetConfig(backupKey);
                if (!old.IsSuccess) return false;   // the row cannot be backed up: better not to let anything be written
                if (string.IsNullOrEmpty(old.Result))
                {
                    if (!db.SetConfig(backupKey, r.Result).IsSuccess) return false;
                    SimpleLogHelper.Warning($"Store: the entry '{key}' cannot be decrypted (different key or damaged data). Backup copy saved in '{key}{UnreadableSuffix}'; starting empty.");
                }
                else
                    SimpleLogHelper.Warning($"Store: the entry '{key}' cannot be decrypted; a backup copy already exists, keeping that one.");
            }
            return true;
        }

        /// <summary>
        /// Reads from the database; if the value is not there yet and the old local file exists, imports it into the database and deletes it
        /// (only after reading the same content back from the database). ready=false: database not ready, the caller must not write.
        /// </summary>
        public static string? ReadOrMigrate(string key, string legacyFile, out bool ready) => ReadOrMigrate(key, legacyFile, out ready, out _);

        /// <param name="migrated">true if the returned text comes from the old file (imported just now)</param>
        public static string? ReadOrMigrate(string key, string legacyFile, out bool ready, out bool migrated)
        {
            migrated = false;
            ready = TryGet(key, out var json);
            if (!ready) return null;
            if (!string.IsNullOrEmpty(json)) return json;
            // row present but unreadable (or emptied): the data was there, so the migration already happened. A file placed there by someone
            // without the key must not replace the encrypted value (security hardening)
            var raw = Db()?.GetConfig(Prefix + key);
            if (raw == null || !raw.IsSuccess || raw.Result != null) return null;
            try
            {
                if (System.IO.File.Exists(legacyFile))
                {
                    migrated = true;
                    var text = System.IO.File.ReadAllText(legacyFile);
                    if (Set(key, text) && TryGet(key, out var back) && back == text)
                        System.IO.File.Delete(legacyFile);
                    return text;
                }
            }
            catch
            {
                // unreadable file: start again from the default values
            }
            return null;
        }

        /// <summary>writes the value (encrypted); json null = deletes the key</summary>
        public static bool Set(string key, string? json)
        {
            var db = Db();
            if (db == null) return false;
            return db.SetConfig(Prefix + key, json == null ? null : DataVault.SimpleEncrypt(json)).IsSuccess;
        }
    }
}
