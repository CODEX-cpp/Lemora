using Lemora.Service;
using Lemora.Setup;
using Lemora.View.Editor;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Lemora.View.ServerView
{
    /// <summary>read-only summary of the chosen folder, shown in the Home right panel</summary>
    public class FolderSummaryViewModel : NotifyPropertyChangedBase
    {
        public TextProxy Tx { get; } = new();
        public string Path { get; }
        public string Name { get; }
        public string Initial => string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1).ToUpper();
        public string Subtitle { get; }
        public string Domain { get; }
        public string Network { get; }
        public int ServerCount { get; }
        public int SubfolderCount { get; }
        public int CredentialsCount { get; }
        public int DevicesCount { get; }
        public RelayCommand CmdEdit { get; }
        public RelayCommand CmdAddServer { get; }

        public static string NetworkText(string prefix) =>
            prefix.Length == 0 ? "-" : prefix.EndsWith('.') && prefix.Count(c => c == '.') == 3 ? prefix + "0/24" : prefix;

        /// <param name="canEdit">false if the folder contains read-only Teams entries: "Edit" stays disabled (like the menu, which does not show it)</param>
        public FolderSummaryViewModel(string path, Action edit, Action addServer, bool canEdit = true)
        {
            Path = path;
            Name = FolderService.Leaf(path);
            var s = FolderService.GetSettings(path);
            Domain = s.Domain.Length > 0 ? s.Domain : "-";
            // "10.0.4." (saved prefix) is shown as the network: 10.0.4.0/24
            Network = NetworkText(s.IpPrefix);
            ServerCount = FolderService.CountDirectServers(path);
            SubfolderCount = FolderService.AllPaths().Count(x => string.Equals(FolderService.Parent(x), path, StringComparison.CurrentCultureIgnoreCase));
            Subtitle = string.Format(SetupText.T(SubfolderCount == 1 ? "fs_sub_count_one" : "fs_sub_count_many"), ServerCount, SubfolderCount);
            CredentialsCount = FolderCredentialService.ForFolder(path).Count;
            DevicesCount = DevicePasswordService.All().Count(x => string.Equals(x.Folder, path, StringComparison.CurrentCultureIgnoreCase));
            CmdEdit = new RelayCommand(_ => edit(), _ => canEdit);
            CmdAddServer = new RelayCommand(_ => addServer());
        }
    }
}
