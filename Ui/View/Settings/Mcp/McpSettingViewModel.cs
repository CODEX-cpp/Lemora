using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service.Mcp;
using Lemora.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.Settings.Mcp
{
    /// <summary>
    /// A row of the "What Claude can use" tree: "All connections" (at the top), folder or connection. Each row inherits its check from the row above;
    /// clearing or setting it creates an exception (an excluded folder, a server allowed inside an excluded folder...). Entries and folders added later
    /// follow the folder that contains them. Changing a folder clears the exceptions inside it: all its content follows it.
    /// </summary>
    public class McpNode : NotifyPropertyChangedBase
    {
        private readonly McpSettingViewModel _owner;
        public McpNode(McpSettingViewModel owner) => _owner = owner;

        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Path { get; init; } = "";
        public McpNode? Parent { get; init; }
        public ProtocolBase? Server { get; init; }
        public bool IsRoot { get; init; }
        public bool IsFolder => Server == null && !IsRoot;
        public bool IsBold => Server == null;
        public ObservableCollection<McpNode> Children { get; } = new();

        private IReadOnlyList<string> Nodes => Path.Split('/');
        private bool TeamsBlocked => Server != null && _owner.Teams.Blocks(Server);

        /// <summary>what would apply without a choice on this row: the row above</summary>
        private bool Inherited => IsRoot ? true : Parent?.Effective ?? McpGuard.Settings.All;

        private bool Effective
        {
            get
            {
                var s = McpGuard.Settings;
                if (IsRoot) return s.All;
                if (Server != null) return McpGuard.ServerAllowed(Server);
                return McpGuard.FolderAllowed(Nodes);
            }
        }

        public bool IsChecked
        {
            get => !_owner.Teams.BlocksAll && !TeamsBlocked && Effective;
            set
            {
                var s = McpGuard.Settings;
                if (IsRoot)
                {
                    s.All = value;
                    s.Folders.Clear(); s.ExcludedFolders.Clear(); s.Servers.Clear(); s.Excluded.Clear();   // start again from a single rule
                }
                else if (Server != null)
                {
                    var id = Server.Id;
                    s.Servers.Remove(id); s.Excluded.Remove(id);
                    if (value != Inherited) (value ? s.Servers : s.Excluded).Add(id);
                }
                else
                {
                    // the exceptions inside the folder are cleared: all its content follows the new choice
                    var inside = Path + "/";
                    s.Folders.RemoveWhere(f => f.Equals(Path, StringComparison.OrdinalIgnoreCase) || f.StartsWith(inside, StringComparison.OrdinalIgnoreCase));
                    s.ExcludedFolders.RemoveWhere(f => f.Equals(Path, StringComparison.OrdinalIgnoreCase) || f.StartsWith(inside, StringComparison.OrdinalIgnoreCase));
                    foreach (var srv in Servers()) { s.Servers.Remove(srv.Id); s.Excluded.Remove(srv.Id); }
                    if (value != Inherited) (value ? s.Folders : s.ExcludedFolders).Add(Path);
                }
                _owner.Changed();
            }
        }

        private IEnumerable<ProtocolBase> Servers() => Children.SelectMany(c => c.Server != null ? new[] { c.Server } : c.Servers());

        public bool CanChange => !_owner.Teams.BlocksAll && !TeamsBlocked;

        public string Note => TeamsBlocked ? IoC.Translate("mcp_note_teams_blocked")
                            : IsRoot ? IoC.Translate(McpGuard.Settings.All ? "mcp_note_all_later" : "mcp_note_only_checked")
                            : Effective == Inherited ? ""
                            : IoC.Translate(Effective ? "mcp_note_allowed_apart" : "mcp_note_excluded");

        private Visibility _visibility = Visibility.Visible;
        public Visibility Visibility { get => _visibility; set => SetAndNotifyIfChanged(ref _visibility, value); }

        public void Refresh()
        {
            RaisePropertyChanged(nameof(IsChecked));
            RaisePropertyChanged(nameof(CanChange));
            RaisePropertyChanged(nameof(Note));
            foreach (var c in Children) c.Refresh();
        }

        /// <summary>visible if the text is in the name or the detail, or in something it contains</summary>
        public bool ApplyFilter(string filter)
        {
            var any = false;
            foreach (var c in Children) any |= c.ApplyFilter(filter);
            var me = filter.Length == 0 || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
            if (me && filter.Length > 0) foreach (var c in Children) c.ShowAll();   // folder found: everything it contains is shown
            Visibility = me || any ? Visibility.Visible : Visibility.Collapsed;
            return me || any;
        }

        private void ShowAll() { Visibility = Visibility.Visible; foreach (var c in Children) c.ShowAll(); }
    }

    /// <summary>"MCP server" settings page: switch, port, token, command for Claude Code and what Claude can use</summary>
    public class McpSettingViewModel : NotifyPropertyChangedBase
    {
        public TeamsClaudePolicy.Snapshot Teams { get; private set; } = new();
        public ObservableCollection<McpNode> Nodes { get; } = new();

        public void Reload()
        {
            Teams = TeamsClaudePolicy.Current();
            Nodes.Clear();
            var root = new McpNode(this) { Name = IoC.Translate("mcp_all_connections"), IsRoot = true };
            // RDP only: VNC connections are allowed all together with the "Enable control of VNC connections" switch
            var servers = IoC.Get<GlobalData>().VmItemList.Select(v => v.Server).Where(s => s is RDP && !s.IsTmpSession())
                .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            var folders = new Dictionary<string, McpNode>(StringComparer.OrdinalIgnoreCase);
            McpNode Folder(IReadOnlyList<string> nodes, int depth)
            {
                var path = string.Join("/", nodes.Take(depth));
                if (folders.TryGetValue(path, out var f)) return f;
                var parent = depth > 1 ? Folder(nodes, depth - 1) : root;
                f = new McpNode(this) { Name = nodes[depth - 1], Path = path, Parent = parent };
                folders[path] = f;
                parent.Children.Add(f);
                return f;
            }
            foreach (var s in servers)
            {
                var tree = s.TreeNodes ?? new List<string>();
                var parent = tree.Count > 0 ? Folder(tree, tree.Count) : root;
                var detail = $"{s.Protocol}{(s is ProtocolBaseWithAddressPort p ? " · " + p.Address : "")}{(TeamsClaudePolicy.IsTeamsEntry(s) ? " · Teams" : "")}";
                parent.Children.Add(new McpNode(this) { Name = s.DisplayName, Detail = detail, Server = s, Parent = parent });
            }
            Nodes.Add(root);
            // folders before connections, in alphabetical order
            void Sort(ObservableCollection<McpNode> list)
            {
                var sorted = list.OrderBy(n => n.Server == null ? 0 : 1).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                list.Clear();
                foreach (var n in sorted) { list.Add(n); Sort(n.Children); }
            }
            Sort(Nodes);
            ApplyFilter();
            RaiseAll();
        }

        private void RaiseAll()
        {
            foreach (var n in new[] { nameof(Enabled), nameof(Locked), nameof(LockText), nameof(StatusText), nameof(StatusOk), nameof(PortText),
                                      nameof(TokenText), nameof(CommandText), nameof(AllowAddresses), nameof(AllowVnc), nameof(CanEdit), nameof(ClaudeLinked), nameof(ClaudeLinkText) })
                RaisePropertyChanged(n);
        }

        /// <summary>a check changed: save and redraw everything (a folder changes the state of what it contains)</summary>
        public void Changed()
        {
            McpGuard.Save();
            foreach (var n in Nodes) n.Refresh();
        }

        public bool Locked => Teams.BlocksAll;
        public bool CanEdit => !Locked;
        public string LockText => IoC.Translate("mcp_lock_text");

        public bool Enabled
        {
            get => McpGuard.Settings.Enabled && !Locked;
            set
            {
                if (Locked) return;
                McpGuard.Settings.Enabled = value;
                McpGuard.Save();
                McpServer.ApplySettings();
                McpGuard.Log(value ? "MCP server turned on from the settings" : "MCP server turned off from the settings");
                RaiseAll();
            }
        }

        public bool StatusOk => McpServer.IsRunning;
        public string StatusText =>
            Locked ? IoC.Translate("mcp_status_off_teams")
            : !McpGuard.Settings.Enabled ? IoC.Translate("mcp_status_off")
            : McpServer.IsRunning ? string.Format(IoC.Translate("mcp_status_running"), $"http://localhost:{McpServer.Port}/mcp")
            : string.Format(IoC.Translate("mcp_status_not_started"), McpGuard.Settings.Port, McpServer.LastError);

        public string PortText
        {
            get => McpGuard.Settings.Port.ToString();
            set
            {
                if (!int.TryParse(value, out var p) || p is < 1025 or > 65535 || p == McpGuard.Settings.Port) { RaisePropertyChanged(); return; }
                McpGuard.Settings.Port = p;
                McpGuard.Save();
                if (McpGuard.Settings.Enabled) McpServer.ApplySettings();
                ClaudeCodeConfig.UpdateIfLinked();
                RaiseAll();
            }
        }

        private bool _tokenShown;
        public string TokenText => _tokenShown ? McpGuard.Token : new string('•', 30);
        private string Url => $"http://localhost:{McpGuard.Settings.Port}/mcp";
        private string Command(string token) => $"claude mcp add --transport http lemora {Url} --header \"Authorization: Bearer {token}\"";
        public string CommandText => Command(_tokenShown ? McpGuard.Token : "••••");

        /// <summary>"Stop Claude" box above other programs when Lemora is not in the foreground</summary>
        public bool BannerEnabled
        {
            get => McpGuard.Settings.BannerEnabled;
            set { McpGuard.SetBannerEnabled(value); RaisePropertyChanged(); }
        }

        public bool AllowVnc
        {
            get => McpGuard.Settings.AllowVnc && !Locked;
            set { McpGuard.Settings.AllowVnc = value; McpGuard.Save(); McpGuard.Log(value ? "control of VNC connections enabled" : "control of VNC connections disabled"); RaisePropertyChanged(); }
        }

        public bool AllowAddresses
        {
            get => McpGuard.Settings.AllowAddresses && !Locked;
            set { McpGuard.Settings.AllowAddresses = value; McpGuard.Save(); RaisePropertyChanged(); }
        }

        private string _filter = "";
        public string Filter { get => _filter; set { if (SetAndNotifyIfChanged(ref _filter, value ?? "")) ApplyFilter(); } }
        private void ApplyFilter() { foreach (var n in Nodes) n.ApplyFilter(_filter.Trim()); }

        private static void Copy(string text)
        {
            // the token is a secret: no clipboard history/cloud, and cleared after 30 s like passwords (security hardening)
            Lemora.View.Host.SecretClipboard.Set(text, Lemora.Service.DevicePasswordService.ClipboardClearSeconds);
        }

        private RelayCommand? _cmdShowToken, _cmdCopyToken, _cmdRegenerate, _cmdCopyCommand;
        public RelayCommand CmdShowToken => _cmdShowToken ??= new RelayCommand(_ => { _tokenShown = !_tokenShown; RaiseAll(); RaisePropertyChanged(nameof(ShowTokenLabel)); });
        public string ShowTokenLabel => IoC.Translate(_tokenShown ? "mcp_token_hide" : "mcp_token_show");
        public RelayCommand CmdCopyToken => _cmdCopyToken ??= new RelayCommand(_ => Copy(McpGuard.Token));
        public RelayCommand CmdCopyCommand => _cmdCopyCommand ??= new RelayCommand(_ => Copy(Command(McpGuard.Token)));
        public RelayCommand CmdRegenerate => _cmdRegenerate ??= new RelayCommand(_ =>
        {
            if (!Lemora.Utils.MessageBoxHelper.Confirm(IoC.Translate("mcp_regen_confirm"), IoC.Translate("mcp_regen_button"), IoC.Translate("Cancel"), IoC.Translate("mcp_server_title"))) return;
            McpGuard.RegenerateToken();
            ClaudeCodeConfig.UpdateIfLinked();   // linked: Claude Code gets the new token right away
            RaiseAll();
        });

        // ---------------------------------------------------------------- link to Claude Code (.claude.json file)

        private string _linkError = "";
        public bool ClaudeLinked => ClaudeCodeConfig.IsLinked(out _);
        public string ClaudeLinkText =>
            _linkError.Length > 0 ? string.Format(IoC.Translate("mcp_link_failed"), _linkError)
            : ClaudeCodeConfig.IsLinked(out var current)
                ? IoC.Translate(current ? "mcp_link_ok" : "mcp_link_outdated")
                : string.Format(IoC.Translate("mcp_link_none"), ClaudeCodeConfig.FilePath);

        private RelayCommand? _cmdLink, _cmdUnlink;
        public RelayCommand CmdLinkClaude => _cmdLink ??= new RelayCommand(_ => { _linkError = ClaudeCodeConfig.Link() ?? ""; RaiseLink(); });
        public RelayCommand CmdUnlinkClaude => _cmdUnlink ??= new RelayCommand(_ => { _linkError = ClaudeCodeConfig.Unlink() ?? ""; RaiseLink(); });
        private void RaiseLink() { RaisePropertyChanged(nameof(ClaudeLinked)); RaisePropertyChanged(nameof(ClaudeLinkText)); }
    }
}
