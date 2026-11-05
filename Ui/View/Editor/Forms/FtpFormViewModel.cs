using Lemora.Model.Protocol;

namespace Lemora.View.Editor.Forms
{
    public class FtpFormViewModel : ProtocolBaseWithAddressPortUserPwdFormViewModel
    {
        public new FTP New { get; }
        public FtpFormViewModel(FTP protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }
    }
}
