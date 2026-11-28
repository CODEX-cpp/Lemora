using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Lemora.Model.Protocol.Base;
using System.Data.SQLite;
using MySql.Data.MySqlClient;
using Npgsql;
using NUlid;
using Shawn.Utils;
using Lemora.Utils;
using Lemora.Utils.Tracing;

// ReSharper disable InconsistentNaming

namespace Lemora.Service.DataSource.DAO.Dapper
{
    public partial class DapperDatabase : IDatabase
    {
        public DapperDatabase(string databaseName, DatabaseType databaseType) : base(databaseName, databaseType)
        {
        }

        /// <summary>
        /// Quote character         sqlite              MySQL             Postgres
        /// Double quote (")      Identifier       String literal       Identifier
        /// Single quote (')    String literal     String literal     String literal
        /// Backtick (`)          Identifier         Identifier            N/A
        ///
        /// So we write SQL in sqlite/MySQL style, using backtick for identifiers
        /// and single quote for string literals. When backend is postgres, replace
        /// the backtick with double quote
        /// </summary>
        string NormalizedSql(string sql)
        {
            return DatabaseType == DatabaseType.PostgreSQL ? sql.Replace('`', '"') : sql;
        }

        protected IDbConnection? _dbConnection;
        public IDbConnection? Connection => _dbConnection;

        public override void CloseConnection()
        {
            lock (this)
            {
                _dbConnection?.Close();
                if (DatabaseType == DatabaseType.Sqlite && _dbConnection is SQLiteConnection sqliteConn)
                {
                    SQLiteConnection.ClearPool(sqliteConn);
                }
            }
        }


        /// <summary>
        /// create a IDatabase instance and open connection
        /// </summary>
        /// <param name="newConnectionString"></param>
        public override Result OpenNewConnection(string newConnectionString)
        {
            lock (this)
            {
                if (_connectionString == newConnectionString && IsConnected())
                    return Result.Success();
                if (string.IsNullOrWhiteSpace(newConnectionString))
                    return Result.Success();
                _connectionString = newConnectionString;
                _dbConnection?.Close();
                _dbConnection?.Dispose();
                _dbConnection = null;
                return OpenConnection();
            }
        }


        /// <summary>
        /// opens the connection checking that the TLS certificate of the MySQL/PostgreSQL server is the remembered one (DbCertPin):
        /// different = ask the user, and without their yes no connection is made (the database password is not sent)
        /// </summary>
        private void OpenWithPinnedCertificate(System.Data.IDbConnection cn)
        {
            if (cn is MySqlConnection)
            {
                var b = new MySqlConnectionStringBuilder(_connectionString);
                var host = b.Server; var port = (int)b.Port;
                var fp = Lemora.Service.DataSource.DbCertPin.ProbeMysql(host, port);
                if (fp != null && !Lemora.Service.DataSource.DbCertPin.Accept("mysql", host, port, fp) && !Lemora.Service.DataSource.DbCertPin.ConfirmChanged("mysql", host, port, fp))
                    throw new InvalidOperationException(string.Format(IoC.Translate("db_cert_changed_not_connected"), "MySQL", host, port));
                cn.Open();
                return;
            }
            if (cn is NpgsqlConnection pg)
            {
                var b = new NpgsqlConnectionStringBuilder(_connectionString);
                var host = b.Host ?? ""; var port = b.Port;
                string? changed = null;
#pragma warning disable CS0618 // the per-connection callback is enough: only one connection per data source
                pg.UserCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert == null) return false;
                    var fp = Lemora.Service.DataSource.DbCertPin.Fingerprint(cert);
                    if (Lemora.Service.DataSource.DbCertPin.Accept("postgresql", host, port, fp)) return true;
                    changed = fp;
                    return false;
                };
#pragma warning restore CS0618
                try { pg.Open(); }
                catch when (changed != null)
                {
                    if (!Lemora.Service.DataSource.DbCertPin.ConfirmChanged("postgresql", host, port, changed))
                        throw new InvalidOperationException(string.Format(IoC.Translate("db_cert_changed_not_connected"), "PostgreSQL", host, port));
                    pg.Open();   // the new one is now the remembered one
                }
                return;
            }
            cn.Open();
        }

        // startup diagnostics: how many times the database file is opened and how much it costs
        public static int OpenCount;
        public static long OpenTicks;
        private Exception? _lastException = null;
        protected virtual Result OpenConnection(string actionInfo = "")
        {
            if (string.IsNullOrWhiteSpace(actionInfo))
                actionInfo = IoC.Translate("We can not connect database:");

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                return Result.Fail(actionInfo, DatabaseName, "The connection string is null!");
            }

            if (IsConnected()) return Result.Success();
            lock (this)
            {
                if (IsConnected()) return Result.Success();
                _dbConnection?.Close();
                _dbConnection = DatabaseType switch
                {
                    DatabaseType.MySql => new MySqlConnection(_connectionString),
                    DatabaseType.Sqlite => new SQLiteConnection(_connectionString),
                    DatabaseType.SqlServer => throw new NotImplementedException(DatabaseType.ToString() + " not supported!"),
                    DatabaseType.PostgreSQL => new NpgsqlConnection(_connectionString),
                    DatabaseType.Oracle => throw new NotImplementedException(DatabaseType.ToString() + " not supported!"),
                    _ => throw new NotImplementedException(DatabaseType.ToString() + " not supported!")
                };

                string error = "";
                try
                {
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    OpenWithPinnedCertificate(_dbConnection);
                    // SQLite leaves replaced or deleted values in the file's free space: a copy of the DB would reveal them
                    // (old names in plaintext, values encrypted with the old key). With secure_delete they are zeroed (security hardening)
                    if (DatabaseType == DatabaseType.Sqlite) _dbConnection.Execute("PRAGMA secure_delete = ON;");
                    System.Threading.Interlocked.Add(ref OpenTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                    System.Threading.Interlocked.Increment(ref OpenCount);
                    _lastException = null;
                    return Result.Success();
                }
                catch (DllNotFoundException e)
                {
                    SimpleLogHelper.Error(e);
                    UnifyTracing.Error(e);
                    MessageBoxHelper.ErrorAlert(e.Message +
                                                "\r\n\r\nPlease contact the developer if you get this error to help us fix it");
                    Environment.Exit(2);
                }
                catch (MySqlException mse)
                {
                    if (_lastException?.Message != mse.Message)
                    {
                        SimpleLogHelper.Error(mse);
                        UnifyTracing.Error(mse);
                    }

                    error = mse.Message;
                    _lastException = mse;
                }
                catch (NpgsqlException pgse)
                {
                    if (_lastException?.Message != pgse.Message)
                    {
                        SimpleLogHelper.Error(pgse);
                        UnifyTracing.Error(pgse);
                    }

                    error = pgse.Message;
                    _lastException = pgse;
                }
                catch (TimeoutException te)
                {
                    if (_lastException?.Message != te.Message)
                        SimpleLogHelper.Error(te);
                    error = te.Message;
                    _lastException = te;
                }
                catch (InvalidCastException ie)
                {
                    // ignore this exception
                    _lastException = ie;
                }
                catch (Exception e)
                {
                    if (_lastException?.Message != e.Message)
                    {
                        SimpleLogHelper.Error(e);
                        UnifyTracing.Error(e,
                            new Dictionary<string, string>() { { "DatabaseType", DatabaseType.ToString() } });
                    }

                    error = e.Message;
                    _lastException = e;
                }
                finally
                {
                    if (error != "" && _dbConnection != null)
                    {
                        try { _dbConnection?.Dispose(); } catch { /* ignore */ }
                        _dbConnection = null;
                    }
                }
                return Result.Fail(actionInfo, DatabaseName, error);
            }
        }

        public override bool IsConnected()
        {
            lock (this)
            {
                return _dbConnection?.State == ConnectionState.Open;
            }
        }

        public override Result InitTables()
        {
            string info = IoC.Translate("We can not create tables on database:");
            var result = OpenConnection(info);
            if (!result.IsSuccess) return result;

            try
            {
                _dbConnection?.Execute(NormalizedSql(@$"
CREATE TABLE IF NOT EXISTS `{TableConfig.TABLE_NAME}` (
    `{nameof(TableConfig.Key)}` VARCHAR (64) PRIMARY KEY
                                        NOT NULL
                                        UNIQUE,
    `{nameof(TableConfig.Value)}` TEXT NOT NULL
);
"));

                _dbConnection?.Execute(NormalizedSql(@$"
CREATE TABLE IF NOT EXISTS `{TableServer.TABLE_NAME}` (
    `{nameof(TableServer.Id)}`       VARCHAR (64) PRIMARY KEY
                                             NOT NULL
                                             UNIQUE,
    `{nameof(TableServer.Protocol)}` VARCHAR (32) NOT NULL,
    `{nameof(TableServer.ClassVersion)}` VARCHAR (32) NOT NULL,
    `{nameof(TableServer.Json)}`     TEXT         NOT NULL
);
"));

                InitPasswordVault();
                SetEncryptionTest();
            }
            catch (Exception e)
            {
                // if creation fails the user may have read-only rights: test the read permission too
                if (!GetConfig("EncryptionTest").IsSuccess)
                {
                    return Result.Fail(info, DatabaseName, e.Message);
                }
            }
            return Result.Success();
        }

        public override Result TableExists(string tableName)
        {
            string info = "We can not check table exists on database: ";
            var result = OpenConnection(info);
            if (!result.IsSuccess) return result;
            try
            {
                var ret = _dbConnection!.QueryFirstOrDefault<int>($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName", new { tableName, });
                if (ret > 0)
                    return Result.Success();
                return Result.Fail(info, DatabaseName, $"Table {tableName} not exists!");
            }
            catch (Exception e)
            {
                return Result.Fail(info, DatabaseName, e.Message);
            }
        }

        public override ResultSelects<ProtocolBase> GetServers()
        {
            string info = IoC.Translate("We can not select from database:");

            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return ResultSelects<ProtocolBase>.Fail(result.ErrorInfo);
                try
                {
                    var ps = _dbConnection!.Query<TableServer>(NormalizedSql($"SELECT * FROM `{TableServer.TABLE_NAME}`"))
                                                            .Select(x => Untrusted(x, x?.ToProtocolServerBase()))
                                                            .Where(x => x != null).ToList();
                    return ResultSelects<ProtocolBase>.Success((ps as List<ProtocolBase>)!);
                }
                catch (Exception e)
                {
                    return ResultSelects<ProtocolBase>.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        /// <summary>
        /// row not encrypted (or encrypted with another key) while the vault is unlocked: Lemora did not write it, someone wrote into the database file without
        /// the key. It is shown, but as an import: no folder credentials linked by id and no ciphertexts decrypted with this PC's key
        /// (security hardening)
        /// </summary>
        private static ProtocolBase? Untrusted(TableServer? row, ProtocolBase? p)
        {
            if (row == null || p == null || !DataVault.IsUnlocked) return p;
            if (DataVault.IsVaultText(row.Json) && DataVault.SimpleDecrypt(row.Json) != null) return p;
            SimpleLogHelper.Warning($"Database: entry '{p.DisplayName}' not encrypted with this PC's key: folder credentials not linked");
            Lemora.Utils.ImportGuard.Clean(p);
            return p;
        }

        private string SqlInsert => NormalizedSql($@"INSERT INTO `{TableServer.TABLE_NAME}`
(`{nameof(TableServer.Id)}`,`{nameof(TableServer.Protocol)}`, `{nameof(TableServer.ClassVersion)}`, `{nameof(TableServer.Json)}`)
VALUES
(@{nameof(TableServer.Id)}, @{nameof(TableServer.Protocol)}, @{nameof(TableServer.ClassVersion)}, @{nameof(TableServer.Json)});");

        /// <summary>
        /// protocolBase.Id is updated after a successful insert
        /// </summary>
        public override Result AddServer(ref ProtocolBase protocolBase)
        {
            string info = IoC.Translate("We can not insert into database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return result;

                try
                {
                    if (protocolBase.IsTmpSession())
                        protocolBase.Id = Ulid.NewUlid().ToString();
                    var server = protocolBase.ToTableServer();
                    int affCount = _dbConnection?.Execute(SqlInsert, server) ?? 0;
                    if (affCount > 0)
                    {
                        SetTableUpdateTimestamp(TableServer.TABLE_NAME);
                        return Result.Success();
                    }
                    return Result.Fail(info, DatabaseName, "Insert failed, no rows affected.");
                }
                catch (Exception e)
                {
                    return Result.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        /// <summary>
        /// protocolBase.Id is updated after a successful insert
        /// </summary>
        public override Result AddServer(IEnumerable<ProtocolBase> protocolBases)
        {
            string info = IoC.Translate("We can not insert into database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess || _dbConnection == null) return result;
                try
                {
                    var credentialsToAdd = new List<Credential>();
                    var credentials = new List<Credential>();
                    var cred2Servers = new Dictionary<string, List<ProtocolBaseWithAddressPortUserPwd>>();
                    foreach (var protocolBase in protocolBases)
                    {
                        protocolBase.DecryptToConnectLevel();
                        if (protocolBase is ProtocolBaseWithAddressPortUserPwd p && !string.IsNullOrEmpty(p.InheritedCredentialName))
                        {
                            var c = p.GetCredential();
                            c.Address = "";
                            c.Port = "";
                            if (cred2Servers.ContainsKey(c.GetHash()))
                            {
                                cred2Servers[c.GetHash()].Add(p);
                            }
                            else
                            {
                                cred2Servers.Add(c.GetHash(), new List<ProtocolBaseWithAddressPortUserPwd>() { p });
                            }
                            credentials.Add(c);
                        }
                    }
                    if (credentials.Count > 0)
                    {
                        var result1 = GetCredentials(false);
                        if (!result1.IsSuccess) return result1;
                        result = OpenConnection(info);
                        if (!result.IsSuccess || _dbConnection == null) return result;
                        var credentialsInDb = result1.Items;
                        foreach (var credential in credentials)
                        {
                            var hash = credential.GetHash();
                            // check if already exists
                            if (credentialsInDb.Any(x => string.Equals(x.Hash, hash, StringComparison.OrdinalIgnoreCase))) continue;
                            string candidate = credential.Name;
                            int i = 2;
                            while (credentialsInDb.Any(x => x.Name == candidate) || credentialsToAdd.Any(x => x.Name == candidate))
                            {
                                candidate = $"{credential.Name}({i})";
                                i++;
                            }
                            credential.Name = candidate;

                            foreach (var ss in cred2Servers[hash])
                            {
                                ss.InheritedCredentialName = credential.Name;
                            }
                            credentialsToAdd.Add(credential);
                        }
                    }

                    using var transaction = _dbConnection.BeginTransaction();


                    bool ret = false;
                    var rng = new NUlid.Rng.MonotonicUlidRng();
                    foreach (var protocolBase in protocolBases)
                    {
                        if (protocolBase.IsTmpSession())
                            protocolBase.Id = Ulid.NewUlid(rng).ToString();
                    }

                    // Re-encrypt before writing: the fingerprint loop above left everything decrypted.
                    // Old plaintext rows stay readable on load via DecryptOrReturnOriginalString.
                    foreach (var credential in credentialsToAdd)
                    {
                        credential.EncryptToDatabaseLevel();
                    }
                    foreach (var protocolBase in protocolBases)
                    {
                        protocolBase.EncryptToDatabaseLevel();
                    }

                    // INSERT CREDENTIALS
                    if (credentialsToAdd.Count > 0)
                    {
                        var tableCredentials = credentialsToAdd.Select(x => x.ToTableCredential()).ToList();
                        ret = _dbConnection?.Execute(SqlInsertCredentialVault, tableCredentials) > 0;
                        if (!ret)
                        {
                            transaction.Rollback();
                            return Result.Fail(info, DatabaseName, "Insert credentials failed, no rows affected.");
                        }
                    }

                    // INSERT SERVERS
                    var servers = protocolBases.Select(x => x.ToTableServer()).ToList();
                    ret = _dbConnection?.Execute(SqlInsert, servers) > 0;
                    if (!ret)
                    {
                        transaction.Rollback();
                        return Result.Fail(info, DatabaseName, "Insert servers failed, no rows affected.");
                    }
                    transaction.Commit();

                    SetTableUpdateTimestamp(TableServer.TABLE_NAME);
                    return Result.Success();
                }
                catch (Exception e)
                {
                    return Result.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        private string SqlUpdate => NormalizedSql($@"UPDATE `{TableServer.TABLE_NAME}` SET
`{nameof(TableServer.Protocol)}` = @{nameof(TableServer.Protocol)},
`{nameof(TableServer.ClassVersion)}` = @{nameof(TableServer.ClassVersion)},
`{nameof(TableServer.Json)}` = @{nameof(TableServer.Json)}
WHERE `{nameof(TableServer.Id)}`= @{nameof(TableServer.Id)};");
        public override Result UpdateServer(ProtocolBase server)
        {
            string info = IoC.Translate("We can not update on database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return result;
                try
                {
                    var ret = _dbConnection?.Execute(SqlUpdate, server.ToTableServer()) > 0;
                    if (ret)
                    {
                        SetTableUpdateTimestamp(TableServer.TABLE_NAME);
                        return Result.Success();
                    }
                    else
                    {
                        // TODO what if the item with `{nameof(Server.Id)}`= @{nameof(Server.Id)} does not exist?
                        return Result.Fail(info, DatabaseName, "Update failed, no rows affected.");
                    }
                }
                catch (Exception e)
                {
                    return ResultString.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        public override Result UpdateServer(IEnumerable<ProtocolBase> servers)
        {
            string info = IoC.Translate("We can not update on database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return result;
                try
                {
                    var items = servers.Select(x => x.ToTableServer());
                    var ret = _dbConnection?.Execute(SqlUpdate, items) > 0;
                    if (ret)
                    {
                        SetTableUpdateTimestamp(TableServer.TABLE_NAME);
                        return Result.Success();
                    }
                    else
                    {
                        return Result.Fail(info, DatabaseName, "Update failed, no rows affected.");
                    }
                }
                catch (Exception e)
                {
                    return ResultString.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        public override Result DeleteServer(IEnumerable<string> ids)
        {
            var info = IoC.Translate("We can not delete from database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return result;
                try
                {
                    // special case: dapper does not support IN operator for postgresql
                    var sql = DatabaseType == DatabaseType.PostgreSQL
                        ? $@"DELETE FROM `{TableServer.TABLE_NAME}` WHERE `{nameof(TableServer.Id)}` = ANY(@{nameof(TableServer.Id)});"
                        : $@"DELETE FROM `{TableServer.TABLE_NAME}` WHERE `{nameof(TableServer.Id)}` IN @{nameof(TableServer.Id)};";
                    var ret = _dbConnection?.Execute(NormalizedSql(sql), new { Id = ids }) > 0;
                    if (ret)
                    {
                        SetTableUpdateTimestamp(TableServer.TABLE_NAME);
                        return Result.Success();
                    }
                    else
                    {
                        return Result.Fail(info, DatabaseName, "Delete failed, no rows affected.");
                    }
                }
                catch (Exception e)
                {
                    return Result.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        public override ResultString GetConfig(string key, bool closeInEnd = false)
        {
            return GetConfigPrivate(key, closeInEnd);
        }

        protected ResultString GetConfigPrivate(string key, bool closeInEnd = false)
        {
            string info = IoC.Translate("We can not read from database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return ResultString.Fail(result.ErrorInfo);
                try
                {
                    var config = _dbConnection?.QueryFirstOrDefault<TableConfig>(NormalizedSql($"SELECT * FROM `{TableConfig.TABLE_NAME}` WHERE `{nameof(TableConfig.Key)}` = @{nameof(TableConfig.Key)}"),
                        new { Key = key, });
                    return ResultString.Success(config?.Value);
                }
                catch (Exception e)
                {
                    return ResultString.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        private string SqlInsertConfig => NormalizedSql($@"INSERT INTO `{TableConfig.TABLE_NAME}` (`{nameof(TableConfig.Key)}`, `{nameof(TableConfig.Value)}`)  VALUES (@{nameof(TableConfig.Key)}, @{nameof(TableConfig.Value)})");
        private string SqlUpdateConfig => NormalizedSql($@"UPDATE `{TableConfig.TABLE_NAME}` SET `{nameof(TableConfig.Value)}` = @{nameof(TableConfig.Value)} WHERE `{nameof(TableConfig.Key)}` = @{nameof(TableConfig.Key)}");
        private string SqlDeleteConfig => NormalizedSql($@"Delete FROM `{TableConfig.TABLE_NAME}` WHERE `{nameof(TableConfig.Key)}` = @{nameof(TableConfig.Key)}");

        public override Result SetConfig(string key, string? value, bool closeInEnd = false)
        {
            return SetConfigPrivate(key, value, closeInEnd);
        }

        protected Result SetConfigPrivate(string key, string? value, bool closeInEnd = false)
        {
            string info = IoC.Translate("We can not update on database:");
            lock (this)
            {
                var result = OpenConnection(info);
                if (!result.IsSuccess) return result;

                var get = GetConfigPrivate(key);
                if (!get.IsSuccess) return Result.Fail(get.ErrorInfo);
                try
                {
                    bool ret = true;
                    if (value == null)
                    {
                        _dbConnection?.Execute(SqlDeleteConfig, new { Key = key, });
                    }
                    else
                    {
                        bool existed = !string.IsNullOrEmpty(get.Result);
                        ret = _dbConnection?.Execute(existed ? SqlUpdateConfig : SqlInsertConfig, new { Key = key, Value = value, }) > 0;
                    }
                    return ret ? Result.Success() : Result.Fail("Failed");
                }
                catch (Exception e)
                {
                    return Result.Fail(info, DatabaseName, e.Message);
                }
            }
        }

        public override Result SetTableUpdateTimestamp(string tableName, long time = -1, bool closeInEnd = false)
        {
            lock (this)
            {
                var timestamp = time;
                if (time <= 0)
                    timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                return SetConfigPrivate("UpdateTimestamp_" + tableName, timestamp.ToString());
            }
        }

        public override ResultLong GetTableUpdateTimestamp(string tableName, bool closeInEnd = false)
        {
            lock (this)
            {
                var val = GetConfigPrivate("UpdateTimestamp_" + tableName);
                if (!val.IsSuccess) return ResultLong.Fail(val.ErrorInfo);
                if (long.TryParse(val.Result, out var t)
                    && t > 0)
                    return ResultLong.Success(t);
                return ResultLong.Success(0);
            }
        }
    }
}
