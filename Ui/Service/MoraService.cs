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
    /// <summary>opening and exporting .mora files (format and registration in <see cref="MoraFile"/>)</summary>
    public static class MoraService
    {
        /// <summary>any argument ending in .mora is a .mora file, even if it no longer exists: the error must be reported, not ignored</summary>
        public static bool IsMoraPath(string arg) => arg.EndsWith(MoraFile.Extension, StringComparison.OrdinalIgnoreCase);

        // ---- errors

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>
        /// Error when opening a .mora: a standalone Windows message box, always in the foreground. The in-app message (like the others) stayed
        /// invisible if Lemora was minimized, in the tray or behind other windows, and blocked the thread that receives the files. On another thread: blocks nothing.
        /// </summary>
        private static void ShowError(string text)
        {
            SimpleLogHelper.Warning("MoraService: " + text);
            _ = System.Threading.Tasks.Task.Run(() => MessageBoxW(IntPtr.Zero, text, "Lemora", 0x10 /*error*/ | 0x10000 /*foreground*/ | 0x40000 /*topmost*/));
        }

        // ---- opening

        /// <summary>
        /// Opens the connection of a .mora file. If the database has the exact same connection (type, address, port and user) that one is used,
        /// with its password; if not (or it has no password) the password is asked at connection time.
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

                var saved = FindSaved(c, fresh);
                var target = saved ?? fresh;
                var token = c.NewWindow ? DateTime.Now.Ticks.ToString() : "";
                void Go()
                {
                    // the password prompt (and connection errors) live in the main window: if needed, it must be visible
                    var needsPassword = target is ProtocolBaseWithAddressPortUserPwd { AskPasswordWhenConnect: true };
                    if (!c.NewWindow || needsPassword) IoC.TryGet<View.MainWindowViewModel>()?.ShowMe(true);
                    GlobalEventHelper.OnRequestServerConnect?.Invoke(target, fromView: "Mora", assignTabToken: token);
                }
                // file that came from outside ("downloaded from the Internet" mark: mail, browser) or pointing to an address not among the saved entries:
                // ask first, showing the real address. The name in the file is chosen by whoever wrote it (security hardening)
                var zone = MarkOfTheWeb.Zone(path);
                if (saved == null || zone >= 3)
                {
                    var text = string.Format(SetupText.T("mora_confirm"), fresh.Protocol, fresh.Address, fresh.GetPort(),
                        string.IsNullOrWhiteSpace(c.UserName) ? "-" : c.UserName, c.Name ?? "", Path.GetFileName(path))
                        + (zone >= 3 ? "\n\n" + SetupText.T("mora_confirm_web") : "")
                        + (saved == null ? "\n\n" + SetupText.T("mora_confirm_new") : "");
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        // 6 = Yes. MB_YESNO | warning | second button (No) default | foreground | topmost
                        if (MessageBoxW(IntPtr.Zero, text, "Lemora", 0x4 | 0x30 | 0x100 | 0x10000 | 0x40000) == 6) Stylet.Execute.OnUIThread(Go);
                        else SimpleLogHelper.Warning("MoraService: opening of the .mora file cancelled by the user");
                    });
                    return;
                }
                Go();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                ShowError(string.Format(SetupText.T("mora_fail"), e.Message));
            }
        }

        /// <summary>new (unsaved) connection with the file's data: the password is asked at connection time</summary>
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

        /// <summary>the identical saved entry (type, address, port, user); null if none. An entry without a password asks for it at connection time.</summary>
        private static ProtocolBase? FindSaved(MoraConnection c, ProtocolBaseWithAddressPort fresh)
        {
            foreach (var vm in IoC.Get<GlobalData>().VmItemList)
            {
                if (vm.Server is not ProtocolBaseWithAddressPort s || !Same(s.Protocol, c.Protocol) || !Same(s.Address, c.Address) || s.GetPort() != fresh.GetPort()) continue;
                if (s is not ProtocolBaseWithAddressPortUserPwd su || string.IsNullOrWhiteSpace(c.UserName) || s is VNC)
                    return WithPasswordCheck(s);   // without a user in the file (or VNC, which uses only the password) the rest is enough
                if (Same(su.UserName, c.UserName)) return WithPasswordCheck(su);
                // the file's user is one of the entry's other users: connect with that one
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
            if (u.ActiveFolderCredentialIds.Count > 0) return s;   // the password comes from the folder credentials
            var clone = (ProtocolBaseWithAddressPortUserPwd)u.Clone();
            clone.AskPasswordWhenConnect = true;
            return clone;
        }

        // ---- export

        /// <summary>writes the .mora file of a server; the password never goes in it</summary>
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

        /// <summary>"Export shortcut": first the options (separate window), then the Windows dialog to choose where to save the .mora</summary>
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

        /// <summary>small window with the file options; null = cancelled, otherwise "open in a separate window"</summary>
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
