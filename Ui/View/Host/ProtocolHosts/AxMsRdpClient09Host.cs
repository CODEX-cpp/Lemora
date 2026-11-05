using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AxMSTSCLib;
using MSTSCLib;
using Lemora.Model.Protocol;
using Lemora.Service.Locality;
using Lemora.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.Controls;
using Stylet;
using Lemora.Service;
using Lemora.Utils.Tracing;

namespace Lemora.View.Host.ProtocolHosts
{
    public partial class AxMsRdpClient09Host : HostBase, IDisposable
    {
        private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
        {
            this.Dispose();
            base.OnClosed?.Invoke(base.ConnectionId);
        }
        /// <summary>"Cambia credenziali": se il server rifiuta l'accesso ma il codice d'errore non e' tra quelli riconosciuti, si puo' sempre reinserirle dal programma</summary>
        private void BtnCreds_OnClick(object sender, RoutedEventArgs e) => HandleBadCredentials("richiesta manuale", disconnectFirst: false);

        private void BtnReconn_OnClick(object sender, RoutedEventArgs e)
        {
            ReConn();
        }

        private bool _skipNextFrame;
        private int _reconnSeq;

        public override void ShowSwitchCover() => CoverNow(true);

        /// <summary>copre subito la sessione con il caricamento (l'immagine vecchia, della misura sbagliata, non deve restare a vista nemmeno per un attimo)</summary>
        private void CoverNow(bool multi)
        {
            _skipNextFrame = true;
            Execute.OnUIThread(() =>
            {
                ShowFrozenFrame(null);
                CenterLoadingOnPrimaryScreen(multi);
                RdpHost.Visibility = System.Windows.Visibility.Collapsed;
                GridLoading.Visibility = System.Windows.Visibility.Visible;
            });
        }

        public override void ReConn()
        {
            Debug.Assert(_rdpClient != null);
            if (Status != ProtocolHostStatus.Connected
                && Status != ProtocolHostStatus.Disconnected)
            {
                SimpleLogHelper.Warning($"RDP Host: Call ReConn, but current status = " + Status);
                return;
            }
            else
            {
                SimpleLogHelper.Warning($"RDP Host: Call ReConn");
            }
            ++_reconnSeq;
            var wasConnected = Status == ProtocolHostStatus.Connected;
            Status = ProtocolHostStatus.WaitingForReconnect;
            Execute.OnUIThreadSync(() =>
            {
                // si tiene ferma l'ultima immagine dello schermo, con un velo e il cerchio di attesa: la riconnessione sembra una pausa
                // passando da uno a piu' monitor l'immagine vecchia (un solo schermo) finirebbe in mezzo ai due: meglio solo il velo
                var toMulti = FullScreenOnAllMonitors && InFullScreenContext && !_connectedMultimon;
                var frame = wasConnected && !toMulti && !_skipNextFrame ? CaptureFrame() : null;
                _skipNextFrame = false;
                ShowFrozenFrame(frame);
                CenterLoadingOnPrimaryScreen(FullScreenOnAllMonitors && InFullScreenContext);
                RdpHost.Visibility = System.Windows.Visibility.Collapsed;
                GridLoading.Visibility = System.Windows.Visibility.Visible;
            });
            RdpClientDispose();


            var t = Task.Factory.StartNew(async () =>
            {
                // check if it needs to auto switch address
                var isAutoAlternateAddressSwitching = _rdpSettings.IsAutoAlternateAddressSwitching == true
                                                      // if none of the alternate credential has host or port，then disabled `AutoAlternateAddressSwitching`
                                                      && _rdpSettings.AlternateCredentials.Any(x => !string.IsNullOrEmpty(x.Address) || !string.IsNullOrEmpty(x.Port));
                if (isAutoAlternateAddressSwitching)
                {
                    var c = await SessionControlService.GetCredential(_rdpSettings);
                    if (c != null)
                    {
                        _rdpSettings.SetCredential(c, true);
                        _rdpSettings.DisplayName = c.Name;
                    }
                }

                Status = ProtocolHostStatus.NotInit;

                await Execute.OnUIThreadAsync(() =>
                {
                    int w = 0;
                    int h = 0;
                    if (ParentWindow is TabWindowView tab)
                    {
                        var size = tab.GetTabContentSize(ColorAndBrushHelper.ColorIsTransparent(this._rdpSettings.ColorHex) == true);
                        w = (int)size.Width;
                        h = (int)size.Height;
                    }
                    else if (ParentWindow is MainWindowView mainWindow)
                    {
                        // sessione nella finestra principale: riparte gia' alla dimensione dell'area, senza passare da 800x600
                        var area = mainWindow.SessionAreaSize;
                        w = (int)area.Width;
                        h = (int)area.Height;
                    }
                    InitRdp(w, h, true);
                    Conn();
                });
            });
        }


        private void ParentWindowSetToWindow()
        {
            // make sure ParentWindow is FullScreen Window
            if (ParentWindow is not FullScreenWindowView)
            {
                return;
            }

            if (ParentWindow is FullScreenWindowView { IsLoaded: false })
            {
                return;
            }

            ParentWindow.Topmost = false;
            ParentWindow.ResizeMode = ResizeMode.CanResize;
            ParentWindow.WindowStyle = WindowStyle.SingleBorderWindow;
            ParentWindow.WindowState = WindowState.Normal;
            ParentWindow.Width = FullScreenWindowView.DESIGN_WIDTH / (_primaryScaleFactor / 100.0);
            ParentWindow.Height = FullScreenWindowView.DESIGN_HEIGHT / (_primaryScaleFactor / 100.0);
            var screenEx = ScreenInfoEx.GetCurrentScreen(this.ParentWindow);
            ParentWindow.Top = screenEx.VirtualWorkingAreaCenter.Y - ParentWindow.Height / 2;
            ParentWindow.Left = screenEx.VirtualWorkingAreaCenter.X - ParentWindow.Width / 2;
        }



        #region event handler

        private int _retryCount = 0;
        private const int MAX_RETRY_COUNT = 5;
        private bool _handlingBadCredentials;

        /// <summary>codici del controllo RDP che significano "credenziali non accettate" (autenticazione di rete / accesso rifiutato)</summary>
        private static bool IsCredentialFailure(int discReason, ExtendedDisconnectReasonCode ext)
            => ext == ExtendedDisconnectReasonCode.exDiscReasonRdpEncInvalidCredentials
               || discReason == 2055      // 0x807: autenticazione (CredSSP/NLA) non riuscita
               || discReason == 2823      // 0xB07: accesso negato
               || discReason == 3335;     // 0xD07: account bloccato o accesso non consentito

        private void OnRdpClientLogonError(object sender, IMsTscAxEvents_OnLogonErrorEvent e)
        {
            SimpleLogHelper.Warning($"RDP({_rdpSettings.DisplayName}) OnLogonError lError={e.lError}");
            // -1 = password errata, -2 = password da cambiare (non sono credenziali "da ripetere")
            if (e.lError == -1) Execute.OnUIThread(() => HandleBadCredentials($"OnLogonError {e.lError}", disconnectFirst: true));
        }

        /// <summary>
        /// Credenziali rifiutate prima del collegamento: si toglie la password salvata (se era dalla cronologia), si mostra la richiesta del programma
        /// (non quella di Windows) e si riprova con le nuove.
        /// </summary>
        private void HandleBadCredentials(string why, bool disconnectFirst)
        {
            if (_handlingBadCredentials || _flagHasEverConnected || _rdpClient == null) return;
            _handlingBadCredentials = true;
            SimpleLogHelper.Warning($"RDP({_rdpSettings.DisplayName}): credenziali non accettate ({why})");
            try
            {
                ActivityReason = $"credenziali non accettate ({why}): si chiedono di nuovo";
                Status = ProtocolHostStatus.Disconnected;
                if (disconnectFirst) { try { _rdpClient.Disconnect(); } catch { /* gia' scollegato */ } }
                RdpHost.Visibility = Visibility.Collapsed;
                GridLoading.Visibility = Visibility.Collapsed;
                QuickSessionFollowUp.ForgetSavedCredentials(ProtocolServer);

                var quick = ProtocolServer.IsTmpSession();
                var dlg = new Lemora.View.Editor.PasswordPopupDialogViewModel(false, quick && IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
                {
                    Title = $"[RDP]{_rdpSettings.Address}",
                    UserName = _rdpSettings.UserName,
                    ErrorText = IoC.Translate("password_wrong"),   // contorno rosso + "Password errata" sotto il campo
                };
                FolderCredentialHints.Apply(dlg, _rdpSettings.Address, ProtocolServer.TreeNodes, useDomain: true);
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Password))
                {
                    _rdpSettings.UserName = dlg.UserName;
                    _rdpSettings.Password = dlg.Password;
                    QuickSessionFollowUp.UpdateCredentials(ProtocolServer, dlg.UserName, dlg.Password, "", dlg.CanRememberInfo && dlg.IsRememberInfo);
                    _handlingBadCredentials = false;
                    ReConn();
                    return;
                }
                // richiesta annullata: resta il messaggio con "Riconnetti"
                GridMessageBox.Visibility = Visibility.Visible;
                TbMessageTitle.Visibility = Visibility.Collapsed;
                BtnReconn.Visibility = Visibility.Visible;
                TbMessage.Text = IoC.Translate("host_bad_credentials");
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Error(ex);
            }
            finally
            {
                _handlingBadCredentials = false;
            }
        }

        private void OnRdpClientDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e)
        {
            SimpleLogHelper.Debug("RDP Host: RdpOnDisconnected");
            if (_handlingBadCredentials) return;   // la richiesta delle credenziali e' gia' in corso

            lock (this)
            {
                if (_rdpClient == null) return;

                // Registro attivita': il motivo di Windows (testo e codici) prima di passare a Disconnected
                try { ActivityReason = $"{_rdpClient.GetErrorDescription((uint)e.discReason, (uint)_rdpClient.ExtendedDisconnectReason)} (codice {e.discReason}, {_rdpClient.ExtendedDisconnectReason})"; } catch { ActivityReason = $"codice {e.discReason}"; }
                Status = ProtocolHostStatus.Disconnected;
                ParentWindowResize_StopWatch();

                // https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected
                const int disconnectReasonLocalNotError = 1;  // Local disconnection. This is not an error code. Note that this will also be returned if the user cancels the RdpClient's attempt to reconnect.
                const int disconnectReasonRemoteByUser = 2;   // Remote disconnection by user (via 'Disconnect', 'Signout', 'Shutdown' and 'Reboot'). This is not an error code.
                const int disconnectReasonByServer = 3;       // Remote disconnection by server. This will be returned when switching to another connection. This is not an error code.

                string reason = _rdpClient.GetErrorDescription((uint)e.discReason, (uint)_rdpClient.ExtendedDisconnectReason) ?? "";
                ExtendedDisconnectReasonCode excode = _rdpClient.ExtendedDisconnectReason;
                SimpleLogHelper.Debug($"RDP({_rdpSettings.DisplayName}) Disconnected with code {e.discReason}({reason}) ex:{excode}");
                if (!_flagHasEverConnected)
                    SimpleLogHelper.Warning($"RDP({_rdpSettings.DisplayName}) disconnessa prima del collegamento: code={e.discReason} ex={excode} ({reason})");   // serve a riconoscere i codici delle credenziali errate
                if (!_flagHasEverConnected && IsCredentialFailure(e.discReason, excode))
                {
                    Execute.OnUIThread(() => HandleBadCredentials($"code {e.discReason} ex {excode}", disconnectFirst: false));
                    return;
                }

                switch (e.discReason)
                {
                    case disconnectReasonRemoteByUser:
                        // we can close the window immediately in the case of disconnectReasonRemoteByUser. No further case analysis is needed.
                        RdpClientDispose();
                        base.OnClosed?.Invoke(base.ConnectionId);
                        break;

                    case disconnectReasonByServer:
                        // https://learn.microsoft.com/en-us/windows/win32/termserv/extendeddisconnectreasoncode
                        if (   _rdpClient.ExtendedDisconnectReason == ExtendedDisconnectReasonCode.exDiscReasonAPIInitiatedLogoff              // log out from win2012 by user
                            || _rdpClient.ExtendedDisconnectReason == ExtendedDisconnectReasonCode.exDiscReasonAPIInitiatedDisconnect          // An application initiated the disconnection.
                            || _rdpClient.ExtendedDisconnectReason == ExtendedDisconnectReasonCode.exDiscReasonNoInfo                          // log out from win2008 by user
                            || _rdpClient.ExtendedDisconnectReason == ExtendedDisconnectReasonCode.exDiscReasonLogoffByUser                    // log out from win10 by user
                            || _rdpClient.ExtendedDisconnectReason == ExtendedDisconnectReasonCode.exDiscReasonRpcInitiatedDisconnectByUser    // log out from win2016 by user
                            )
                        {
                            // Terminate the session without notifying the user. Because the disconnection is initiated by the user.
                            RdpClientDispose();
                            base.OnClosed?.Invoke(base.ConnectionId);
                            break;
                        }
                        else
                        {
                            // show the message to user, and let user decide what to do next.
                            // potential reasons: 
                            // exDiscReasonServerIdleTimeout: user leave and no input for a long time without disconnect or log off, and server set a timeout to drop the session.
                            // exDiscReasonReplacedByOtherConnection: another user (maybe the same user) logon to the server, and the server drop this session.
                            RdpHost.Visibility = Visibility.Collapsed;
                            GridMessageBox.Visibility = Visibility.Visible;
                            TbMessageTitle.Visibility = Visibility.Collapsed;
                            BtnReconn.Visibility = Visibility.Visible;
                            TbMessage.Text = reason;
                            ParentWindowSetToWindow();
                            this.ParentWindow?.FlashIfNotActive();
                            break;
                        }

                    case disconnectReasonLocalNotError:
                        // Maybe the user has cancelled the RdpClient's attempt to reconnect.
                        // In this case, we will attempt to reconnect in order to switch to an alternate host.
                    default:
                        // A communication breakdown occurred by unplugging, invalidating, going to sleep or crashing, etc.
                        SimpleLogHelper.Warning($"RDP({_rdpSettings.DisplayName}) exit with error code {e.discReason}({reason}) ex:{excode}");
                        // We try reconnecting.
                        RdpHost.Visibility = Visibility.Collapsed;
                        GridMessageBox.Visibility = Visibility.Visible;
                        if (_flagHasEverConnected // a rdp session with never successful connection should not retry (in the case error code = 4 ex:exDiscReasonNoInfo)
                            && _retryCount < MAX_RETRY_COUNT)
                        {
                            // Continue to retry.
                            ++_retryCount;
                            TbMessageTitle.Text = IoC.Translate("host_reconecting_info") + $"({_retryCount}/{MAX_RETRY_COUNT})";
                            TbMessageTitle.Visibility = Visibility.Visible;
                            TbMessage.Text = reason;   // perche' si e' interrotta (es. rete o VPN caduta)
                            TbMessage.Visibility = Visibility.Visible;
                            BtnReconn.Visibility = Visibility.Collapsed;
                            // un secondo di pausa tra un tentativo e l'altro non basta se cade la VPN: si aspetta qualche secondo; se nel frattempo
                            // l'utente preme Riconnetti o chiude, il tentativo programmato non parte
                            var seq = _reconnSeq;
                            _ = Task.Delay(4000).ContinueWith(_ =>
                            {
                                if (seq == _reconnSeq && Status == ProtocolHostStatus.Disconnected) Execute.OnUIThread(() => this.ReConn());
                            });
                        }
                        else
                        {
                            // The number of retries has reached its limit. Display an error.
                            _retryCount = 0;  // Reset for next time.
                            TbMessageTitle.Visibility = Visibility.Collapsed;
                            BtnReconn.Visibility = Visibility.Visible;
                            BtnCreds.Visibility = !_flagHasEverConnected ? Visibility.Visible : Visibility.Collapsed;
                            TbMessage.Text = reason;
                            ParentWindowSetToWindow();
                        }
                        this.ParentWindow?.FlashIfNotActive();
                        break;
                }
            }
        }

        private void OnRdpClientConnected(object? sender, EventArgs e)
        {
            SimpleLogHelper.Debug("RDP Host:  RdpOnOnConnected");
            Diag("OnConnected (collegato al server, schermo ancora nero fino al login)");
            this.ParentWindow?.FlashIfNotActive();

            _lastLoginTime = DateTime.Now;
            _loginResizeTimer.Start();

            _flagHasConnected = true;
            _flagHasEverConnected = true;
            _retryCount = 0;
            Execute.OnUIThread(() =>
            {
                RdpHost.Visibility = Visibility.Visible;
                HideFrozenFrame();
                GridLoading.Visibility = Visibility.Collapsed;
                GridMessageBox.Visibility = Visibility.Collapsed;

                // if parent is FullScreenWindowView, go to full screen.
                if (ParentWindow is FullScreenWindowView)
                {
                    SimpleLogHelper.Debug("RDP Host: ReConn with full screen");
                    GoFullScreen();
                }
            });
        }

        private void OnRdpClientLoginComplete(object? sender, EventArgs e)
        {
            SimpleLogHelper.Debug("RDP Host:  OnRdpClientLoginComplete");
            Diag("LoginComplete (accesso finito, il desktop remoto si sta caricando)");

            OnCanResizeNowChanged?.Invoke();
            RdpHost.Visibility = Visibility.Visible;
            HideFrozenFrame();
            GridLoading.Visibility = Visibility.Collapsed;
            GridMessageBox.Visibility = Visibility.Collapsed;
            ParentWindowResize_StartWatch();
            //_resizeEndTimer?.Start();
            //Task.Factory.StartNew(() =>
            //{
            //    Thread.Sleep(5000);
            //    _resizeEndTimer?.Stop();
            //});
        }


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private System.Drawing.Rectangle? _fullScreenRectPx;
        private System.Windows.Window? _fullScreenDpiHooked;

        private static void ApplyRect(IntPtr hwnd, System.Drawing.Rectangle r)
        {
            const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
            SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>mette la finestra a schermo intero esattamente su quel rettangolo, in pixel</summary>
        private void PlaceFullScreenWindowInPixels(System.Windows.Window win, System.Drawing.Rectangle rectPx)
        {
            _fullScreenRectPx = rectPx;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
            ApplyRect(hwnd, rectPx);
            if (!ReferenceEquals(_fullScreenDpiHooked, win))
            {
                _fullScreenDpiHooked = win;
                // una finestra che copre piu' monitor riceve il cambio di scala quando cambia il monitor "principale" della finestra:
                // Windows la ridimensiona da solo, quindi si rimette il rettangolo giusto
                win.DpiChanged += (s, e) =>
                {
                    if (_fullScreenRectPx is { } r && ParentWindow is FullScreenWindowView)
                        win.Dispatcher.BeginInvoke(new Action(() => ApplyRect(hwnd, r)));
                };
            }
        }

        private void OnGoToFullScreenRequested()
        {
            Debug.Assert(_rdpClient != null);
            // make sure ParentWindow is FullScreen Window
            Debug.Assert(ParentWindow != null);
            switch (ParentWindow)
            {
                case null:
                    return;
                case TabWindowView:
                case MainWindowView:
                case DetachedSessionWindow:
                {
                    // full-all-screen session switch to TabWindow, and click "Reconn" button, will entry this case.
                    _rdpClient!.FullScreen = false;
                    LocalityConnectRecorder.RdpCacheUpdate(_rdpSettings.Id, false);
                    return;
                }
            }


            var screenSize = this.GetScreenSizeIfRdpIsFullScreen();

            // Con la scala per monitor i rettangoli dei monitor sono in pixel veri: la finestra si posiziona direttamente in pixel
            // (SetWindowPos), cosi' copre esattamente il monitor o i monitor scelti, qualunque sia la loro scala.
            int ceilingWidth = screenSize.Width;
            int ceilingHeight = screenSize.Height;
            ParentWindow.Dispatcher.Invoke(() =>
            {
                // ! do not remove
                ParentWindow.WindowState = WindowState.Normal;
                ParentWindow.WindowStyle = WindowStyle.None;
                ParentWindow.ResizeMode = ResizeMode.NoResize;
                PlaceFullScreenWindowInPixels(ParentWindow, screenSize);
            });

            SimpleLogHelper.Debug($"RDP to FullScreen resize ParentWindow to (pixels): {screenSize.Left},{screenSize.Top} {ceilingWidth} × {ceilingHeight}");

            // WARNING!: EnableFullAllScreens do not need a SetRdpResolution
            if (!FullScreenOnAllMonitors)   // con tutti i monitor la risoluzione remota e' gia' quella dei monitor; altrimenti si adatta al monitor
            {
                switch (_rdpSettings.RdpWindowResizeMode)
                {
                    case null:
                    case ERdpWindowResizeMode.AutoResize:
                    case ERdpWindowResizeMode.FixedFullScreen:
                    case ERdpWindowResizeMode.StretchFullScreen:
                        SetRdpResolution((uint)screenSize.Width, (uint)screenSize.Height, true);
                        break;
                    case ERdpWindowResizeMode.Stretch:
                    case ERdpWindowResizeMode.Fixed:
                        SetRdpResolution((uint)(_rdpSettings.RdpWidth ?? 800), (uint)(_rdpSettings.RdpHeight ?? 600), true);
                        break;
                    default:
                        UnifyTracing.Error(new ArgumentOutOfRangeException($"{_rdpSettings.RdpWindowResizeMode} is not processed!"));
                        SetRdpResolution((uint)screenSize.Width, (uint)screenSize.Height, true);
                        break;
                }
            }
        }

        private void OnConnectionBarRestoreWindowCall()
        {
            // make sure ParentWindow is FullScreen Window
            if (ParentWindow is not FullScreenWindowView)
            {
                return;
            }

            // !do not remove
            ParentWindowSetToWindow();
            LocalityConnectRecorder.RdpCacheUpdate(_rdpSettings.Id, false);
            base.OnFullScreen2Window?.Invoke(base.ConnectionId);
        }

        #endregion event handler
    }
}
