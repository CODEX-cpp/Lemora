using Lemora.Model.Protocol;

namespace Lemora.View.Editor.Forms
{
    public class VncFormViewModel : ProtocolBaseWithAddressPortUserPwdFormViewModel
    {
        public new VNC New { get; }
        public VncFormViewModel(VNC protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }

        /// <summary>
        /// Il VNC ha solo la password (nessuna sezione Utenti): il salvataggio non deve passare dal modulo degli utenti, che azzerava la password
        /// salvata e impostava "chiedi la password" a ogni collegamento.
        /// </summary>
        public override void BeforeSave() { }
    }
}
