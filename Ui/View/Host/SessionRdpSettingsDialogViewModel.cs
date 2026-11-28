using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using Lemora.Model.Protocol;
using Lemora.Service;
using Lemora.View.Editor.Forms;
using Lemora.View.Host.ProtocolHosts;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils.Wpf;

namespace Lemora.View.Host
{
    public enum SessionRdpSettingsKind
    {
        Display,
        Advanced,
    }

    /// <summary>
    /// Display or advanced settings of an open RDP session: same form as creation, but applied to the session.
    /// Display changes (fit, resolution, scale) apply immediately; the others (audio, clipboard, drives...) are read by the RDP
    /// client only at startup: "Reconnect now" appears. We work on the session copy; the changes then also go into the saved server
/// (RdpSessionSettingsSaver), so the next connection starts from them.
    /// </summary>
    public class SessionRdpSettingsDialogViewModel : PopupBase
    {
        public HostBase Host { get; }
        public RDP Rdp { get; }
        public RdpFormViewModel Form { get; }
        public SessionRdpSettingsKind Kind { get; }

        public string Title => IoC.Translate(Kind == SessionRdpSettingsKind.Display ? "session_settings_display" : "session_settings_advanced")
                               + " · " + Rdp.DisplayName;

        private bool _needsReconnect;
        /// <summary>true while a change is waiting for the automatic reconnection</summary>
        public bool NeedsReconnect
        {
            get => _needsReconnect;
            private set => SetAndNotifyIfChanged(ref _needsReconnect, value);
        }

        public new RelayCommand CmdQuit { get; }

        // display fields the client can change while the session is open
        public static readonly HashSet<string> LiveDisplay = new()
        {
            nameof(RDP.RdpWindowResizeMode), nameof(RDP.RdpWidth), nameof(RDP.RdpHeight),
            nameof(RDP.IsScaleFactorFollowSystem), nameof(RDP.ScaleFactorCustomValue),
        };

        // display fields that are read only at startup
        public static readonly HashSet<string> ReconnectDisplay = new()
        {
            nameof(RDP.DisplayPerformance), nameof(RDP.RdpFullScreenFlag), nameof(RDP.RdpFullScreenMonitors), nameof(RDP.RdpFullScreenFollowMonitors),
        };

        // advanced section fields: all are read only at startup
        public static readonly HashSet<string> Advanced = new()
        {
            nameof(RDP.IsAdministrativePurposes), nameof(RDP.AudioRedirectionMode), nameof(RDP.AudioQualityMode),
            nameof(RDP.EnableClipboard), nameof(RDP.EnableKeyCombinations), nameof(RDP.EnableAudioCapture),
            nameof(RDP.EnablePorts), nameof(RDP.EnablePrinters), nameof(RDP.EnableSmartCardsAndWinHello),
            nameof(RDP.EnableDiskDrives), nameof(RDP.EnableRedirectDrivesPlugIn), nameof(RDP.EnableRedirectCameras),
        };

        private readonly DispatcherTimer _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        // changes the client reads only at startup apply on their own: reconnection after a short pause (groups several changes)
        private readonly DispatcherTimer _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        public SessionRdpSettingsDialogViewModel(HostBase host, SessionRdpSettingsKind kind)
        {
            Host = host;
            Kind = kind;
            Rdp = (RDP)host.ProtocolServer;
            Form = new RdpFormViewModel(Rdp) { HideLiveResize = Kind == SessionRdpSettingsKind.Display && !host.RemoteResizeSupported };
            CmdQuit = new RelayCommand(_ => RequestClose());
            _applyTimer.Tick += (s, e) =>
            {
                _applyTimer.Stop();
                Host.ApplyDisplaySettings();
            };
            _reconnectTimer.Tick += (s, e) =>
            {
                _reconnectTimer.Stop();
                NeedsReconnect = false;
                ReconnectNow();
            };
            Rdp.PropertyChanged += OnRdpChanged;
            // the session closes (end, server refusal): the panel is no longer needed and must not reconnect a closed tab
            void HostClosed(string _) { _reconnectTimer.Stop(); _applyTimer.Stop(); Stylet.Execute.OnUIThread(() => RequestClose()); }
            host.OnClosed += HostClosed;
            Closed += (s, e) =>
            {
                host.OnClosed -= HostClosed;
                Rdp.PropertyChanged -= OnRdpChanged;
                _applyTimer.Stop();
                // if the panel is closed before the timer expires, the reconnection starts anyway: the changes have already been made
                if (_reconnectTimer.IsEnabled) { _reconnectTimer.Stop(); ReconnectNow(); }
            };
        }

        private bool _goFullScreen;

        /// <summary>
        /// With "multiple monitors" the session FIRST goes full screen and only then reconnects: this way the remote resolution is computed on the monitors
        /// (reconnecting in the tab, full screen came later with a wrong resolution: one monitor with the image and the other black).
        /// </summary>
        private void ReconnectNow()
        {
            var host = Host;
            // FreeRDP on Windows has no cameras or COM ports: with these the session reopens with the Microsoft component (RunnerHelper.NeedsMicrosoftClient)
            if (host is IntegrateHost { IsFreeRdp: true } && Lemora.Model.ProtocolRunner.RunnerHelper.NeedsMicrosoftClient(Rdp))
            {
                ReopenWithMicrosoft(host);
                return;
            }
            if (!_goFullScreen) { host.ReConn(); return; }
            _goFullScreen = false;
            if (host.ParentWindow is not FullScreenWindowView)
            {
                host.ShowSwitchCover();   // the window is about to stretch over both monitors: the old image must not end up in the middle
                IoC.Get<SessionControlService>().MoveSessionToFullScreen(host.ConnectionId);
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (s, e) => { t.Stop(); host.ReConn(); };
            t.Start();
        }

        /// <summary>closes the FreeRDP tab and reopens the saved server (the changes are already there, RdpSessionSettingsSaver): RunnerHelper picks Microsoft</summary>
        private void ReopenWithMicrosoft(HostBase host)
        {
            var saved = IoC.Get<Lemora.Model.GlobalData>().VmItemList.FirstOrDefault(v => v.Server.Id == Rdp.Id)?.Server;
            if (saved == null) { host.ReConn(); return; }   // ad-hoc session, not saved: stays on FreeRDP
            // the changes go into the saved server BEFORE reopening (RdpSessionSettingsSaver saves them after 0.7 s: the reopening came first, with FreeRDP)
            Lemora.Service.RdpSessionSettingsSaver.SaveNow(Rdp, saved);
            var ms = IoC.Get<ProtocolConfigurationService>().ProtocolConfigs.TryGetValue(RDP.ProtocolName, out var cfg)
                ? cfg.Runners.OfType<Lemora.Model.ProtocolRunner.Default.InternalDefaultRunner>().FirstOrDefault()?.Name ?? "" : "";
            Lemora.Service.ToastService.Show(IoC.Translate("rdp_reopen_mstsc_devices"));
            RequestClose();
            IoC.Get<SessionControlService>().CloseProtocolHostAsync(host.ConnectionId);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (s, e) => { t.Stop(); Lemora.Model.GlobalEventHelper.OnRequestServerConnect?.Invoke(saved, fromView: "Session settings", assignRunnerName: ms); };
            t.Start();
        }

        private void ScheduleReconnect()
        {
            NeedsReconnect = true;
            _reconnectTimer.Stop();
            _reconnectTimer.Start();
        }

        private void OnRdpChanged(object? sender, PropertyChangedEventArgs e)
        {
            var name = e.PropertyName ?? "";
            if (Kind == SessionRdpSettingsKind.Display)
            {
                if (ReconnectDisplay.Contains(name))
                {
                    if (name == nameof(RDP.RdpFullScreenFlag) && Rdp.RdpFullScreenFlag == ERdpFullScreenFlag.EnableFullAllScreens) _goFullScreen = true;
                    ScheduleReconnect();
                }
                else if (LiveDisplay.Contains(name))
                {
                    // full screen modes are decided at startup
                    // and with FreeRDP everything is read at startup
                    if (!Host.AppliesDisplayLive || Rdp.RdpWindowResizeMode is ERdpWindowResizeMode.StretchFullScreen or ERdpWindowResizeMode.FixedFullScreen)
                        ScheduleReconnect();
                    else
                    {
                        // wait for typing to finish (width/height are typed digit by digit)
                        _applyTimer.Stop();
                        _applyTimer.Start();
                    }
                }
            }
            else if (Advanced.Contains(name))
            {
                ScheduleReconnect();
            }
        }
    }
}
