using Lemora.Model.Protocol.FileTransmit.Transmitters;

namespace Lemora.Model.Protocol.FileTransmit
{
    public interface IFileTransmittable
    {
        ITransmitter GeTransmitter();
        string GetStartupPath();
    }
}
