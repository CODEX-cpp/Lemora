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
        /// VNC only has the password (no Users section): saving must not go through the users form, which cleared the saved
        /// password and set "ask for the password" on every connection.
        /// </summary>
        public override void BeforeSave() { }
    }
}
