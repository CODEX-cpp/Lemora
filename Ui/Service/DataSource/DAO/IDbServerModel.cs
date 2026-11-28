using Lemora.Model.Protocol.Base;

namespace Lemora.Service.DataSource.DAO
{
    public interface IDataBaseServer
    {
        /// <summary>
        /// ULID
        /// </summary>
        string GetId();

        string GetProtocol();

        string GetClassVersion();

        string GetJson();

        ProtocolBase? ToProtocolServerBase();
    }
}