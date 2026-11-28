using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Lemora;
using Lemora.Service;
using Lemora.Service.DataSource.DAO;
using Lemora.Utils.RdpFile;
using Xunit;
using Assert = Xunit.Assert;

namespace Tests.Utils
{
    /// <summary>security fixes: external DBs always encrypted, per-user .rdp password, "Keep unlocked" expiry</summary>
    public class SecurityGroup1Tests
    {
        private const string User = "secret-db-user";

        /// <summary>fake server on 127.0.0.1: sends the greeting and collects everything the client writes until it closes</summary>
        private static async Task<(Exception? error, string received)> FakeServer(Func<int, string> connStr, Func<NetworkStream, Task> greet, Func<string, System.Data.Common.DbConnection> open)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var got = new MemoryStream();
            var server = Task.Run(async () =>
            {
                using var c = await l.AcceptTcpClientAsync();
                var s = c.GetStream();
                await greet(s);
                var buf = new byte[4096];
                // collect for at most 3 seconds, then close: the client waiting for a reply gets the close and stops
                using var cts = new System.Threading.CancellationTokenSource(3000);
                try { int n; while ((n = await s.ReadAsync(buf, cts.Token)) > 0) got.Write(buf, 0, n); } catch { /* closed or timed out */ }
            });
            Exception? error = null;
            var client = Task.Run(async () => { try { using var cn = open(connStr(port)); await cn.OpenAsync(); } catch (Exception e) { error = e; } });
            await Task.WhenAny(Task.WhenAll(server, client), Task.Delay(15000));
            l.Stop();
            return (error, Encoding.ASCII.GetString(got.ToArray()));
        }

        // MySQL v10 greeting WITHOUT CLIENT_SSL (0x800): the server says it cannot encrypt
        private static async Task MySqlGreetingWithoutSsl(NetworkStream s)
        {
            var p = new MemoryStream();
            p.WriteByte(10); p.Write(Encoding.ASCII.GetBytes("8.0.36\0"));
            p.Write(BitConverter.GetBytes(7));                    // connection id
            p.Write(Encoding.ASCII.GetBytes("abcdefgh")); p.WriteByte(0);
            uint caps = 0x1 | 0x8 | 0x200 | 0x8000 | 0x80000;     // LONG_PASSWORD, CONNECT_WITH_DB, PROTOCOL_41, SECURE_CONNECTION, PLUGIN_AUTH
            p.Write(BitConverter.GetBytes((ushort)(caps & 0xFFFF)));
            p.WriteByte(33); p.Write(BitConverter.GetBytes((ushort)2));
            p.Write(BitConverter.GetBytes((ushort)(caps >> 16)));
            p.WriteByte(21); p.Write(new byte[10]);
            p.Write(Encoding.ASCII.GetBytes("ijklmnopqrst\0"));
            p.Write(Encoding.ASCII.GetBytes("mysql_native_password\0"));
            var body = p.ToArray();
            await s.WriteAsync(new byte[] { (byte)body.Length, (byte)(body.Length >> 8), (byte)(body.Length >> 16), 0 });
            await s.WriteAsync(body);
        }

        // PostgreSQL: to the encryption request (SSLRequest) the server replies 'N' = not supported
        private static async Task PgRefusesSsl(NetworkStream s)
        {
            var req = new byte[8];
            var n = 0;
            while (n < 8) n += await s.ReadAsync(req.AsMemory(n));
            await s.WriteAsync(new[] { (byte)'N' });
        }

        [Fact]
        public async Task MySql_ServerWithoutEncryption_UserNameNeverSent()
        {
            var (err, got) = await FakeServer(p => DbExtensions.GetMysqlConnectionString("127.0.0.1", p, "db", User, "pw-secret", 5),
                MySqlGreetingWithoutSsl, cs => new MySql.Data.MySqlClient.MySqlConnection(cs));
            Assert.NotNull(err);
            Assert.DoesNotContain(User, got);
        }

        [Fact]
        public async Task MySql_OldDefaultPreferred_WouldSendUserNameInClear()
        {
            // test control: with the previous default (Preferred) the user name is sent in plain text
            var (_, got) = await FakeServer(p => new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(DbExtensions.GetMysqlConnectionString("127.0.0.1", p, "db", User, "pw-secret", 5))
                    { SslMode = MySql.Data.MySqlClient.MySqlSslMode.Preferred }.ConnectionString,
                MySqlGreetingWithoutSsl, cs => new MySql.Data.MySqlClient.MySqlConnection(cs));
            Assert.Contains(User, got);
        }

        [Fact]
        public async Task Pgsql_ServerWithoutEncryption_UserNameNeverSent()
        {
            var (err, got) = await FakeServer(p => DbExtensions.GetPgsqlConnectionString("127.0.0.1", p, "db", User, "pw-secret", 5),
                PgRefusesSsl, cs => new Npgsql.NpgsqlConnection(cs));
            Assert.NotNull(err);
            Assert.DoesNotContain(User, got);
        }

        [Fact]
        public async Task Pgsql_OldDefaultPrefer_WouldSendUserNameInClear()
        {
            var (_, got) = await FakeServer(p => new Npgsql.NpgsqlConnectionStringBuilder(DbExtensions.GetPgsqlConnectionString("127.0.0.1", p, "db", User, "pw-secret", 5)) { SslMode = Npgsql.SslMode.Prefer }.ConnectionString,
                PgRefusesSsl, cs => new Npgsql.NpgsqlConnection(cs));
            Assert.Contains(User, got);
        }

        /// <summary>dwFlags field of the DPAPI blob (after version, provider, version and key GUID): 0x4 = machine scope</summary>
        private static int BlobFlags(byte[] blob) => BitConverter.ToInt32(blob, 4 + 16 + 4 + 16);

        [Fact]
        public void RdpFilePassword_IsUserScope_NotMachineScope()
        {
            var blob = DataProtection.ProtectData(Encoding.Unicode.GetBytes("pw-rdp"), "");
            Assert.Equal(0, BlobFlags(blob) & 0x4);
            // test control: with the previous flag the field is really set
            var old = DataProtection.ProtectData(Encoding.Unicode.GetBytes("pw-rdp"), "",
                DataProtection.CryptProtectDataFlags.CRYPTPROTECT_UI_FORBIDDEN | DataProtection.CryptProtectDataFlags.CRYPTPROTECT_LOCAL_MACHINE);
            Assert.Equal(4, BlobFlags(old) & 0x4);
        }

        [Theory]
        [InlineData("ctrl+v", true)]
        [InlineData("Ctrl + V", true)]
        [InlineData("ctrl+shift+v", true)]
        [InlineData("shift+insert", true)]
        [InlineData("maiusc+ins", true)]
        [InlineData("ctrl+c", false)]
        [InlineData("v", false)]
        [InlineData("alt+v", false)]
        public void McpPasteChordRecognized(string keys, bool paste) => Assert.Equal(paste, Lemora.Service.Mcp.McpTools.IsPasteChord(keys));

        [Fact]
        public void DbLock_SecondCopyRefused_FreeAfterOwnerCloses()
        {
            var db = Path.Combine(Path.GetTempPath(), "lemora-lock-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                // "other copy": same open mode as the real lock
                using (var other = new FileStream(DbInstanceLock.LockPath(db), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    other.Write(Encoding.UTF8.GetBytes("OTHER-PC user pid 1")); other.Flush();
                    Assert.False(DbInstanceLock.TryAcquire(db, out var owner));
                    Assert.Equal("OTHER-PC user pid 1", owner);
                }
                Assert.True(DbInstanceLock.TryAcquire(db, out _));   // file left behind but no longer held: it is taken
                Assert.True(DbInstanceLock.TryAcquire(db, out _));   // same process, same database: fine
            }
            finally { DbInstanceLock.ReleaseForTests(); try { File.Delete(DbInstanceLock.LockPath(db)); } catch { } }
        }

        [Fact]
        public void KeepUnlocked_ExpiredFileIsDeleted_ValidOneStays()
        {
            AppPathHelper.Instance ??= new AppPathHelper(Environment.CurrentDirectory, Environment.CurrentDirectory);
            var f = Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".vault-session");
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            string Json(DateTime exp) => "{\"ExpiresUtc\":\"" + exp.ToString("o") + "\",\"Db\":\"x\",\"Key\":\"y\"}";
            try
            {
                File.WriteAllText(f, Json(DateTime.UtcNow.AddMinutes(-1)));
                VaultUnlock.ForgetIfExpired();
                Assert.False(File.Exists(f));

                File.WriteAllText(f, Json(DateTime.UtcNow.AddMinutes(30)));
                VaultUnlock.ForgetIfExpired();
                Assert.True(File.Exists(f));
            }
            finally { if (File.Exists(f)) File.Delete(f); }
        }
    }
}
