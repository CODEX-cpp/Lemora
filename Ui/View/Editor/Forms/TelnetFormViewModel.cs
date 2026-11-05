using Lemora.Model.Protocol;

namespace Lemora.View.Editor.Forms
{
    public class TelnetFormViewModel : ProtocolBaseWithAddressPortFormViewModel
    {
        public new Telnet New { get; }
        public TelnetFormViewModel(Telnet protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }
    }
}
