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
    /// <summary>riepilogo di sola lettura della cartella scelta, mostrato nel pannello destro della Home</summary>
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
        public string CredentialsHeader { get; }
        public string CredentialsNames { get; }
        public string DevicesHeader { get; }
        public string DevicesNames { get; }
        public RelayCommand CmdEdit { get; }
        public RelayCommand CmdAddServer { get; }

        public static string NetworkText(string prefix) =>
            prefix.Length == 0 ? "-" : prefix.EndsWith('.') && prefix.Count(c => c == '.') == 3 ? prefix + "0/24" : prefix;

        public FolderSummaryViewModel(string path, Action edit, Action addServer)
        {
            Path = path;
            Name = FolderService.Leaf(path);
            var s = FolderService.GetSettings(path);
            Domain = s.Domain.Length > 0 ? s.Domain : "-";
            // "10.0.4." (prefisso salvato) si mostra come la rete: 10.0.4.0/24
            Network = NetworkText(s.IpPrefix);
            ServerCount = FolderService.CountServers(path);
            SubfolderCount = FolderService.AllPaths().Count(x => string.Equals(FolderService.Parent(x), path, StringComparison.CurrentCultureIgnoreCase));
            Subtitle = string.Format(SetupText.T(SubfolderCount == 1 ? "fs_sub_count_one" : "fs_sub_count_many"), ServerCount, SubfolderCount);
            var creds = FolderCredentialService.ForFolder(path).Select(c => c.UserName.Contains('\\') ? c.UserName.Substring(c.UserName.LastIndexOf('\\') + 1) : c.UserName).ToList();
            CredentialsHeader = string.Format(SetupText.T("fs_creds"), creds.Count);
            CredentialsNames = creds.Count > 0 ? string.Join(" · ", creds) : SetupText.T("fs_none");
            var devices = DevicePasswordService.All().Where(x => string.Equals(x.Folder, path, StringComparison.CurrentCultureIgnoreCase))
                .Select(x => x.Name).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            DevicesHeader = string.Format(SetupText.T("fs_devices"), devices.Count);
            DevicesNames = devices.Count > 0 ? string.Join(" · ", devices) : SetupText.T("fs_none");
            CmdEdit = new RelayCommand(_ => edit());
            CmdAddServer = new RelayCommand(_ => addServer());
        }
    }
}
