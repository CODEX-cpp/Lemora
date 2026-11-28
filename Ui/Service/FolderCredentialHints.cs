using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.View.Editor;

namespace Lemora.Service
{
    /// <summary>
    /// On a credential prompt: if the server is in a folder (or the IP falls within a folder's network) the folder's domain appears
    /// as a chip in front of the user name (removed with the X: without a domain the local user is used) and below appear the users of the folder's
    /// shared credentials, which connect with one click.
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
                dlg.UserName = current;   // redistributes the existing text between chip and field

                var users = new List<PasswordPopupDialogViewModel.SuggestedUser>();
                foreach (var c in FolderCredentialService.ApplicableTo(FolderService.ToNodes(folder)))
                {
                    var d = FolderCredentialService.GetDecryptedById(c.DatabaseId);
                    if (d == null || string.IsNullOrWhiteSpace(d.UserName)) continue;
                    var name = d.UserName.Trim();
                    // domain credentials ("ACME\user"): the domain is kept only for RDP
                    if (!useDomain && name.IndexOf('\\') > 0) name = name.Substring(name.IndexOf('\\') + 1);
                    var full = domain.Length > 0 && name.IndexOfAny(new[] { '\\', '@' }) < 0 ? domain + "\\" + name : name;
                    users.Add(new PasswordPopupDialogViewModel.SuggestedUser { Display = full, UserName = full, Password = d.Password, PrivateKey = d.PrivateKeyPath, CredentialId = c.DatabaseId });
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
