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
        private System.Windows.Forms.ContextMenuStrip? _trayMenu = null;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>right click on the icon: the items are filled in FIRST, so the size is the real one, and the menu opens above the icon</summary>
        private void TaskTrayIconOnMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Right) return;
            var menu = _trayMenu;
            if (menu == null) return;
            try
            {
                FillTaskTrayMenu(menu);
                menu.PerformLayout();
                var size = menu.PreferredSize;
                var cursor = Control.MousePosition;
                var wa = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
                var bounds = System.Windows.Forms.Screen.FromPoint(cursor).Bounds;
                int x, y;
                if (wa.Bottom < bounds.Bottom || wa.Top > bounds.Top || (wa.Left == bounds.Left && wa.Right == bounds.Right))
                {
                    // taskbar at the bottom (or top): centered on the icon, with the bottom edge just above it (or above the taskbar)
                    x = cursor.X - size.Width / 2;
                    y = wa.Top > bounds.Top ? wa.Top + 4 : Math.Min(cursor.Y - 8, wa.Bottom - 4) - size.Height;
                }
                else
                {
                    // taskbar on the left or right: next to the taskbar, at the height of the click
                    x = wa.Left > bounds.Left ? wa.Left + 4 : wa.Right - size.Width - 4;
                    y = cursor.Y - size.Height / 2;
                }
                x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - size.Width - 4));
                y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - size.Height - 4));
                SetForegroundWindow(menu.Handle);   // without it, a click outside the menu does not close it
                menu.Show(new System.Drawing.Point(x, y));
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning($"Tray menu: opening failed: {ex.Message}");
            }
        }
        public void TaskTrayInit()
        {
            Debug.Assert(Application.GetResourceStream(ResourceUriHelper.GetUriFromCurrentAssembly("LOGO.ico"))?.Stream != null);
#if DEBUG
            var icon = new System.Drawing.Icon(Application.GetResourceStream(ResourceUriHelper.GetUriFromCurrentAssembly("LOGO_D.ico")).Stream);
#else
            // notification area: icon for Windows light or dark theme, and it changes on its own if the user changes theme
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
                // double click = opens the app; right click opens the menu (the ContextMenuStrip does it on its own)
                _taskTrayIcon.MouseDoubleClick -= TaskTrayIconOnMouseDoubleClick;
                _taskTrayIcon.MouseDoubleClick += TaskTrayIconOnMouseDoubleClick;
                _taskTrayIcon.MouseUp -= TaskTrayIconOnMouseUp;
                _taskTrayIcon.MouseUp += TaskTrayIconOnMouseUp;
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
        /// Prepares the right-click menu. The items are rebuilt at every opening (Opening event):
        /// favorites and language are always up to date without having to remember to reload the menu.
        /// </summary>
        public void ReloadTaskTrayContextMenu()
        {
            Execute.OnUIThreadSync(() =>
            {
                if (_taskTrayIcon == null) return;

                var menu = new System.Windows.Forms.ContextMenuStrip();
                Lemora.Utils.TrayMenuRenderer.Apply(menu);
                // no filling here: at startup the data (and the favorites database) are not ready yet
                // WinForms cancels opening a menu with no items before notifying us: the placeholder item and Cancel=false are there for this
                menu.Items.Add(CreateExitItem());
                menu.Opening += (sender, args) =>
                {
                    args.Cancel = false;
                    FillTaskTrayMenu(menu);                };
                _trayMenu = menu;   // not assigned to the NotifyIcon: WinForms would position it before the items fix its size
            });

            // After startup and initializing our application and when closing our window and minimize the application to tray we free memory with the following line:
            System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
        }

        /// <summary>tests only (demo tray-exit): opens the tray menu off screen and clicks Exit, as the user would</summary>
        internal void ClickExitForTest()
        {
            Execute.OnUIThread(() =>
            {
                var menu = _trayMenu;
                if (menu == null) { Lemora.Utils.ExitDiag.Mark("test: tray menu missing"); return; }
                menu.Show(new System.Drawing.Point(-4000, -4000));
                var exit = menu.Items.OfType<System.Windows.Forms.ToolStripItem>().LastOrDefault(x => Equals(x.Tag, Lemora.Utils.TrayMenuRenderer.TagDanger));
                Lemora.Utils.ExitDiag.Mark($"test: menu opened with {menu.Items.Count} items, Exit found={exit != null}");
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
                SimpleLogHelper.Warning($"Tray menu: favorites not readable: {e.Message}");
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
            // with "Keep unlocked" active: close and forget the unlock (the next launch asks for fingerprint or password)
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
                Lemora.Utils.ExitDiag.Mark("tray: Exit clicked");
                IoC.Get<TaskTrayService>().TaskTrayDispose();
                Lemora.Utils.ExitDiag.Mark("tray: icon removed");
                App.Close();
            };
            return exit;
        }
        #endregion TaskTray

    }
}
