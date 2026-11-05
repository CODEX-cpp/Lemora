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

        /// <summary>nel pannello di una sessione aperta su un server che non sa cambiare risoluzione: si nascondono adatta alla finestra, larghezza/altezza e scala</summary>
        public bool HideLiveResize { get; set; }

        /// <summary>sollevato quando il campo Dominio perde il focus: l'editor propone di collegarlo a una cartella</summary>
        public event System.Action? DomainCommitted;
        public void RaiseDomainCommitted() => DomainCommitted?.Invoke();
    }
}
