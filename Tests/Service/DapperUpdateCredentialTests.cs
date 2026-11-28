using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.DataSource.DAO.Dapper;
using Lemora.Service;
using Lemora.Utils;
using Dapper;
using Newtonsoft.Json;
using Shawn.Utils;
using Xunit;

namespace Tests.Service
{
    /// <summary>
    /// End-to-end tests for the Dapper credential vault update and batch import paths, on a real temporary SQLite file.
    /// Covers: unreferenced-credential updates must commit; related servers must store the new state; batch imports must encrypt.
    /// </summary>
    public class DapperUpdateCredentialTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly DapperDatabase _db;

        public DapperUpdateCredentialTests()
        {
            DataVault.SetKey(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat((byte)7, 32)));
            _dbPath = Path.Combine(Path.GetTempPath(), "lemora-test-" + Guid.NewGuid().ToString("N") + ".db");
            _db = new DapperDatabase("test-db", DatabaseType.Sqlite);
            var open = _db.OpenNewConnection(DbExtensions.GetSqliteConnectionString(_dbPath));
            Assert.True(open.IsSuccess, "failed to open sqlite db: " + open.ErrorInfo);
            var init = _db.InitTables();
            Assert.True(init.IsSuccess, "failed to init tables: " + init.ErrorInfo);
        }

        public void Dispose()
        {
            try { _db.CloseConnection(); } catch { /* ignore */ }
            try { File.Delete(_dbPath); } catch { /* ignore */ }
        }

        [Fact]
        public void UpdateCredential_WithNoRelatedServers_MustPersist()
        {
            // arrange: a credential not referenced by any server
            var credential = new Credential { Name = "c1", UserName = "root", Password = "pw1" };
            Assert.True(_db.AddCredential(ref credential).IsSuccess);

            // act: edit it (password change) with no related protocols - the C-1 scenario
            var edited = credential.CloneMe();
            edited.Password = DataVault.EncryptOnce("pw2");
            edited.DatabaseId = credential.DatabaseId;
            var result = _db.UpdateCredential(edited, relatedProtocols: null);

            // assert: the UI saw success, so the database must contain the new value after re-read
            Assert.True(result.IsSuccess, "UpdateCredential reported failure: " + result.ErrorInfo);
            var reloaded = _db.GetCredentials();
            Assert.True(reloaded.IsSuccess);
            var fromDb = reloaded.Items.Single();
            Assert.Equal("c1", fromDb.Name);
            var decryptedPw = DataVault.DecryptOrReturnOriginalString(fromDb.Password);
            Assert.True(decryptedPw == "pw2",
                $"the edit was rolled back on dispose because the transaction was never committed (actual password: '{decryptedPw}')");
        }
        /// <summary>Reads raw server rows straight from the sqlite file, bypassing ToProtocolServerBase (needs full IoC).</summary>
        private List<SSH> ReadServersRawFromDisk()
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            using var con = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(_dbPath));
            con.Open();
            var jsons = con.Query<string>(NormalizedSqlHolder.SelectServersJson).ToList();
            // on disk the whole connection is encrypted by the vault: no name or address in plain text
            Assert.All(jsons, j => Assert.True(DataVault.IsVaultText(j)));
            return jsons.Select(j => JsonConvert.DeserializeObject<SSH>(DataVault.SimpleDecrypt(j)!)!).ToList();
        }

        private static class NormalizedSqlHolder
        {
            public const string SelectServersJson = "SELECT Json FROM Servers";
        }

        [Fact]
        public void UpdateCredential_WithRelatedServers_MustStoreNewSnapshotInServers()
        {
            // arrange: credential c1(pw1) + a server inheriting it with the pw1 snapshot
            var credential = new Credential { Name = "c1", UserName = "root", Password = "pw1" };
            Assert.True(_db.AddCredential(ref credential).IsSuccess);

            var server = new SSH { Address = "192.0.2.10", Port = "22", UserName = "root" };
            server.InheritedCredentialName = "c1";
            server.Password = DataVault.EncryptOnce("pw1");
            ProtocolBase serverBase = server;
            Assert.True(_db.AddServer(ref serverBase).IsSuccess);

            // act: rename c1 -> c2 and change the password (the C-2 scenario)
            var edited = credential.CloneMe();
            edited.Name = "c2";
            edited.Password = DataVault.EncryptOnce("pw2");
            edited.DatabaseId = credential.DatabaseId;
            var result = _db.UpdateCredential(edited, new List<ProtocolBaseWithAddressPortUserPwd> { server });

            // assert: after the UI reloads from the database, the server must reference the
            // NEW credential name and carry the NEW password snapshot (finding C-2: the old
            // code serialized the servers BEFORE applying the new state, so the reload
            // resurrected the old name/password and the vault link was lost)
            Assert.True(result.IsSuccess, "UpdateCredential reported failure: " + result.ErrorInfo);
            var fromDb = ReadServersRawFromDisk().Single();
            Assert.True(fromDb.InheritedCredentialName == "c2",
                $"server still references the old credential name '{fromDb.InheritedCredentialName}' after rename");
            var serverPw = DataVault.DecryptOrReturnOriginalString(fromDb.Password);
            Assert.True(serverPw == "pw2",
                $"server still carries the old password snapshot ('{serverPw}') after the credential password changed");
        }
        [Fact]
        public void AddServer_Batch_MustStoreEncryptedSecrets()
        {
            // arrange: mimic DataSourceBase.Database_InsertServer(List): the caller passes
            // ALREADY-ENCRYPTED clones into the batch AddServer
            var server = new SSH { Address = "192.0.2.20", Port = "22", UserName = "root" };
            server.InheritedCredentialName = "c1";
            server.Password = "plain-secret-pw";
            ProtocolBase encryptedClone = (ProtocolBase)server.Clone();
            encryptedClone.SetNotifyPropertyChangedEnabled(false);
            encryptedClone.EncryptToDatabaseLevel();
            Assert.True(_db.AddServer(new[] { encryptedClone }).IsSuccess);

            // assert server row: password must round-trip AND must not be stored as plaintext
            // (finding C-3: the batch path called DecryptToConnectLevel for the fingerprint
            // math and then serialized the DECRYPTED objects straight into the database)
            var fromDb = ReadServersRawFromDisk().Single();
            var roundTrip = DataVault.DecryptOrReturnOriginalString(fromDb.Password);
            Assert.True(roundTrip == "plain-secret-pw",
                $"stored password no longer round-trips to the original (got '{roundTrip}')");
            Assert.True(fromDb.Password != "plain-secret-pw",
                "the batch-import path stores the password as PLAINTEXT in the database");

            // assert credential row: the credential derived from the server must be encrypted too
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            using (var con = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(_dbPath)))
            {
                con.Open();
                var credJson = con.Query<string>("SELECT Json FROM Credentials").Single();
                Assert.True(DataVault.IsVaultText(credJson), "the whole credential must be encrypted in the database");
                var credential = JsonConvert.DeserializeObject<Credential>(DataVault.SimpleDecrypt(credJson)!)!;
                var credRoundTrip = DataVault.DecryptOrReturnOriginalString(credential.Password);
                Assert.True(credRoundTrip == "plain-secret-pw",
                    $"stored credential password no longer round-trips (got '{credRoundTrip}')");
                Assert.True(credential.Password != "plain-secret-pw",
                    "the batch-import path stores the derived credential as PLAINTEXT in the database");
            }
        }

        private List<string> RawNames()
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            using var con = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(_dbPath));
            con.Open();
            return con.Query<string>("SELECT Name FROM Credentials").ToList();
        }

        /// <summary>security review point 12: only the fingerprint in the Name column; add, rename, duplicate, delete and import work with the real name</summary>
        [Fact]
        public void CredentialName_IsOpaqueOnDisk_AndAllPathsUseTheRealName()
        {
            var a = new Credential { Name = "Customer / Site /DOM\\administrator", UserName = "DOM\\administrator", Password = "pw1" };
            Assert.True(_db.AddCredential(ref a).IsSuccess);
            Assert.All(RawNames(), n => Assert.StartsWith("n:", n));
            Assert.DoesNotContain(RawNames(), n => n.Contains("Customer") || n.Contains("administrator"));
            Assert.Equal("Customer / Site /DOM\\administrator", _db.GetCredentials().Items.Single().Name);

            // duplicate: same name -> the UNIQUE column still rejects it
            var dup = new Credential { Name = "Customer / Site /DOM\\administrator", UserName = "x", Password = "pw" };
            Assert.False(_db.AddCredential(ref dup).IsSuccess);

            // rename (like a renamed folder) + a server using it: the server gets the real name
            var server = new SSH { Address = "192.0.2.30", Port = "22", UserName = "root", InheritedCredentialName = "Customer / Site /DOM\\administrator" };
            ProtocolBase serverBase = server;
            Assert.True(_db.AddServer(ref serverBase).IsSuccess);
            var edited = _db.GetCredentials().Items.Single();
            edited.Name = "Customer2 / Site /DOM\\administrator";
            Assert.True(_db.UpdateCredential(edited, new List<ProtocolBaseWithAddressPortUserPwd> { server }).IsSuccess);
            Assert.Equal("Customer2 / Site /DOM\\administrator", _db.GetCredentials().Items.Single().Name);
            Assert.Equal("Customer2 / Site /DOM\\administrator", ReadServersRawFromDisk().Single().InheritedCredentialName);
            Assert.DoesNotContain(RawNames(), n => n.Contains("Customer"));

            // deletion by real name
            Assert.True(_db.DeleteCredential(new[] { "Customer2 / Site /DOM\\administrator" }).IsSuccess);
            Assert.Empty(_db.GetCredentials().Items);
        }

        /// <summary>old database: plain name in the column -> becomes a fingerprint on load, and can still be deleted by name</summary>
        [Fact]
        public void OldPlainNameRow_IsRewrittenOnLoad()
        {
            var c = new Credential { Name = "Old / admin", UserName = "admin", Password = "pw" };
            Assert.True(_db.AddCredential(ref c).IsSuccess);
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            using (var con = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(_dbPath)))
            {
                con.Open();
                con.Execute("UPDATE Credentials SET Name = 'Old / admin'");   // as it was before the fix
            }
            Assert.Equal("Old / admin", _db.GetCredentials().Items.Single().Name);
            Assert.All(RawNames(), n => Assert.StartsWith("n:", n));
            Assert.True(_db.DeleteCredential(new[] { "Old / admin" }).IsSuccess);
            Assert.Empty(_db.GetCredentials().Items);
        }

        private byte[] RawFileBytes()
        {
            var all = new List<byte>();
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-journal" }.Where(File.Exists))
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var b = new byte[fs.Length]; fs.ReadExactly(b); all.AddRange(b);
            }
            return all.ToArray();
        }

        private static bool Contains(byte[] hay, string text)
        {
            var n = System.Text.Encoding.UTF8.GetBytes(text);
            for (var i = 0; i + n.Length <= hay.Length; i++)
                if (hay.AsSpan(i, n.Length).SequenceEqual(n)) return true;
            return false;
        }

        /// <summary>after rewriting the old names, no plain name remains in the file bytes (rows and UNIQUE index)</summary>
        [Fact]
        public void OldPlainNames_LeaveNoBytesInTheFile()
        {
            for (var i = 0; i < 20; i++)
            {
                var c = new Credential { Name = $"CustomerTest{i:00} / Site /TESTDOM\\administrator", UserName = "TESTDOM\\administrator", Password = "pw" };
                Assert.True(_db.AddCredential(ref c).IsSuccess);
            }
            // database written by an old version (without secure_delete): plain names in the column
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            using (var con = new System.Data.SQLite.SQLiteConnection(DbExtensions.GetSqliteConnectionString(_dbPath)))
            {
                con.Open();
                con.Execute("PRAGMA secure_delete = OFF;");
                foreach (var r in con.Query("SELECT Id, Json FROM Credentials").ToList())
                {
                    var cred = JsonConvert.DeserializeObject<Credential>(DataVault.SimpleDecrypt((string)r.Json)!)!;
                    con.Execute("UPDATE Credentials SET Name = @n WHERE Id = @id", new { n = cred.Name, id = (string)r.Id });
                }
            }
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            Assert.True(Contains(RawFileBytes(), "CustomerTest"), "setup: the plain names must be in the file before the rewrite");

            Assert.Equal(20, _db.GetCredentials().Items.Count);   // names rewritten (only once)
            Assert.All(RawNames(), n => Assert.StartsWith("n:", n));
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            var bytes = RawFileBytes();
            Assert.False(Contains(bytes, "CustomerTest"), "plain names left in the free space of the file");
            Assert.False(Contains(bytes, "TESTDOM\\administrator"), "plain domain user left in the file");
        }
    }
}
