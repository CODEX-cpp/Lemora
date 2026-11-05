using System;
using System.Linq;
using Lemora.View.Launcher;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.ServerView
{
    /// <summary>Riga della home (preferiti / cronologia): un server salvato oppure una connessione rapida.</summary>
    public class HomeItem : NotifyPropertyChangedBase
    {
        private bool _isSelected;
        /// <summary>voce evidenziata con le frecce dalla barra di ricerca (Invio la collega)</summary>
        public bool IsSelected { get => _isSelected; set => SetAndNotifyIfChanged(ref _isSelected, value); }

        /// <summary>identifica la voce anche se compare in piu' sezioni (preferiti e cronologia): stesso server salvato o stessa connessione rapida</summary>
        public string Key => Server != null ? "S|" + Server.Server.Id : "Q|" + Quick?.Protocol + "|" + Quick?.Host;

        public ProtocolBaseViewModel? Server { get; init; }
        public QuickConnectionItem? Quick { get; init; }
        public string ProtocolUpper { get; init; } = "";
        public string Title { get; init; } = "";
        public string HostText { get; init; } = "";
        public string UserText { get; init; } = "";
        /// <summary>cartella: per un server salvato quella dove sta, vuota per un IP digitato non salvato</summary>
        public string Folder { get; init; } = "";
        /// <summary>nome (o IP) con la cartella tra parentesi</summary>
        public string DisplayTitle => Folder.Length == 0 ? Title : $"{Title} ({Folder})";
        /// <summary>stella: per un server salvato e' il suo preferito, per una connessione rapida e' il preferito della voce</summary>
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

        // ---- utente: se il server ha piu' utenti (propri o delle credenziali della cartella) la scritta a destra e' un menu a tendina
        private System.Collections.Generic.List<Lemora.Service.UserResolver.UserChoice> _choices = new();
        /// <summary>
        /// Scelte di utente. Vengono ASSEGNATE da chi crea la riga (SetChoices), non lette dal database qui: questa proprieta' e' letta da WPF mentre applica
        /// lo stile/template della riga, dentro un blocco delle risorse; leggere il database da qui bloccava il programma (un altro thread teneva il blocco
        /// del database e aspettava quello delle risorse).
        /// </summary>
        public System.Collections.Generic.List<Lemora.Service.UserResolver.UserChoice> UserChoices => _choices;
        public bool HasUserChoices => _choices.Count > 1;

        public void SetChoices(System.Collections.Generic.IReadOnlyList<Lemora.Model.Protocol.Base.Credential> folderCredentials)
        {
            _choices = Server?.Server is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd u
                ? Lemora.Service.UserResolver.GetChoices(u, folderCredentials) : new System.Collections.Generic.List<Lemora.Service.UserResolver.UserChoice>();
        }

        /// <summary>"Chiedi sempre" se il server chiede l'utente a ogni collegamento, altrimenti l'utente predefinito (o il primo)</summary>
        public string UserLabel
        {
            get
            {
                if (!HasUserChoices || Server?.Server is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd u) return UserText;
                return Lemora.Service.UserResolver.RowLabel(u, UserChoices);
            }
        }

        /// <summary>dopo una scelta dal menu (chiamata da un clic, non dal binding): rilegge le scelte e aggiorna la riga</summary>
        public void RefreshUser()
        {
            SetChoices(Lemora.Service.FolderCredentialService.Snapshot());
            RaisePropertyChanged(nameof(UserLabel)); RaisePropertyChanged(nameof(HasUserChoices));
        }
    }
}
