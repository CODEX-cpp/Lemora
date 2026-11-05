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
    /// Credenziali condivise di una cartella. Stanno nell'archivio credenziali del database (le stesse del Vault),
    /// con il campo <see cref="Credential.FolderPath"/> valorizzato. Valgono per i server dentro la cartella e le sue sottocartelle.
    /// I server le richiamano per id al momento della connessione: cambiando una password cambia per tutti i server che la usano.
    /// </summary>
    public static class FolderCredentialService
    {
        private static DataSourceBase? Source => IoC.TryGet<DataSourceService>()?.LocalDataSource;

        public static string MakeName(string folderPath, string userName) => $"{folderPath}{FolderService.Separator}{userName}";

        /// <summary>
        /// Le credenziali salvate in una cartella con un dominio impostato sono credenziali di dominio: "administrator" diventa "DOMINIO\administrator"
        /// (se l'utente ha gia' scritto "dominio\utente" o "utente@dominio" resta com'e'). Per i server non RDP della cartella il dominio si toglie all'uso (UserResolver.Apply).
        /// </summary>
        public static string Domainize(string folderPath, string userName)
        {
            userName = (userName ?? "").Trim();
            var domain = FolderService.ConfiguredDomain(folderPath);
            return domain.Length > 0 && userName.Length > 0 && userName.IndexOfAny(new[] { '\\', '@' }) < 0 ? domain + "\\" + userName : userName;
        }

        /// <summary>tutte le credenziali di cartella (con la password ancora cifrata)</summary>
        private static List<Credential> AllFolderCredentials()
        {
            var all = (Source?.GetCredentials() ?? Enumerable.Empty<Credential>()).Where(x => !string.IsNullOrEmpty(x.FolderPath)).ToList();
            Cached = all;   // ogni lettura dal database aggiorna anche la copia in memoria
            return all;
        }

        /// <summary>
        /// Ultimo elenco letto (password ancora cifrate). I binding delle righe lo usano al posto di <see cref="Snapshot"/>: leggere il database
        /// mentre WPF applica uno stile puo' bloccare il programma (vedi HomeItem). Si aggiorna a ogni lettura e dopo ogni cambiamento dei dati.
        /// </summary>
        public static IReadOnlyList<Credential> Cached { get; private set; } = Array.Empty<Credential>();

        /// <summary>
        /// Tutte le credenziali di cartella in un colpo solo (password ancora cifrate). Si legge il database una volta, FUORI dalla costruzione dell'interfaccia:
        /// chiamarlo mentre WPF applica uno stile o un template puo' bloccare il programma (vedi HomeItem).
        /// </summary>
        public static List<Credential> Snapshot() => AllFolderCredentials();

        /// <summary>credenziali definite proprio in questa cartella</summary>
        public static List<Credential> ForFolder(string folderPath) =>
            AllFolderCredentials().Where(x => string.Equals(x.FolderPath, folderPath, StringComparison.CurrentCultureIgnoreCase))
                                  .OrderBy(x => x.UserName, StringComparer.CurrentCultureIgnoreCase).ToList();

        /// <summary>credenziali valide per un server: quelle della sua cartella e delle cartelle genitore</summary>
        public static List<Credential> ApplicableTo(IReadOnlyList<string> serverNodes) =>
            AllFolderCredentials().Where(x => FolderService.IsIn(serverNodes, x.FolderPath))
                                  .OrderBy(x => FolderService.Level(x.FolderPath)).ThenBy(x => x.UserName, StringComparer.CurrentCultureIgnoreCase).ToList();

        public static Credential? GetById(string id) =>
            string.IsNullOrEmpty(id) ? null : AllFolderCredentials().FirstOrDefault(x => x.DatabaseId == id);

        /// <summary>copia con password e chiave in chiaro, pronta per la connessione</summary>
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

        /// <param name="remote">true = arriva dal server Teams (o e' una conseguenza di cio' che ha fatto il server): nessun permesso da controllare e niente da rimandare al server</param>
        public static Result Add(string folderPath, string userName, string password, out Credential? added, bool remote = false)
        {
            added = null;
            var source = Source;
            if (source == null) return Result.Fail("database");
            userName = Domainize(folderPath, userName);
            if (userName.Length == 0) return Result.Fail(IoC.Translate(LanguageService.CAN_NOT_BE_EMPTY));
            if (ForFolder(folderPath).Any(x => string.Equals(x.UserName, userName, StringComparison.CurrentCultureIgnoreCase)))
                return Result.Fail(IoC.Translate(LanguageService.XXX_IS_ALREADY_EXISTED, userName));
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
                if (!remote) Lemora.Setup.TeamsShared.CredUpsert(folderPath, userName, password ?? "");
            }
            return ret;
        }

        private static Credential? GetByNameInternal(string name) => AllFolderCredentials().FirstOrDefault(x => x.Name == name);

        /// <summary>cambia nome utente e/o password (password null = invariata); i server che la usano la vedono subito, perche' la leggono per id</summary>
        public static Result Update(Credential credential, string userName, string? password, bool remote = false)
        {
            var source = Source;
            if (source == null) return Result.Fail("database");
            var nameBefore = credential.Name;
            var c = credential.CloneMe();
            var userBefore = c.UserName;
            c.UserName = Domainize(c.FolderPath, userName);
            c.Name = MakeName(c.FolderPath, c.UserName);
            if (password != null) c.Password = password;
            var ret = source.Database_UpdateCredential(c, nameBefore);
            if (ret.IsSuccess)
            {
                source.GetCredentials(true);
                if (!remote)
                {
                    bool renamed = !string.Equals(userBefore, c.UserName, StringComparison.CurrentCultureIgnoreCase);
                    if (renamed) Lemora.Setup.TeamsShared.CredDelete(c.FolderPath, userBefore);   // sul server l'identita' e' cartella + utente: cambiare utente = togliere il vecchio e aggiungere il nuovo
                    if (renamed || password != null) Lemora.Setup.TeamsShared.CredUpsert(c.FolderPath, c.UserName, password ?? GetDecryptedById(credential.DatabaseId)?.Password ?? "");
                }
            }
            return ret;
        }

        /// <summary>elimina la credenziale e la toglie dai server che la usavano</summary>
        public static Result Delete(Credential credential, bool remote = false)
        {
            var source = Source;
            if (source == null) return Result.Fail("database");
            if (!remote && !Lemora.Setup.TeamsShared.CanDeleteDevices) return Result.Fail(Lemora.Setup.TeamsSync.ErrorText("permission"));   // la sola lettura non elimina
            var id = credential.DatabaseId;
            var ret = source.Database_DeleteCredential(new[] { credential.Name });
            if (!ret.IsSuccess) return ret;
            if (!remote) Lemora.Setup.TeamsShared.CredDelete(credential.FolderPath, credential.UserName);
            source.GetCredentials(true);
            var affected = IoC.Get<GlobalData>().VmItemList.Select(x => x.Server).OfType<ProtocolBaseWithAddressPortUserPwd>()
                              .Where(s => s.ActiveFolderCredentialIds.Contains(id) || s.DefaultUserKey == UserResolver.FolderKey(id)).ToList();
            foreach (var s in affected)
            {
                s.ActiveFolderCredentialIds = s.ActiveFolderCredentialIds.Where(x => x != id).ToList();
                if (s.DefaultUserKey == UserResolver.FolderKey(id)) s.DefaultUserKey = "";
            }
            if (affected.Count > 0) IoC.Get<GlobalData>().UpdateServer(affected);
            return ret;
        }

        /// <summary>aggiorna percorso e nome delle credenziali quando una cartella viene rinominata</summary>
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

        /// <summary>elimina tutte le credenziali di una cartella (e delle sue sottocartelle)</summary>
        public static void OnFolderDeleted(string path)
        {
            foreach (var c in AllFolderCredentials().Where(x => FolderService.IsIn(FolderService.ToNodes(x.FolderPath), path)).ToList())
                Delete(c, remote: true);   // la cartella eliminata porta via le sue credenziali anche sul server (a cascata)
        }

        /// <summary>la credenziale di una cartella per percorso e utente (come le riconosce il server), senza badare alle maiuscole</summary>
        public static Credential? Find(string folderPath, string userName) =>
            AllFolderCredentials().FirstOrDefault(x => string.Equals(x.FolderPath, folderPath, StringComparison.CurrentCultureIgnoreCase) && string.Equals(x.UserName, userName, StringComparison.CurrentCultureIgnoreCase));
    }
}
