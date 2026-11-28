using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Newtonsoft.Json;

namespace Lemora.View.Editor.Forms
{
    public class RdpFormViewModel : ProtocolBaseWithAddressPortUserPwdFormViewModel
    {
        public new RDP New { get; }
        public RdpFormViewModel(RDP protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }

        /// <summary>in the panel of a session open on a server that cannot change resolution: fit to window, width/height and scale are hidden</summary>
        public bool HideLiveResize { get; set; }

        /// <summary>raised when the Domain field loses focus: the editor offers to link it to a folder</summary>
        public event System.Action? DomainCommitted;
        public void RaiseDomainCommitted() => DomainCommitted?.Invoke();
    }
}
