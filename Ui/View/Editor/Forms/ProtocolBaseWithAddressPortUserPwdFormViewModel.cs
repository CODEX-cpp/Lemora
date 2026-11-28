using Lemora.Model.Protocol.Base;
using Lemora.View.Editor.Forms.AlternativeCredential;
using Lemora.View.Editor.Forms.Utils;

namespace Lemora.View.Editor.Forms
{
    public class ProtocolBaseWithAddressPortUserPwdFormViewModel : ProtocolBaseWithAddressPortFormViewModel
    {
        public new ProtocolBaseWithAddressPortUserPwd New { get; }
        /// <summary>"Users" section: the folder's shared credentials and local users</summary>
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
