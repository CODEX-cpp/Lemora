using System;
using Lemora.Service.DataSource;
using Lemora.Utils;
using Shawn.Utils;

namespace Lemora.Service.Locality
{
    /// <summary>
    /// Archivio chiave/valore dentro il database dell'app (tabella Configs).
    /// I valori sono JSON cifrati con lo stesso cifrario delle password: niente di cio' che scrive l'app resta fuori dal database.
    /// </summary>
    public static class AppDbStore
    {
        private const string Prefix = "lm.";

        private static DataSource.DAO.IDatabase? Db() => IoC.TryGet<DataSourceService>()?.LocalDataSource?.GetDataBase();

        /// <summary>
        /// true se il database ha risposto. json e' null quando la chiave non esiste ancora (o non si decifra).
        /// false se il database non e' ancora pronto: chi chiama deve riprovare piu' tardi e non scrivere nulla.
        /// </summary>
        public static bool TryGet(string key, out string? json)
        {
            json = null;
            var db = Db();
            if (db == null) return false;
            var r = db.GetConfig(Prefix + key);
            if (!r.IsSuccess) return false;
            if (!string.IsNullOrEmpty(r.Result))
                json = DataVault.SimpleDecrypt(r.Result!);
            return true;
        }

        /// <summary>
        /// Legge dal database; se non c'e' ancora e esiste il vecchio file locale, lo importa nel database e lo cancella
        /// (solo dopo aver riletto dal database lo stesso contenuto). ready=false: database non pronto, chi chiama non deve scrivere.
        /// </summary>
        public static string? ReadOrMigrate(string key, string legacyFile, out bool ready)
        {
            ready = TryGet(key, out var json);
            if (!ready) return null;
            if (!string.IsNullOrEmpty(json)) return json;
            try
            {
                if (System.IO.File.Exists(legacyFile))
                {
                    var text = System.IO.File.ReadAllText(legacyFile);
                    if (Set(key, text) && TryGet(key, out var back) && back == text)
                        System.IO.File.Delete(legacyFile);
                    return text;
                }
            }
            catch
            {
                // file illeggibile: si riparte dai valori predefiniti
            }
            return null;
        }

        /// <summary>scrive (cifrato) il valore; json null = cancella la chiave</summary>
        public static bool Set(string key, string? json)
        {
            var db = Db();
            if (db == null) return false;
            return db.SetConfig(Prefix + key, json == null ? null : DataVault.SimpleEncrypt(json)).IsSuccess;
        }
    }
}
