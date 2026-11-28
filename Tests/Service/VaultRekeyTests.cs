using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.DataSource.DAO.Dapper;
using Lemora.Utils;
using Newtonsoft.Json;
using Xunit;

namespace Tests.Service
{
    [CollectionDefinition("vault-key", DisableParallelization = true)]
    public class VaultKeyCollection { }

    /// <summary>
    /// Security review vault/master-change-keeps-data-key-and-hello-locks: changing the master password changes the data key and re-encrypts the whole database.
    /// Tests on a real SQLite file; the key is global, so these tests run on their own.
    /// </summary>
    [Collection("vault-key")]
    public class VaultRekeyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lemora-rekey-" + Guid.NewGuid().ToString("N"));
        private readonly string _db;
        private readonly byte[] _oldKey = DataVault.NewKey();
        private const string OldPw = "old-password-1", NewPw = "new-password-2";

        public VaultRekeyTests()
        {
            Directory.CreateDirectory(_dir);
            _db = Path.Combine(_dir, "Lemora.db");
            DataVault.UnfreezeForTests();
            DataVault.SetKey(_oldKey);
        }

        public void Dispose()
        {
            DataVault.UnfreezeForTests();
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private DapperDatabase NewDb()
        {
            var db = new DapperDatabase("test",DatabaseType.Sqlite);
            Assert.True(db.OpenNewConnection(DbExtensions.GetSqliteConnectionString(_db)).IsSuccess);
            Assert.True(db.InitTables().IsSuccess);
            return db;
        }

        private System.Data.SQLite.SQLiteConnection Raw(string path)
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            var cn = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(path));
            cn.Open();
            return cn;
        }

        /// <summary>database with data of every kind encrypted with the old key + header with the old password (and a Windows Hello from another PC)</summary>
        private void Fill()
        {
            var db = NewDb();
            var rdp = new RDP { DisplayName = "Customer PC1", Address = "10.1.1.1", Port = "3389", UserName = "admin", Password = "pw-rdp", GatewayPassword = "pw-gw" };
            rdp.Users.Add(new UserEntry { UserName = "operator",Password = "pw-user" });
            ProtocolBase pb = rdp; var enc = (ProtocolBase)pb.Clone(); enc.EncryptToDatabaseLevel();
            Assert.True(db.AddServer(ref enc).IsSuccess);
            var ssh = new SSH { DisplayName = "Linux", Address = "10.1.1.2", Port = "22", UserName = "root", Password = "pw-ssh" };
            ProtocolBase ps = ssh; var enc2 = (ProtocolBase)ps.Clone(); enc2.EncryptToDatabaseLevel();
            Assert.True(db.AddServer(ref enc2).IsSuccess);
            var c = new Credential { Name = "Customer / admin", FolderPath = "Customer", UserName = "DOM\\admin", Password = "pw-folder" };
            var cenc = c.CloneMe(); cenc.EncryptToDatabaseLevel();
            Assert.True(db.AddCredential(ref cenc).IsSuccess);
            var v = new Credential { Name = "Vault-Mario", UserName = "mario", Password = "pw-vault" };
            var venc = v.CloneMe(); venc.EncryptToDatabaseLevel();
            Assert.True(db.AddCredential(ref venc).IsSuccess);
            // key/value store: a value containing another encrypted value (like the quick connection history)
            var inner = JsonConvert.SerializeObject(new { host = "10.9.9.9", misc = DataVault.SimpleEncrypt("user|pw-quick") });
            Assert.True(db.SetConfig("lm.test_nested", DataVault.SimpleEncrypt(inner)).IsSuccess);
            Assert.True(db.SetConfig("lm.device_passwords", DataVault.SimpleEncrypt("[{\"Name\":\"Switch\",\"Users\":[{\"User\":\"admin\",\"Password\":\"pw-switch\"}]}]")).IsSuccess);
            // a row already unreadable before (different key): must stay as it is
            var foreign = DataVault.NewKey(); DataVault.SetKey(foreign);
            var bad = DataVault.SimpleEncrypt("data of another key");
            DataVault.SetKey(_oldKey);
            Assert.True(db.SetConfig("lm.folders.unreadable", bad).IsSuccess);
            var header = DataVault.NewHeader(_oldKey, OldPw);
            header.Hello.Add(new DataVault.HelloLock { Who = "OTHERPC\\S-1-5-21-1", Locked = DataVault.Lock(_oldKey, DataVault.NewKey()) });
            Assert.True(db.SetConfig(DataVault.HeaderConfigKey, DataVault.HeaderToJson(header)).IsSuccess);
            db.CloseConnection();
        }

        private static List<string> VaultValues(System.Data.SQLite.SQLiteConnection cn)
        {
            var all = new List<string>();
            foreach (var t in cn.Query<string>("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"))
                foreach (IDictionary<string, object> row in cn.Query($"SELECT * FROM `{t}`"))
                    foreach (var val in row.Values) if (val is string s && s.Contains("lv1:")) all.Add(s);
            return all;
        }

        [Fact]
        public void Reencrypt_NestedValues_AndUnreadableOnesStay()
        {
            var newKey = DataVault.NewKey();
            var inner = DataVault.SimpleEncrypt("secret");
            var whole = DataVault.SimpleEncrypt("{\"p\":\"" + inner + "\"}");
            var st = new DataVault.RekeyStats();
            var r = DataVault.Reencrypt(whole, _oldKey, newKey, st);
            Assert.Equal((1, 1, 0), (st.Whole, st.Inner, st.Unreadable));
            Assert.Equal((2, 0), DataVault.CountReadable(r, newKey));
            Assert.Equal((0, 1), DataVault.CountReadable(r, _oldKey));
            var mixed = "text " + inner + " and more";
            var st2 = new DataVault.RekeyStats();
            var r2 = DataVault.Reencrypt(mixed, _oldKey, newKey, st2);
            Assert.StartsWith("text lv1:", r2); Assert.EndsWith(" and more", r2);
            Assert.Equal((0, 1, 0), (st2.Whole, st2.Inner, st2.Unreadable));
            var st3 = new DataVault.RekeyStats();
            var foreign = "lv1:" + Convert.ToBase64String(new byte[40]);
            Assert.Equal(foreign, DataVault.Reencrypt(foreign, _oldKey, newKey, st3));
            Assert.Equal(1, st3.Unreadable);
        }

        [Fact]
        public void ChangePassword_ReencryptsEverything_OldCopyAndOldPasswordCannotReadNewData()
        {
            Fill();
            var oldCopy = Path.Combine(_dir, "old-copy.db");
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            File.Copy(_db, oldCopy);
            int valuesBefore;
            using (var cn = Raw(_db)) valuesBefore = VaultValues(cn).Count;

            var r = VaultUnlock.ChangePassword(_db, NewPw);
            Assert.True(r.Ok, r.Error);
            Assert.True(DataVault.Frozen);
            Assert.Equal(1, r.HelloRemoved);
            Assert.Equal(1, r.Stats.Unreadable);
            Assert.False(File.Exists(_db + ".cambio-chiave.bak"));
            Assert.Throws<InvalidOperationException>(() => DataVault.SimpleEncrypt("after the change"));   // no saving until restart

            using var cnNew = Raw(_db);
            using var cnOld = Raw(oldCopy);
            var newHeader = DataVault.ParseHeader(cnNew.QueryFirst<string>("SELECT Value FROM Configs WHERE Key=@k", new { k = DataVault.HeaderConfigKey }))!;
            var oldHeader = DataVault.ParseHeader(cnOld.QueryFirst<string>("SELECT Value FROM Configs WHERE Key=@k", new { k = DataVault.HeaderConfigKey }))!;
            Assert.Empty(newHeader.Hello);                                   // Windows Hello of the other PCs removed
            Assert.Null(DataVault.UnlockWithPassword(newHeader, OldPw));      // the old password does not open the new database
            var newKey = DataVault.UnlockWithPassword(newHeader, NewPw);
            Assert.NotNull(newKey);
            Assert.False(newKey!.SequenceEqual(_oldKey));                    // new data key
            // the attacker: old copy + old password -> old key; with it the NEW data cannot be read
            var attackerKey = DataVault.UnlockWithPassword(oldHeader, OldPw)!;
            Assert.True(attackerKey.SequenceEqual(_oldKey));
            var newValues = VaultValues(cnNew);
            Assert.Equal(valuesBefore, newValues.Count);
            Assert.All(newValues, v => Assert.Equal(0, DataVault.CountReadable(v, attackerKey).ok));
            // with the new key everything can be read (except the row that was already unreadable)
            var readable = newValues.Sum(v => DataVault.CountReadable(v, newKey).ok);
            var unreadable = newValues.Sum(v => DataVault.CountReadable(v, newKey).bad);
            Assert.Equal(r.Stats.Whole + r.Stats.Inner, readable);
            Assert.Equal(1, unreadable);

            // the real data, read back as the app does, with the new key
            DataVault.UnfreezeForTests(); DataVault.SetKey(newKey);
            var db = NewDb();
            var servers = db.GetServers().Items;
            var rdp = (RDP)servers.First(s => s.DisplayName == "Customer PC1"); rdp.DecryptToConnectLevel();
            Assert.Equal(("pw-rdp", "pw-gw", "pw-user"), (rdp.Password, rdp.GatewayPassword, rdp.Users.Single().Password));
            var ssh = (SSH)servers.First(s => s.DisplayName == "Linux"); ssh.DecryptToConnectLevel();
            Assert.Equal("pw-ssh", ssh.Password);
            var creds = db.GetCredentials().Items;
            var fc = creds.Single(x => x.Name == "Customer / admin"); fc.DecryptToConnectLevel();
            Assert.Equal(("Customer", "DOM\\admin", "pw-folder"), (fc.FolderPath, fc.UserName, fc.Password));
            var vc = creds.Single(x => x.Name == "Vault-Mario"); vc.DecryptToConnectLevel();
            Assert.Equal("pw-vault", vc.Password);
            Assert.Contains("pw-quick", DataVault.SimpleDecrypt(JsonConvert.DeserializeAnonymousType(DataVault.SimpleDecrypt(db.GetConfig("lm.test_nested").Result)!, new { host = "", misc = "" })!.misc));
            Assert.Contains("pw-switch", DataVault.SimpleDecrypt(db.GetConfig("lm.device_passwords").Result));
            // fingerprints recomputed with the new key: names hidden, duplicates detected, deletion by name
            using (var cn = Raw(_db)) Assert.All(cn.Query<string>("SELECT Name FROM Credentials"), n => Assert.StartsWith("n:", n));
            var dup = new Credential { Name = "Customer / admin", UserName = "x", Password = "y" }; var dupEnc = dup.CloneMe(); dupEnc.EncryptToDatabaseLevel();
            Assert.False(db.AddCredential(ref dupEnc).IsSuccess);
            Assert.All(db.GetCredentials().Items, x => Assert.Equal(x.Hash, x.GetHash()));
            Assert.True(db.DeleteCredential(new[] { "Vault-Mario" }).IsSuccess);
            Assert.DoesNotContain(db.GetCredentials().Items, x => x.Name == "Vault-Mario");
            // and new data can be saved
            ProtocolBase add = new VNC { DisplayName = "New", Address = "10.2.2.2", Port = "5900", Password = "pw-new" }; var addEnc = (ProtocolBase)add.Clone(); addEnc.EncryptToDatabaseLevel();
            Assert.True(db.AddServer(ref addEnc).IsSuccess);
            var added = (VNC)db.GetServers().Items.First(s => s.DisplayName == "New"); added.DecryptToConnectLevel();
            Assert.Equal("pw-new", added.Password);
            db.CloseConnection();
        }

        /// <summary>after the change, neither the old header nor the values encrypted with the old key remain in the file bytes</summary>
        [Fact]
        public void ChangePassword_LeavesNoOldBytesInTheFile()
        {
            Fill();
            List<string> before; string oldHeader;
            using (var cn = Raw(_db))
            {
                before = VaultValues(cn);
                oldHeader = cn.QueryFirst<string>("SELECT Value FROM Configs WHERE Key=@k", new { k = DataVault.HeaderConfigKey });
            }
            Assert.True(VaultUnlock.ChangePassword(_db, NewPw).Ok);
            // the row already unreadable before stays identical by design: values still present in the database are excluded
            string current;
            using (var cn = Raw(_db)) current = string.Join("\n", VaultValues(cn));
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(_db));
            // long chunks (40 characters) of every old encrypted value: none must remain in the file
            var leftovers = before.Concat(new[] { oldHeader })
                .SelectMany(v => System.Text.RegularExpressions.Regex.Matches(v, "[A-Za-z0-9+/]{40}").Select(m => m.Value))
                .Where(s => !current.Contains(s) && text.Contains(s)).ToList();
            Assert.True(before.Count > 5, "setup: encrypted values to check are needed");
            Assert.True(leftovers.Count == 0, $"{leftovers.Count} values with the old key still in the file");
        }

        [Fact]
        public void ChangePassword_TwiceInARow_StillReadsEverything()
        {
            Fill();
            Assert.True(VaultUnlock.ChangePassword(_db, NewPw).Ok);
            DataVault.UnfreezeForTests();
            Assert.True(VaultUnlock.ChangePassword(_db, "third-password-3").Ok);
            DataVault.UnfreezeForTests();
            using var cn = Raw(_db);
            var h = DataVault.ParseHeader(cn.QueryFirst<string>("SELECT Value FROM Configs WHERE Key=@k", new { k = DataVault.HeaderConfigKey }))!;
            Assert.Null(DataVault.UnlockWithPassword(h, NewPw));
            var k = DataVault.UnlockWithPassword(h, "third-password-3")!;
            var values = VaultValues(cn);
            Assert.Equal(1, values.Sum(v => DataVault.CountReadable(v, k).bad));
            Assert.True(values.Sum(v => DataVault.CountReadable(v, k).ok) >= 8);
        }

        [Fact]
        public void ChangePassword_FailureHalfway_LeavesTheDatabaseExactlyAsItWas()
        {
            Fill();
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            byte[] Dump() { using var cn = Raw(_db); return System.Text.Encoding.UTF8.GetBytes(string.Join("\n", VaultValues(cn)) + cn.QueryFirst<string>("SELECT Value FROM Configs WHERE Key=@k", new { k = DataVault.HeaderConfigKey })); }
            var before = Dump();
            Environment.SetEnvironmentVariable("LEMORA_TEST_REKEY_FAIL", "1");
            VaultUnlock.ChangeResult r;
            try { r = VaultUnlock.ChangePassword(_db, NewPw); }
            finally { Environment.SetEnvironmentVariable("LEMORA_TEST_REKEY_FAIL", null); }
            Assert.False(r.Ok);
            Assert.False(DataVault.Frozen);                       // nothing changed: work continues
            Assert.True(DataVault.CurrentKey()!.SequenceEqual(_oldKey));
            Assert.Equal(before, Dump());                          // identical database
            Assert.False(File.Exists(_db + ".cambio-chiave.bak"));
            Assert.Equal("ok", DataVault.SimpleDecrypt(DataVault.SimpleEncrypt("ok")));
        }
    }
}
