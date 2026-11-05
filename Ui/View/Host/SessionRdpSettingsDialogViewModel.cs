using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// Impostazioni dello schermo o avanzate di una sessione RDP aperta: stesso modulo della creazione, ma applicato alla sessione.
    /// Le modifiche allo schermo (adattamento, risoluzione, scala) valgono subito; le altre (audio, appunti, dischi...) il client
    /// RDP le legge solo all'avvio: compare "Riconnetti ora". Si lavora sulla copia della sessione; le modifiche vanno poi anche nel server salvato
/// (RdpSessionSettingsSaver), cosi' la connessione dopo riparte da queste.
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
        /// <summary>true mentre una modifica aspetta la riconnessione automatica</summary>
        public bool NeedsReconnect
        {
            get => _needsReconnect;
            private set => SetAndNotifyIfChanged(ref _needsReconnect, value);
        }

        public new RelayCommand CmdQuit { get; }

        // campi dello schermo che il client sa cambiare a sessione aperta
        public static readonly HashSet<string> LiveDisplay = new()
        {
            nameof(RDP.RdpWindowResizeMode), nameof(RDP.RdpWidth), nameof(RDP.RdpHeight),
            nameof(RDP.IsScaleFactorFollowSystem), nameof(RDP.ScaleFactorCustomValue),
        };

        // campi dello schermo che si leggono solo all'avvio
        public static readonly HashSet<string> ReconnectDisplay = new()
        {
            nameof(RDP.DisplayPerformance), nameof(RDP.RdpFullScreenFlag), nameof(RDP.RdpFullScreenMonitors), nameof(RDP.RdpFullScreenFollowMonitors),
        };

        // campi della sezione avanzata: tutti si leggono solo all'avvio
        public static readonly HashSet<string> Advanced = new()
        {
            nameof(RDP.IsAdministrativePurposes), nameof(RDP.AudioRedirectionMode), nameof(RDP.AudioQualityMode),
            nameof(RDP.EnableClipboard), nameof(RDP.EnableKeyCombinations), nameof(RDP.EnableAudioCapture),
            nameof(RDP.EnablePorts), nameof(RDP.EnablePrinters), nameof(RDP.EnableSmartCardsAndWinHello),
            nameof(RDP.EnableDiskDrives), nameof(RDP.EnableRedirectDrivesPlugIn), nameof(RDP.EnableRedirectCameras),
        };

        private readonly DispatcherTimer _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        // le modifiche che il client legge solo all'avvio si applicano da sole: riconnessione dopo una breve pausa (raggruppa piu' modifiche)
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
            Closed += (s, e) =>
            {
                Rdp.PropertyChanged -= OnRdpChanged;
                _applyTimer.Stop();
                // se si chiude il pannello prima dello scadere, la riconnessione parte comunque: le modifiche sono gia' state fatte
                if (_reconnectTimer.IsEnabled) { _reconnectTimer.Stop(); ReconnectNow(); }
            };
        }

        private bool _goFullScreen;

        /// <summary>
        /// Con "schermi multipli" la sessione passa PRIMA a schermo intero e solo dopo si riconnette: cosi' la risoluzione remota si calcola sui monitor
        /// (riconnettendo nella scheda, lo schermo intero arrivava dopo con una risoluzione sbagliata: un monitor con l'immagine e l'altro nero).
        /// </summary>
        private void ReconnectNow()
        {
            var host = Host;
            if (!_goFullScreen) { host.ReConn(); return; }
            _goFullScreen = false;
            if (host.ParentWindow is not FullScreenWindowView)
            {
                host.ShowSwitchCover();   // la finestra sta per allargarsi ai due monitor: l'immagine vecchia non deve finire in mezzo
                IoC.Get<SessionControlService>().MoveSessionToFullScreen(host.ConnectionId);
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (s, e) => { t.Stop(); host.ReConn(); };
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
                    // le modalita' a schermo intero si decidono all'avvio
                    if (Rdp.RdpWindowResizeMode is ERdpWindowResizeMode.StretchFullScreen or ERdpWindowResizeMode.FixedFullScreen)
                        ScheduleReconnect();
                    else
                    {
                        // si aspetta la fine della digitazione (larghezza/altezza si scrivono cifra per cifra)
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
