using System;
using System.Collections.Generic;
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
    /// Vault unlock at startup, before the database is read: Windows Hello if this PC/user has its own lock,
    /// otherwise the master password. New database: the password is chosen (and Windows Hello is added).
    /// The header (salt and wrapped data key) lives in the Configs table of the database itself: it follows the database everywhere
    /// (portable mode, reinstall, another PC).
    /// </summary>
    public static class VaultUnlock
    {
        private const string HelloName = "Lemora.DataVault";
        // fixed signature with the Windows Hello key (RSA, deterministic signature): its hash is the key of this PC's lock
        private static readonly byte[] HelloChallenge = Encoding.UTF8.GetBytes("Lemora data vault v1");

        /// <summary>PC and Windows user: a Windows Hello lock is only valid there</summary>
        private static string Who => Environment.MachineName + "\\" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

        /// <summary>fixed password for hidden test instances: never unlock windows, never Windows Hello. Development build only</summary>
        private static string? TestPassword =>
            DevOnly.Env("LEMORA_VAULT_PASSWORD")
            ?? (DevOnly.Env("LEMORA_HEADLESS") == "1" || DevOnly.Env("LEMORA_DEMO_VISIBLE") == "1" ? "lemora-solo-prove" : null);

        /// <summary>false: the user gave up (or the database is in the old format) and the app must close</summary>
        /// <param name="keepMinutes">"Keep unlocked": after an unlock the key stays remembered on this PC for this many minutes (0 = never)</param>
        public static bool Run(string dbPath, int keepMinutes, string keepMac = "")
        {
            KeepMinutes = AllowedKeep(keepMinutes);
            KeepMacFromProfile = keepMac ?? "";
            _dbPathForKeep = dbPath;
            // backup copy of a key change interrupted abruptly: the database is intact (transaction), the copy has the old key and must be removed
            try { if (File.Exists(BackupPath(dbPath))) File.Delete(BackupPath(dbPath)); } catch { /* in use: retried at the next launch */ }
            // leaving Windows (sign-out or shutdown): the remembered unlock does not survive
            Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Forget();
            using var cn = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(dbPath));
            cn.Open();
            cn.Execute("CREATE TABLE IF NOT EXISTS `Configs` (`Key` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Value` TEXT NOT NULL);");
            string? Get(string k) => cn.QueryFirstOrDefault<string>("SELECT `Value` FROM `Configs` WHERE `Key` = @k", new { k });
            void Put(string k, string v) => cn.Execute("INSERT OR REPLACE INTO `Configs` (`Key`, `Value`) VALUES (@k, @v)", new { k, v });

            var header = DataVault.ParseHeader(Get(DataVault.HeaderConfigKey));
            KeepEpoch = Get(KeepEpochKey) ?? "";
            var test = TestPassword;

            if (header == null)
            {
                var et = Get("EncryptionTest");
                if (!string.IsNullOrEmpty(et) && !DataVault.IsVaultText(et))
                {
                    // data encrypted with the old system: it must be converted once (outside this version) before it can be opened
                    System.Windows.MessageBox.Show(IoC.Translate("vault_old_format_unsupported"), Assert.APP_DISPLAY_NAME, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
                SimpleLogHelper.Warning($"Vault created (Windows Hello on this PC: {header.Hello.Any(x => x.Who == Who)})");
                RememberUnlock();
                return true;
            }

            CurrentHeader = header;

            // recently unlocked and "Keep unlocked" active: no fingerprint or password
            var remembered = ReadRememberedKey(header);
            if (remembered != null) { DataVault.SetKey(remembered); SimpleLogHelper.Warning("Vault opened: remembered unlock still valid"); return true; }

            if (test != null)
            {
                var k = DataVault.UnlockWithPassword(header, test);
                if (k == null) { SimpleLogHelper.Warning("Vault: wrong test password"); return false; }
                DataVault.SetKey(k);
                RememberUnlock();
                return true;
            }

            var mine = header.Hello.FirstOrDefault(x => x.Who == Who);
            if (mine != null)
            {
                var lockKey = HelloKey(create: false, mine.Nonce);
                var k = lockKey == null ? null : DataVault.Unlock(mine.Locked, lockKey);
                if (k != null) { DataVault.SetKey(k); SimpleLogHelper.Info("Vault opened with Windows Hello"); IdentityConfirmedUtc = DateTime.UtcNow; RememberUnlock(); return true; }
                SimpleLogHelper.Warning("Vault: Windows Hello failed, asking for the master password");
            }

            byte[]? found = null;
            var win = new VaultWindow(VaultWindow.Mode.Unlock, mine == null && HelloAvailable(), p => (found = DataVault.UnlockWithPassword(header, p)) != null);
            if (win.ShowDialog() != true || found == null) return false;
            DataVault.SetKey(found);
            SimpleLogHelper.Info("Vault opened with the master password");
            IdentityConfirmedUtc = DateTime.UtcNow;
            if (win.RememberWithHello && AddHello(header, found))
                Put(DataVault.HeaderConfigKey, DataVault.HeaderToJson(header));
            RememberUnlock();
            return true;
        }

        /// <summary>
        /// "is it really you?" with the vault already open (browser extension, every 20 minutes): Windows Hello if this user set it up for the vault,
        /// otherwise the master password. To be called on the UI thread. true = confirmed
        /// </summary>
        /// <summary>last time the person really proved who they are (Windows Hello or master password typed now, not a remembered unlock):
        /// for the browser extension it is worth 20 minutes, so it does not ask again right after Lemora was unlocked (tested in the sandbox)</summary>
        public static DateTime IdentityConfirmedUtc { get; private set; } = DateTime.MinValue;

        /// <summary>this user set up Windows Hello for the vault on this PC</summary>
        public static bool HelloSetUp => CurrentHeader?.Hello.Any(x => x.Who == Who) == true;

        /// <summary>Windows Hello only (the browser extension asks for the password in its own window). true = confirmed</summary>
        public static bool ConfirmWithHello()
        {
            if (CurrentHeader?.Hello.FirstOrDefault(x => x.Who == Who) is not { } mine) return false;
            var lockKey = HelloKey(create: false, mine.Nonce);
            var ok = lockKey != null && DataVault.Unlock(mine.Locked, lockKey) != null;
            if (ok) IdentityConfirmedUtc = DateTime.UtcNow;
            return ok;
        }

        private static int _pwFailures;
        private static DateTime _pwBlockedUntil = DateTime.MinValue;

        /// <summary>
        /// master password typed in the extension window: "ok" | "wrong" | "wait" (after 5 wrong attempts, 30 s pause: the extension window
        /// must not become a way to try passwords quickly) | "locked"
        /// </summary>
        public static string CheckPassword(string password)
        {
            var header = CurrentHeader;
            if (header == null || !DataVault.IsUnlocked) return "locked";
            lock (HelloChallenge)
            {
                if (DateTime.UtcNow < _pwBlockedUntil) return "wait";
                if (password.Length > 0 && DataVault.UnlockWithPassword(header, password) != null) { _pwFailures = 0; IdentityConfirmedUtc = DateTime.UtcNow; return "ok"; }
                if (++_pwFailures >= 5) { _pwFailures = 0; _pwBlockedUntil = DateTime.UtcNow.AddSeconds(30); }
                SimpleLogHelper.Warning("Confirmation for the extension: wrong master password");
                return "wrong";
            }
        }

        // ---------------------------------------------------------------- "Keep unlocked"
        // The data key stays in a file on THIS PC (never in the database, which travels), protected by Windows with the user
        // (DataProtectionProvider "LOCAL=user"), with the expiry inside and tied to this database (hash of the password
        // lock: it changes if the password or the database changes). Once expired, it is deleted.

        private static int KeepMinutes;
        private static DataVault.Header? CurrentHeader;
        private static string SessionFile => Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".vault-session");

        private sealed class Remembered
        {
            public DateTime ExpiresUtc { get; set; }
            public string Db { get; set; } = "";
            public string Key { get; set; } = "";
        }

        /// <summary>
        /// the duration comes from the profile, an unencrypted file: only the menu values, otherwise 0 (always ask).
        /// Whoever writes the file cannot turn on or extend the remembered unlock beyond the possible choices (security hardening)
        /// </summary>
        internal static int AllowedKeep(int minutes) =>
            Array.IndexOf(Lemora.View.Settings.General.GeneralSettingViewModel.KeepUnlockedChoices, minutes) >= 0 ? minutes : 0;

        /// <summary>MAC of "minutes chosen by the user" with the data key: whoever writes the profile without the key cannot recreate it</summary>
        public static string KeepMac(int minutes, byte[]? key = null)
        {
            key ??= DataVault.CurrentKey();
            if (key == null) return "";
            using var h = new HMACSHA256(key);
            // with the value that changes at every user choice: an old profile (480 minutes and its MAC) put back after choosing
            // "always ask" is no longer valid (security hardening). Empty = older MACs
            var epoch = KeepEpoch.Length > 0 ? ":" + KeepEpoch : "";
            return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes("keep-unlocked:" + minutes + epoch)));
        }

        private const string KeepEpochKey = "vault.keep.epoch";
        private static string KeepEpoch = "";
        private static string _dbPathForKeep = "";

        /// <summary>new value for "Keep unlocked", in the database (not in the profile): older MACs stop being valid</summary>
        private static void NewKeepEpoch()
        {
            var e = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            try
            {
                using var cn = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(_dbPathForKeep));
                cn.Open();
                cn.Execute("INSERT OR REPLACE INTO `Configs` (`Key`, `Value`) VALUES (@k, @v)", new { k = KeepEpochKey, v = e });
                KeepEpoch = e;
            }
            catch (Exception ex) { SimpleLogHelper.Warning("Keep unlocked: new value not saved (" + ex.Message + ")"); }
        }

        private static string KeepMacFromProfile = "";
        /// <summary>MAC to save in the profile (after a choice in Settings or a master password change)</summary>
        public static string CurrentKeepMac => KeepMacFromProfile;

        /// <summary>true if the profile's minutes were really chosen by the user (correct MAC with this key)</summary>
        // without the value in the database (deleted row, or profile from before the fix) it is not valid: the password is asked once
        // and the choice must be made again (security hardening).
        // shortcut: a process of the same user that copied profile and row and puts them back together can still restore an old choice; defending against it would need a counter outside its control
        private static bool KeepIsGenuine(byte[] key) =>
            KeepMinutes > 0 && KeepMacFromProfile.Length > 0 && KeepEpoch.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(KeepMacFromProfile), Encoding.ASCII.GetBytes(KeepMac(KeepMinutes, key)));

        private static string DbMark(DataVault.Header h) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h.ByPassword)));

        /// <summary>to be called when the setting changes: 0 deletes right away, otherwise remembers from now</summary>
        public static void SetKeepMinutes(int minutes)
        {
            KeepMinutes = AllowedKeep(minutes);
            if (_dbPathForKeep.Length > 0) NewKeepEpoch();   // every choice invalidates the MACs of previous choices
            KeepMacFromProfile = KeepMac(KeepMinutes);   // choice made now by the user, with the vault open
            if (minutes <= 0) Forget(); else RememberUnlock();
        }

        private static void RememberUnlock()
        {
            try
            {
                var key = DataVault.CurrentKey();
                if (KeepMinutes <= 0 || key == null || CurrentHeader == null) { Forget(); return; }
                if (!KeepIsGenuine(key)) { SimpleLogHelper.Warning("Keep unlocked: profile value not chosen in Settings, ignored"); Forget(); return; }
                var locked = Task.Run(() => Lemora.Utils.WindowsSdk.DataProtectionForLocal.Protect(Convert.ToBase64String(key))).GetAwaiter().GetResult();
                if (locked == null) { SimpleLogHelper.Warning("Keep unlocked: Windows protection not available"); return; }
                var r = new Remembered { ExpiresUtc = DateTime.UtcNow.AddMinutes(KeepMinutes), Db = DbMark(CurrentHeader), Key = locked };
                Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
                File.WriteAllText(SessionFile, Newtonsoft.Json.JsonConvert.SerializeObject(r));
                ForgetAt(r.ExpiresUtc);
                SimpleLogHelper.Info($"Keep unlocked: until {r.ExpiresUtc.ToLocalTime():HH:mm}");
            }
            catch (Exception e) { SimpleLogHelper.Warning("Keep unlocked not saved: " + e.Message); }
        }

        private static byte[]? ReadRememberedKey(DataVault.Header header)
        {
            try
            {
                if (KeepMinutes <= 0 || !File.Exists(SessionFile)) { Forget(); return null; }
                var r = Newtonsoft.Json.JsonConvert.DeserializeObject<Remembered>(File.ReadAllText(SessionFile));
                // expiry beyond the duration chosen now (file written with a longer duration, or tampered with): not valid
                if (r == null || r.ExpiresUtc <= DateTime.UtcNow || r.ExpiresUtc > DateTime.UtcNow.AddMinutes(KeepMinutes) || r.Db != DbMark(header)) { Forget(); return null; }
                var b64 = Task.Run(() => Lemora.Utils.WindowsSdk.DataProtectionForLocal.Unprotect(r.Key)).GetAwaiter().GetResult();
                var key = b64 == null ? null : Convert.FromBase64String(b64);
                if (key?.Length != 32) { Forget(); return null; }
                if (!KeepIsGenuine(key)) { Forget(); return null; }
                ForgetAt(r.ExpiresUtc);
                return key;
            }
            catch { Forget(); return null; }
        }

        private static System.Threading.Timer? _expiryTimer;

        /// <summary>
        /// at expiry (with Lemora open) the file is really deleted, not only at the next launch (security hardening). The file is re-read first:
        /// if in the meantime a new unlock rewrote it with a later expiry, it stays
        /// </summary>
        private static void ForgetAt(DateTime expiresUtc)
        {
            var due = expiresUtc - DateTime.UtcNow;
            if (due < TimeSpan.Zero) due = TimeSpan.Zero;
            _expiryTimer?.Dispose();
            _expiryTimer = new System.Threading.Timer(_ => ForgetIfExpired(), null, due + TimeSpan.FromSeconds(1), System.Threading.Timeout.InfiniteTimeSpan);
        }

        internal static void ForgetIfExpired()
        {
            try
            {
                if (!File.Exists(SessionFile)) return;
                var r = Newtonsoft.Json.JsonConvert.DeserializeObject<Remembered>(File.ReadAllText(SessionFile));
                if (r == null || r.ExpiresUtc <= DateTime.UtcNow) { Forget(); SimpleLogHelper.Info("Keep unlocked: expired, file deleted"); }
                else ForgetAt(r.ExpiresUtc);
            }
            catch { Forget(); }
        }

        /// <summary>"Lock": the next launch asks again for fingerprint or password</summary>
        public static void Forget()
        {
            try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { /* retried at the next launch */ }
        }

        public sealed class ChangeResult
        {
            public bool Ok;
            public string Error = "";
            public DataVault.RekeyStats Stats = new();
            public int Rows;
            public int HelloRemoved;
            public bool HelloKept;
        }

        private static string BackupPath(string dbPath) => dbPath + ".cambio-chiave.bak";

        /// <summary>
        /// New master password with a NEW data key (from Settings, with the vault open). Security hardening:
        /// before, only the password changed and the key stayed the same, so an old copy of the database with the old password also opened the new data.
        /// Now: the whole database is re-encrypted with a new key in a single transaction, checked before saving it (all or nothing);
        /// credential hashes recomputed; Windows Hello locks of other PCs removed (here it is recreated if it existed and the user confirms).
        /// Afterwards, the vault stays "frozen" (DataVault.Frozen): Lemora must be reopened, because memory holds values encrypted with the old key.
        /// </summary>
        public static ChangeResult ChangePassword(string dbPath, string newPassword)
        {
            var res = new ChangeResult();
            try
            {
                if (string.IsNullOrEmpty(newPassword)) { res.Error = IoC.Translate("vault_err_empty_password"); return res; }
                DataVault.Header? header;
                using (var cn0 = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(dbPath)))
                {
                    cn0.Open();
                    header = DataVault.ParseHeader(cn0.QueryFirstOrDefault<string>("SELECT `Value` FROM `Configs` WHERE `Key` = @k", new { k = DataVault.HeaderConfigKey }));
                }
                if (header == null || !DataVault.IsUnlocked) { res.Error = IoC.Translate("vault_err_not_open"); return res; }

                // Windows Hello of this PC: recreated with the new key (fingerprint or PIN asked now, outside the lock); the other PCs are removed
                var hadHello = header.Hello.Any(x => x.Who == Who);
                var helloNonce = NewNonce();   // new lock = new signature: the key of the previous lock does not open this one (security hardening)
                var helloLockKey = hadHello ? HelloKey(create: false, helloNonce) : null;
                res.HelloRemoved = header.Hello.Count(x => x.Who != Who);

                var keepWasGenuine = DataVault.CurrentKey() is { } oldKeyNow && KeepIsGenuine(oldKeyNow);

                // backup copy (deleted as soon as done: it still has the old key)
                File.Copy(dbPath, BackupPath(dbPath), true);

                DataVault.Header? newHeader = null;
                var newKey = DataVault.NewKey();
                var stats = new DataVault.RekeyStats();
                string error = "";
                var ok = DataVault.ChangeKey(newKey, (oldK, newK) =>
                {
                    using var cn = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(dbPath));
                    cn.Open();
                    cn.Execute("PRAGMA busy_timeout = 10000;");
                    cn.Execute("PRAGMA secure_delete = ON;");   // values with the old key do not remain in free space (security hardening)
                    using var tx = cn.BeginTransaction();
                    try
                    {
                        var tables = cn.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", transaction: tx).ToList();
                        // 1) every encrypted value of every table (including those nested inside other data) with the new key
                        foreach (var t in tables)
                        {
                            var cols = cn.Query<string>($"SELECT name FROM pragma_table_info('{t.Replace("'", "''")}')", transaction: tx).ToList();
                            foreach (var row in Rows(cn, tx, t, cols))
                            {
                                var changes = new Dictionary<string, object?>();
                                foreach (var (col, val) in row.values)
                                {
                                    if (val is not string s || s.IndexOf("lv1:", StringComparison.Ordinal) < 0) continue;
                                    if (t == "Configs" && col == "Value" && row.values.Any(v => v.col == "Key" && (v.val as string) == DataVault.HeaderConfigKey)) continue;
                                    var n = DataVault.Reencrypt(s, oldK, newK, stats);
                                    if (n != s) changes[col] = n;
                                }
                                if (changes.Count == 0) continue;
                                var set = string.Join(", ", changes.Keys.Select((c, i) => $"`{c}` = @p{i}"));
                                var p = new DynamicParameters();
                                var idx = 0;
                                foreach (var v in changes.Values) p.Add("p" + idx++, v);
                                p.Add("rid", row.rowid);
                                cn.Execute($"UPDATE `{t}` SET {set} WHERE rowid = @rid", p, tx);
                                res.Rows++;
                            }
                        }

                        // 2) key-bound hashes: Hash column (duplicates) and Name (hidden name) of the credentials, computed with the new key
                        DataVault.UseKeyDuringChange(newK);
                        if (tables.Contains("Credentials"))
                        {
                            foreach (var r in cn.Query("SELECT Id, Name, Json FROM Credentials", transaction: tx).ToList())
                            {
                                string id = r.Id, json = r.Json, oldName = r.Name;
                                var plain = DataVault.SimpleDecrypt(json);
                                if (plain == null) continue;   // already unreadable before: stays as is
                                var c = Newtonsoft.Json.JsonConvert.DeserializeObject<Lemora.Model.Protocol.Base.Credential>(plain);
                                if (c == null) continue;
                                var name = string.IsNullOrEmpty(c.Name) ? oldName : Lemora.Service.DataSource.DAO.Dapper.TableCredential.OpaqueName(c.Name);
                                cn.Execute("UPDATE Credentials SET Name = @n, Hash = @h WHERE Id = @id", new { n = name, h = c.GetHash(), id }, tx);
                            }
                        }

                        // 3) new header: new key wrapped with the new password (+ Windows Hello of this PC)
                        newHeader = DataVault.NewHeader(newK, newPassword);
                        if (helloLockKey != null) newHeader.Hello.Add(new DataVault.HelloLock { Who = Who, Locked = DataVault.Lock(newK, helloLockKey), Nonce = helloNonce });
                        cn.Execute("INSERT OR REPLACE INTO `Configs` (`Key`, `Value`) VALUES (@k, @v)", new { k = DataVault.HeaderConfigKey, v = DataVault.HeaderToJson(newHeader) }, tx);

                        // 4) check before saving: with the new key everything readable before is readable, with the old one nothing
                        int okNew = 0, badNew = 0, okOld = 0;
                        foreach (var t in tables)
                        {
                            var cols = cn.Query<string>($"SELECT name FROM pragma_table_info('{t.Replace("'", "''")}')", transaction: tx).ToList();
                            foreach (var row in Rows(cn, tx, t, cols))
                                foreach (var (_, val) in row.values)
                                {
                                    if (val is not string s || s.IndexOf("lv1:", StringComparison.Ordinal) < 0) continue;
                                    var (o, b) = DataVault.CountReadable(s, newK); okNew += o; badNew += b;
                                    okOld += DataVault.CountReadable(s, oldK).ok;
                                }
                        }
                        var hdr = DataVault.ParseHeader(cn.QueryFirstOrDefault<string>("SELECT `Value` FROM `Configs` WHERE `Key` = @k", new { k = DataVault.HeaderConfigKey }, tx));
                        var back = hdr == null ? null : DataVault.UnlockWithPassword(hdr, newPassword);
                        if (okNew != stats.Whole + stats.Inner || badNew != stats.Unreadable || okOld != 0 || back == null || !back.SequenceEqual(newK))
                        {
                            error = string.Format(IoC.Translate("vault_err_check_failed"), okNew, stats.Whole + stats.Inner, badNew, stats.Unreadable, okOld, back != null);
                            tx.Rollback();
                            return false;
                        }
                        if (Lemora.Utils.DevOnly.Env("LEMORA_TEST_REKEY_FAIL") == "1") { error = "simulated error (test)"; tx.Rollback(); return false; }
                        tx.Commit();
                        // compacts the file: also removes leftovers (old header and values in free pages)
                        try { cn.Execute("VACUUM;"); } catch (Exception ve) { SimpleLogHelper.Warning("Password change: VACUUM failed: " + ve.Message); }
                        return true;
                    }
                    catch (Exception e)
                    {
                        error = e.GetType().Name + ": " + e.Message;
                        try { tx.Rollback(); } catch { /* already closed */ }
                        return false;
                    }
                });
                res.Stats = stats;
                if (!ok) { res.Error = error.Length > 0 ? error : IoC.Translate("vault_err_change_failed"); return res; }

                CurrentHeader = newHeader;
                // "Keep unlocked" chosen by the user: the MAC is recreated with the new key (the caller saves it in the profile)
                if (keepWasGenuine) KeepMacFromProfile = KeepMac(KeepMinutes, newKey);
                RememberUnlock();   // the remembered unlock (if active) with the new key
                res.HelloKept = helloLockKey != null;
                res.Ok = true;
                return res;
            }
            catch (Exception e) { res.Error = e.GetType().Name + ": " + e.Message; return res; }
            finally
            {
                try { if (File.Exists(BackupPath(dbPath))) File.Delete(BackupPath(dbPath)); } catch { /* the next launch removes it */ }
            }
        }

        /// <summary>rows of a table with rowid and column values</summary>
        private static IEnumerable<(long rowid, List<(string col, object? val)> values)> Rows(System.Data.SQLite.SQLiteConnection cn, System.Data.IDbTransaction tx, string table, List<string> cols)
        {
            var list = new List<(long, List<(string, object?)>)>();
            using var cmd = cn.CreateCommand();
            cmd.Transaction = (System.Data.SQLite.SQLiteTransaction)tx;
            cmd.CommandText = $"SELECT rowid, {string.Join(", ", cols.Select(c => $"`{c}`"))} FROM `{table}`";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var vals = new List<(string, object?)>();
                for (var i = 0; i < cols.Count; i++) vals.Add((cols[i], r.IsDBNull(i + 1) ? null : r.GetValue(i + 1)));
                list.Add((r.GetInt64(0), vals));
            }
            return list;
        }

        public static bool HelloAvailable()
        {
            try { return Task.Run(async () => await KeyCredentialManager.IsSupportedAsync()).GetAwaiter().GetResult(); }
            catch { return false; }
        }

        private static bool AddHello(DataVault.Header header, byte[] dataKey)
        {
            var nonce = NewNonce();
            var lockKey = HelloKey(create: true, nonce);
            if (lockKey == null) return false;
            header.Hello.RemoveAll(x => x.Who == Who);
            header.Hello.Add(new DataVault.HelloLock { Who = Who, Locked = DataVault.Lock(dataKey, lockKey), Nonce = nonce });
            return true;
        }

        internal static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        /// <summary>text that Hello signs: fixed phrase + random value of the lock (empty for older locks)</summary>
        internal static byte[] HelloMessage(string? nonce) =>
            string.IsNullOrEmpty(nonce) ? HelloChallenge : HelloChallenge.Concat(Encoding.UTF8.GetBytes("|" + nonce)).ToArray();

        /// <summary>key of the Windows Hello lock of this PC/user (Windows asks for fingerprint, face or PIN); null if unavailable or cancelled</summary>
        private static byte[]? HelloKey(bool create, string? nonce)
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
                    var sig = await r.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(HelloMessage(nonce)));
                    if (sig.Status != KeyCredentialStatus.Success) { SimpleLogHelper.Warning($"Windows Hello signature: {sig.Status}"); return null; }
                    CryptographicBuffer.CopyToByteArray(sig.Result, out byte[] bytes);
                    return SHA256.HashData(bytes);
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("Windows Hello not available: " + e.Message);
                return null;
            }
        }
    }
}
