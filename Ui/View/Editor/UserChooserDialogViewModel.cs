using System.Collections.Generic;
using Lemora.Service;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor
{
    /// <summary>finestra "con quale utente vuoi collegarti?", mostrata quando il server e' impostato su "chiedi sempre quale utente usare"</summary>
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
