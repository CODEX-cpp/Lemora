using System;
using System.Linq;
using System.Windows;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View
{
    /// <summary>data and commands of the bar below the tabs, for the session being shown</summary>
    public class SessionBarViewModel : NotifyPropertyChangedBase
    {
        public HostBase Host { get; }
        private readonly MainWindowViewModel _main;

        public SessionBarViewModel(HostBase host, MainWindowViewModel main)
        {
            Host = host;
            _main = main;
            CmdDetach = new RelayCommand(_ => _main.DetachSession(Host, false));
            CmdPip = new RelayCommand(_ => _main.DetachSession(Host, true));
            CmdReattach = new RelayCommand(_ => _main.ReattachSession(Host));
            CmdShowWindow = new RelayCommand(_ => _main.ShowDetachedWindow(Host));
            CmdFullScreen = new RelayCommand(_ => { if (Host.CanFullScreen && Host.CanResizeNow()) _main.FullScreenSession(Host); });
            CmdCtrlAltDel = new RelayCommand(_ => FindCtrlAltDel()?.Command?.Execute(null));
            CmdReconnect = new RelayCommand(_ => Host.ReConn());            CmdDisplaySettings = new RelayCommand(_ => _main.ShowSessionRdpSettings(Host, Lemora.View.Host.SessionRdpSettingsKind.Display));
            CmdAdvancedSettings = new RelayCommand(_ => _main.ShowSessionRdpSettings(Host, Lemora.View.Host.SessionRdpSettingsKind.Advanced));
            CmdFit = new RelayCommand(o => { if (o is FrameworkElement fe) Host.ShowFitMenu(fe); });
            CmdKeys = new RelayCommand(o => { if (o is FrameworkElement fe) Lemora.View.Host.SessionKeysMenu.Show(fe, Host); });
            Host.ViewControlsChanged += () => { RaisePropertyChanged(nameof(HasViewControls)); RaisePropertyChanged(nameof(FitLabel)); RaisePropertyChanged(nameof(HasScreenGroup)); };
            CmdPin = new RelayCommand(_ => _main.TogglePin());
            if (Host is TerminalHost th) th.HistoryChanged += () => RaisePropertyChanged(nameof(IsHistoryOpen));
            ResolveMachineName();
            CmdCopyAddress = new RelayCommand(_ => { try { Clipboard.SetText(AddressText); Lemora.Service.ToastService.Show(string.Format(IoC.Translate("toast_address_copied"), AddressText)); } catch { /* clipboard held by another program */ } });
        }

        public string ProtocolText => (Host.ProtocolServer.ProtocolDisplayName ?? "").ToUpperInvariant();
        public string Protocol => Host.ProtocolServer.ProtocolDisplayName ?? "";
        public string Name => Host.ProtocolServer.DisplayName;

        public string AddressText => Host.ProtocolServer is ProtocolBaseWithAddressPort a
            ? (string.IsNullOrEmpty(a.Port) ? a.Address : $"{a.Address}:{a.Port}")
            : Host.ProtocolServer.SubTitle;

        public string SubText
        {
            get
            {
                var user = Host.ProtocolServer is ProtocolBaseWithAddressPortUserPwd u ? u.UserName : "";
                if (IsTerminal) return string.IsNullOrEmpty(user) ? AddressText : $"{user}@{AddressText}";   // terminal: user@host:port as in the mockup
                var address = Host.ProtocolServer is ProtocolBaseWithAddressPort a ? a.Address : AddressText;   // without :port
                var parts = new[] { address, MachineName, user }.Where(x => !string.IsNullOrEmpty(x));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>remote PC name (RDP certificate or reverse DNS), available after connecting</summary>
        public string? MachineName { get; private set; }

        private async void ResolveMachineName()
        {
            if (Host.ProtocolServer is not ProtocolBaseWithAddressPort a || string.IsNullOrEmpty(a.Address)) return;
            int.TryParse(a.Port, out var port);
            var name = await Lemora.Utils.MachineNameResolver.ResolveAsync(a.Address, port, Host.ProtocolServer is RDP, (Host.ProtocolServer as RDP)?.Domain);
            if (string.IsNullOrEmpty(name)) return;
            MachineName = name;
            Application.Current?.Dispatcher.BeginInvoke(() => RaisePropertyChanged(nameof(SubText)));
        }

        // ---- built-in SSH terminal
        private TerminalHost? Term => Host as TerminalHost;
        public bool IsTerminal => Term != null;
        public bool IsNotTerminal => Term == null;
        public bool HasFingerprint => !string.IsNullOrEmpty(Term?.Fingerprint);
        public string FingerprintTooltip => string.Format(IoC.Translate("term_fingerprint_tip"), Term?.Fingerprint);
        public bool IsHistoryOpen => Term?.IsHistoryOpen == true;
        public RelayCommand CmdTerminalSettings { get; } = new RelayCommand(_ => TerminalSettingsWindow.ShowSingle(Application.Current?.MainWindow));
        public RelayCommand CmdHistory => new RelayCommand(_ => Term?.ToggleHistory());
        public RelayCommand CmdSearch => new RelayCommand(_ => Term?.ShowSearch());

        // ---- split view (any protocol)
        public bool IsSplit => _main.IsSplit(Host);
        /// <summary>RDP and VNC open a single session per server: the "Split" button appears only when the tab is already split (to unsplit it); they are placed side by side by dragging one tab onto the other</summary>
        public bool ShowSplitButtonNonTerminal => IsNotTerminal && (IsSplit || !Host.ProtocolServer.IsOnlyOneInstance());
        public RelayCommand CmdSplit => new RelayCommand(_ => _main.SplitCurrent());
        public RelayCommand CmdOpenSftp => new RelayCommand(_ =>
        {
            if (Host.ProtocolServer is not SSH ssh) return;
            var sftp = new SFTP { ColorHex = ssh.ColorHex, IconBase64 = ssh.IconBase64, DisplayName = ssh.DisplayName + " (SFTP)", Address = ssh.Address, Port = ssh.Port, UserName = ssh.UserName, Password = ssh.Password, PrivateKey = ssh.PrivateKey };
            GlobalEventHelper.OnRequestServerConnect?.Invoke(sftp, fromView: "SessionBar");
        });

        // ---- file transfer (VNC only)
        public Lemora.View.FileTransfer.FileTransferViewModel? FileTransfer => (Host as VncHost)?.FileTransferVm;
        public bool HasFileTransfer => Host is VncHost;

        public bool IsConnected => Host.Status == ProtocolHostStatus.Connected;
        /// <summary>disconnected (never connected, or dropped or unreachable): red dot</summary>
        public bool IsError => Host.Status == ProtocolHostStatus.Disconnected;
        /// <summary>reconnecting after a drop: yellow dot</summary>
        public bool IsReconnecting => Host.HasConnected && Host.Status != ProtocolHostStatus.Connected && Host.Status != ProtocolHostStatus.Disconnected;

        public string StatusText
        {
            get
            {
                switch (Host.Status)
                {
                    case ProtocolHostStatus.Connected:
                        var t = DateTime.Now - (Host.ConnectedAt ?? DateTime.Now);
                        return $"{IoC.Translate("session_connected")} · {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
                    case ProtocolHostStatus.Disconnected:
                        return IoC.Translate("session_disconnected");
                    default:
                        return IoC.Translate("session_connecting");
                }
            }
        }

        public bool CanFullScreenButton => Host.CanFullScreen && !IsDetached;
        public bool CanCtrlAltDel => FindCtrlAltDel() != null;
        public bool IsDetached => _main.IsDetached(Host);

        private System.Windows.Controls.MenuItem? FindCtrlAltDel()
            => Host.MenuItems.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(x => x.Header as string == "Ctrl + Alt + Del");

        public bool CanChangeDisplayMode => Host.CanChangeDisplayModeLive;
        public RelayCommand CmdDisplaySettings { get; }
        public RelayCommand CmdAdvancedSettings { get; }
        public RelayCommand CmdDetach { get; }
        public RelayCommand CmdPip { get; }
        public RelayCommand CmdReattach { get; }
        public RelayCommand CmdShowWindow { get; }
        public RelayCommand CmdFullScreen { get; }
        public RelayCommand CmdCtrlAltDel { get; }        public RelayCommand CmdReconnect { get; }
        public RelayCommand CmdCopyAddress { get; }
        public RelayCommand CmdPin { get; }

        /// <summary>always on top: the main window stays above the others</summary>
        public bool IsPinned => _main.IsPinned;

        /// <summary>"Adatta · 100%": the VNC button also shows the current zoom</summary>
        public string FitLabel => $"{IoC.Translate("vnc_fit")} · {Host.ZoomText}";

        /// <summary>"screen" group of the bar (if empty, the separator line disappears too)</summary>
        public bool HasScreenGroup => HasViewControls || CanChangeDisplayMode || CanFullScreenButton || HasKeys;

        // VNC with several merged monitors: "Fit" (fit width/height, zoom in the menu)
        public bool HasViewControls => Host.HasViewControls;
        public RelayCommand CmdFit { get; }

        /// <summary>"Keys": the keys of Key mapping for this connection only (RDP and VNC)</summary>
        public bool HasKeys => Lemora.View.Host.SessionKeysMenu.Supports(Host);
        public RelayCommand CmdKeys { get; }


        /// <summary>called every second: updates status and elapsed time</summary>
        public void Refresh()
        {
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(IsConnected));
            RaisePropertyChanged(nameof(IsError));
            RaisePropertyChanged(nameof(IsReconnecting));
            RaisePropertyChanged(nameof(IsDetached));
            RaisePropertyChanged(nameof(CanFullScreenButton));
            RaisePropertyChanged(nameof(CanCtrlAltDel));
            RaisePropertyChanged(nameof(HasViewControls));
            RaisePropertyChanged(nameof(FitLabel));
            RaisePropertyChanged(nameof(HasScreenGroup));
            RaisePropertyChanged(nameof(IsPinned));
            RaisePropertyChanged(nameof(HasFingerprint));
            RaisePropertyChanged(nameof(IsHistoryOpen));
            RaisePropertyChanged(nameof(IsSplit));
            RaisePropertyChanged(nameof(ShowSplitButtonNonTerminal));
        }
    }
}
