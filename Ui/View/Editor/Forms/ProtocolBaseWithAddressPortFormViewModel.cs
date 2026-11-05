using Lemora.Model.Protocol.Base;
using Lemora.View.Editor.Forms.AlternativeCredential;
using Lemora.View.Editor.Forms.Utils;

namespace Lemora.View.Editor.Forms
{
    public class ProtocolBaseWithAddressPortFormViewModel : ProtocolBaseFormViewModel
    {
        public new ProtocolBaseWithAddressPort New { get; }
        public HostViewModel HostViewModel { get; }
        public AlternativeCredentialListViewModel AlternativeCredentialListViewModel { get; }
        public ProtocolBaseWithAddressPortFormViewModel(ProtocolBaseWithAddressPort protocolBase) : base(protocolBase)
        {
            New = protocolBase;
            HostViewModel = new HostViewModel(protocolBase);
            AlternativeCredentialListViewModel = new AlternativeCredentialListViewModel(protocolBase);
        }



        public override void OnFolderChanged(string folderPath)
        {
            base.OnFolderChanged(folderPath);
            HostViewModel.SetFolder(folderPath);
        }

        public override bool CanSave()
        {
            if (!string.IsNullOrEmpty(New[nameof(New.Address)]))
                return false;
            return base.CanSave();
        }
    }
}
