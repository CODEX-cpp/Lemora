using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Utils;

namespace Lemora.Service.DataSource.DAO.Dapper
{
    public class TableServer : IDataBaseServer
    {
        public const string TABLE_NAME = "Servers";

        /// <summary>
        /// ULID
        /// </summary>
        public string Id { get; set; } = string.Empty;
        public string Protocol { get; set; } = "";
        public string ClassVersion { get; set; } = "";
        public string Json { get; set; } = "";

        public ProtocolBase? ToProtocolServerBase()
        {
            return ItemCreateHelper.CreateFromDbOrm(this);
        }
        /// <summary>
        /// ULID
        /// </summary>
        public string GetId()
        {
            return Id;
        }

        public string GetProtocol()
        {
            return Protocol;
        }

        public string GetClassVersion()
        {
            return ClassVersion;
        }

        /// <summary>la connessione intera e' cifrata nel database (nomi, indirizzi, utenti): qui torna in chiaro</summary>
        public string GetJson()
        {
            return DataVault.DecryptOrReturnOriginalString(Json);
        }
    }

    internal static class TableServerHelperStatic
    {
        public static TableServer ToTableServer(this ProtocolBase s)
        {
            var ret = new TableServer()
            {
                Id = s.Id,
                ClassVersion = s.ClassVersion,
                Json = DataVault.SimpleEncrypt(s.ToJsonString()),
                Protocol = s.Protocol,
            };
            return ret;
        }
    }
}