using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.View.Editor;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace Lemora.Service
{
    /// <summary>
    /// Quick connection (from search or quick launch) closed: if the IP falls within a folder's network and is not already a saved server,
    /// a "Save in X" / "Ignore" notification appears. "Save in X" opens the server creation already filled in (address, folder and,
    /// if they were remembered, credentials).
    /// </summary>
    public static class QuickSessionFollowUp
    {
        private sealed class Pending
        {
            public string Host = "", UserName = "", Password = "", PrivateKey = "";
            /// <summary>true = the user chose to remember the credentials: they are written to the history only after a successful connection</summary>
            public bool Remember => !string.IsNullOrEmpty(UserName) || !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(PrivateKey);
            public string Protocol = "";
        }

        private static readonly ConcurrentDictionary<string, Pending> PendingByConnection = new();

        /// <summary>to be called when a quick connection starts (credentials only if the user chose to remember them, otherwise empty)</summary>
        public static void Register(ProtocolBase server, string host, string userName, string password, string privateKey)
        {
            if (server is not ProtocolBaseWithAddressPort) return;
            PendingByConnection[server.Id] = new Pending { Host = host, UserName = userName, Password = password, PrivateKey = privateKey, Protocol = server.Protocol };
        }

        // ---- "Use and link": a domain credential chosen in the prompt is linked to the server ONLY after a successful connection
        private sealed class LinkRequest { public string ServerId = ""; public object? DataSource; public string CredentialId = ""; }
        private static readonly ConcurrentDictionary<string, LinkRequest> LinkByConnection = new();

        /// <summary>to be called when a connection starts with a suggested credential: <paramref name="sessionId"/> is the session id, <paramref name="original"/> the saved entry</summary>
        public static void RegisterLink(string sessionId, ProtocolBase original, string credentialId)
        {
            if (string.IsNullOrEmpty(credentialId) || original.IsTmpSession()) return;
            LinkByConnection[sessionId] = new LinkRequest { ServerId = original.Id, DataSource = original.DataSource, CredentialId = credentialId };
        }

        /// <summary>
        /// successful connection: the credential used becomes active (and default if there was none) on the server. Administrator/write: it is saved and reaches everyone;
        /// "requests" permission: a request goes to the administrator and it is linked for everyone only if approved. If the connection fails (e.g. server outside the domain) we never get here.
        /// </summary>
        private static void LinkCredential(LinkRequest l)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var gd = IoC.Get<Lemora.Model.GlobalData>();
                        var orig = gd.VmItemList.FirstOrDefault(x => x.Id == l.ServerId && ReferenceEquals(x.DataSource, l.DataSource))?.Server as ProtocolBaseWithAddressPortUserPwd;
                        if (orig == null || orig.ActiveFolderCredentialIds.Contains(l.CredentialId)) return;
                        orig.ActiveFolderCredentialIds = new System.Collections.Generic.List<string>(orig.ActiveFolderCredentialIds) { l.CredentialId };
                        if (string.IsNullOrEmpty(orig.DefaultUserKey)) orig.DefaultUserKey = UserResolver.FolderKey(l.CredentialId);
                        var r = gd.UpdateServer(orig);
                        SimpleLogHelper.Info($"Folder credential linked to '{orig.DisplayName}' after a successful connection (result {(r.IsSuccess ? "ok" : r.ErrorInfo)})");
                    }
                    catch (Exception e) { SimpleLogHelper.Error(e); }
                }));
            }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        // ---- "Save in the connection": the credentials typed in the prompt become a user of the saved connection, but only after a successful connection
        private sealed class SaveRequest { public string ServerId = ""; public object? DataSource; public string User = "", Password = "", PrivateKey = ""; public bool UsePrivateKey; }
        private static readonly ConcurrentDictionary<string, SaveRequest> SaveByConnection = new();

        public static void RegisterSave(string sessionId, ProtocolBase original, string user, string password, string privateKey, bool usePrivateKey)
        {
            if (original.IsTmpSession() || string.IsNullOrWhiteSpace(user)) return;
            SaveByConnection[sessionId] = new SaveRequest { ServerId = original.Id, DataSource = original.DataSource, User = user.Trim(), Password = password, PrivateKey = privateKey, UsePrivateKey = usePrivateKey };
        }

        /// <summary>
        /// credential prompt after a failed connection ("Change credentials" or rejected password): like the prompt before connecting,
        /// the chosen folder credential is linked to the server and the hand-typed one is saved in the connection, always only on a successful connection.
        /// Before, nothing was saved here: after "Change credentials" the working credential was lost.
        /// </summary>
        public static void RegisterRetry(ProtocolBase server, PasswordPopupDialogViewModel dlg, string password)
        {
            if (!string.IsNullOrEmpty(dlg.LinkCredentialId)) RegisterLink(server.Id, server, dlg.LinkCredentialId);
            else if (dlg.CanSaveToConnection && dlg.SaveToConnection) RegisterSave(server.Id, server, dlg.UserName, password, "", false);
        }

        private static void SaveCredentials(SaveRequest s)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var gd = IoC.Get<Lemora.Model.GlobalData>();
                        var vm = gd.VmItemList.FirstOrDefault(x => x.Id == s.ServerId && ReferenceEquals(x.DataSource, s.DataSource));
                        if (vm?.Server.Clone() is not ProtocolBaseWithAddressPortUserPwd clone) return;
                        clone.DecryptToConnectLevel();   // as in the editor: UpdateServer encrypts on its own
                        var existing = clone.Users.FirstOrDefault(u => string.Equals(u.UserName, s.User, StringComparison.CurrentCultureIgnoreCase));
                        // the user already exists with the same password or key: nothing to save (before, "Nothing to save" appeared after every connection)
                        if (existing != null && clone.AskPasswordWhenConnect != true
                            && (s.UsePrivateKey ? existing.UsePrivateKey && existing.PrivateKey == s.PrivateKey : !existing.UsePrivateKey && existing.Password == s.Password)) return;
                        if (existing != null) { existing.Password = s.UsePrivateKey ? "" : s.Password; existing.PrivateKey = s.UsePrivateKey ? s.PrivateKey : ""; existing.UsePrivateKey = s.UsePrivateKey; }
                        else clone.Users.Add(new Lemora.Model.Protocol.Base.UserEntry { UserName = s.User, Password = s.UsePrivateKey ? "" : s.Password, PrivateKey = s.UsePrivateKey ? s.PrivateKey : "", UsePrivateKey = s.UsePrivateKey });
                        if (string.IsNullOrEmpty(clone.DefaultUserKey) || !clone.Users.Any(u => UserResolver.LocalKey(u.UserName) == clone.DefaultUserKey))
                            clone.DefaultUserKey = UserResolver.LocalKey(existing?.UserName ?? s.User);
                        clone.AskPasswordWhenConnect = false;   // now it has a user: the prompt is decided at connection time, for the chosen user (as in the editor)
                        var r = gd.UpdateServer(clone);
                        SimpleLogHelper.Info($"Credentials saved in the connection '{clone.DisplayName}' after a successful connection (result {(r.IsSuccess ? "ok" : r.ErrorInfo)})");
                        if (!r.IsSuccess) ToastService.Show(r.ErrorInfo, ToastKind.Warning);
                        else if (clone.DataSource is not Lemora.Service.DataSource.Model.TeamsSource) ToastService.Show(string.Format(Lemora.Setup.SetupText.T("pw_saved_conn"), clone.DisplayName));
                    }
                    catch (Exception e) { SimpleLogHelper.Error(e); }
                }));
            }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <summary>successful connection: the remembered credentials go into the quick connection history</summary>
        public static void OnSessionConnected(ProtocolBase server)
        {
            if (LinkByConnection.TryRemove(server.Id, out var link)) LinkCredential(link);
            if (SaveByConnection.TryRemove(server.Id, out var save)) SaveCredentials(save);
            if (!PendingByConnection.TryGetValue(server.Id, out var p) || !p.Remember) return;
            try { Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(p.Host, p.Protocol, p.UserName, p.Password, p.PrivateKey); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <summary>
        /// Pending "Use and link" / "Save in the connection" for this entry: dropped. Called when login fails, when the session closes
        /// and for external programs (which do not report whether login succeeded): before, they stayed and the next successful session of the same entry,
        /// with any credential, linked or saved the rejected one (security hardening)
        /// </summary>
        public static void DropLinkAndSave(ProtocolBase server)
        {
            LinkByConnection.TryRemove(server.Id, out _);
            SaveByConnection.TryRemove(server.Id, out _);
        }

        /// <summary>credentials rejected by the server: the saved one is removed from the history (otherwise every launch would use the wrong one)</summary>
        public static void ForgetSavedCredentials(ProtocolBase server)
        {
            DropLinkAndSave(server);
            if (!PendingByConnection.TryGetValue(server.Id, out var p)) return;
            p.UserName = p.Password = p.PrivateKey = "";
            try { Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(p.Host, p.Protocol, "", "", ""); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <summary>new credentials after a prompt: remembered (after the successful connection) only if the user asked for it</summary>
        public static void UpdateCredentials(ProtocolBase server, string userName, string password, string privateKey, bool remember)
        {
            if (!PendingByConnection.TryGetValue(server.Id, out var p)) return;
            p.UserName = remember ? userName : "";
            p.Password = remember ? password : "";
            p.PrivateKey = remember ? privateKey : "";
        }

        /// <summary>to be called when any session closes (any thread, and inside the service lock: only light work here)</summary>
        public static void OnSessionClosed(ProtocolBase server, bool hadConnected)
        {
            DropLinkAndSave(server);   // session ended without connecting (or already applied): nothing must remain for the next one
            if (!PendingByConnection.TryRemove(server.Id, out var pending)) return;
            if (!hadConnected) return;   // it never connected: nothing to save
            if (server is not ProtocolBaseWithAddressPort ap) return;
            var protocol = server.Protocol;
            var address = (ap.Address ?? "").Trim();
            var port = ap.Port;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Propose(protocol, address, port, pending)));
        }

        /// <summary>folder whose network contains the IP (the most specific: longest prefix, then deepest folder); null if none</summary>
        public static string? FindFolderForAddress(string address)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
            string? best = null;
            int bestLen = -1, bestLevel = -1;
            foreach (var path in FolderService.AllPaths())
            {
                var prefix = FolderService.EffectiveIpPrefix(path).Trim();
                if (prefix.Length == 0) continue;
                prefix = prefix.TrimEnd('.') + ".";   // compared by whole numbers: "10.0.4." is not "10.0.40."
                if (!address.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var level = FolderService.Level(path);
                if (prefix.Length > bestLen || (prefix.Length == bestLen && level > bestLevel))
                {
                    best = path; bestLen = prefix.Length; bestLevel = level;
                }
            }
            return best;
        }

        private static void Propose(string protocol, string address, string port, Pending pending)
        {
            try
            {
                if (address.Length == 0) return;
                // already among the saved servers: nothing to suggest
                var saved = IoC.Get<GlobalData>().VmItemList.Any(x => x.Server is ProtocolBaseWithAddressPort s
                                                                    && string.Equals((s.Address ?? "").Trim(), address, StringComparison.OrdinalIgnoreCase));
                if (saved) return;
                var folder = FindFolderForAddress(address);
                if (folder == null) return;
                var leaf = FolderService.Leaf(folder);

                ToastService.ShowAction(
                    string.Format(IoC.Translate("quickfolder_title"), address, leaf),
                    IoC.Translate("quickfolder_body"),
                    string.Format(IoC.Translate("quickfolder_save"), leaf),
                    () => OpenAddPage(protocol, address, port, folder, pending),
                    IoC.Translate("quickfolder_ignore"));
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
            }
        }

        private static void OpenAddPage(string protocol, string address, string port, string folder, Pending pending)
        {
            var hostText = string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
            GlobalEventHelper.OnGoToServerAddPage?.Invoke(null, null, new ServerEditorPageViewModel.ParamsServerAddPreset
            {
                Protocol = protocol,
                Host = hostText,   // the name stays empty: whoever saves writes it
                TreeNodes = FolderService.ToNodes(folder),
                UserName = pending.UserName,
                Password = pending.Password,
                PrivateKey = pending.PrivateKey,
            });
        }
    }
}
