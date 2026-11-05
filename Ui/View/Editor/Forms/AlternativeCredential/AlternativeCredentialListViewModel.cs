using Lemora.Model.Protocol.Base;
using Lemora.Utils;

namespace Lemora.View.Editor.Forms.AlternativeCredential;

public class AlternativeCredentialListViewModel : NotifyPropertyChangedBaseScreen
{
    public ProtocolBaseWithAddressPort New { get; }
    public AlternativeCredentialListViewModel(ProtocolBaseWithAddressPort protocol)
    {
        New = protocol;
    }
}