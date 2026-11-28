using System;
using System.Linq;
using Lemora.View.Launcher;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.ServerView
{
    /// <summary>Home row (favorites / history): a saved server or a quick connection.</summary>
    public class HomeItem : NotifyPropertyChangedBase
    {
        private bool _isSelected;
        /// <summary>entry highlighted with the arrows from the search bar (Enter connects it)</summary>
        public bool IsSelected { get => _isSelected; set => SetAndNotifyIfChanged(ref _isSelected, value); }

        /// <summary>identifies the entry even if it appears in several sections (favorites and history): same saved server or same quick connection</summary>
        public string Key => Server != null ? "S|" + Server.Server.Id : "Q|" + Quick?.Protocol + "|" + Quick?.Host;

        public ProtocolBaseViewModel? Server { get; init; }
        public QuickConnectionItem? Quick { get; init; }
        public string ProtocolUpper { get; init; } = "";
        public string Title { get; init; } = "";
        public string HostText { get; init; } = "";
        public string UserText { get; init; } = "";
        /// <summary>folder: for a saved server the one it is in, empty for a typed unsaved IP</summary>
        public string Folder { get; init; } = "";
        /// <summary>"[folder] name" (or IP), as in the browser extension</summary>
        public string DisplayTitle => Folder.Length == 0 ? Title : $"[{Folder}] {Title}";
        /// <summary>star: for a saved server it is its favorite flag, for a quick connection it is the entry's favorite flag</summary>
        public bool IsFavorite
        {
            get => Server != null ? Server.IsFavorite : Quick?.IsFavorite == true;
            set
            {
                if (Server != null) Server.IsFavorite = value;
                else if (Quick != null) Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionSetFavorite(Quick, value);
            }
        }
        public DateTime Time { get; init; } = DateTime.MinValue;
        public string LastConnectText => Time == DateTime.MinValue ? "" : Time.ToString("g");

        // ---- user: if the server has several users (its own or from the folder credentials) the text on the right is a drop-down menu
        private System.Collections.Generic.List<Lemora.Service.UserResolver.UserChoice> _choices = new();
        /// <summary>
        /// User choices. They are ASSIGNED by whoever creates the row (SetChoices), not read from the database here: this property is read by WPF while applying
        /// the row style/template, inside a resource lock; reading the database from here froze the program (another thread held the database
        /// lock and was waiting for the resource lock).
        /// </summary>
        public System.Collections.Generic.List<Lemora.Service.UserResolver.UserChoice> UserChoices => _choices;
        public bool HasUserChoices => _choices.Count > 1;

        public void SetChoices(System.Collections.Generic.IReadOnlyList<Lemora.Model.Protocol.Base.Credential> folderCredentials)
        {
            // only the server's users and the folder credentials ACTIVE on it (the others are activated from the editor)
            _choices = Server?.Server is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd u
                ? Lemora.Service.UserResolver.Usable(u, Lemora.Service.UserResolver.GetChoices(u, folderCredentials)) : new System.Collections.Generic.List<Lemora.Service.UserResolver.UserChoice>();
        }

        /// <summary>"Always ask" if the server asks for the user on every connection, otherwise the default user (or the first one)</summary>
        public string UserLabel
        {
            get
            {
                if (!HasUserChoices || Server?.Server is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd u) return UserText;
                return Lemora.Service.UserResolver.RowLabel(u, UserChoices);
            }
        }

        /// <summary>after a choice from the menu (called by a click, not by the binding): reloads the choices and refreshes the row</summary>
        public void RefreshUser()
        {
            SetChoices(Lemora.Service.FolderCredentialService.Snapshot());
            RaisePropertyChanged(nameof(UserLabel)); RaisePropertyChanged(nameof(HasUserChoices));
        }
    }
}
