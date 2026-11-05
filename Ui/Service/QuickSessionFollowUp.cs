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
    /// Connessione rapida (da ricerca o avvio rapido) chiusa: se l'IP rientra nella rete di una cartella e non e' gia' un server salvato,
    /// compare una notifica "Salva in X" / "Ignora". "Salva in X" apre la creazione del server gia' compilata (indirizzo, cartella e,
    /// se erano state ricordate, credenziali).
    /// </summary>
    public static class QuickSessionFollowUp
    {
        private sealed class Pending
        {
            public string Host = "", UserName = "", Password = "", PrivateKey = "";
            /// <summary>true = l'utente ha scelto di ricordare le credenziali: si scrivono nella cronologia solo dopo un collegamento riuscito</summary>
            public bool Remember => !string.IsNullOrEmpty(UserName) || !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(PrivateKey);
            public string Protocol = "";
        }

        private static readonly ConcurrentDictionary<string, Pending> PendingByConnection = new();

        /// <summary>da chiamare quando parte una connessione rapida (le credenziali solo se l'utente ha scelto di ricordarle, altrimenti vuote)</summary>
        public static void Register(ProtocolBase server, string host, string userName, string password, string privateKey)
        {
            if (server is not ProtocolBaseWithAddressPort) return;
            PendingByConnection[server.Id] = new Pending { Host = host, UserName = userName, Password = password, PrivateKey = privateKey, Protocol = server.Protocol };
        }

        /// <summary>collegamento riuscito: le credenziali ricordate vanno nella cronologia delle connessioni rapide</summary>
        public static void OnSessionConnected(ProtocolBase server)
        {
            if (!PendingByConnection.TryGetValue(server.Id, out var p) || !p.Remember) return;
            try { Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(p.Host, p.Protocol, p.UserName, p.Password, p.PrivateKey); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <summary>credenziali rifiutate dal server: si toglie dalla cronologia quella salvata (altrimenti ogni avvio userebbe quella sbagliata)</summary>
        public static void ForgetSavedCredentials(ProtocolBase server)
        {
            if (!PendingByConnection.TryGetValue(server.Id, out var p)) return;
            p.UserName = p.Password = p.PrivateKey = "";
            try { Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(p.Host, p.Protocol, "", "", ""); }
            catch (Exception e) { SimpleLogHelper.Error(e); }
        }

        /// <summary>nuove credenziali dopo una richiesta: si ricordano (dopo il collegamento riuscito) solo se l'utente lo ha chiesto</summary>
        public static void UpdateCredentials(ProtocolBase server, string userName, string password, string privateKey, bool remember)
        {
            if (!PendingByConnection.TryGetValue(server.Id, out var p)) return;
            p.UserName = remember ? userName : "";
            p.Password = remember ? password : "";
            p.PrivateKey = remember ? privateKey : "";
        }

        /// <summary>da chiamare alla chiusura di ogni sessione (qualsiasi thread, e dentro il lock del servizio: qui si fa solo lavoro leggero)</summary>
        public static void OnSessionClosed(ProtocolBase server, bool hadConnected)
        {
            if (!PendingByConnection.TryRemove(server.Id, out var pending)) return;
            if (!hadConnected) return;   // non si e' mai collegata: niente da salvare
            if (server is not ProtocolBaseWithAddressPort ap) return;
            var protocol = server.Protocol;
            var address = (ap.Address ?? "").Trim();
            var port = ap.Port;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Propose(protocol, address, port, pending)));
        }

        /// <summary>cartella la cui rete contiene l'IP (la piu' specifica: prefisso piu' lungo, poi cartella piu' in profondita'); null se nessuna</summary>
        public static string? FindFolderForAddress(string address)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
            string? best = null;
            int bestLen = -1, bestLevel = -1;
            foreach (var path in FolderService.AllPaths())
            {
                var prefix = FolderService.EffectiveIpPrefix(path).Trim();
                if (prefix.Length == 0) continue;
                prefix = prefix.TrimEnd('.') + ".";   // si confronta per numeri interi: "10.0.4." non e' "10.0.40."
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
                // gia' tra i server salvati: niente da proporre
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
                Host = hostText,
                DisplayName = pending.Host,
                TreeNodes = FolderService.ToNodes(folder),
                UserName = pending.UserName,
                Password = pending.Password,
                PrivateKey = pending.PrivateKey,
            });
        }
    }
}
