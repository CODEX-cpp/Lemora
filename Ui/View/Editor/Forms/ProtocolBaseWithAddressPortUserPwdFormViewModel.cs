using Lemora.Model.Protocol.Base;
using Lemora.View.Editor.Forms.AlternativeCredential;
using Lemora.View.Editor.Forms.Utils;

namespace Lemora.View.Editor.Forms
{
    public class ProtocolBaseWithAddressPortUserPwdFormViewModel : ProtocolBaseWithAddressPortFormViewModel
    {
        public new ProtocolBaseWithAddressPortUserPwd New { get; }
        /// <summary>sezione "Utenti": credenziali condivise della cartella e utenti locali</summary>
        public UsersEditorViewModel UsersViewModel { get; }


        public ProtocolBaseWithAddressPortUserPwdFormViewModel(ProtocolBaseWithAddressPortUserPwd protocol) : base(protocol)
        {
            New = protocol;
            UsersViewModel = new UsersEditorViewModel(protocol);
        }

        public override void OnFolderChanged(string folderPath)
        {
            base.OnFolderChanged(folderPath);
            UsersViewModel.SetFolder(folderPath);
        }
        public override void BeforeSave() => UsersViewModel.ApplyToServer();
    }
}
