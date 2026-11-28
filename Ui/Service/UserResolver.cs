using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;

namespace Lemora.Service
{
    /// <summary>user that can be used to connect to a server (local or shared folder credential)</summary>
    public class UserCandidate
    {
        /// <summary>"L:name" for local users, "F:id" for folder credentials</summary>
        public string Key { get; init; } = "";
        public string UserName { get; init; } = "";
        public string Password { get; init; } = "";
        public string PrivateKey { get; init; } = "";
        public bool UsePrivateKey { get; init; }
        public bool IsShared { get; init; }
        /// <summary>name to show in the choice (for shared ones it adds the folder)</summary>
        public string Display { get; init; } = "";
    }

    /// <summary>
    /// List of a server's users and choice of the one to use when connecting:
    /// active shared folder credentials + local users. If no user was configured
    /// with the new system (servers saved earlier) the legacy single user (UserName/Password) stays valid.
    /// </summary>
    public static class UserResolver
    {
        /// <summary>
        /// The legacy single user (UserName) of a server without a user list. When the default user is a folder credential,
        /// saving copies its name into UserName (for lists and exports): that is NOT a local user and must not be shown as such.
        /// </summary>
        public static string LegacyUserName(ProtocolBaseWithAddressPortUserPwd server) =>
            // always the in-memory copy, never the database: this function runs inside row bindings (reading the database while WPF applies a style can hang the program, see HomeItem)
            LegacyUserName(server, FolderCredentialService.Cached);

        public static string LegacyUserName(ProtocolBaseWithAddressPortUserPwd server, IReadOnlyList<Credential>? folderCredentials)
        {
            if (server.Users.Count > 0 || string.IsNullOrWhiteSpace(server.UserName)) return "";
            // user without password and without key that matches a folder credential valid for this server: it is the mirror of the folder
            // credential (saved as "entry user"), not a local user: it is not shown as a row
            if (folderCredentials != null && string.IsNullOrEmpty(server.Password) && string.IsNullOrEmpty(server.PrivateKey)
                && folderCredentials.Any(c => FolderService.IsIn(server.TreeNodes, c.FolderPath)
                                              && string.Equals(c.UserName, server.UserName, System.StringComparison.CurrentCultureIgnoreCase)))
                return "";
            if (folderCredentials != null && server.ActiveFolderCredentialIds.Count > 0
                && folderCredentials.Any(c => server.ActiveFolderCredentialIds.Contains(c.DatabaseId)
                                              && string.Equals(c.UserName, server.UserName, System.StringComparison.CurrentCultureIgnoreCase)))
                return "";
            return server.UserName;
        }

        public static string FolderKey(string credentialId) => "F:" + credentialId;
        public static string LocalKey(string userName) => "L:" + userName;

        /// <summary>the server must be the already decrypted copy used for the connection</summary>
        public static List<UserCandidate> GetCandidates(ProtocolBaseWithAddressPortUserPwd server)
        {
            var list = new List<UserCandidate>();
            if (server is Lemora.Model.Protocol.VNC) return list;   // VNC uses only the password: users (and folder credentials) do not concern it
            foreach (var id in server.ActiveFolderCredentialIds)
            {
                var c = FolderCredentialService.GetDecryptedById(id);
                // only credentials of the entry's folder (or of the folders above): an entry written into the database by someone without the key
                // cannot fetch by id another folder's credential and send it to its own address (security hardening)
                if (c == null || !FolderService.IsIn(server.TreeNodes, c.FolderPath)) continue;
                list.Add(new UserCandidate
                {
                    Key = FolderKey(id),
                    UserName = c.UserName,
                    Password = c.Password,
                    PrivateKey = c.PrivateKeyPath,
                    UsePrivateKey = !string.IsNullOrEmpty(c.PrivateKeyPath),
                    IsShared = true,
                    Display = $"{c.UserName}  ·  {FolderService.Leaf(c.FolderPath)}",
                });
            }
            foreach (var u in server.Users)
            {
                if (string.IsNullOrWhiteSpace(u.UserName)) continue;
                list.Add(new UserCandidate
                {
                    Key = LocalKey(u.UserName),
                    UserName = u.UserName,
                    Password = u.Password,
                    PrivateKey = u.PrivateKey,
                    UsePrivateKey = u.UsePrivateKey,
                    Display = u.UserName,
                });
            }
            return list;
        }

        public record UserChoice(string Key, string Display, bool IsFolder = false);

        /// <summary>
        /// Users selectable for a server (by name, without decrypting passwords): its users (or the legacy single user) and ALL shared credentials
        /// of its folder and of the parent folders (including those not yet active on the server: choosing them activates them).
        /// </summary>
        public static List<UserChoice> GetChoices(ProtocolBaseWithAddressPortUserPwd server)
        {
            var list = new List<UserChoice>();
            if (server is Lemora.Model.Protocol.VNC) return list;
            if (server.Users.Count > 0)
            {
                foreach (var u in server.Users)
                    if (!string.IsNullOrWhiteSpace(u.UserName)) list.Add(new UserChoice(LocalKey(u.UserName), u.UserName));
            }
            else if (LegacyUserName(server).Length > 0)
            {
                list.Add(new UserChoice(LocalKey(server.UserName), server.UserName));
            }
            foreach (var c in FolderCredentialService.ApplicableTo(server.TreeNodes))
                list.Add(new UserChoice(FolderKey(c.DatabaseId), $"{c.UserName}  ·  {FolderService.Leaf(c.FolderPath)}", true));
            return list;
        }

        /// <summary>like <see cref="GetChoices(ProtocolBaseWithAddressPortUserPwd)"/> but with the folder credentials already read (<see cref="FolderCredentialService.Snapshot"/>): does not touch the database</summary>
        public static List<UserChoice> GetChoices(ProtocolBaseWithAddressPortUserPwd server, IReadOnlyList<Credential> folderCredentials)
        {
            var list = new List<UserChoice>();
            if (server is Lemora.Model.Protocol.VNC) return list;
            if (server.Users.Count > 0)
            {
                foreach (var u in server.Users)
                    if (!string.IsNullOrWhiteSpace(u.UserName)) list.Add(new UserChoice(LocalKey(u.UserName), u.UserName));
            }
            else if (LegacyUserName(server, folderCredentials).Length > 0)
            {
                list.Add(new UserChoice(LocalKey(server.UserName), server.UserName));
            }
            foreach (var c in folderCredentials.Where(x => FolderService.IsIn(server.TreeNodes, x.FolderPath))
                                               .OrderBy(x => FolderService.Level(x.FolderPath)).ThenBy(x => x.UserName, System.StringComparer.CurrentCultureIgnoreCase))
                list.Add(new UserChoice(FolderKey(c.DatabaseId), $"{c.UserName}  ·  {FolderService.Leaf(c.FolderPath)}", true));
            return list;
        }

        /// <summary>the choices the server can actually use: its users and the folder credentials active on it (the others are activated only by choosing them)</summary>
        public static List<UserChoice> Usable(ProtocolBaseWithAddressPortUserPwd server, List<UserChoice> choices) =>
            choices.Where(x => !x.IsFolder || server.ActiveFolderCredentialIds.Contains(x.Key.Substring(2))).ToList();

        /// <summary>
        /// Row label: "Always ask" if the server is set that way (also when chosen from the row menu), otherwise the default user.
        /// A server with a single usable credential no longer starts with "always ask" (see UsersEditorViewModel.ApplyToServer).
        /// </summary>
        public static string RowLabel(ProtocolBaseWithAddressPortUserPwd server, List<UserChoice> choices)
        {
            // only the choices the server can actually use (its users + ACTIVE folder credentials): with none, the row stays empty
            // (before, the folder's first credential was shown even if it was not active on this server)
            var usable = Usable(server, choices);
            if (usable.Count == 0) return "";
            if (server.AskWhichUser == true) return IoC.Translate("users_ask_short");   // the flag is on: the row says "Always ask" (with a single credential it connects without asking)
            return (EffectiveChoice(server, usable) ?? usable[0]).Display.Split('·')[0].Trim();
        }

        /// <summary>the user that will be used when connecting without asking: the default one, otherwise (as in GetCandidates) the first active one</summary>
        public static UserChoice? EffectiveChoice(ProtocolBaseWithAddressPortUserPwd server, List<UserChoice> choices)
        {
            var byKey = choices.FirstOrDefault(x => x.Key == server.DefaultUserKey);
            if (byKey != null) return byKey;
            var active = choices.FirstOrDefault(x => x.IsFolder && server.ActiveFolderCredentialIds.Contains(x.Key.Substring(2)));
            return active ?? choices.FirstOrDefault();
        }

        /// <summary>chooses the server's default user: if it is a folder credential it activates it, if it is the legacy user it moves it among the server's users</summary>
        public static void ApplyChoice(ProtocolBaseWithAddressPortUserPwd server, UserChoice choice)
        {
            if (choice.IsFolder)
            {
                var id = choice.Key.Substring(2);
                if (!server.ActiveFolderCredentialIds.Contains(id)) server.ActiveFolderCredentialIds = new List<string>(server.ActiveFolderCredentialIds) { id };
            }
            else if (LegacyUserName(server).Length > 0)
            {
                // former single user: it becomes the server's first user, so it stays selectable next to the folder credentials
                server.Users.Add(new UserEntry { UserName = server.UserName, Password = server.Password, PrivateKey = server.PrivateKey, UsePrivateKey = server.UsePrivateKeyForConnect == true });
            }
            server.DefaultUserKey = choice.Key;
            server.AskWhichUser = false;
        }

        /// <summary>default user, or the first one</summary>
        public static UserCandidate? Default(ProtocolBaseWithAddressPortUserPwd server, List<UserCandidate> candidates) =>
            candidates.FirstOrDefault(x => x.Key == server.DefaultUserKey) ?? candidates.FirstOrDefault();

        public static void Apply(ProtocolBaseWithAddressPortUserPwd server, UserCandidate user)
        {
            // credentials of a folder with a domain are "DOMAIN\user": the domain is only needed by RDP, for SSH/VNC/etc. only the user is used
            var name = user.UserName;
            if (server is not RDP && user.IsShared && name.IndexOf('\\') > 0) name = name.Substring(name.IndexOf('\\') + 1);
            server.UserName = name;
            if (user.UsePrivateKey)
            {
                server.UsePrivateKeyForConnect = true;
                server.Password = "";
                server.PrivateKey = user.PrivateKey;
            }
            else
            {
                server.UsePrivateKeyForConnect = false;
                server.PrivateKey = "";
                server.Password = user.Password;
            }
        }
    }
}
