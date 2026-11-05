using System.Collections.Generic;
using System.Linq;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;

namespace Lemora.Service
{
    /// <summary>utente utilizzabile per collegarsi a un server (locale oppure credenziale condivisa della cartella)</summary>
    public class UserCandidate
    {
        /// <summary>"L:nome" per gli utenti locali, "F:id" per le credenziali di cartella</summary>
        public string Key { get; init; } = "";
        public string UserName { get; init; } = "";
        public string Password { get; init; } = "";
        public string PrivateKey { get; init; } = "";
        public bool UsePrivateKey { get; init; }
        public bool IsShared { get; init; }
        /// <summary>nome da mostrare nella scelta (per i condivisi aggiunge la cartella)</summary>
        public string Display { get; init; } = "";
    }

    /// <summary>
    /// Elenco degli utenti di un server e scelta di quello da usare alla connessione:
    /// credenziali condivise attive della cartella + utenti locali. Se non e' stato configurato nessun utente
    /// con il nuovo sistema (server salvati prima) resta valido l'utente unico di sempre (UserName/Password).
    /// </summary>
    public static class UserResolver
    {
        public static string FolderKey(string credentialId) => "F:" + credentialId;
        public static string LocalKey(string userName) => "L:" + userName;

        /// <summary>il server deve essere la copia gia' decifrata usata per la connessione</summary>
        public static List<UserCandidate> GetCandidates(ProtocolBaseWithAddressPortUserPwd server)
        {
            var list = new List<UserCandidate>();
            if (server is Lemora.Model.Protocol.VNC) return list;   // il VNC usa solo la password: gli utenti (e le credenziali delle cartelle) non lo riguardano
            foreach (var id in server.ActiveFolderCredentialIds)
            {
                var c = FolderCredentialService.GetDecryptedById(id);
                if (c == null) continue;
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
        /// Utenti scegliibili per un server (per nome, senza decifrare password): i suoi utenti (o l'utente unico di sempre) e TUTTE le credenziali condivise
        /// della sua cartella e delle cartelle genitore (anche quelle non ancora attive sul server: scegliendole si attivano).
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
            else if (!string.IsNullOrWhiteSpace(server.UserName))
            {
                list.Add(new UserChoice(LocalKey(server.UserName), server.UserName));
            }
            foreach (var c in FolderCredentialService.ApplicableTo(server.TreeNodes))
                list.Add(new UserChoice(FolderKey(c.DatabaseId), $"{c.UserName}  ·  {FolderService.Leaf(c.FolderPath)}", true));
            return list;
        }

        /// <summary>come <see cref="GetChoices(ProtocolBaseWithAddressPortUserPwd)"/> ma con le credenziali di cartella gia' lette (<see cref="FolderCredentialService.Snapshot"/>): non tocca il database</summary>
        public static List<UserChoice> GetChoices(ProtocolBaseWithAddressPortUserPwd server, IReadOnlyList<Credential> folderCredentials)
        {
            var list = new List<UserChoice>();
            if (server is Lemora.Model.Protocol.VNC) return list;
            if (server.Users.Count > 0)
            {
                foreach (var u in server.Users)
                    if (!string.IsNullOrWhiteSpace(u.UserName)) list.Add(new UserChoice(LocalKey(u.UserName), u.UserName));
            }
            else if (!string.IsNullOrWhiteSpace(server.UserName))
            {
                list.Add(new UserChoice(LocalKey(server.UserName), server.UserName));
            }
            foreach (var c in folderCredentials.Where(x => FolderService.IsIn(server.TreeNodes, x.FolderPath))
                                               .OrderBy(x => FolderService.Level(x.FolderPath)).ThenBy(x => x.UserName, System.StringComparer.CurrentCultureIgnoreCase))
                list.Add(new UserChoice(FolderKey(c.DatabaseId), $"{c.UserName}  ·  {FolderService.Leaf(c.FolderPath)}", true));
            return list;
        }

        /// <summary>le scelte che il server puo' davvero usare: i suoi utenti e le credenziali di cartella attive su di lui (le altre si attivano solo scegliendole)</summary>
        public static List<UserChoice> Usable(ProtocolBaseWithAddressPortUserPwd server, List<UserChoice> choices) =>
            choices.Where(x => !x.IsFolder || server.ActiveFolderCredentialIds.Contains(x.Key.Substring(2))).ToList();

        /// <summary>
        /// Scritta per riga: "Chiedi sempre" se il server e' impostato cosi' (anche scelto dal menu della riga), altrimenti l'utente predefinito.
        /// Un server con una sola credenziale utilizzabile non nasce piu' con "chiedi sempre" (vedi UsersEditorViewModel.ApplyToServer).
        /// </summary>
        public static string RowLabel(ProtocolBaseWithAddressPortUserPwd server, List<UserChoice> choices)
            => server.AskWhichUser == true
                ? IoC.Translate("users_ask_short")
                : (EffectiveChoice(server, choices) ?? choices[0]).Display.Split('·')[0].Trim();

        /// <summary>l'utente che verra' usato al collegamento senza chiedere: quello predefinito, altrimenti (come in GetCandidates) il primo attivo</summary>
        public static UserChoice? EffectiveChoice(ProtocolBaseWithAddressPortUserPwd server, List<UserChoice> choices)
        {
            var byKey = choices.FirstOrDefault(x => x.Key == server.DefaultUserKey);
            if (byKey != null) return byKey;
            var active = choices.FirstOrDefault(x => x.IsFolder && server.ActiveFolderCredentialIds.Contains(x.Key.Substring(2)));
            return active ?? choices.FirstOrDefault();
        }

        /// <summary>sceglie l'utente predefinito del server: se e' una credenziale di cartella la attiva, se e' l'utente di sempre lo porta fra gli utenti del server</summary>
        public static void ApplyChoice(ProtocolBaseWithAddressPortUserPwd server, UserChoice choice)
        {
            if (choice.IsFolder)
            {
                var id = choice.Key.Substring(2);
                if (!server.ActiveFolderCredentialIds.Contains(id)) server.ActiveFolderCredentialIds = new List<string>(server.ActiveFolderCredentialIds) { id };
            }
            else if (server.Users.Count == 0 && !string.IsNullOrWhiteSpace(server.UserName))
            {
                // utente unico di prima: diventa il primo utente del server, cosi' resta scegliibile accanto alle credenziali della cartella
                server.Users.Add(new UserEntry { UserName = server.UserName, Password = server.Password, PrivateKey = server.PrivateKey, UsePrivateKey = server.UsePrivateKeyForConnect == true });
            }
            server.DefaultUserKey = choice.Key;
            server.AskWhichUser = false;
        }

        /// <summary>utente predefinito, oppure il primo</summary>
        public static UserCandidate? Default(ProtocolBaseWithAddressPortUserPwd server, List<UserCandidate> candidates) =>
            candidates.FirstOrDefault(x => x.Key == server.DefaultUserKey) ?? candidates.FirstOrDefault();

        public static void Apply(ProtocolBaseWithAddressPortUserPwd server, UserCandidate user)
        {
            // le credenziali di una cartella con dominio sono "DOMINIO\utente": il dominio serve solo a RDP, per SSH/VNC/ecc. si usa il solo utente
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
