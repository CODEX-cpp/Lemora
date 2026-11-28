using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;

namespace Lemora.View.Host
{
    /// <summary>
    /// "Keys" button of the session bar (RDP and VNC): the keys of Settings › Key mapping for this connection only.
    /// A key set different from the protocol default becomes an override of the connection, saved with it and used again next time;
    /// set back to the default (or "Reset to default") it follows the page again
    /// </summary>
    public static class SessionKeysMenu
    {
        internal static ContextMenu? Last;   // for tests (menu screenshot)

        public static bool Supports(HostBase host) => host.ProtocolServer is RDP or VNC;

        public static void Show(FrameworkElement anchor, HostBase host)
        {
            var server = host.ProtocolServer;
            if (!Supports(host)) return;
            var vnc = server is VNC;
            const double width = 420;
            var menu = new ContextMenu
            {
                PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Width = width, HorizontalOffset = anchor.ActualWidth - width, VerticalOffset = 2, Padding = new Thickness(0),
            };

            TextBlock Txt(string text, double size, string brush, FontWeight? weight = null)
            {
                var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = weight ?? FontWeights.Normal };
                t.SetResourceReference(TextBlock.ForegroundProperty, brush);
                return t;
            }
            Border Rule() { var b = new Border { Height = 1 }; b.SetResourceReference(Border.BackgroundProperty, "IslandBorderBrush"); return b; }

            var root = new StackPanel();
            var head = Txt(IoC.Translate("keys_menu_title"), 11, "AccentMidBrush", FontWeights.SemiBold);
            head.Margin = new Thickness(16, 14, 16, 4);
            root.Children.Add(head);
            var note = Txt(string.Format(IoC.Translate("keys_menu_note"), server.DisplayName, vnc ? "VNC" : "RDP"), 12, "IslandSecondaryTextBrush");
            note.Margin = new Thickness(16, 0, 16, 10);
            root.Children.Add(note);
            root.Children.Add(Rule());

            var list = new StackPanel { Margin = new Thickness(16, 4, 16, 8) };
            var refreshers = new System.Collections.Generic.List<Action>();
            foreach (var group in KeyMap.All.GroupBy(k => k.Group))
            {
                var gh = Txt(group.Key, 11, "AccentMidBrush", FontWeights.SemiBold);
                gh.Margin = new Thickness(0, 10, 0, 4);
                list.Children.Add(gh);
                foreach (var def in group)
                {
                    var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var combo = Txt(def.Combo, 13, "PrimaryTextBrush", FontWeights.SemiBold);
                    combo.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    combo.TextWrapping = TextWrapping.NoWrap;
                    var changed = Txt(IoC.Translate("keys_changed"), 11, "AccentMidBrush");
                    changed.Margin = new Thickness(8, 0, 0, 0);
                    left.Children.Add(combo); left.Children.Add(changed);
                    left.ToolTip = def.Description;
                    var status = Txt("", 12, "IslandSecondaryTextBrush");
                    status.HorizontalAlignment = HorizontalAlignment.Right;
                    status.TextWrapping = TextWrapping.NoWrap;
                    var toggle = new CheckBox { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = !def.Fixed, Focusable = true };
                    toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
                    System.Windows.Automation.AutomationProperties.SetName(toggle, string.Format(IoC.Translate("keys_send_to_remote_name"), def.Combo));
                    var syncing = false;
                    void Sync()
                    {
                        syncing = true;
                        var on = KeyMap.ToRemote(server, def.Id);
                        toggle.IsChecked = on;
                        status.Text = IoC.Translate(on ? "keys_to_remote" : "keys_on_my_pc");
                        status.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentMidBrush" : "IslandSecondaryTextBrush");
                        changed.Visibility = KeyMap.ParseOverrides(KeyMap.OverridesOf(server)).ContainsKey(def.Id) ? Visibility.Visible : Visibility.Collapsed;
                        syncing = false;
                    }
                    toggle.Checked += (_, _) => { if (!syncing) { Set(server, def.Id, true); Sync(); } };
                    toggle.Unchecked += (_, _) => { if (!syncing) { Set(server, def.Id, false); Sync(); } };
                    refreshers.Add(Sync);
                    Sync();
                    Grid.SetColumn(status, 1); Grid.SetColumn(toggle, 2);
                    row.Children.Add(left); row.Children.Add(status); row.Children.Add(toggle);
                    list.Children.Add(row);
                }
            }
            var scroll = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
            root.Children.Add(scroll);
            root.Children.Add(Rule());

            var foot = new Grid { Margin = new Thickness(16, 10, 16, 12) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Children.Add(Txt(IoC.Translate("keys_saved_auto"), 12, "IslandSecondaryTextBrush"));
            var reset = Txt(IoC.Translate("keys_reset_default"), 12, "AccentMidBrush");
            reset.Cursor = System.Windows.Input.Cursors.Hand;
            reset.Margin = new Thickness(12, 0, 0, 0);
            reset.MouseLeftButtonUp += (_, _) =>
            {
                KeyMap.SetOverridesOf(server, "");
                SaveToConnection(server);
                foreach (var r in refreshers) r();
            };
            Grid.SetColumn(reset, 1); foot.Children.Add(reset);
            root.Children.Add(foot);

            // WPF wraps the content in a menu item and the theme highlights it on hover: the item becomes a plain container (as in the VNC "Fit" menu)
            var plainItem = new Style(typeof(MenuItem));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            plainItem.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter }));
            plainItem.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            plainItem.Setters.Add(new Setter(MenuItem.StaysOpenOnClickProperty, true));   // "Reset to default" must not close the panel
            menu.ItemContainerStyle = plainItem;
            menu.Items.Add(root);
            menu.Closed += (_, _) => host.FocusOnMe();   // the keyboard goes back to the remote screen
            Last = menu;
            menu.IsOpen = true;
        }

        /// <summary>key set by hand in this connection: equal to the default = no override</summary>
        internal static void Set(ProtocolBase server, string id, bool toRemote)
        {
            var d = KeyMap.ParseOverrides(KeyMap.OverridesOf(server));
            d.Remove(id);
            if (toRemote != KeyMap.Default(server is VNC, id)) d[id] = toRemote;
            KeyMap.SetOverridesOf(server, KeyMap.FormatOverrides(d));
            SaveToConnection(server);
        }

        /// <summary>the session works on a copy of the connection: the overrides are written to the saved one too (as RdpSessionSettingsSaver does)</summary>
        private static void SaveToConnection(ProtocolBase session)
        {
            try
            {
                var data = IoC.Get<GlobalData>();
                if (data.GetItemById(session.DataSource?.DataSourceName ?? "", session.Id)?.Server is not { } target || (target is not RDP && target is not VNC))
                {
                    SimpleLogHelper.Info($"Keys changed for '{session.DisplayName}' but it is not a saved entry: they apply to this session only");
                    return;
                }
                KeyMap.SetOverridesOf(target, KeyMap.OverridesOf(session) ?? "");
                if (target.DataSource is Lemora.Service.DataSource.Model.TeamsSource)
                {
                    // shared Teams entry: personal settings stay on THIS PC, never sent to the server
                    const string prefix = "teams-";
                    if (target.Id.StartsWith(prefix)) Lemora.Service.DataSource.Model.TeamsOverlay.Save(target.Id.Substring(prefix.Length), target);
                    return;
                }
                if (target.DataSource?.IsWritable != true)
                {
                    SimpleLogHelper.Info($"Keys changed for '{session.DisplayName}' but its source is not writable: they apply to this session only");
                    return;
                }
                var r = data.UpdateServer(target);
                if (!r.IsSuccess) SimpleLogHelper.Warning("Connection keys not saved: " + r.ErrorInfo);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("SessionKeysMenu: " + e.Message);
            }
        }
    }
}
