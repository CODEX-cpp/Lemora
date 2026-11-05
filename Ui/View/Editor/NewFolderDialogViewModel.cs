using System.Collections.Generic;
using System.Linq;
using Lemora.Service;
using Lemora.Utils;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor
{
    /// <summary>finestra "Nuova cartella" (blueprint §12, F1): nome, cartella che la contiene, dominio e rete della sede</summary>
    public class NewFolderDialogViewModel : PopupBase
    {
        public class ParentItem
        {
            public string Path { get; init; } = "";
            public string Display => string.IsNullOrEmpty(Path) ? IoC.Translate("folder_parent_none") : Path;
        }

        public List<ParentItem> ParentChoices { get; }

        private string _parent;
        public new string Parent
        {
            get => _parent;
            set { if (SetAndNotifyIfChanged(ref _parent, value ?? "")) RaisePropertyChanged(nameof(SuggestedIpPrefix)); }
        }

        private string _name = "";
        public string Name { get => _name; set { if (SetAndNotifyIfChanged(ref _name, value)) Validate(); } }

        private string _domain = "";
        public string Domain { get => _domain; set => SetAndNotifyIfChanged(ref _domain, value); }

        private string _ipPrefix = "";
        public string IpPrefix { get => _ipPrefix; set => SetAndNotifyIfChanged(ref _ipPrefix, value); }

        /// <summary>suggerimento mostrato nel campo vuoto: la rete della cartella che la contiene, se c'e'</summary>
        public string SuggestedIpPrefix => string.IsNullOrEmpty(Parent) ? "192.168.x.x/24" : FolderService.EffectiveIpPrefix(Parent);

        private string _error = "";
        public string Error { get => _error; private set => SetAndNotifyIfChanged(ref _error, value); }

        /// <summary>percorso della cartella creata (vuoto se si e' annullato)</summary>
        public string CreatedPath { get; private set; } = "";

        public NewFolderDialogViewModel(string parentPath = "", string defaultName = "")
        {
            ParentChoices = new List<ParentItem> { new ParentItem { Path = "" } }.Concat(FolderService.AllPaths().Select(x => new ParentItem { Path = x })).ToList();
            DisplayName = IoC.Translate("folder_new_title");   // titolo della finestra (senza, compariva il nome tecnico della classe)
            _parent = parentPath ?? "";
            _name = defaultName;
            if (!string.IsNullOrEmpty(_parent)) _domain = FolderService.EffectiveDomain(_parent);
        }

        private new bool Validate()
        {
            var err = FolderService.ValidateName(Name);
            if (err.Length == 0)
            {
                var path = string.IsNullOrEmpty(Parent) ? Name.Trim() : FolderService.ToPath(FolderService.ToNodes(Parent).Append(Name.Trim()));
                if (FolderService.Exists(path)) err = IoC.Translate(Lemora.Service.LanguageService.XXX_IS_ALREADY_EXISTED, Name.Trim());
            }
            // il messaggio compare solo dopo aver scritto qualcosa
            Error = string.IsNullOrEmpty(Name) ? "" : err;
            return err.Length == 0;
        }

        private RelayCommand? _cmdCreate;
        public RelayCommand CmdCreate => _cmdCreate ??= new RelayCommand(o =>
        {
            if (!Validate()) { if (string.IsNullOrEmpty(Error)) Error = FolderService.ValidateName(Name); return; }
            if (!FolderService.Create(Parent, Name, out var error)) { Error = error; return; }
            CreatedPath = string.IsNullOrEmpty(Parent) ? Name.Trim() : FolderService.ToPath(FolderService.ToNodes(Parent).Append(Name.Trim()));
            if (Domain.Trim().Length > 0 || IpPrefix.Trim().Length > 0)
                FolderService.SetSettings(CreatedPath, Domain, IpPrefix);
            RequestClose(true);
        });
    }
}
