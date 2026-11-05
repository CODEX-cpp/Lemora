using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.View.Editor;

namespace Lemora.Service
{
    /// <summary>
    /// Alla richiesta di credenziali: se il server e' in una cartella (o l'IP rientra nella rete di una cartella) il dominio della cartella compare
    /// come chip davanti al nome utente (si toglie con la X: senza dominio si usa l'utente locale) e sotto compaiono gli utenti delle credenziali
    /// condivise della cartella, che con un clic collegano.
    /// </summary>
    public static class FolderCredentialHints
    {
        public static void Apply(PasswordPopupDialogViewModel dlg, string address, IReadOnlyList<string>? serverNodes, bool useDomain)
        {
            try
            {
                var folder = serverNodes is { Count: > 0 } ? FolderService.ToPath(serverNodes) : QuickSessionFollowUp.FindFolderForAddress(address);
                if (string.IsNullOrEmpty(folder)) return;
                var domain = useDomain ? FolderService.ConfiguredDomain(folder) : "";
                var current = dlg.UserName;
                dlg.Domain = domain;
                dlg.UserName = current;   // ridistribuisce il testo gia' presente fra chip e campo

                var users = new List<PasswordPopupDialogViewModel.SuggestedUser>();
                foreach (var c in FolderCredentialService.ApplicableTo(FolderService.ToNodes(folder)))
                {
                    var d = FolderCredentialService.GetDecryptedById(c.DatabaseId);
                    if (d == null || string.IsNullOrWhiteSpace(d.UserName)) continue;
                    var name = d.UserName.Trim();
                    // credenziali di dominio ("OMG\utente"): il dominio resta solo per RDP
                    if (!useDomain && name.IndexOf('\\') > 0) name = name.Substring(name.IndexOf('\\') + 1);
                    var full = domain.Length > 0 && name.IndexOfAny(new[] { '\\', '@' }) < 0 ? domain + "\\" + name : name;
                    users.Add(new PasswordPopupDialogViewModel.SuggestedUser { Display = full, UserName = full, Password = d.Password, PrivateKey = d.PrivateKeyPath });
                }
                dlg.SuggestedUsers = users;
                dlg.SuggestionsTitle = string.Format(IoC.Translate("folder_users_suggested"), FolderService.Leaf(folder).ToUpperInvariant());
            }
            catch (Exception e)
            {
                Shawn.Utils.SimpleLogHelper.Error(e);
            }
        }
    }
}
