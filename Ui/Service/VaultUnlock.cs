using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Lemora.Service.DataSource.DAO;
using Lemora.Utils;
using Lemora.View.Utils;
using Shawn.Utils;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Lemora.Service
{
    /// <summary>
    /// Sblocco della cassaforte all'avvio, prima che il database venga letto: Windows Hello se questo PC/utente ha il suo lucchetto,
    /// altrimenti la password principale. Database nuovo: si sceglie la password (e si aggiunge Windows Hello).
    /// L'intestazione (sale e chiave dei dati chiusa) sta nella tabella Configs del database stesso: segue il database ovunque
    /// (modo portatile, reinstallazione, altro PC).
    /// </summary>
    public static class VaultUnlock
    {
        private const string HelloName = "Lemora.DataVault";
        // firma fissa con la chiave Windows Hello (RSA, firma deterministica): il suo hash e' la chiave del lucchetto di questo PC
        private static readonly byte[] HelloChallenge = Encoding.UTF8.GetBytes("Lemora data vault v1");

        /// <summary>PC e utente Windows: un lucchetto Windows Hello vale solo li'</summary>
        private static string Who => Environment.MachineName + "\\" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

        /// <summary>password fissa per le istanze di prova nascoste: mai finestre di sblocco, mai Windows Hello</summary>
        private static string? TestPassword =>
            Environment.GetEnvironmentVariable("LEMORA_VAULT_PASSWORD")
            ?? (Environment.GetEnvironmentVariable("LEMORA_HEADLESS") == "1" || Environment.GetEnvironmentVariable("LEMORA_DEMO_VISIBLE") == "1" ? "lemora-solo-prove" : null);

        /// <summary>false: l'utente ha rinunciato (o il database e' nel formato vecchio) e l'app deve chiudersi</summary>
        /// <param name="keepMinutes">"Mantieni sbloccata": dopo uno sblocco la chiave resta ricordata su questo PC per tanti minuti (0 = mai)</param>
        public static bool Run(string dbPath, int keepMinutes)
        {
            KeepMinutes = keepMinutes;
            // uscita da Windows (disconnessione o spegnimento): lo sblocco ricordato non sopravvive
            Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Forget();
            using var cn = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(dbPath));
            cn.Open();
            cn.Execute("CREATE TABLE IF NOT EXISTS `Configs` (`Key` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Value` TEXT NOT NULL);");
            string? Get(string k) => cn.QueryFirstOrDefault<string>("SELECT `Value` FROM `Configs` WHERE `Key` = @k", new { k });
            void Put(string k, string v) => cn.Execute("INSERT OR REPLACE INTO `Configs` (`Key`, `Value`) VALUES (@k, @v)", new { k, v });

            var header = DataVault.ParseHeader(Get(DataVault.HeaderConfigKey));
            var test = TestPassword;

            if (header == null)
            {
                var et = Get("EncryptionTest");
                if (!string.IsNullOrEmpty(et) && !DataVault.IsVaultText(et))
                {
                    // dati cifrati col vecchio sistema: si convertono una volta con ui-map\tools\migra-cifratura
                    System.Windows.MessageBox.Show(IoC.Translate("vault_old_format"), Assert.APP_DISPLAY_NAME, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return false;
                }
                string password; bool hello;
                if (test != null) { password = test; hello = false; }
                else
                {
                    var w = new VaultWindow(VaultWindow.Mode.Create, HelloAvailable());
                    if (w.ShowDialog() != true) return false;
                    password = w.Password; hello = w.RememberWithHello;
                }
                var key = DataVault.NewKey();
                header = DataVault.NewHeader(key, password);
                if (hello) AddHello(header, key);
                Put(DataVault.HeaderConfigKey, DataVault.HeaderToJson(header));
                DataVault.SetKey(key);
                CurrentHeader = header;
                SimpleLogHelper.Warning($"Cassaforte creata (Windows Hello su questo PC: {header.Hello.Any(x => x.Who == Who)})");
                RememberUnlock();
                return true;
            }

            CurrentHeader = header;

            // sbloccata da poco e "Mantieni sbloccata" attivo: niente impronta ne' password
            var remembered = ReadRememberedKey(header);
            if (remembered != null) { DataVault.SetKey(remembered); SimpleLogHelper.Warning("Cassaforte aperta: sblocco ricordato ancora valido"); return true; }

            if (test != null)
            {
                var k = DataVault.UnlockWithPassword(header, test);
                if (k == null) { SimpleLogHelper.Warning("Cassaforte: password di prova sbagliata"); return false; }
                DataVault.SetKey(k);
                RememberUnlock();
                return true;
            }

            var mine = header.Hello.FirstOrDefault(x => x.Who == Who);
            if (mine != null)
            {
                var lockKey = HelloKey(create: false);
                var k = lockKey == null ? null : DataVault.Unlock(mine.Locked, lockKey);
                if (k != null) { DataVault.SetKey(k); SimpleLogHelper.Info("Cassaforte aperta con Windows Hello"); RememberUnlock(); return true; }
                SimpleLogHelper.Warning("Cassaforte: Windows Hello non riuscito, si chiede la password principale");
            }

            byte[]? found = null;
            var win = new VaultWindow(VaultWindow.Mode.Unlock, mine == null && HelloAvailable(), p => (found = DataVault.UnlockWithPassword(header, p)) != null);
            if (win.ShowDialog() != true || found == null) return false;
            DataVault.SetKey(found);
            SimpleLogHelper.Info("Cassaforte aperta con la password principale");
            if (win.RememberWithHello && AddHello(header, found))
                Put(DataVault.HeaderConfigKey, DataVault.HeaderToJson(header));
            RememberUnlock();
            return true;
        }

        // ---------------------------------------------------------------- "Mantieni sbloccata"
        // La chiave dei dati resta in un file di QUESTO PC (mai nel database, che viaggia), protetto da Windows con l'utente
        // (DataProtectionProvider "LOCAL=user"), con la scadenza dentro e legato a questo database (impronta del lucchetto
        // della password: cambia se cambia la password o il database). Scaduto, si cancella.

        private static int KeepMinutes;
        private static DataVault.Header? CurrentHeader;
        private static string SessionFile => Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".vault-session");

        private sealed class Remembered
        {
            public DateTime ExpiresUtc { get; set; }
            public string Db { get; set; } = "";
            public string Key { get; set; } = "";
        }

        private static string DbMark(DataVault.Header h) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h.ByPassword)));

        /// <summary>da chiamare quando cambia l'impostazione: 0 cancella subito, altrimenti ricorda da adesso</summary>
        public static void SetKeepMinutes(int minutes)
        {
            KeepMinutes = minutes;
            if (minutes <= 0) Forget(); else RememberUnlock();
        }

        private static void RememberUnlock()
        {
            try
            {
                var key = DataVault.CurrentKey();
                if (KeepMinutes <= 0 || key == null || CurrentHeader == null) { Forget(); return; }
                var locked = Task.Run(() => Lemora.Utils.WindowsSdk.DataProtectionForLocal.Protect(Convert.ToBase64String(key))).GetAwaiter().GetResult();
                if (locked == null) { SimpleLogHelper.Warning("Mantieni sbloccata: protezione di Windows non disponibile"); return; }
                var r = new Remembered { ExpiresUtc = DateTime.UtcNow.AddMinutes(KeepMinutes), Db = DbMark(CurrentHeader), Key = locked };
                Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
                File.WriteAllText(SessionFile, Newtonsoft.Json.JsonConvert.SerializeObject(r));
                SimpleLogHelper.Info($"Mantieni sbloccata: fino alle {r.ExpiresUtc.ToLocalTime():HH:mm}");
            }
            catch (Exception e) { SimpleLogHelper.Warning("Mantieni sbloccata non salvato: " + e.Message); }
        }

        private static byte[]? ReadRememberedKey(DataVault.Header header)
        {
            try
            {
                if (KeepMinutes <= 0 || !File.Exists(SessionFile)) { Forget(); return null; }
                var r = Newtonsoft.Json.JsonConvert.DeserializeObject<Remembered>(File.ReadAllText(SessionFile));
                if (r == null || r.ExpiresUtc <= DateTime.UtcNow || r.Db != DbMark(header)) { Forget(); return null; }
                var b64 = Task.Run(() => Lemora.Utils.WindowsSdk.DataProtectionForLocal.Unprotect(r.Key)).GetAwaiter().GetResult();
                var key = b64 == null ? null : Convert.FromBase64String(b64);
                if (key?.Length != 32) { Forget(); return null; }
                return key;
            }
            catch { Forget(); return null; }
        }

        /// <summary>"Blocca": il prossimo avvio chiede di nuovo impronta o password</summary>
        public static void Forget()
        {
            try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { /* al prossimo avvio si riprova */ }
        }

        /// <summary>nuova password principale (dalle Impostazioni, a cassaforte aperta). I lucchetti Windows Hello restano validi.</summary>
        public static bool ChangePassword(IDatabase db, string newPassword)
        {
            var header = DataVault.ParseHeader(db.GetConfig(DataVault.HeaderConfigKey).Result);
            var key = DataVault.CurrentKey();
            if (header == null || key == null) return false;
            DataVault.SetPassword(header, key, newPassword);
            if (!db.SetConfig(DataVault.HeaderConfigKey, DataVault.HeaderToJson(header)).IsSuccess) return false;
            CurrentHeader = header;
            RememberUnlock();   // lo sblocco ricordato era legato al lucchetto vecchio
            return true;
        }

        public static bool HelloAvailable()
        {
            try { return Task.Run(async () => await KeyCredentialManager.IsSupportedAsync()).GetAwaiter().GetResult(); }
            catch { return false; }
        }

        private static bool AddHello(DataVault.Header header, byte[] dataKey)
        {
            var lockKey = HelloKey(create: true);
            if (lockKey == null) return false;
            header.Hello.RemoveAll(x => x.Who == Who);
            header.Hello.Add(new DataVault.HelloLock { Who = Who, Locked = DataVault.Lock(dataKey, lockKey) });
            return true;
        }

        /// <summary>chiave del lucchetto Windows Hello di questo PC/utente (Windows chiede impronta, volto o PIN); null se non disponibile o annullato</summary>
        private static byte[]? HelloKey(bool create)
        {
            try
            {
                return Task.Run(async () =>
                {
                    var r = create
                        ? await KeyCredentialManager.RequestCreateAsync(HelloName, KeyCredentialCreationOption.FailIfExists)
                        : await KeyCredentialManager.OpenAsync(HelloName);
                    if (r.Status == KeyCredentialStatus.CredentialAlreadyExists) r = await KeyCredentialManager.OpenAsync(HelloName);
                    if (r.Status != KeyCredentialStatus.Success) { SimpleLogHelper.Warning($"Windows Hello: {r.Status}"); return null; }
                    var sig = await r.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(HelloChallenge));
                    if (sig.Status != KeyCredentialStatus.Success) { SimpleLogHelper.Warning($"Windows Hello firma: {sig.Status}"); return null; }
                    CryptographicBuffer.CopyToByteArray(sig.Result, out byte[] bytes);
                    return SHA256.HashData(bytes);
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("Windows Hello non disponibile: " + e.Message);
                return null;
            }
        }
    }
}
