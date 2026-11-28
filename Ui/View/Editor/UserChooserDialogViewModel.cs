using System.Collections.Generic;
using Lemora.Service;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor
{
    /// <summary>"which user do you want to connect with?" dialog, shown when the server is set to "always ask which user to use"</summary>
    public class UserChooserDialogViewModel : PopupBase
    {
        public string Title { get; set; } = "";
        public List<UserCandidate> Users { get; set; } = new List<UserCandidate>();
        public UserCandidate? Chosen { get; private set; }

        private RelayCommand? _cmdChoose;
        public RelayCommand CmdChoose => _cmdChoose ??= new RelayCommand(o =>
        {
            if (o is not UserCandidate user) return;
            Chosen = user;
            RequestClose(true);
        });
    }
}
