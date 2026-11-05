using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.View.Editor.Forms;
using Shawn.Utils.Wpf;
using Stylet;

namespace Lemora.View.Editor
{
    /// <summary>collegamento cartella ⇄ dominio e prefisso IP, e messaggio a comparsa con "Annulla"</summary>
    public partial class ServerEditorPageViewModel
    {
        // ---- messaggio a comparsa in basso al centro
        private System.Windows.Threading.DispatcherTimer? _toastTimer;
        private Action? _toastAction;
        private Action? _toastCancel;

        private string _toastText = "";
        public string ToastText
        {
            get => _toastText;
            private set { if (SetAndNotifyIfChanged(ref _toastText, value)) RaisePropertyChanged(nameof(IsToastVisible)); }
        }
        public bool IsToastVisible => _toastText.Length > 0;

        private string _toastActionText = "";
        public string ToastActionText
        {
            get => _toastActionText;
            private set { if (SetAndNotifyIfChanged(ref _toastActionText, value)) RaisePropertyChanged(nameof(HasToastAction)); }
        }
        public bool HasToastAction => _toastActionText.Length > 0;

        /// <summary>mostra un messaggio per 8 secondi; "Annulla" esegue onCancel, il pulsante azione (se c'e') esegue onAction</summary>
        public void ShowToast(string text, Action? onCancel, string actionText = "", Action? onAction = null)
        {
            Execute.OnUIThread(() =>
            {
                _toastCancel = onCancel;
                _toastAction = onAction;
                ToastActionText = actionText;
                ToastText = text;
                _toastTimer?.Stop();
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _toastTimer.Tick += (_, _) => HideToast();
                _toastTimer.Start();
            });
        }

        public void HideToast()
        {
            _toastTimer?.Stop();
            _toastAction = null;
            _toastCancel = null;
            ToastText = "";
            ToastActionText = "";
        }

        private RelayCommand? _cmdToastAction;
        public RelayCommand CmdToastAction => _cmdToastAction ??= new RelayCommand(o => { var a = _toastAction; HideToast(); a?.Invoke(); });

        private RelayCommand? _cmdToastCancel;
        public RelayCommand CmdToastCancel => _cmdToastCancel ??= new RelayCommand(o => { var c = _toastCancel; HideToast(); c?.Invoke(); });

        // ---- cartella scelta: dominio e prefisso IP
        private readonly record struct FolderState(string IpPrefix, string Address, string Domain);

        private FolderState CaptureFolderState() => new FolderState(
            (EditorViewModel as ProtocolBaseWithAddressPortFormViewModel)?.HostViewModel.IpPrefix ?? "",
            (Server as ProtocolBaseWithAddressPort)?.Address ?? "",
            (Server as RDP)?.Domain ?? "");

        /// <summary>
        /// Dopo il cambio cartella: se il dominio e' vuoto (o era quello proposto dalla cartella precedente) si imposta quello della nuova cartella.
        /// Con un solo messaggio si avvisa di dominio e prefisso IP, con "Annulla" che rimette cartella, dominio e indirizzo come prima.
        /// </summary>
        private void ApplyFolderDefaults(string previousFolder, FolderState before)
        {
            var folder = SelectedFolder;
            var host = (EditorViewModel as ProtocolBaseWithAddressPortFormViewModel)?.HostViewModel;
            string? domainApplied = null;
            if (Server is RDP rdp)
            {
                var oldAuto = previousFolder.Length > 0 ? FolderService.EffectiveDomain(previousFolder) : "";
                var newAuto = folder.Length > 0 ? FolderService.EffectiveDomain(folder) : "";
                if ((string.IsNullOrEmpty(rdp.Domain) || rdp.Domain == oldAuto) && rdp.Domain != newAuto)
                {
                    rdp.Domain = newAuto;
                    _domainBeforeEdit = newAuto;
                    if (newAuto.Length > 0) domainApplied = newAuto;
                }
            }

            // il prefisso si "applica" solo a un indirizzo ancora vuoto: un indirizzo scritto non cambia mai (cambia solo come si mostra)
            var addressWasEmpty = before.Address.Length == 0 || before.Address == before.IpPrefix;
            var prefixApplied = host != null && addressWasEmpty && host.HasIpPrefix && host.IpPrefix != before.IpPrefix ? host.IpPrefix : null;
            if (folder.Length == 0 || (domainApplied == null && prefixApplied == null)) return;

            var parts = new List<string>();
            if (domainApplied != null) parts.Add(string.Format(IoC.Translate("toast_part_domain"), domainApplied));
            if (prefixApplied != null)
                parts.Add(string.Format(IoC.Translate("toast_part_prefix"), prefixApplied, FolderService.CountServers(folder)));
            ShowToast(string.Format(IoC.Translate("toast_folder_applied"), FolderService.Leaf(folder), string.Join(IoC.Translate("toast_and"), parts)), () =>
            {
                // ripristina cartella, dominio e indirizzo
                _selectedFolder = previousFolder;
                ApplyFolderToServer();
                RaisePropertyChanged(nameof(SelectedFolder));
                RaisePropertyChanged(nameof(Subtitle));
                if (Server is RDP r) { r.Domain = before.Domain; _domainBeforeEdit = before.Domain; }
                host?.Restore(before.IpPrefix, before.Address);
                EditorViewModel?.OnFolderChanged(previousFolder);
            });
        }

        // ---- dominio scritto a mano: si propone di collegarlo a una cartella
        private string _domainBeforeEdit = "";

        private void OnDomainCommitted()
        {
            if (Server is not RDP rdp) return;
            var domain = (rdp.Domain ?? "").Trim();
            if (domain.Length == 0 || domain == _domainBeforeEdit) { _domainBeforeEdit = domain; return; }
            var current = SelectedFolder;
            if (current.Length > 0 && string.Equals(FolderService.EffectiveDomain(current), domain, StringComparison.CurrentCultureIgnoreCase))
            {
                _domainBeforeEdit = domain;
                return;
            }

            var paths = FolderService.AllPaths();
            var match = paths.FirstOrDefault(p => FolderService.ToNodes(p).Count == 1 && string.Equals(p, domain, StringComparison.CurrentCultureIgnoreCase))
                        ?? paths.FirstOrDefault(p => string.Equals(FolderService.Leaf(p), domain, StringComparison.CurrentCultureIgnoreCase));
            var previousDomain = _domainBeforeEdit;
            void RestoreDomain() { rdp.Domain = previousDomain; _domainBeforeEdit = previousDomain; }

            if (match != null && !string.Equals(match, current, StringComparison.CurrentCultureIgnoreCase))
            {
                var moving = current.Length > 0;
                ShowToast(string.Format(IoC.Translate(moving ? "toast_domain_matches_move" : "toast_domain_matches_link"), domain, match),
                    RestoreDomain,
                    IoC.Translate(moving ? "toast_move" : "toast_link"),
                    () => { _domainBeforeEdit = domain; SelectedFolder = match; });
            }
            else if (match == null)
            {
                ShowToast(string.Format(IoC.Translate("toast_domain_no_folder"), domain),
                    RestoreDomain,
                    IoC.Translate("toast_create_folder"),
                    () =>
                    {
                        if (FolderService.Create("", domain, out var error)) { _domainBeforeEdit = domain; SelectedFolder = domain; }
                        else { RestoreDomain(); Lemora.Utils.MessageBoxHelper.ErrorAlert(error); }
                    });
            }
            else
            {
                _domainBeforeEdit = domain;
            }
        }
    }
}
