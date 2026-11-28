using System.Windows.Media;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View
{
    /// <summary>a tab in the title bar (Home, Settings, New connection...)</summary>
    public class MainTabItem : NotifyPropertyChangedBase
    {
        // session status dot: green connected, red disconnected (unreachable, dropped...), yellow while connecting or reconnecting
        private static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        private static readonly Brush Green = Frozen("#6CCB5F"), Red = Frozen("#F28B82"), Amber = Frozen("#F2C66D");
        private static Brush StatusOf(HostBase? h) => h == null ? Green : h.Status switch
        {
            ProtocolHostStatus.Connected => Green,
            ProtocolHostStatus.Disconnected => Red,
            _ => Amber,
        };
        public Brush StatusBrush => StatusOf(Host);
        public Brush Seg1StatusBrush => StatusOf(Host);
        public Brush Seg2StatusBrush => StatusOf(Seg2Host);
        /// <summary>second session of a split tab</summary>
        public HostBase? Seg2Host { get; set; }
        /// <summary>called when a session changes status: the dots are redrawn</summary>
        public void RefreshStatus()
        {
            RaisePropertyChanged(nameof(StatusBrush)); RaisePropertyChanged(nameof(Seg1StatusBrush)); RaisePropertyChanged(nameof(Seg2StatusBrush));
            RaisePropertyChanged(nameof(IsClaude));
        }

        /// <summary>a session of the tab is driven by Claude: yellow outline and "AI" mark</summary>
        public bool IsClaude => Host != null && Lemora.Service.Mcp.McpGuard.IsClaude(Host.ConnectionId)
                                || Seg2Host != null && Lemora.Service.Mcp.McpGuard.IsClaude(Seg2Host.ConnectionId);

        public string Title { get; set; } = "";
        private string? _toolTipText;
        /// <summary>tab tooltip (if empty, the title)</summary>
        public string ToolTipText { get => string.IsNullOrEmpty(_toolTipText) ? Title : _toolTipText!; set => _toolTipText = value; }
        public bool IsActive { get; set; }
        public bool CanClose { get; set; }
        /// <summary>"+" tab at the end: opens the quick launcher (fixed width)</summary>
        public bool IsAdd { get; set; }
        /// <summary>tab of an open session: shows the green "connected" dot</summary>
        public bool IsSession { get; set; }
        /// <summary>shows the "full screen" button (RDP sessions)</summary>
        public bool CanFullScreen { get; set; }
        public RelayCommand? FullScreenCommand { get; set; }
        /// <summary>click on the tab (null = it is already the open one)</summary>
        public RelayCommand? Command { get; set; }
        /// <summary>click on the ✕</summary>
        public RelayCommand? CloseCommand { get; set; }

        /// <summary>session of this tab (to drag it onto another tab and place them side by side)</summary>
        public Lemora.View.Host.ProtocolHosts.HostBase? Host { get; set; }

        // ---- split tab: two sessions side by side in the same tab (split view)
        public bool IsSplit { get; set; }
        public string Seg1Proto { get; set; } = "";
        public string Seg1Title { get; set; } = "";
        public string Seg2Proto { get; set; } = "";
        public string Seg2Title { get; set; } = "";
        public bool Seg1Active { get; set; }
        public bool Seg2Active { get; set; }
        public RelayCommand? Seg1Command { get; set; }
        public RelayCommand? Seg2Command { get; set; }
    }
}
