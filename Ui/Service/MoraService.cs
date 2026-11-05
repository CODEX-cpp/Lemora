using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Setup;
using Lemora.Utils;
using Lemora.View.Launcher;
using Shawn.Utils;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.Service
{
    /// <summary>apertura ed esportazione dei file .mora (formato e registrazione in <see cref="MoraFile"/>)</summary>
    public static class MoraService
    {
        /// <summary>qualsiasi argomento che finisce in .mora e' un file .mora, anche se non esiste piu': l'errore va detto, non ignorato</summary>
        public static bool IsMoraPath(string arg) => arg.EndsWith(MoraFile.Extension, StringComparison.OrdinalIgnoreCase);

        // ---- errori

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>
        /// Errore all'apertura di un .mora: finestra di Windows a se', sempre in primo piano. Il messaggio dentro l'app (come gli altri) restava
        /// invisibile se Lemora era ridotta, nel vassoio o dietro altre finestre, e fermava il thread che riceve i file. Su un altro thread: non blocca nulla.
        /// </summary>
        private static void ShowError(string text)
        {
            SimpleLogHelper.Warning("MoraService: " + text);
            _ = System.Threading.Tasks.Task.Run(() => MessageBoxW(IntPtr.Zero, text, "Lemora", 0x10 /*errore*/ | 0x10000 /*primo piano*/ | 0x40000 /*sempre sopra*/));
        }

        // ---- apertura

        /// <summary>
        /// Apre la connessione di un file .mora. Se nel database c'e' la stessa identica connessione (tipo, indirizzo, porta e utente) si usa quella,
        /// con la sua password; se non c'e' (o non ha password) la password si chiede al collegamento.
        /// </summary>
        public static void Open(string path)
        {
            try
            {
                if (!File.Exists(path)) { ShowError(string.Format(SetupText.T("mora_missing"), path)); return; }
                var c = MoraFile.Read(path);
                if (c == null) { ShowError(SetupText.T("mora_bad")); return; }
                var fresh = Create(c);
                if (fresh == null) { ShowError(SetupText.T("mora_proto")); return; }

                var target = FindSaved(c, fresh) ?? fresh;
                var token = c.NewWindow ? DateTime.Now.Ticks.ToString() : "";
                // la richiesta della password (e gli errori di collegamento) stanno nella finestra principale: se serve, deve essere visibile
                var needsPassword = target is ProtocolBaseWithAddressPortUserPwd { AskPasswordWhenConnect: true };
                if (!c.NewWindow || needsPassword) IoC.TryGet<View.MainWindowViewModel>()?.ShowMe(true);
                GlobalEventHelper.OnRequestServerConnect?.Invoke(target, fromView: "Mora", assignTabToken: token);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                ShowError(string.Format(SetupText.T("mora_fail"), e.Message));
            }
        }

        /// <summary>connessione nuova (non salvata) con i dati del file: la password si chiede al collegamento</summary>
        private static ProtocolBaseWithAddressPort? Create(MoraConnection c)
        {
            var template = IoC.Get<QuickConnectionViewModel>().Protocols.FirstOrDefault(x => string.Equals(x.Protocol, c.Protocol, StringComparison.OrdinalIgnoreCase));
            if (template == null) return null;
            var s = (ProtocolBaseWithAddressPort)Activator.CreateInstance(template.GetType())!;
            s.DisplayName = string.IsNullOrWhiteSpace(c.Name) ? c.Address : c.Name;
            s.Address = c.Address;
            s.Port = c.Port;
            if (s is ProtocolBaseWithAddressPortUserPwd u)
            {
                u.UserName = c.UserName;
                u.Password = "";
                u.AskPasswordWhenConnect = true;
            }
            if (s is RDP rdp) rdp.Domain = c.Domain;
            return s;
        }

        private static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>la voce salvata identica (tipo, indirizzo, porta, utente); null se non c'e'. Una voce senza password chiede la password al collegamento.</summary>
        private static ProtocolBase? FindSaved(MoraConnection c, ProtocolBaseWithAddressPort fresh)
        {
            foreach (var vm in IoC.Get<GlobalData>().VmItemList)
            {
                if (vm.Server is not ProtocolBaseWithAddressPort s || !Same(s.Protocol, c.Protocol) || !Same(s.Address, c.Address) || s.GetPort() != fresh.GetPort()) continue;
                if (s is not ProtocolBaseWithAddressPortUserPwd su || string.IsNullOrWhiteSpace(c.UserName) || s is VNC)
                    return WithPasswordCheck(s);   // senza utente nel file (o VNC, che usa solo la password) basta il resto
                if (Same(su.UserName, c.UserName)) return WithPasswordCheck(su);
                // l'utente del file e' uno degli altri utenti della voce: si collega con quello
                var other = su.Users.FirstOrDefault(x => Same(x.UserName, c.UserName));
                if (other == null) continue;
                var clone = (ProtocolBaseWithAddressPortUserPwd)su.Clone();
                clone.DefaultUserKey = UserResolver.LocalKey(other.UserName);
                clone.AskWhichUser = false;
                clone.AskPasswordWhenConnect = string.IsNullOrEmpty(other.Password) && string.IsNullOrEmpty(other.PrivateKey);
                return clone;
            }
            return null;
        }

        private static ProtocolBase WithPasswordCheck(ProtocolBaseWithAddressPort s)
        {
            if (s is not ProtocolBaseWithAddressPortUserPwd u || u.AskPasswordWhenConnect == true) return s;
            if (!string.IsNullOrEmpty(u.Password) || !string.IsNullOrEmpty(u.PrivateKey) || u.Users.Any(x => !string.IsNullOrEmpty(x.Password) || !string.IsNullOrEmpty(x.PrivateKey))) return s;
            if (u.ActiveFolderCredentialIds.Count > 0) return s;   // la password viene dalle credenziali di cartella
            var clone = (ProtocolBaseWithAddressPortUserPwd)u.Clone();
            clone.AskPasswordWhenConnect = true;
            return clone;
        }

        // ---- esportazione

        /// <summary>scrive il file .mora di un server; la password non ci va mai</summary>
        public static void Write(string path, ProtocolBase server, bool newWindow)
        {
            var a = server as ProtocolBaseWithAddressPort;
            MoraFile.Write(path, new MoraConnection
            {
                Protocol = server.Protocol,
                Name = server.DisplayName,
                Address = a?.Address ?? "",
                Port = a?.Port ?? "",
                UserName = (server as ProtocolBaseWithAddressPortUserPwd)?.UserName ?? "",
                Domain = (server as RDP)?.Domain ?? "",
                NewWindow = newWindow,
            });
        }

        /// <summary>"Esporta collegamento": prima le opzioni (finestra separata), poi la finestra di Windows per scegliere dove salvare il .mora</summary>
        public static void ExportAs(ProtocolBase server)
        {
            var newWindow = AskOptions();
            if (newWindow == null) return;
            var path = SelectFileHelper.SaveFile(filter: "mora|*" + MoraFile.Extension, selectedFileName: MoraFile.SafeFileName(server.DisplayName) + MoraFile.Extension);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Write(path, server, newWindow.Value);
                ToastService.Show(string.Format(SetupText.T("mora_export_done"), Path.GetFileName(path)));
            }
            catch (Exception e) { MessageBoxHelper.ErrorAlert(e.Message); }
        }

        /// <summary>finestrella con le opzioni del file; null = annullato, altrimenti "apri in finestra separata"</summary>
        private static bool? AskOptions()
        {
            var win = new Window
            {
                Title = "Lemora",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Application.Current?.MainWindow,
                WindowStartupLocation = Application.Current?.MainWindow != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            win.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
            win.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            var box = new CheckBox { Content = SetupText.T("mora_opt_window"), Margin = new Thickness(0, 12, 0, 16) };
            box.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            var title = new TextBlock { Text = SetupText.T("mora_opt_title"), FontSize = 15, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            var ok = new Button { Content = SetupText.T("mora_ok"), MinWidth = 96, Height = 34, IsDefault = true };
            var cancel = new Button { Content = SetupText.T("mora_cancel"), MinWidth = 96, Height = 34, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            if (Application.Current?.TryFindResource("BtnPrimary") is Style bp) ok.Style = bp;
            if (Application.Current?.TryFindResource("BtnSecondary") is Style bs) cancel.Style = bs;
            ok.Click += (_, _) => win.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(24), MinWidth = 320 };
            panel.Children.Add(title); panel.Children.Add(box); panel.Children.Add(buttons);
            win.Content = panel;
            return win.ShowDialog() == true ? box.IsChecked == true : null;
        }
    }
}
