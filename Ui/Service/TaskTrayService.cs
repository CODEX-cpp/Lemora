using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Lemora.Model;
using Lemora.Utils;
using Lemora.View;
using Dragablz.Dockablz;
using Shawn.Utils;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.Controls;
using Stylet;
using Application = System.Windows.Application;

namespace Lemora.Service
{
    public class TaskTrayService
    {

        ~TaskTrayService()
        {
            TaskTrayDispose();
        }

        #region TaskTray

        private System.Windows.Forms.NotifyIcon? _taskTrayIcon = null;
        public void TaskTrayInit()
        {
            Debug.Assert(Application.GetResourceStream(ResourceUriHelper.GetUriFromCurrentAssembly("LOGO.ico"))?.Stream != null);
#if DEBUG
            var icon = new System.Drawing.Icon(Application.GetResourceStream(ResourceUriHelper.GetUriFromCurrentAssembly("LOGO_D.ico")).Stream);
#else
            // area di notifica: icona per tema chiaro o scuro di Windows, e cambia da sola se l'utente cambia tema
            var icon = AppIcons.TrayIcon();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnWindowsThemeChanged;
#endif
            Debug.Assert(icon != null);
            Execute.OnUIThreadSync(() =>
            {
                _taskTrayIcon ??= new System.Windows.Forms.NotifyIcon
                {
                    Text = Assert.APP_DISPLAY_NAME,
                    BalloonTipText = "",
                    Icon = icon,
                    Visible = true
                };
                _taskTrayIcon.Visible = false;
                ReloadTaskTrayContextMenu();
                GlobalEventHelper.OnLanguageChanged -= ReloadTaskTrayContextMenu;
                GlobalEventHelper.OnLanguageChanged += ReloadTaskTrayContextMenu;
                // doppio clic = apre l'app; il tasto destro apre il menu (lo fa da solo la ContextMenuStrip)
                _taskTrayIcon.MouseDoubleClick -= TaskTrayIconOnMouseDoubleClick;
                _taskTrayIcon.MouseDoubleClick += TaskTrayIconOnMouseDoubleClick;
                _taskTrayIcon.Visible = true;
            });
        }

        private void OnWindowsThemeChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            Execute.OnUIThread(() =>
            {
                if (_taskTrayIcon != null) _taskTrayIcon.Icon = AppIcons.TrayIcon();
            });
        }

        private void TaskTrayIconOnMouseDoubleClick(object? sender, MouseEventArgs e)
        {
            if (_taskTrayIcon?.Visible == true
                && e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                IoC.Get<MainWindowViewModel>()?.ShowMe();
            }
        }

        public void TaskTrayDispose()
        {
            // Unsubscribe from static events to prevent memory leaks
            GlobalEventHelper.OnLanguageChanged -= ReloadTaskTrayContextMenu;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;

            if (_taskTrayIcon != null)
            {
                _taskTrayIcon.Visible = false;
                _taskTrayIcon.Dispose();
                _taskTrayIcon = null;
            }
        }


        /// <summary>
        /// Prepara il menu del tasto destro. Le voci si ricostruiscono a ogni apertura (evento Opening):
        /// preferiti e lingua sono sempre aggiornati senza dover ricordare di ricaricare il menu.
        /// </summary>
        public void ReloadTaskTrayContextMenu()
        {
            Execute.OnUIThreadSync(() =>
            {
                if (_taskTrayIcon == null) return;

                var menu = new System.Windows.Forms.ContextMenuStrip();
                Lemora.Utils.TrayMenuRenderer.Apply(menu);
                // niente riempimento qui: all'avvio i dati (e il database dei preferiti) non sono ancora pronti
                // WinForms annulla l'apertura di un menu senza voci prima di avvisarci: la voce segnaposto e Cancel=false servono a questo
                menu.Items.Add(CreateExitItem());
                menu.Opening += (sender, args) =>
                {
                    args.Cancel = false;
                    FillTaskTrayMenu(menu);                };
                _taskTrayIcon.ContextMenuStrip = menu;
            });

            // After startup and initializing our application and when closing our window and minimize the application to tray we free memory with the following line:
            System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
        }

        /// <summary>solo prove (demo tray-exit): apre il menu della tray fuori dallo schermo e clicca Esci, come farebbe l'utente</summary>
        internal void ClickExitForTest()
        {
            Execute.OnUIThread(() =>
            {
                var menu = _taskTrayIcon?.ContextMenuStrip;
                if (menu == null) { Lemora.Utils.ExitDiag.Mark("prova: menu della tray assente"); return; }
                menu.Show(new System.Drawing.Point(-4000, -4000));
                var exit = menu.Items.OfType<System.Windows.Forms.ToolStripItem>().LastOrDefault(x => Equals(x.Tag, Lemora.Utils.TrayMenuRenderer.TagDanger));
                Lemora.Utils.ExitDiag.Mark($"prova: menu aperto con {menu.Items.Count} voci, Esci trovato={exit != null}");
                exit?.PerformClick();
            });
        }

        private const int MaxFavorites = 15;

        private static void FillTaskTrayMenu(System.Windows.Forms.ContextMenuStrip menu)
        {
            menu.Items.Clear();

            menu.Items.Add(Lemora.Utils.TrayMenuRenderer.Header(IoC.Translate("tray_favorites")));
            ProtocolBaseViewModel[] favorites;
            try
            {
                favorites = IoC.Get<GlobalData>().VmItemList
                    .Where(x => x.IsFavorite)
                    .OrderBy(x => x.Server.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Take(MaxFavorites)
                    .ToArray();
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning($"Menu vassoio: preferiti non leggibili: {e.Message}");
                favorites = Array.Empty<ProtocolBaseViewModel>();
            }
            if (favorites.Length == 0)
                menu.Items.Add(Lemora.Utils.TrayMenuRenderer.Hint(IoC.Translate("tray_no_favorites")));
            for (var i = 0; i < favorites.Length; i++)
            {
                var server = favorites[i].Server;
                var name = server.DisplayName.Length > 30 ? server.DisplayName.Substring(0, 30) + "..." : server.DisplayName;
                var item = new Lemora.Utils.TrayMenuRenderer.ProtocolItem(server.ProtocolDisplayName, name, server.SubTitle);
                item.Click += (sender, args) => GlobalEventHelper.OnRequestServerConnect?.Invoke(server, fromView: "Tray");
                if (i > 0) menu.Items.Add("-");
                menu.Items.Add(item);
            }

            menu.Items.Add("-");
            var add = new Lemora.Utils.TrayMenuRenderer.ProtocolItem("", IoC.Translate("tray_new_connection")) { IsGlyph = true, LeadColor = System.Drawing.Color.FromArgb(0x60, 0xB4, 0xFF), Bold = true };
            add.Click += (sender, args) =>
            {
                var main = IoC.Get<MainWindowViewModel>();
                main.ShowMe(true);
                main.ShortcutAddServer();
            };
            menu.Items.Add(add);

            menu.Items.Add("-");
            var settings = new Lemora.Utils.TrayMenuRenderer.ProtocolItem("", IoC.Translate("settings_title")) { IsGlyph = true, LeadColor = System.Drawing.Color.FromArgb(0x9A, 0x9A, 0x9A) };
            settings.Click += (sender, args) => IoC.Get<MainWindowViewModel>().ShowMe(true, EnumMainWindowPage.SettingsGeneral);
            menu.Items.Add(settings);

            menu.Items.Add("-");
            // con "Mantieni sbloccata" attivo: chiudere e dimenticare lo sblocco (il prossimo avvio chiede impronta o password)
            if (IoC.Get<ConfigurationService>().General.VaultKeepUnlockedMinutes > 0)
            {
                var lockItem = new Lemora.Utils.TrayMenuRenderer.ProtocolItem("", IoC.Translate("vault_lock_close")) { IsGlyph = true, LeadColor = System.Drawing.Color.FromArgb(0x9A, 0x9A, 0x9A) };
                lockItem.Click += (sender, args) =>
                {
                    VaultUnlock.Forget();
                    IoC.Get<TaskTrayService>().TaskTrayDispose();
                    App.Close();
                };
                menu.Items.Add(lockItem);
            }
            menu.Items.Add(CreateExitItem());
            Lemora.Utils.TrayMenuRenderer.EqualizeWidths(menu);
        }

        private static Lemora.Utils.TrayMenuRenderer.ProtocolItem CreateExitItem()
        {
            var exit = new Lemora.Utils.TrayMenuRenderer.ProtocolItem("", IoC.Translate("Exit")) { IsGlyph = true, LeadColor = System.Drawing.Color.FromArgb(0xFF, 0x9B, 0x94), Bold = true, Tag = Lemora.Utils.TrayMenuRenderer.TagDanger };
            exit.Click += (sender, args) =>
            {
                Lemora.Utils.ExitDiag.Mark("tray: clic su Esci");
                IoC.Get<TaskTrayService>().TaskTrayDispose();
                Lemora.Utils.ExitDiag.Mark("tray: icona tolta");
                App.Close();
            };
            return exit;
        }
        #endregion TaskTray

    }
}
