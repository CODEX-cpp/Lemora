using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.RdpFile;
using Lemora.Utils.WindowsApi;
using MSTSCLib;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.WpfResources.Theme.Styles;
using Stylet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using Color = System.Drawing.Color;
using Timer = System.Timers.Timer;

namespace Lemora.View.Host.ProtocolHosts
{
    internal static class AxMsRdpClient9NotSafeForScriptingExAdd
    {
        public static void SetExtendedProperty(this AxHost axHost, string propertyName, object value)
        {
            try
            {
                ((IMsRdpExtendedSettings)axHost.GetOcx()!).set_Property(propertyName, ref value);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
            }
        }
    }

    internal class AxMsRdpClient9NotSafeForScriptingEx : AxMSTSCLib.AxMsRdpClient9NotSafeForScripting
    {
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            // Falsifying the response to WM_GETOBJECT to resolve issue #1053 that the RDP client to crash when using the word capture feature
            if (m.Msg == Win32Api.WM_GETOBJECT)
            {
                m.Result = (IntPtr)(-1); // Setting it to IntPtr.Zero (or 0) did not resolve the issue.
                               // Setting it experimentally to 1 or -1 solved the problem, though I cannot explain why.
                return;
            }
            // Fix for the missing focus issue on the rdp client component
            if (m.Msg == Win32Api.WM_MOUSEACTIVATE)
            {
                if (!this.ContainsFocus)
                {
                    SimpleLogHelper.Debug("AxMsRdpClient9NotSafeForScriptingEx.WndProc: Focus");
                    this.Focus();
                }
            }
            base.WndProc(ref m);
        }
    }


    public sealed partial class AxMsRdpClient09Host : HostBase, IDisposable
    {
        private AxMsRdpClient9NotSafeForScriptingEx? _rdpClient = null;
        //private readonly DataSourceBase? _dataSource;
        private readonly RDP _rdpSettings;
        /// <summary>
        /// system scale factor, 100 = 100%, 200 = 200%
        /// </summary>
        private uint _primaryScaleFactor = 100;

        /// <summary>scala di Windows (100, 125, 150...) del monitor su cui sta la finestra della sessione; con Per-Monitor V2 cambia da monitor a monitor</summary>
        private uint CurrentMonitorScaleFactor()
        {
            try
            {
                var scale = 100u;
                Execute.OnUIThreadSync(() => scale = (uint)Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100));
                return scale < 100 ? 100u : scale;
            }
            catch
            {
                return ScreenInfoEx.GetPrimaryScreenScaleFactor();
            }
        }
        /// <summary>
        /// if has connected, then rdp can resize
        /// </summary>
        private bool _flagHasConnected = false;
        /// <summary>
        /// if has ever connected successfully, then enabled auto reconnect feature
        /// </summary>
        private bool _flagHasEverConnected = false;


        private readonly System.Timers.Timer _loginResizeTimer; // timer for login resize, to fix the issue that the rdp client size is not correct when login
        private DateTime _lastLoginTime = DateTime.MinValue;

        private readonly object _rdpClientDisposeLock = new object();


        public static AxMsRdpClient09Host Create(RDP rdp, int width = 0, int height = 0)
        {
            AxMsRdpClient09Host? view = null;
            Execute.OnUIThreadSync(() =>
            {
                view = new AxMsRdpClient09Host(rdp, width, height);
            });
            return view!;
        }

        private AxMsRdpClient09Host(RDP rdp, int width = 0, int height = 0) : base(rdp, true)
        {
            InitializeComponent();


            MenuItems.Add(new System.Windows.Controls.Separator());
            MenuItems.Add(new System.Windows.Controls.MenuItem()
            {
                Header = "Ctrl + Alt + Del",
                Command = new RelayCommand((o) =>
                {
                    if (_rdpClient != null)
                    {
                        _rdpClient.Focus();
                        new MsRdpClientNonScriptableWrapper(_rdpClient.GetOcx()!).SendKeys(
                            new int[] { 0x1d, 0x38, 0x53, 0x53, 0x38, 0x1d },
                            new bool[] { false, false, false, true, true, true, });
                    }
                }, o => HasConnected)
            });

            GridMessageBox.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Visible;

            _rdpSettings = rdp;

            _loginResizeTimer = new Timer(300) { Enabled = false, AutoReset = false };
            _loginResizeTimer.Elapsed += (sender, args) =>
            {
                _loginResizeTimer.Stop();
                try
                {
                    var nw = (uint)(_rdpClient?.Width ?? 0);
                    var nh = (uint)(_rdpClient?.Height ?? 0);
                    // tip: the control default width is 288
                    if (_rdpClient?.DesktopWidth > nw
                        || _rdpClient?.DesktopHeight > nh)
                    {
                        SimpleLogHelper.DebugInfo($@"_loginResizeTimer start run... {_rdpClient?.DesktopWidth}, {nw}, {_rdpClient?.DesktopHeight}, {nh}");
                        ReSizeRdpToControlSize();
                    }
                    else
                    {
                        _lastLoginTime = DateTime.MinValue;
                    }
                }
                finally
                {
                    if (DateTime.Now < _lastLoginTime.AddMinutes(1))
                    {
                        _loginResizeTimer.Start();
                    }
                    else
                    {
                        SimpleLogHelper.DebugWarning($@"_loginResizeTimer stop");
                    }
                }
            };

            InitRdp(width, height);
            GlobalEventHelper.OnScreenResolutionChanged += OnScreenResolutionChanged;
        }

        ~AxMsRdpClient09Host()
        {
            SimpleLogHelper.Debug($"Release {this.GetType().Name}({this.GetHashCode()})");
            Dispose();
        }

        public void Dispose()
        {
            SimpleLogHelper.Debug($"Disposing {this.GetType().Name}({this.GetHashCode()})");
            _resizeEndTimer?.Dispose();
            _loginResizeTimer?.Dispose();
            RdpClientDispose();
            SimpleLogHelper.Debug($"Dispose done {this.GetType().Name}({this.GetHashCode()})");
        }

        private void OnScreenResolutionChanged()
        {
            lock (_rdpClientDisposeLock)
            {
                // 全屏模式下客户端机器发生了屏幕分辨率改变，则将RDP还原到窗口模式（仿照 MSTSC 的逻辑）
                if (_rdpClient?.FullScreen == true && _rdpSettings.RdpFullScreenFollowMonitors == true && ParentWindow is FullScreenWindowView)
                {
                    FollowCurrentMonitors();   // "segui i monitor": invece di tornare alla finestra, si adatta ai monitor presenti
                }
                else if (_rdpClient?.FullScreen == true)
                {
                    Execute.OnUIThread(() =>
                    {
                        _rdpClient.FullScreen = false;
                    });
                }
            }
        }

        /// <summary>
        /// init server connection info: user name\ psw \ port \ LoadBalanceInfo...
        /// </summary>
        /// <summary>
        /// Dominio e utente da inviare al server. Quello che e' scritto nel nome utente vince sul campo Dominio del server:
        /// "DOMINIO\\utente" usa quel dominio, ".\\utente" (o "NOMESERVER\\utente") un utente locale del server, "utente@dominio" (UPN) non riceve nessun dominio in piu'.
        /// Un nome semplice usa il dominio del server (e della cartella), se c'e'.
        /// </summary>
        internal static (string domain, string user) ResolveLogon(string userName, string? serverDomain)
        {
            userName = (userName ?? "").Trim();
            var bs = userName.IndexOf('\\');
            if (bs > 0) return (userName.Substring(0, bs), userName.Substring(bs + 1));
            if (userName.Contains('@')) return ("", userName);
            return ((serverDomain ?? "").Trim(), userName);
        }

        private void RdpInitServerInfo()
        {
            #region server info
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            // server connection info: user name\ psw \ port ...
            _rdpClient.Server = _rdpSettings.Address;
            {
                var (domain, user) = ResolveLogon(_rdpSettings.UserName ?? "", _rdpSettings.Domain);
                _rdpClient.Domain = domain;
                _rdpClient.UserName = user;
            }
            _rdpClient.AdvancedSettings2.RDPPort = _rdpSettings.GetPort();


            if (string.IsNullOrWhiteSpace(_rdpSettings.LoadBalanceInfo) == false)
            {
                var loadBalanceInfo = _rdpSettings.LoadBalanceInfo;
                if (loadBalanceInfo.Length % 2 == 1)
                    loadBalanceInfo += " ";
                loadBalanceInfo += "\r\n";
                var bytes = Encoding.UTF8.GetBytes(loadBalanceInfo);
                _rdpClient.AdvancedSettings2.LoadBalanceInfo = Encoding.Unicode.GetString(bytes);
            }



            var secured = (MSTSCLib.IMsTscNonScriptable)_rdpClient.GetOcx()!;
            secured.ClearTextPassword = DataVault.DecryptOrReturnOriginalString(_rdpSettings.Password);
            // con una password fornita, se il server la rifiuta il controllo NON deve aprire la finestra di Windows: segnala l'errore e la richiesta la fa il programma
            // (senza password si lascia com'era: la chiede Windows)
            if (!string.IsNullOrEmpty(DataVault.DecryptOrReturnOriginalString(_rdpSettings.Password)))
            {
                try { ((MSTSCLib.IMsRdpClientNonScriptable5)_rdpClient.GetOcx()!).AllowPromptingForCredentials = false; }
                catch (Exception e) { SimpleLogHelper.Warning("RDP: AllowPromptingForCredentials non impostabile: " + e.Message); }
            }
            _rdpClient.FullScreenTitle = _rdpSettings.DisplayName + " - " + _rdpSettings.SubTitle;

            #endregion server info
        }

        private void RdpInitStatic()
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            SimpleLogHelper.Debug("RDP Host: init Static");
            _rdpClient.AdvancedSettings2.EncryptionEnabled = 1;
            // niente riconnessione automatica del controllo: mostrava la sua finestra di Windows ("Riconnessione in corso...") sopra lo schermo;
            // la caduta arriva a OnRdpClientDisconnected e la riconnessione la fa Lemora con la sua schermata
            _rdpClient.AdvancedSettings5.EnableAutoReconnect = false;
            // setting PublicMode to false allows the saving of credentials, which prevents
            _rdpClient.AdvancedSettings6.PublicMode = false;
            _rdpClient.AdvancedSettings5.EnableWindowsKey = 1;
            _rdpClient.AdvancedSettings5.GrabFocusOnConnect = true;
            _rdpClient.AdvancedSettings2.keepAliveInterval = 1000 * 60 * 1; // 1000 = 1000 ms
            _rdpClient.AdvancedSettings2.overallConnectionTimeout = 600; // The new time, in seconds. The maximum value is 600, which represents 10 minutes.

            // enable CredSSP, will use CredSsp if the client supports.
            _rdpClient.AdvancedSettings9.EnableCredSspSupport = true;

            //- 0: If server authentication fails, connect to the computer without warning (Connect and don't warn me)
            //- 1: If server authentication fails, don't establish a connection (Don't connect)
            //- 2: If server authentication fails, show a warning and allow me to connect or refuse the connection (Warn me)
            //- 3: No authentication requirement specified.
            _rdpClient.AdvancedSettings9.AuthenticationLevel = 0;

            // ref: https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings6-connecttoadministerserver
            _rdpClient.AdvancedSettings7.ConnectToAdministerServer = _rdpSettings.IsAdministrativePurposes == true;
        }

        private void CreateRdpClient()
        {
            lock (_rdpClientDisposeLock)
            {
                _rdpClient = new AxMsRdpClient9NotSafeForScriptingEx();

                SimpleLogHelper.Debug("RDP Host: init new AxMsRdpClient9NotSafeForScriptingEx()");

                ((System.ComponentModel.ISupportInitialize)(_rdpClient)).BeginInit();
                _rdpClient.Dock = DockStyle.Fill;
                _rdpClient.Enabled = true;
                _rdpClient.BackColor = Color.Black;
                // set call back
                _rdpClient.OnRequestGoFullScreen += (sender, args) =>
                {
                    SimpleLogHelper.Debug("RDP Host:  OnRequestGoFullScreen");
                    Diag("richiesto lo schermo intero");
                    OnGoToFullScreenRequested();
                };
                _rdpClient.OnRequestLeaveFullScreen += (sender, args) =>
                {
                    SimpleLogHelper.Debug("RDP Host:  OnRequestLeaveFullScreen");
                    OnConnectionBarRestoreWindowCall();
                };
                _rdpClient.OnRequestContainerMinimize += (sender, args) =>
                {
                    SimpleLogHelper.Debug("RDP Host:  OnRequestContainerMinimize");
                    if (ParentWindow is FullScreenWindowView)
                    {
                        ParentWindow.WindowState = WindowState.Minimized;
                    }
                };
                _rdpClient.OnDisconnected += OnRdpClientDisconnected;
                _rdpClient.OnConfirmClose += (sender, args) =>
                {
                    // invoke in the full screen mode.
                    SimpleLogHelper.Debug("RDP Host:  RdpOnConfirmClose");
                    base.OnClosed?.Invoke(base.ConnectionId);
                };
                _rdpClient.OnConnecting += (sender, args) => Diag("OnConnecting (inizio negoziazione col server)");
                _rdpClient.OnConnected += OnRdpClientConnected;
                _rdpClient.OnLogonError += OnRdpClientLogonError;
                _rdpClient.OnLoginComplete += OnRdpClientLoginComplete;
                // riconnessione automatica del controllo di Windows (rete caduta per poco): il motivo resta nel log a ogni livello
                _rdpClient.OnAutoReconnecting += (_, a) => SimpleLogHelper.Warning($"RDP({_rdpSettings.DisplayName}): riconnessione automatica, tentativo {a.attemptCount} (motivo {a.disconnectReason})");
                _rdpClient.OnAutoReconnected += (_, _) => Lemora.Utils.ActivityLog.Write($"Ricollegato a {Lemora.Utils.ActivityLog.Describe(ProtocolServer)} (riconnessione automatica di Windows)");
                _rdpClient.OnWarning += (_, a) => SimpleLogHelper.Warning($"RDP({_rdpSettings.DisplayName}): avviso del controllo RDP, codice {a.warningCode}");
                _rdpClient.OnFatalError += (_, a) => { ActivityReason = $"errore grave del controllo RDP, codice {a.errorCode}"; SimpleLogHelper.Error($"RDP({_rdpSettings.DisplayName}): {ActivityReason}"); };
                ((System.ComponentModel.ISupportInitialize)(_rdpClient)).EndInit();
                RdpHost.Child = _rdpClient;

                SimpleLogHelper.Debug("RDP Host: init CreateControl();");
                _rdpClient.CreateControl();
            }
        }

        private void RdpInitConnBar()
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            SimpleLogHelper.Debug("RDP Host: init conn bar");
            _rdpClient.AdvancedSettings6.DisplayConnectionBar = _rdpSettings.IsFullScreenWithConnectionBar == true;
            if (_rdpClient.AdvancedSettings6.DisplayConnectionBar)
            {
                _rdpClient.AdvancedSettings6.ConnectionBarShowPinButton = true;
                _rdpClient.AdvancedSettings6.PinConnectionBar = true;   // a schermo intero la barra resta sempre visibile (prima si nascondeva da sola)
                _rdpClient.AdvancedSettings6.ConnectionBarShowMinimizeButton = true;
                _rdpClient.AdvancedSettings6.ConnectionBarShowRestoreButton = true;
            }
            _rdpClient.AdvancedSettings6.BitmapVirtualCache32BppSize = 48;
        }

        public void NotifyRedirectDeviceChange(int msg, IntPtr wParam, IntPtr lParam)
        {
            const int WM_DEVICECHANGE = 0x0219;

            /* from https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable-notifyredirectdevicechange
             *      https://learn.microsoft.com/en-us/windows/win32/devio/wm-devicechange
             * wParam case when msg == WM_DEVICECHANGE:
             * DBT_DEVNODES_CHANGED     0x0007      A device has been added to or removed from the system. param = 0
             * DBT_DEVICEARRIVAL        0x8000      A device or piece of media has been inserted and is now available. param = A pointer to a structure identifying the device inserted. 
             */
            SimpleLogHelper.Debug($"RDP: NotifyRedirectDeviceChange Receive(0x{msg:X}, 0x{wParam:X}, 0x{lParam:X})");
            if (msg == WM_DEVICECHANGE
                && _rdpClient != null
                && ((IMsRdpClientNonScriptable3)_rdpClient.GetOcx()!).RedirectDynamicDevices)
            {
                new MsRdpClientNonScriptableWrapper(_rdpClient.GetOcx()!).NotifyRedirectDeviceChange(wParam, lParam);
            }
        }

        private void RdpInitRedirect()
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            SimpleLogHelper.Debug("RDP Host: init Redirect");


            #region Redirect

            // purpose is not clear
            ((IMsRdpClientNonScriptable3)_rdpClient.GetOcx()!).RedirectDynamicDrives = true; // Specifies or retrieves whether dynamically attached Plug and Play (PnP) drives that are enumerated while in a session are available for redirection. https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable3-redirectdynamicdrives

            if (_rdpSettings.EnableDiskDrives == true || _rdpSettings.EnableRedirectDrivesPlugIn == true)
            {
                _rdpClient.AdvancedSettings9.RedirectDrives = true;
                // you must redirect disk drive if you want to redirect usb disk
                if (_rdpSettings.EnableRedirectDrivesPlugIn == true)
                {
                    ((IMsRdpClientNonScriptable3)_rdpClient.GetOcx()!).RedirectDynamicDevices = true; // Specifies whether dynamically attached PnP devices that are enumerated while in a session are available for redirection. https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable3-redirectdynamicdevices
                    RedirectDevice();
                }
            }

            // disable local disk
            if (_rdpSettings.EnableDiskDrives == false)
            {
                var ocx = (MSTSCLib.IMsRdpClientNonScriptable7)_rdpClient.GetOcx()!;
                ocx.DriveCollection.RescanDrives(false);
                for (int i = 0; i < ocx.DriveCollection.DriveCount; i++)
                {
                    ocx.DriveCollection.DriveByIndex[(uint)i].RedirectionState = false;
                }
            }


            _rdpClient.AdvancedSettings9.RedirectClipboard = _rdpSettings.EnableClipboard == true;
            _rdpClient.AdvancedSettings9.RedirectPrinters = _rdpSettings.EnablePrinters == true;
            _rdpClient.AdvancedSettings9.RedirectPOSDevices = _rdpSettings.EnablePorts == true;
            _rdpClient.AdvancedSettings9.RedirectSmartCards = _rdpSettings.EnableSmartCardsAndWinHello == true;


            if (_rdpSettings.EnableKeyCombinations == true)
            {
                // - 0 Apply key combinations only locally at the client computer.
                // - 1 Apply key combinations at the remote server.
                // - 2 Apply key combinations to the remote server only when the client is running in full-screen mode. This is the default value.
                _rdpClient.SecuredSettings3.KeyboardHookMode = 1;
            }
            else
            {
                _rdpClient.SecuredSettings3.KeyboardHookMode = 0;
            }

            if (_rdpSettings.AudioRedirectionMode == EAudioRedirectionMode.RedirectToLocal)
            {
                // - 0 (Audio redirection is enabled and the option for redirection is "Bring to this computer". This is the default mode.)
                // - 1 (Audio redirection is enabled and the option is "Leave at remote computer". The "Leave at remote computer" option is supported only when connecting remotely to a host computer that is running Windows Vista. If the connection is to a host computer that is running Windows Server 2008, the option "Leave at remote computer" is changed to "Do not play".)
                // - 2 (Audio redirection is enabled and the mode is "Do not play".)
                _rdpClient.SecuredSettings3.AudioRedirectionMode = 0;

                // Only set AudioQuality Moode when AudioRedirectionMode == RedirectToLocal
                if (_rdpSettings.AudioQualityMode == EAudioQualityMode.Dynamic)
                {
                    // - 0 Dynamic audio quality. This is the default audio quality setting. The server dynamically adjusts audio output quality in response to network conditions and the client and server capabilities.
                    // - 1 Medium audio quality. The server uses a fixed but compressed format for audio output.
                    // - 2 High audio quality. The server provides audio output in uncompressed PCM format with lower processing overhead for latency.
                    _rdpClient.AdvancedSettings8.AudioQualityMode = 0;
                }
                else if (_rdpSettings.AudioQualityMode == EAudioQualityMode.Medium)
                {
                    // - 1 Medium audio quality. The server uses a fixed but compressed format for audio output.
                    _rdpClient.AdvancedSettings8.AudioQualityMode = 1;
                }
                else if (_rdpSettings.AudioQualityMode == EAudioQualityMode.High)
                {
                    // - 2 High audio quality. The server provides audio output in uncompressed PCM format with lower processing overhead for latency.
                    _rdpClient.AdvancedSettings8.AudioQualityMode = 2;
                }

            }
            else if (_rdpSettings.AudioRedirectionMode == EAudioRedirectionMode.LeaveOnRemote)
            {
                // - 1 (Audio redirection is enabled and the option is "Leave at remote computer". The "Leave at remote computer" option is supported only when connecting remotely to a host computer that is running Windows Vista. If the connection is to a host computer that is running Windows Server 2008, the option "Leave at remote computer" is changed to "Do not play".)
                _rdpClient.SecuredSettings3.AudioRedirectionMode = 1;
            }
            else if (_rdpSettings.AudioRedirectionMode == EAudioRedirectionMode.Disabled)
            {
                // - 2 Disable sound redirection; do not play sounds at the server.
                _rdpClient.SecuredSettings3.AudioRedirectionMode = 2;
            }

            if (_rdpSettings.EnableAudioCapture == true)
            {
                // indicates whether the default audio input device is redirected from the client to the remote session
                _rdpClient.AdvancedSettings8.AudioCaptureRedirectionMode = true;
            }
            else
            {
                _rdpClient.AdvancedSettings8.AudioCaptureRedirectionMode = false;
            }
            #endregion Redirect
        }


        public void RedirectDevice()
        {
            var ocx = _rdpClient?.GetOcx() as MSTSCLib.IMsRdpClientNonScriptable7;
            if (ocx == null)
                return;

            // Collect FriendlyNames of cameras redirected via the RDPECCAM channel so that
            // the same physical device is not also claimed by the USB DeviceCollection channel,
            // which would cause a server-side double-redirect conflict. ref: segnalazione 1071 del progetto originale
            var cameraFriendlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // redirect camera
            {
                ocx.CameraRedirConfigCollection.RedirectByDefault = false;
                if (_rdpSettings.EnableRedirectCameras == true)
                {
                    ocx.CameraRedirConfigCollection.Rescan(); // enumerates connected camera devices
                    for (int i = 0; i < ocx.CameraRedirConfigCollection.Count; i++)
                    {
                        var camera = ocx.CameraRedirConfigCollection.ByIndex[(uint) i];
                        camera.Redirected = true;
                        cameraFriendlyNames.Add(ocx.CameraRedirConfigCollection.ByIndex[(uint)i].FriendlyName ?? "");
                        SimpleLogHelper.Debug($"Redirect camera: {camera.FriendlyName}");
                    }
                }
            }
            // redirect device
            {
                ocx.DeviceCollection.RescanDevices(false);
                for (uint i = 0; i < ocx.DeviceCollection.DeviceCount; i++)
                {
                    var d = ocx.DeviceCollection.DeviceByIndex[i];
                    if (!string.IsNullOrEmpty(d.FriendlyName) && cameraFriendlyNames.Contains(d.FriendlyName))
                    {
                        // Skip cameras already handled by `redirect camera` to avoid double-redirect conflict
                        SimpleLogHelper.Debug($"Redirect device: skip {d.FriendlyName}({d.DeviceDescription}) for being already redirected as camera");
                        continue;
                    }

                    SimpleLogHelper.Debug($"Redirect device: {d.FriendlyName}({d.DeviceDescription})");
                    d.RedirectionState = true;
                }
            }
        }


        private void RdpInitDisplay(int width = 0, int height = 0, bool isReconnecting = false)
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            #region Display

            _primaryScaleFactor = CurrentMonitorScaleFactor();
            SimpleLogHelper.Debug($"RDP Host: init Display with ScaleFactor = {_primaryScaleFactor}, W = {width}, H = {height}, isReconnecting = {isReconnecting}");

            if (this._rdpSettings.IsScaleFactorFollowSystem == false && this._rdpSettings.ScaleFactorCustomValue != null)
            {
                _rdpClient.SetExtendedProperty("DesktopScaleFactor", this._rdpSettings.ScaleFactorCustomValue ?? _primaryScaleFactor);
            }
            else
            {
                _rdpClient.SetExtendedProperty("DesktopScaleFactor", _primaryScaleFactor);
            }
            _rdpClient.SetExtendedProperty("DeviceScaleFactor", (uint)100);
            if (_rdpSettings.RdpWindowResizeMode == ERdpWindowResizeMode.Stretch || _rdpSettings.RdpWindowResizeMode == ERdpWindowResizeMode.StretchFullScreen
                || (_resizeUnsupported && _rdpSettings.RdpWindowResizeMode == ERdpWindowResizeMode.AutoResize))
                _rdpClient.AdvancedSettings2.SmartSizing = true;
            // to enhance user experience, i let the form handled full screen
            _rdpClient.AdvancedSettings6.ContainerHandledFullScreen = 1;

            // pre-set the rdp width & height
            switch (_rdpSettings.RdpWindowResizeMode)
            {
                case ERdpWindowResizeMode.Stretch:
                case ERdpWindowResizeMode.Fixed:
                    // larghezza/altezza non scritte: la misura della finestra, non 800x600 (sul server vecchio restava un francobollo al centro)
                    _rdpClient.DesktopWidth = _rdpSettings.RdpWidth is >= 100 ? (int)_rdpSettings.RdpWidth.Value : (width >= 100 ? (int)(width * (_primaryScaleFactor / 100.0)) : 800);
                    _rdpClient.DesktopHeight = _rdpSettings.RdpHeight is >= 100 ? (int)_rdpSettings.RdpHeight.Value : (height >= 100 ? (int)(height * (_primaryScaleFactor / 100.0)) : 600);
                    break;
                case ERdpWindowResizeMode.FixedFullScreen:
                case ERdpWindowResizeMode.StretchFullScreen:
                    {
                        var size = GetScreenSizeIfRdpIsFullScreen();
                        _rdpClient.DesktopWidth = size.Width;
                        _rdpClient.DesktopHeight = size.Height;
                        break;
                    }
                case ERdpWindowResizeMode.AutoResize:
                case null:
                default:
                    {
                        // default case, set rdp size to tab window size.
                        if (width < 100)
                            width = 800;
                        if (height < 100)
                            height = 600;


                        //if (isReconnecting == true)
                        //{
                        //    // if isReconnecting == true, then width is DesktopWidth, ScaleFactor should be 100
                        //    _rdpClient.DesktopWidth = (int)(width);
                        //    _rdpClient.DesktopHeight = (int)(height);
                        //}
                        //else
                        {
                            // if isReconnecting == false, then width is Tab width, true width = Tab width * ScaleFactor
                            if (_rdpSettings.IsThisTimeConnWithFullScreen() && InFullScreenContext)
                            {
                                var size = GetScreenSizeIfRdpIsFullScreen();
                                _rdpClient.DesktopWidth = size.Width;
                                _rdpClient.DesktopHeight = size.Height;
                                SimpleLogHelper.DebugInfo($"RDP Host: init Display set FullScreen DesktopWidth = {_rdpClient.DesktopWidth},  DesktopHeight = {_rdpClient.DesktopHeight}");
                            }
                            else
                            {
                                _rdpClient.DesktopWidth = (int)(width * (_primaryScaleFactor / 100.0));
                                _rdpClient.DesktopHeight = (int)(height * (_primaryScaleFactor / 100.0));
                                SimpleLogHelper.DebugInfo(@$"RDP Host: init Display set DesktopWidth = {width} * {(_primaryScaleFactor / 100.0):F3} = {_rdpClient.DesktopWidth},  DesktopHeight = {height} * {(_primaryScaleFactor / 100.0):F3} = {_rdpClient.DesktopHeight},     RdpControl.Width = {_rdpClient.Width}, RdpControl.Height = {_rdpClient.Height}");
                                if (_primaryScaleFactor > 100)
                                {
                                    // size compensation since segnalazione 537 del progetto originale
                                    int c = (_primaryScaleFactor % 100) switch
                                    {
                                        50 => 1,
                                        75 => 2,
                                        _ => 0
                                    };
                                    if (ColorAndBrushHelper.ColorIsTransparent(_rdpSettings.ColorHex) != true)
                                    {
                                        c *= 2;
                                    }
                                    if (c < _rdpClient.DesktopWidth && c < _rdpClient.DesktopHeight)
                                    {
                                        //_rdpClient.DesktopWidth -= c;
                                        _rdpClient.DesktopHeight -= c;
                                    }
                                    SimpleLogHelper.DebugInfo($"RDP Host: init Display set DesktopWidth = {_rdpClient.DesktopWidth},  DesktopHeight = {_rdpClient.DesktopHeight}");
                                }
                            }
                        }

                        break;
                    }
            }



            switch (_rdpSettings.RdpFullScreenFlag)
            {
                case ERdpFullScreenFlag.Disable:
                    base.CanFullScreen = false;
                    break;

                case ERdpFullScreenFlag.EnableFullAllScreens:
                    base.CanFullScreen = true;
                    break;
                case ERdpFullScreenFlag.EnableFullScreen:
                default:
                    base.CanFullScreen = true;
                    break;
            }
            // monitor multipli: "tutti i monitor", oppure piu' monitor scelti nel selettore
            // solo se la sessione e' (o sta per essere) a schermo intero: in una scheda o in finestra si vede un solo schermo
            _connectedMultimon = base.CanFullScreen && FullScreenOnAllMonitors && InFullScreenContext;
            if (_connectedMultimon) ((IMsRdpClientNonScriptable5)_rdpClient.GetOcx()!).UseMultimon = true;

            #endregion Display

            // 2022.07.23 try to fix the rdp error code 4360, ref: https://forum.asg-rd.com/showthread.php?tid=11016&page=2
            _rdpClient.AdvancedSettings8.BitmapPersistence = 0;
            _rdpClient.AdvancedSettings8.CachePersistenceActive = 0;

            SimpleLogHelper.Debug($"RDP Host: Display init end: RDP.DesktopWidth = {_rdpClient.DesktopWidth}, RDP.DesktopHeight = {_rdpClient.DesktopHeight},");
            // diagnostica schermo (livello avviso, resta nel log): serve a capire i problemi con lo schermo intero e i monitor multipli
            Lemora.Utils.Diag.Log($"RDP-DIAG: {_rdpSettings.DisplayName}: schermo intero={_rdpSettings.RdpFullScreenFlag} monitor scelti=[{_rdpSettings.RdpFullScreenMonitors}] multimon={_connectedMultimon} modo={_rdpSettings.RdpWindowResizeMode} desktop={_rdpClient.DesktopWidth}x{_rdpClient.DesktopHeight} scala={_primaryScaleFactor} finestra={ParentWindow?.GetType().Name} monitor=[{string.Join("; ", System.Linq.Enumerable.Select(System.Windows.Forms.Screen.AllScreens, s => $"{s.Bounds.X},{s.Bounds.Y} {s.Bounds.Width}x{s.Bounds.Height}"))}]");
        }

        private void RdpInitPerformance()
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            SimpleLogHelper.Debug("RDP Host: init Performance");

            #region Performance

            // if win11 disable BandwidthDetection, make a workaround for #437 to hide info button after OS Win11 22H2 to avoid app crash when click the info button on Win11
            // detail: segnalazione 437 del progetto originale
            // 20250126: removed due to segnalazione 559 del progetto originale is fixed
            //try
            //{
            //    if (Lemora.Utils.WindowsApi.WindowsVersionHelper.IsWindows1122H2OrHigher()) // Win11 22H2
            //    {
            //        _rdpClient.AdvancedSettings9.BandwidthDetection = false;
            //    }
            //}
            //catch (Exception)
            //{
            //    // ignored
            //}

            // ref: https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings-performanceflags
            int nDisplayPerformanceFlag = 0;
            if (_rdpSettings.DisplayPerformance != EDisplayPerformance.Auto)
            {
                // ref: https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings7-networkconnectiontype
                // CONNECTION_TYPE_MODEM (1 (0x1)) Modem (56 Kbps)
                // CONNECTION_TYPE_BROADBAND_LOW (2 (0x2)) Low-speed broadband (256 Kbps to 2 Mbps) CONNECTION_TYPE_SATELLITE (3 (0x3)) Satellite (2 Mbps to 16 Mbps, with high latency)
                // CONNECTION_TYPE_BROADBAND_HIGH (4 (0x4)) High-speed broadband (2 Mbps to 10 Mbps) CONNECTION_TYPE_WAN (5 (0x5)) Wide area network (WAN) (10 Mbps or higher, with high latency)
                // CONNECTION_TYPE_LAN (6 (0x6)) Local area network (LAN) (10 Mbps or higher)
                _rdpClient.AdvancedSettings8.NetworkConnectionType = 1;
                switch (_rdpSettings.DisplayPerformance)
                {
                    case EDisplayPerformance.Auto:
                        break;

                    case EDisplayPerformance.Low:
                        // 8,16,24,32
                        _rdpClient.ColorDepth = 8;
                        nDisplayPerformanceFlag += 0x00000001;//TS_PERF_DISABLE_WALLPAPER;      Wallpaper on the desktop is not displayed.
                        nDisplayPerformanceFlag += 0x00000002;//TS_PERF_DISABLE_FULLWINDOWDRAG; Full-window drag is disabled; only the window outline is displayed when the window is moved.
                        nDisplayPerformanceFlag += 0x00000004;//TS_PERF_DISABLE_MENUANIMATIONS; Menu animations are disabled.
                        nDisplayPerformanceFlag += 0x00000008;//TS_PERF_DISABLE_THEMING ;       Themes are disabled.
                        nDisplayPerformanceFlag += 0x00000020;//TS_PERF_DISABLE_CURSOR_SHADOW;  No shadow is displayed for the cursor.
                        nDisplayPerformanceFlag += 0x00000040;//TS_PERF_DISABLE_CURSORSETTINGS; Cursor blinking is disabled.
                        break;

                    case EDisplayPerformance.Middle:
                        _rdpClient.ColorDepth = 16;
                        nDisplayPerformanceFlag += 0x00000001;//TS_PERF_DISABLE_WALLPAPER;      Wallpaper on the desktop is not displayed.
                        nDisplayPerformanceFlag += 0x00000002;//TS_PERF_DISABLE_FULLWINDOWDRAG; Full-window drag is disabled; only the window outline is displayed when the window is moved.
                        nDisplayPerformanceFlag += 0x00000004;//TS_PERF_DISABLE_MENUANIMATIONS; Menu animations are disabled.
                        nDisplayPerformanceFlag += 0x00000008;//TS_PERF_DISABLE_THEMING ;       Themes are disabled.
                        nDisplayPerformanceFlag += 0x00000020;//TS_PERF_DISABLE_CURSOR_SHADOW;  No shadow is displayed for the cursor.
                        nDisplayPerformanceFlag += 0x00000040;//TS_PERF_DISABLE_CURSORSETTINGS; Cursor blinking is disabled.
                        nDisplayPerformanceFlag += 0x00000080;//TS_PERF_ENABLE_FONT_SMOOTHING;        Enable font smoothing.
                        nDisplayPerformanceFlag += 0x00000100;//TS_PERF_ENABLE_DESKTOP_COMPOSITION ;  Enable desktop composition.

                        break;

                    case EDisplayPerformance.High:
                        _rdpClient.ColorDepth = 32;
                        nDisplayPerformanceFlag += 0x00000080;//TS_PERF_ENABLE_FONT_SMOOTHING;        Enable font smoothing.
                        nDisplayPerformanceFlag += 0x00000100;//TS_PERF_ENABLE_DESKTOP_COMPOSITION ;  Enable desktop composition.
                        break;
                }
            }
            SimpleLogHelper.Debug("RdpInit: DisplayPerformance = " + _rdpSettings.DisplayPerformance + ", flag = " + Convert.ToString(nDisplayPerformanceFlag, 2));
            _rdpClient.AdvancedSettings9.PerformanceFlags = nDisplayPerformanceFlag;

            #endregion Performance
        }

        private void RdpInitGateway()
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            SimpleLogHelper.Debug("RDP Host: init Gateway");

            #region Gateway

            // Specifies whether Remote Desktop Gateway (RD Gateway) is supported.
            if (_rdpClient.TransportSettings.GatewayIsSupported != 0
                && _rdpSettings.GatewayMode != EGatewayMode.DoNotUseGateway)
            {
                // https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings-gatewayprofileusagemethod
                _rdpClient.TransportSettings2.GatewayProfileUsageMethod = 1; // Use explicit settings, as specified by the user.

                // ref: https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings-gatewayusagemethod
                _rdpClient.TransportSettings.GatewayUsageMethod = _rdpSettings.GatewayMode switch
                {
                    EGatewayMode.UseTheseGatewayServerSettings =>
                    1 // 1 : Always use an RD Gateway server. In the RDC client UI, the Bypass RD Gateway server for local addresses check box is cleared.
                    ,
                    EGatewayMode.AutomaticallyDetectGatewayServerSettings =>
                    2 // 2 : Use an RD Gateway server if a direct connection cannot be made to the RD Session Host server. In the RDC client UI, the Bypass RD Gateway server for local addresses check box is selected.
                    ,
                    _ => throw new ArgumentOutOfRangeException()
                };

                _rdpClient.TransportSettings2.GatewayHostname = _rdpSettings.GatewayHostName;
                //_rdpClient.TransportSettings2.GatewayDomain = "XXXXX";

                // ref: https://docs.microsoft.com/en-us/windows/win32/termserv/imsrdpclienttransportsettings-gatewaycredssource
                // TSC_PROXY_CREDS_MODE_USERPASS (0): Use a password (NTLM) as the authentication method for RD Gateway.
                // TSC_PROXY_CREDS_MODE_SMARTCARD (1): Use a smart card as the authentication method for RD Gateway.
                // TSC_PROXY_CREDS_MODE_ANY (4): Use any authentication method for RD Gateway.
                switch (_rdpSettings.GatewayLogonMethod)
                {
                    case EGatewayLogonMethod.SmartCard:
                        _rdpClient.TransportSettings.GatewayCredsSource = 1; // TSC_PROXY_CREDS_MODE_SMARTCARD
                        break;

                    case EGatewayLogonMethod.Password:
                        _rdpClient.TransportSettings.GatewayCredsSource = 0; // TSC_PROXY_CREDS_MODE_USERPASS
                        _rdpClient.TransportSettings2.GatewayUsername = _rdpSettings.GatewayUserName;
                        _rdpClient.TransportSettings2.GatewayPassword = _rdpSettings.GatewayPassword;
                        break;

                    default:
                        _rdpClient.TransportSettings.GatewayCredsSource = 4; // TSC_PROXY_CREDS_MODE_ANY
                        break;
                }

                _rdpClient.TransportSettings2.GatewayCredSharing = 0;
            }

            #endregion Gateway
        }

        private void InitRdp(int width = 0, int height = 0, bool isReconnecting = false)
        {
            if (Status != ProtocolHostStatus.NotInit)
                return;
            try
            {
                Status = ProtocolHostStatus.Initializing;
                RdpClientDispose();
                CreateRdpClient();
                RdpInitServerInfo();
                RdpInitStatic();
                RdpInitConnBar();
                RdpInitRedirect();
                RdpInitDisplay(width, height, isReconnecting);
                RdpInitPerformance();
                RdpInitGateway();
                _rdpSettings.ApplyRdpControlAdditionalSettings(_rdpClient!);
                Status = ProtocolHostStatus.Initialized;
            }
            catch (Exception e)
            {
                GridMessageBox.Visibility = Visibility.Visible;
                TbMessageTitle.Visibility = Visibility.Collapsed;
                TbMessage.Text = e.Message;

                Status = ProtocolHostStatus.NotInit;
            }
        }

        #region ultima immagine durante la riconnessione
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>immagine attuale dello schermo remoto (PrintWindow: non cattura le finestre che ci stanno sopra)</summary>
        private System.Windows.Media.Imaging.BitmapSource? CaptureFrame()
        {
            try
            {
                var ctl = RdpHost.Child;
                if (ctl == null || !ctl.IsHandleCreated || ctl.Width < 50 || ctl.Height < 50) return null;
                using var bmp = new System.Drawing.Bitmap(ctl.Width, ctl.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    var ok = PrintWindow(ctl.Handle, hdc, 2);   // 2 = PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                    if (!ok) return null;
                }
                // un'immagine tutta nera vuol dire che il controllo non si e' lasciato fotografare: meglio il velo semplice
                var dark = true;
                for (var i = 0; i < 40 && dark; i++)
                {
                    var px = bmp.GetPixel((i * 37) % bmp.Width, (i * 53) % bmp.Height);
                    if (px.R + px.G + px.B > 24) dark = false;
                }
                if (dark) return null;
                var hbmp = bmp.GetHbitmap();
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DeleteObject(hbmp); }
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning("RDP Host: CaptureFrame " + e.Message);
                return null;
            }
        }

        /// <summary>su piu' monitor il cerchio di attesa starebbe a cavallo tra i due schermi: si sposta al centro del monitor principale</summary>
        private void CenterLoadingOnPrimaryScreen(bool multi)
        {
            try
            {
                if (!multi) { GridLoading.Margin = new Thickness(0); return; }
                var all = ScreenInfoEx.GetAllScreensSize();
                var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? all;
                var extra = (all.Width - primary.Width) / System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                GridLoading.Margin = primary.X > all.X ? new Thickness(extra, 0, 0, 0) : new Thickness(0, 0, extra, 0);
            }
            catch { GridLoading.Margin = new Thickness(0); }
        }

        private void ShowFrozenFrame(System.Windows.Media.Imaging.BitmapSource? frame)
        {
            FrozenFrame.Source = frame;
            FrozenFrame.Visibility = frame != null ? Visibility.Visible : Visibility.Collapsed;
            if (frame != null) GridLoading.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 0, 0, 0));
            else GridLoading.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BackgroundBrush");
        }

        private void HideFrozenFrame()
        {
            if (FrozenFrame.Visibility != Visibility.Visible) return;
            FrozenFrame.Visibility = Visibility.Collapsed;
            FrozenFrame.Source = null;
            GridLoading.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BackgroundBrush");
        }
        #endregion

        #region Base Interface
        private readonly Stopwatch _diagWatch = new();
        /// <summary>tempi della connessione: a ogni tappa scrive i millisecondi passati da quando parte il collegamento</summary>
        private void Diag(string what) => Lemora.Utils.Diag.Log($"RDP-DIAG({_rdpSettings.Address}): +{_diagWatch.ElapsedMilliseconds} ms {what}");

        public override void Conn()
        {
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (Status == ProtocolHostStatus.Connected || Status == ProtocolHostStatus.Connecting)
                    {
                        return;
                    }
                    _diagWatch.Restart();
                    Diag("parte il collegamento");

                    Status = ProtocolHostStatus.Connecting;
                    CenterLoadingOnPrimaryScreen(FullScreenOnAllMonitors && InFullScreenContext);
                    GridLoading.Visibility = Visibility.Visible;
                    RdpHost.Visibility = Visibility.Collapsed;
                    _rdpClient.Connect();
                    Status = ProtocolHostStatus.Connected;
                }
                catch (Exception e)
                {
                    GridMessageBox.Visibility = Visibility.Visible;
                    TbMessageTitle.Visibility = Visibility.Collapsed;
                    TbMessage.Text = e.Message;
                    Status = ProtocolHostStatus.Disconnected;
                }
            });
        }

        public override void Close()
        {
            this.Dispose();
            base.Close();
        }

        protected override void GoFullScreen()
        {
            if (_rdpSettings.RdpFullScreenFlag == ERdpFullScreenFlag.Disable
                || ParentWindow is not FullScreenWindowView
                || _rdpClient?.FullScreen == true)
            {
                return;
            }
            Debug.Assert(_rdpClient != null); if (_rdpClient == null) return;
            if (_rdpClient.FullScreen != true)
                _rdpClient.FullScreen = true; // this will invoke OnRequestGoFullScreen -> MakeNormal2FullScreen
        }

        public override ProtocolHostType GetProtocolHostType()
        {
            return ProtocolHostType.Native;
        }

        public override IntPtr GetHostHwnd()
        {
            return IntPtr.Zero;
        }

        public override bool CanResizeNow()
        {
            return Status == ProtocolHostStatus.Connected || Status == ProtocolHostStatus.Disconnected;
        }

        #endregion Base Interface


        #region WindowOnResizeEnd

        private readonly Timer _resizeEndTimer = new Timer(500) { Enabled = false, AutoReset = false };
        private readonly object _resizeEndLocker = new object();
        private bool _canAutoResizeByWindowSizeChanged = true;

        /// <summary>
        /// when tab window goes to min from max, base.SizeChanged invoke and size will get bigger, normal to min will not tiger this issue, don't know why.
        /// so stop resize when window status change to min until status restore.
        /// </summary>
        /// <param name="isEnable"></param>
        public override bool CanChangeDisplayModeLive => true;

        /// <summary>
        /// applica a sessione aperta come si adatta lo schermo remoto alla finestra (adatta, scala, fisso), la risoluzione e la scala;
        /// legge i valori dall'oggetto server della sessione, che il pannello delle impostazioni modifica
        /// </summary>
        public override void ApplyDisplaySettings()
        {
            Execute.OnUIThreadSync(() =>
            {
                if (_rdpClient == null) return;
                try
                {
                    var mode = _rdpSettings.RdpWindowResizeMode;
                    _rdpClient.AdvancedSettings2.SmartSizing = mode == ERdpWindowResizeMode.Stretch || (mode == ERdpWindowResizeMode.AutoResize && _resizeUnsupported);
                    uint w, h;
                    if (mode == ERdpWindowResizeMode.Stretch || mode == ERdpWindowResizeMode.Fixed)
                    {
                        w = _rdpSettings.RdpWidth is >= 100 ? (uint)_rdpSettings.RdpWidth.Value : (uint)_rdpClient.Width;
                        h = _rdpSettings.RdpHeight is >= 100 ? (uint)_rdpSettings.RdpHeight.Value : (uint)_rdpClient.Height;
                    }
                    else
                    {
                        // "adatta alla finestra": la risoluzione e' la dimensione del controllo in PIXEL (come in ReSizeRdpToControlSize),
                        // non quella in unita' WPF: con la scala di Windows al 125% darebbe uno schermo piu' piccolo con bordi grigi
                        w = (uint)_rdpClient.Width;
                        h = (uint)_rdpClient.Height;
                    }
                    if (w >= 100 && h >= 100) SetRdpResolution(w, h, true);
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                }
            });
        }

        public override System.Windows.Size RemoteDesktopSize
        {
            get
            {
                var w = _rdpClient?.DesktopWidth ?? 0; var h = _rdpClient?.DesktopHeight ?? 0;
                return w > 0 && h > 0 ? new System.Windows.Size(w, h) : System.Windows.Size.Empty;
            }
        }

        private ERdpWindowResizeMode? _modeBeforeFit;

        /// <summary>
        /// Picture in picture: la risoluzione remota resta quella che era e lo schermo si riduce per stare tutto nella finestra
        /// (come "scala nella finestra"). All'uscita si torna alla modalita' scelta e, se era "adatta alla finestra", la risoluzione si riadatta.
        /// </summary>
        public override void SetFitToWindow(bool on)
        {
            Execute.OnUIThreadSync(() =>
            {
                if (_rdpClient == null) return;
                try
                {
                    if (on)
                    {
                        if (_modeBeforeFit == null) _modeBeforeFit = _rdpSettings.RdpWindowResizeMode;
                        Lemora.Service.RdpSessionSettingsSaver.Ignoring(() => _rdpSettings.RdpWindowResizeMode = ERdpWindowResizeMode.Stretch);   // la modalita' "adatta" non agisce piu': niente cambi di risoluzione
                        _rdpClient.AdvancedSettings2.SmartSizing = true;
                    }
                    else if (_modeBeforeFit != null)
                    {
                        var back = _modeBeforeFit.Value;
                        Lemora.Service.RdpSessionSettingsSaver.Ignoring(() => _rdpSettings.RdpWindowResizeMode = back);
                        _modeBeforeFit = null;
                        ApplyDisplaySettings();
                    }
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                }
            });
        }

        public override void ToggleAutoResize(bool isEnable)
        {
            lock (_resizeEndLocker)
            {
                _canAutoResizeByWindowSizeChanged = isEnable;
            }
        }

        private void ParentWindowResize_StartWatch()
        {
            lock (_resizeEndLocker)
            {
                _resizeEndTimer.Elapsed -= ResizeEndTimerOnElapsed;
                _resizeEndTimer.Elapsed += ResizeEndTimerOnElapsed;
                base.SizeChanged -= WindowSizeChanged;
                base.SizeChanged += WindowSizeChanged;
            }
        }

        private void ParentWindowResize_StopWatch()
        {
            lock (_resizeEndLocker)
            {
                _resizeEndTimer.Stop();
                _resizeEndTimer.Elapsed -= ResizeEndTimerOnElapsed;
                base.SizeChanged -= WindowSizeChanged;
            }
        }

        private uint _previousWidth = 0;
        private uint _previousHeight = 0;
        private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ParentWindow?.WindowState != WindowState.Minimized
                && _canAutoResizeByWindowSizeChanged
                && this._rdpSettings.RdpWindowResizeMode == ERdpWindowResizeMode.AutoResize)
            {
                // start a timer to resize RDP after 500ms
                var nw = (uint)e.NewSize.Width;
                var nh = (uint)e.NewSize.Height;
                if (nw != _previousWidth || nh != _previousHeight)
                {
                    _previousWidth = (uint)e.NewSize.Width;
                    _previousHeight = (uint)e.NewSize.Height;
                    Execute.OnUIThreadSync(() =>
                    {
                        _loginResizeTimer.Stop();
                        _resizeEndTimer.Stop();
                        _resizeEndTimer.Start();
                    });
                }
            }
        }

        private void ResizeEndTimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            ReSizeRdpToControlSize();
        }

        #endregion WindowOnResizeEnd

        private void DisposeRdpClient()
        {
            lock (_rdpClientDisposeLock)
            {
                try
                {
                    if (_rdpClient is { IsDisposed: false })
                    {
                        _rdpClient.Dispose();
                    }
                    _rdpClient = null;
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error($"Error disposing RDP client: {e}");
                }
            }
        }

        private void RdpClientDispose()
        {
            GlobalEventHelper.OnScreenResolutionChanged -= OnScreenResolutionChanged;
            _followTimer?.Stop();
            try
            {
                // Use synchronous disposal to ensure the RDP client is fully disposed before continuing
                // This prevents race conditions where the client might be accessed or disposed multiple times
                Execute.OnUIThreadSync(DisposeRdpClient);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error($"Error scheduling RDP client disposal on UI thread: {e}");
                // 如果UI线程调度失败，直接处理
                DisposeRdpClient();
            }
            SimpleLogHelper.Debug("RDP Host: _rdpClient.Disposed.");
        }




        private static bool _isReSizeRdpToControlSizeRunning = false;
        /// <summary>
        /// set remote resolution to _rdpClient size if is AutoResize
        /// if focus == false, then set size only if new size != old size
        /// </summary>
        private void ReSizeRdpToControlSize()
        {
            if (!_flagHasConnected
                || _rdpClient?.FullScreen != false
                || _rdpSettings.RdpWindowResizeMode != ERdpWindowResizeMode.AutoResize) return;


            lock (this)
            {
                if (_isReSizeRdpToControlSizeRunning == true)
                {
                    SimpleLogHelper.Debug($@"ReSizeRdpToControlSize return by isReSizeRdpToControlSizeRunning == true");
                    return;
                }
                _isReSizeRdpToControlSizeRunning = true;
            }


            Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    // Window drag and drop resize only after mouse button release, 当拖动最大化的窗口时，需检测鼠标按键释放后再调整分辨率，详见：segnalazione 553 del progetto originale
                    var isPressed = false;
                    Execute.OnUIThreadSync(() => { isPressed = Mouse.LeftButton == MouseButtonState.Pressed; });
                    if (!isPressed)
                        break;
#if DEBUG
                    SimpleLogHelper.Debug($@"RDP ReSizeRdpToControlSize  delay since mouse is pressed");
#endif
                    Thread.Sleep(300);
                }

                var nw = (uint)(_rdpClient?.Width ?? 0);
                var nh = (uint)(_rdpClient?.Height ?? 0);
                // tip: the control default width is 288
                if (_rdpClient?.DesktopWidth != nw
                    || _rdpClient?.DesktopHeight != nh)
                {
                    SetRdpResolution(nw, nh, false);
                }

                lock (this)
                {
                    _isReSizeRdpToControlSizeRunning = false;
                }
            });
        }


        private uint _lastScaleFactor = 0;
        /// <summary>
        /// if focus == false, then set size only if new size != old size
        /// </summary>
        private void SetRdpResolution(uint w, uint h, bool focus = false)
        {
            if (w <= 0 || h <= 0) return;

            lock (_resizeEndLocker)
            {
                if (_canAutoResizeByWindowSizeChanged == false) return;
            }

            _primaryScaleFactor = CurrentMonitorScaleFactor();
            var newScaleFactor = _primaryScaleFactor;
            if (this._rdpSettings is { IsScaleFactorFollowSystem: false, ScaleFactorCustomValue: { } })
                newScaleFactor = this._rdpSettings.ScaleFactorCustomValue ?? _primaryScaleFactor;
            bool needUpdate = focus
                         || _rdpClient?.DesktopWidth != w
                         || _rdpClient?.DesktopHeight != h
                         || newScaleFactor != _lastScaleFactor;
            if (newScaleFactor != 100)
            {
                // in this case we allow 1pix error
                needUpdate = focus
                        || Math.Abs((int)(_rdpClient?.DesktopWidth ?? 0) - (int)w) > 1
                        || Math.Abs((int)(_rdpClient?.DesktopHeight ?? 0) - (int)h) > 1
                        || newScaleFactor != _lastScaleFactor;
            }
            SimpleLogHelper.Debug($@"SetRdpResolution needUpdate = {needUpdate}, UpdateSessionDisplaySettings, by: W = {_rdpClient?.DesktopWidth} -> {w}, H = {_rdpClient?.DesktopHeight} -> {h}, ScaleFactor = {_lastScaleFactor} -> {newScaleFactor}, focus = {focus}");
            if (_resizeUnsupported) return;   // server vecchio: l'immagine e' gia' scalata nella finestra
            if (needUpdate)
            {
                Diag($"cambio risoluzione remota {w}x{h} (scala {newScaleFactor})");
                Execute.OnUIThreadSync(() =>
                {
                    try
                    {
                        _lastScaleFactor = newScaleFactor;
                        _rdpClient?.UpdateSessionDisplaySettings(w, h, w, h, 0, newScaleFactor, 100);
                    }
                    catch (COMException)
                    {
                        // ignore error code 0x8000FFFF
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Error(e);
                    }
                });
                if (_rdpSettings.RdpWindowResizeMode == ERdpWindowResizeMode.AutoResize) CheckResizeAccepted(w, h);
            }
        }

        /// <summary>true se il server ha rifiutato il cambio di risoluzione (prima di Windows Server 2012 R2): "adatta alla finestra" non puo' funzionare</summary>
        private bool _resizeUnsupported;
        public override bool RemoteResizeSupported => !_resizeUnsupported;

        /// <summary>
        /// Dopo qualche secondo la risoluzione del server deve essere quella chiesta; se non lo e', il server non sa ridimensionarsi:
        /// si passa alla modalita' "estesa" (l'immagine si scala per stare nella finestra, senza barre di scorrimento).
        /// </summary>
        private async void CheckResizeAccepted(uint w, uint h)
        {
            await Task.Delay(3000);
            Execute.OnUIThread(() =>
            {
                if (_rdpClient == null || _resizeUnsupported) return;
                if (Math.Abs((int)_rdpClient.DesktopWidth - (int)w) <= 1 && Math.Abs((int)_rdpClient.DesktopHeight - (int)h) <= 1) return;
                _resizeUnsupported = true;
                _rdpClient.AdvancedSettings2.SmartSizing = true;
                Lemora.Utils.Diag.Log($"RDP-DIAG: il server non ha accettato {w}x{h} (resta {_rdpClient.DesktopWidth}x{_rdpClient.DesktopHeight}): passo a schermo scalato nella finestra");
            });
        }

        /// <summary>
        /// "Abilita (monitor multipli)", ma solo se tutti i monitor hanno la stessa scala di Windows. Con scale diverse (es. 100% e 125%)
        /// il programma vede i monitor in coordinate logiche che non combaciano con quelle vere (tra i due restano buchi) e lo schermo
        /// intero su tutti i monitor usciva ingrandito e spostato (4800x1350 invece di 3840x1200): in quel caso si usa il monitor corrente.
        /// </summary>
        private bool FullScreenOnAllMonitors
        {
            get
            {
                if (_rdpSettings.RdpFullScreenFlag == ERdpFullScreenFlag.Disable) return false;
                var selected = Lemora.Utils.PhysicalScreens.ParseSelection(_rdpSettings.RdpFullScreenMonitors);
                if (selected.Count > 0) return selected.Count > 1;   // scelta esplicita nel selettore dei monitor
                return _rdpSettings.RdpFullScreenFlag == ERdpFullScreenFlag.EnableFullAllScreens && !Lemora.Utils.PhysicalScreens.HasMixedScaling();
            }
        }

        private bool _connectedMultimon;

        /// <summary>la sessione non sta in una finestra "normale" (scheda, finestra staccata): senza finestra ancora assegnata la connessione parte a schermo intero</summary>
        private bool InFullScreenContext => ParentWindow == null || ParentWindow is FullScreenWindowView;
        private System.Windows.Threading.DispatcherTimer? _followTimer;

        /// <summary>
        /// I monitor collegati sono cambiati con la sessione a schermo intero e "segui i monitor" e' attivo: la finestra si risistema sui monitor presenti
        /// e la risoluzione remota li segue (su piu' monitor serve una riconnessione rapida).
        /// </summary>
        private void FollowCurrentMonitors()
        {
            Execute.OnUIThread(() =>
            {
                _followTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _followTimer.Tick -= FollowTimerTick;
                _followTimer.Tick += FollowTimerTick;   // i monitor si assestano in piu' passi: si agisce solo all'ultimo cambio
                _followTimer.Stop();
                _followTimer.Start();
            });
        }

        private void FollowTimerTick(object? sender, EventArgs e)
        {
            _followTimer?.Stop();
            try
            {
                if (ParentWindow is not FullScreenWindowView win || _rdpClient == null) return;
                SourceScreenIndex = -1;
                var rect = GetScreenSizeIfRdpIsFullScreen();
                var multi = FullScreenOnAllMonitors;
                SimpleLogHelper.Debug($"RDP follow monitors: {rect}, multi = {multi}");
                PlaceFullScreenWindowInPixels(win, rect);
                if (multi != _connectedMultimon || (multi && (rect.Width != _rdpClient.DesktopWidth || rect.Height != _rdpClient.DesktopHeight)))
                    ReConn();
                else if (!multi)
                    SetRdpResolution((uint)rect.Width, (uint)rect.Height, true);
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Error(ex);
            }
        }

        private System.Drawing.Rectangle GetScreenSizeIfRdpIsFullScreen()
        {
            var selected = Lemora.Utils.PhysicalScreens.ParseSelection(_rdpSettings.RdpFullScreenMonitors);
            if (selected.Count > 0 && _rdpSettings.RdpFullScreenFlag != ERdpFullScreenFlag.Disable)
            {
                LocalityConnectRecorder.RdpCacheUpdate(_rdpSettings.Id, true, selected.Count > 1 ? -1 : selected[0]);
                return Lemora.Utils.PhysicalScreens.UnionOf(selected);
            }

            if (FullScreenOnAllMonitors)
            {
                LocalityConnectRecorder.RdpCacheUpdate(_rdpSettings.Id, true, -1);
                return ScreenInfoEx.GetAllScreensSize();
            }

            // prima il monitor su cui sta la finestra della sessione, poi quello dell'ultima volta (sessioni che partono gia' a schermo intero)
            int screenIndex = SourceScreenIndex >= 0 ? SourceScreenIndex : (LocalityConnectRecorder.RdpCacheGet(_rdpSettings.Id)?.FullScreenLastSessionScreenIndex ?? -1);
            if (screenIndex < 0
                || screenIndex >= System.Windows.Forms.Screen.AllScreens.Length)
            {
                screenIndex = this.ParentWindow != null ? ScreenInfoEx.GetCurrentScreen(this.ParentWindow).Index : ScreenInfoEx.GetCurrentScreenBySystemPosition(ScreenInfoEx.GetMouseSystemPosition()).Index;
            }
            LocalityConnectRecorder.RdpCacheUpdate(_rdpSettings.Id, true, screenIndex);
            return System.Windows.Forms.Screen.AllScreens[screenIndex].Bounds;
        }

        /// <summary>
        /// set the parent window of rdp, if parent window is FullScreenWindowView and it's loaded, go full screen
        /// </summary>
        /// <param name="value"></param>
        public override void SetParentWindow(WindowBase? value)
        {
            base.SetParentWindow(value);
            if (value is FullScreenWindowView && value.IsLoaded && value.IsClosed == false)
            {
                this.GoFullScreen();
            }
            // INVESTIGATION #1119 (candidate fix): when the host is reparented to a tab window while the
            // control still reports FullScreen == true, the control's fullscreen state desyncs from its
            // container. Every later return to a fullscreen window then no-ops in GoFullScreen()
            // (early return on FullScreen == true), mstscax never re-runs its fullscreen transition, and
            // the connection bar stays hidden forever (issue #1119 symptom; only Ctrl+Alt+Home revives it).
            // Forcing the flag false here mirrors what the control does when the user restores via the
            // connection bar (OnConnectionBarRestoreWindowCall), whose early-return guard
            // (ParentWindow is not FullScreenWindowView) makes this re-entry safe.
            else if ((value is TabWindowView || value is MainWindowView || value is DetachedSessionWindow) && _rdpClient?.FullScreen == true)
            {
                Execute.OnUIThread(() =>
                {
                    if (_rdpClient?.FullScreen == true)
                        _rdpClient.FullScreen = false;
                });
            }
            // tornando in finestra da "schermi multipli" il desktop remoto e' largo quanto tutti i monitor: ci si riconnette con la misura della finestra
            if ((value is TabWindowView || value is MainWindowView || value is DetachedSessionWindow) && _connectedMultimon)
            {
                _connectedMultimon = false;
                CoverNow(false);   // il desktop remoto largo due monitor, stirato nella finestra, non deve vedersi nell'attesa della riconnessione
                // fuori dallo schermo intero la scelta "schermi multipli" non ha piu' senso: le impostazioni tornano a "schermo singolo"
                Execute.OnUIThread(() => Lemora.Service.RdpSessionSettingsSaver.Ignoring(() => _rdpSettings.RdpFullScreenFlag = ERdpFullScreenFlag.EnableFullScreen));
                // non subito: la finestra a schermo intero si sta ancora chiudendo e il controllo RDP non va distrutto mentre cambia contenitore (log: il programma si bloccava qui)
                var target = value;
                var seq = _reconnSeq;
                _ = Task.Delay(1500).ContinueWith(_ => Execute.OnUIThread(() =>
                {
                    if (_reconnSeq != seq) return;   // nel frattempo si e' gia' riconnessa da sola (nuovo tentativo dopo la disconnessione): una seconda riconnessione e' inutile
                    if (ParentWindow == target && target.IsLoaded && !target.IsClosed && _rdpClient != null)
                        ReConn();
                    else if (_rdpClient != null && Status == ProtocolHostStatus.Connected)
                    {
                        // la finestra e' cambiata nel frattempo: si toglie la copertura
                        RdpHost.Visibility = Visibility.Visible;
                        GridLoading.Visibility = Visibility.Collapsed;
                    }
                }));
            }
        }

        public override void FocusOnMe()
        {
            Execute.OnUIThread(() =>
            {
                // Kill logical focus
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(RdpHost), null);
                Keyboard.ClearFocus();
                this.Focus();
                RdpHost.Focus();
                if (_rdpClient is { } rdp)
                {
                    // try to fix segnalazione 530 del progetto originale, but failed
                    rdp.Focus();
                    //rdp.Show();
                    //rdp.Update();
                    //rdp.Refresh();
                    //rdp.BringToFront();
                }
            });
        }
    }
}