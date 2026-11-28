using System;
using System.Collections.Generic;
using System.Linq;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.DAO;
using Lemora.Service.DataSource.Model;
using Shawn.Utils;

namespace Lemora.Service
{
    /// <summary>
    /// Shared credentials of a folder. They live in the database's credential store (the same as the Vault),
    /// with the <see cref="Credential.FolderPath"/> field set. They apply to the servers inside the folder and its subfolders.
    /// Servers fetch them by id at connection time: changing a password changes it for all servers that use it.
    /// </summary>
    public static class FolderCredentialService
    {
        private static DataSourceBase? Source => IoC.TryGet<DataSourceService>()?.LocalDataSource;

        public static string MakeName(string folderPath, string userName) => $"{folderPath}{FolderService.Separator}{userName}";

        /// <summary>
        /// Credentials saved in a folder with a domain set are domain credentials: "administrator" becomes "DOMAIN\administrator"
        /// (if the user already wrote "domain\user" or "user@domain" it stays as is). For the folder's non-RDP servers the domain is removed on use (UserResolver.Apply).
        /// </summary>
        public static string Domainize(string folderPath, string userName)
        {
            userName = (userName ?? "").Trim();
            var domain = FolderService.ConfiguredDomain(folderPath);
            return domain.Length > 0 && userName.Length > 0 && userName.IndexOfAny(new[] { '\\', '@' }) < 0 ? domain + "\\" + userName : userName;
        }

        /// <summary>all folder credentials (with the password still encrypted)</summary>
        private static List<Credential> AllFolderCredentials()
        {
            var all = (Source?.GetCredentials() ?? Enumerable.Empty<Credential>()).Where(x => !string.IsNullOrEmpty(x.FolderPath)).ToList();
            Cached = all;   // every read from the database also updates the in-memory copy
            return all;
        }

        /// <summary>
        /// Last list read (passwords still encrypted). Row bindings use it instead of <see cref="Snapshot"/>: reading the database
        /// while WPF applies a style can hang the program (see HomeItem). Updated at every read and after every data change.
        /// </summary>
        public static IReadOnlyList<Credential> Cached { get; private set; } = Array.Empty<Credential>();

        /// <summary>
        /// All folder credentials in one go (passwords still encrypted). The database is read once, OUTSIDE of UI construction:
        /// calling it while WPF applies a style or a template can hang the program (see HomeItem).
        /// </summary>
        public static List<Credential> Snapshot() => AllFolderCredentials();

        /// <summary>credentials defined in this very folder</summary>
        public static List<Credential> ForFolder(string folderPath) =>
            AllFolderCredentials().Where(x => string.Equals(x.FolderPath, folderPath, StringComparison.CurrentCultureIgnoreCase))
                                  .OrderBy(x => x.UserName, StringComparer.CurrentCultureIgnoreCase).ToList();

        /// <summary>credentials valid for a server: those of its folder and of the parent folders</summary>
        public static List<Credential> ApplicableTo(IReadOnlyList<string> serverNodes) =>
            AllFolderCredentials().Where(x => FolderService.IsIn(serverNodes, x.FolderPath))
                                  .OrderBy(x => FolderService.Level(x.FolderPath)).ThenBy(x => x.UserName, StringComparer.CurrentCultureIgnoreCase).ToList();

        public static Credential? GetById(string id) =>
            string.IsNullOrEmpty(id) ? null : AllFolderCredentials().FirstOrDefault(x => x.DatabaseId == id);

        /// <summary>copy with password and key in plaintext, ready for the connection</summary>
        public static Credential? GetDecryptedById(string id)
        {
            var c = GetById(id);
            if (c == null) return null;
            var clone = c.CloneMe();
            clone.DecryptToConnectLevel();
            return clone;
        }

        public static int UsedByCount(string credentialId) =>
            IoC.Get<GlobalData>().VmItemList.Count(x => (x.Server as ProtocolBaseWithAddressPortUserPwd)?.ActiveFolderCredentialIds.Contains(credentialId) == true);

        /// <param name="remote">true = comes from the Teams server (or is a consequence of what the server did): no permission to check and nothing to send back to the server</param>
        public static Result Add(string folderPath, string userName, string password, out Credential? added, bool remote = false)
        {
            added = null;
            var source = Source;
            if (source == null) return Result.Fail("database");
            if (string.IsNullOrWhiteSpace(folderPath)) return Result.Fail("missing folder");   // without a folder it would be a Vault credential (security hardening)
            if (!remote && Lemora.Setup.TeamsShared.ReadOnlyTeam) return Result.Fail(Lemora.Setup.TeamsSync.ErrorText("permission"));   // read-only: view only
            userName = Domainize(folderPath, userName);
            if (userName.Length == 0) return Result.Fail(IoC.Translate(LanguageService.CAN_NOT_BE_EMPTY));
            if (ForFolder(folderPath).Any(x => string.Equals(x.UserName, userName, StringComparison.CurrentCultureIgnoreCase)))
                return Result.Fail(IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, userName));
            // "requests": the new credential is requested from the administrator and, meanwhile, kept on this PC so it can be used right away
            // (before: it appeared only after the approval). Once approved, the shared one with the same password replaces it (TeamsShared.ApplyCreds)
            bool requested = false;
            if (!remote && Lemora.Setup.TeamsShared.RequestsTeam)
            {
                if (Lemora.Setup.TeamsSync.RequestShared("fcred-create", null, folderPath, null, null, userName, password ?? "", null, MakeName(folderPath, userName)) is { } reqErr) return Result.Fail(reqErr);
                requested = true;
            }
            var c = new Credential
            {
                Name = MakeName(folderPath, userName),
                FolderPath = folderPath,
                UserName = userName,
                Password = password ?? "",
            };
            var ret = source.Database_InsertCredential(c);
            if (ret.IsSuccess)
            {
                source.GetCredentials(true);
                added = GetByNameInternal(c.Name);
                if (!remote && !requested) Lemora.Setup.TeamsShared.CredUpsert(folderPath, userName, password ?? "");
            }
            return ret;
        }

        private static Credential? GetByNameInternal(string name) => AllFolderCredentials().FirstOrDefault(x => x.Name == name);

        /// <summary>changes user name and/or password (password null = unchanged); servers that use it see it right away, because they read it by id</summary>
        /// <param name="domainize">false = the name is already the final one (the "Domain user" switch decided whether the domain is needed)</param>
        public static Result Update(Credential credential, string userName, string? password, bool remote = false, bool domainize = true)
        {
            var source = Source;
            if (source == null) return Result.Fail("database");
            if (!remote && Lemora.Setup.TeamsShared.ReadOnlyTeam) return Result.Fail(Lemora.Setup.TeamsSync.ErrorText("permission"));   // read-only: view only
            var nameBefore = credential.Name;
            var c = credential.CloneMe();
            var userBefore = c.UserName;
            c.UserName = domainize ? Domainize(c.FolderPath, userName) : (userName ?? "").Trim();
            if (c.UserName.Length == 0) return Result.Fail(IoC.Translate(LanguageService.CAN_NOT_BE_EMPTY));
            // "requests": user and password of a shared credential change only with approval (here it stays as is). A credential the server
            // does not know (made only on this PC) is not shared: it is just modified here
            bool requests = !remote && Lemora.Setup.TeamsShared.RequestsTeam;
            if (requests && Lemora.Setup.TeamsShared.ServerCredId(c.FolderPath, userBefore) is { } sid)
                return Lemora.Setup.TeamsSync.RequestShared("fcred-update", sid, c.FolderPath, null, null, c.UserName, password, null, nameBefore) is { } reqErr
                    ? Result.Fail(reqErr) : Result.Success();
            c.Name = MakeName(c.FolderPath, c.UserName);
            if (password != null) c.Password = password;
            var ret = source.Database_UpdateCredential(c, nameBefore);
            if (ret.IsSuccess)
            {
                source.GetCredentials(true);
                if (!remote && !requests)
                {
                    bool renamed = !string.Equals(userBefore, c.UserName, StringComparison.CurrentCultureIgnoreCase);
                    if (renamed) Lemora.Setup.TeamsShared.CredDelete(c.FolderPath, userBefore);   // on the server the identity is folder + user: changing user = removing the old one and adding the new one
                    if (renamed || password != null) Lemora.Setup.TeamsShared.CredUpsert(c.FolderPath, c.UserName, password ?? GetDecryptedById(credential.DatabaseId)?.Password ?? "");
                }
            }
            return ret;
        }

        /// <summary>deletes the credential and removes it from the servers that used it</summary>
        public static Result Delete(Credential credential, bool remote = false)
        {
            var source = Source;
            if (source == null) return Result.Fail("database");
            if (!remote && Lemora.Setup.TeamsShared.ReadOnlyTeam) return Result.Fail(Lemora.Setup.TeamsSync.ErrorText("permission"));   // read-only does not delete
            // "requests": deleting a shared credential is requested from the administrator (it stays here until approved)
            bool requests = !remote && Lemora.Setup.TeamsShared.RequestsTeam;
            if (requests && Lemora.Setup.TeamsShared.ServerCredId(credential.FolderPath, credential.UserName) is { } sid)
                return Lemora.Setup.TeamsSync.RequestShared("fcred-delete", sid, credential.FolderPath, null, null, null, null, null, credential.Name) is { } reqErr
                    ? Result.Fail(reqErr) : Result.Success();
            var id = credential.DatabaseId;
            var ret = source.Database_DeleteCredential(new[] { credential.Name });
            if (!ret.IsSuccess) return ret;
            if (!remote && !requests) Lemora.Setup.TeamsShared.CredDelete(credential.FolderPath, credential.UserName);
            source.GetCredentials(true);
            var affected = IoC.Get<GlobalData>().VmItemList.Select(x => x.Server).OfType<ProtocolBaseWithAddressPortUserPwd>()
                              .Where(s => s.ActiveFolderCredentialIds.Contains(id) || s.DefaultUserKey == UserResolver.FolderKey(id))
                              .Where(s => s.DataSource is not Lemora.Service.DataSource.Model.TeamsSource).ToList();   // Teams entries do not keep the active credentials: writing them would be an empty request
            foreach (var s in affected)
            {
                s.ActiveFolderCredentialIds = s.ActiveFolderCredentialIds.Where(x => x != id).ToList();
                if (s.DefaultUserKey == UserResolver.FolderKey(id)) s.DefaultUserKey = "";
            }
            if (affected.Count > 0) IoC.Get<GlobalData>().UpdateServer(affected);
            return ret;
        }

        /// <summary>
        /// the folder domain has just changed: "OLD\user" credentials become "NEW\user" ("user" if the folder no longer has a domain).
        /// Before, they kept the old domain in the name and the connection went out with the new domain and "OLD\user" (wrong credentials).
        /// A name written by hand with another domain, or that would collide with an existing credential, stays as it is
        /// </summary>
        public static void FollowDomain(IEnumerable<(Credential Cred, string OldDomain)> before)
        {
            foreach (var (c, oldDomain) in before)
            {
                var newDomain = FolderService.ConfiguredDomain(c.FolderPath);
                if (FollowedName(c.UserName, oldDomain, newDomain) is not { } userName) continue;
                if (ForFolder(c.FolderPath).Any(x => x.DatabaseId != c.DatabaseId && string.Equals(x.UserName, userName, StringComparison.CurrentCultureIgnoreCase))) continue;
                var r = Update(c, userName, null, domainize: false);
                if (!r.IsSuccess) SimpleLogHelper.Warning($"Folder credential {c.UserName} not moved to domain {newDomain}: {r.ErrorInfo}");
            }
        }

        /// <summary>"OLD\user" with the domain changed to NEW -> "NEW\user" (NEW empty -> "user"); null = nothing to change</summary>
        public static string? FollowedName(string userName, string oldDomain, string newDomain)
        {
            oldDomain = (oldDomain ?? "").Trim(); newDomain = (newDomain ?? "").Trim();
            if (oldDomain.Length == 0 || string.Equals(oldDomain, newDomain, StringComparison.OrdinalIgnoreCase)) return null;
            if (userName == null || !userName.StartsWith(oldDomain + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            var shortName = userName.Substring(oldDomain.Length + 1);
            if (shortName.Length == 0) return null;
            return newDomain.Length > 0 ? newDomain + "\\" + shortName : shortName;
        }

        /// <summary>updates path and name of the credentials when a folder is renamed</summary>
        public static void OnFolderRenamed(string oldPath, string newPath)
        {
            var source = Source;
            if (source == null) return;
            var oldNodes = FolderService.ToNodes(oldPath);
            var newNodes = FolderService.ToNodes(newPath);
            foreach (var c in AllFolderCredentials().Where(x => FolderService.IsIn(FolderService.ToNodes(x.FolderPath), oldPath)).ToList())
            {
                var nodes = FolderService.ToNodes(c.FolderPath);
                for (var i = 0; i < newNodes.Count; i++) nodes[i] = newNodes[i];
                var nameBefore = c.Name;
                var clone = c.CloneMe();
                clone.FolderPath = FolderService.ToPath(nodes);
                clone.Name = MakeName(clone.FolderPath, clone.UserName);
                source.Database_UpdateCredential(clone, nameBefore);
            }
            source.GetCredentials(true);
        }

        /// <summary>moves a credential to another folder keeping the same id (comes from the Teams server: no permissions and no sending back). null on failure</summary>
        public static Credential? MoveRemote(Credential credential, string newFolder)
        {
            var source = Source;
            if (source == null) return null;
            var clone = credential.CloneMe();
            var nameBefore = clone.Name;
            clone.FolderPath = newFolder;
            clone.Name = MakeName(newFolder, clone.UserName);
            if (!source.Database_UpdateCredential(clone, nameBefore).IsSuccess) return null;
            source.GetCredentials(true);
            return GetById(credential.DatabaseId);
        }

        /// <summary>deletes all credentials of a folder (and of its subfolders)</summary>
        public static void OnFolderDeleted(string path)
        {
            foreach (var c in AllFolderCredentials().Where(x => FolderService.IsIn(FolderService.ToNodes(x.FolderPath), path)).ToList())
                Delete(c, remote: true);   // the deleted folder takes its credentials away on the server too (cascade)
        }

        /// <summary>the folder credential by path and user (as the server identifies them), case-insensitive</summary>
        public static Credential? Find(string folderPath, string userName) =>
            AllFolderCredentials().FirstOrDefault(x => string.Equals(x.FolderPath, folderPath, StringComparison.CurrentCultureIgnoreCase) && string.Equals(x.UserName, userName, StringComparison.CurrentCultureIgnoreCase));
    }
}
