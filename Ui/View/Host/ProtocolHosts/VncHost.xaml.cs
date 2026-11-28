using System;
using System.Diagnostics;
using System.Windows;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.DataSource;
using Lemora.Utils;
using Lemora.View.Editor;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.Controls;
using Stylet;
using VncSharpCore;

namespace Lemora.View.Host.ProtocolHosts
{
    public sealed partial class VncHost : HostBase, Lemora.Service.Mcp.IMcpScreen
    {
        private readonly VNC _vncBase;

        public static VncHost Create(VNC protocolServer)
        {
            VncHost? view = null;
            Execute.OnUIThreadSync(() =>
            {
                view = new VncHost(protocolServer);
            });
            return view!;
        }

        private VncHost(VNC vnc) : base(vnc, false)
        {
            InitializeComponent();
            // click in the bar track (outside the thumb): one page in the click direction.
            // A ScrollBar outside a ScrollViewer runs these commands without changing the value: we do it here.
            foreach (var bar in new[] { BarH, BarV })
            {
                var b = bar;
                b.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Controls.Primitives.ScrollBar.PageDownCommand, (s, e) => { b.Value = Math.Min(b.Maximum, b.Value + b.LargeChange); e.Handled = true; }));
                b.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Controls.Primitives.ScrollBar.PageUpCommand, (s, e) => { b.Value = Math.Max(b.Minimum, b.Value - b.LargeChange); e.Handled = true; }));
            }
            GridMessageBox.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Visible;


            Vnc.ConnectComplete += OnConnected;
            Vnc.ConnectionLost += OnConnectionLost;
            Vnc.Resize += (_, _) =>
            {
                Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase?.DisplayName}): control resized by the layout to {Vnc.Width}x{Vnc.Height} px (not a resolution change of the remote PC)");
                _zoom?.ScrollTo(_zoom.ScrollX, _zoom.ScrollY); Execute.OnUIThread(UpdateBars);
            };

            // the library writes here what goes wrong in the VNC stream (read errors, misaligned stream, absurd lengths)
            // VNC library lines: quality changes and read errors at Medium level (they explain slowness and drops), the rest (TRACE, timings) only at Detailed
            VncClient.DiagLog ??= m =>
            {
                // the prefixes are the library's own message texts (VncSharpCore): they must match them exactly
                if (m.StartsWith("quality") || m.StartsWith("server read") || m.StartsWith("stream out of sync") || m.StartsWith("error in"))
                    Shawn.Utils.SimpleLogHelper.Debug("VNC library: " + m);
                else Lemora.Utils.Diag.Log("VNC-DIAG library: " + m);
            };

            // clipboard: from the server (the library cannot write it from its thread) and to the server when returning to the session or pasting
            Vnc.ClipboardChanged += OnServerClipboard;
            Vnc.GotFocus += (_, _) => SyncClipboardToServer(explicitPaste: false);
            // right click (and the terminal menu) paste from the remote PC clipboard: it must already be updated BEFORE the click, so the text is sent
            // as soon as the mouse enters the screen and on the first Ctrl key (so the server has time to receive it before the V of Ctrl+V)
            Vnc.MouseEnter += (_, _) => SyncClipboardToServer(explicitPaste: false);
            Vnc.PreviewKeyDown += (_, e) =>
            {
                // Ctrl+V and Shift+Ins are an intentional paste: only these also send passwords copied from Lemora
                bool paste = (e.Control && e.KeyCode == System.Windows.Forms.Keys.V) || (e.Shift && e.KeyCode == System.Windows.Forms.Keys.Insert);
                if (e.Control || paste) SyncClipboardToServer(explicitPaste: paste);
            };

            // dragging files onto the remote screen: they are uploaded to the default destination (the drop visual sits outside the screen, in the session bar)
            Vnc.AllowDrop = true;
            void FileDragOver(object? s, System.Windows.Forms.DragEventArgs e)
            {
                var ok = e.Data?.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop) == true;
                e.Effect = ok ? System.Windows.Forms.DragDropEffects.Copy : System.Windows.Forms.DragDropEffects.None;
                if (ok && !FileTransferVm.IsDragActive) FileTransferVm.DragEnter();
            }
            Vnc.DragEnter += FileDragOver;
            Vnc.DragOver += FileDragOver;
            Vnc.DragLeave += (_, _) => _ftVm?.DragLeave();
            Vnc.DragDrop += async (_, e) =>
            {
                var paths = (e.Data?.GetData(System.Windows.Forms.DataFormats.FileDrop) as string[])?.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
                if (paths is { Count: > 0 }) await FileTransferVm.DropAsync(paths); else _ftVm?.DragLeave();
            };

            _vncBase = vnc;

            MenuItems.Add(new System.Windows.Controls.Separator());
            MenuItems.Add(new System.Windows.Controls.MenuItem()
            {
                Header = "Ctrl + Alt + Del",
                Command = new RelayCommand((o) =>
                {
                    Vnc.SendSpecialKeys(SpecialKeys.CtrlAltDel);
                }, o => Status == ProtocolHostStatus.Connected)
            });
            MenuItems.Add(new System.Windows.Controls.MenuItem()
            {
                Header = "Ctrl + Esc",
                Command = new RelayCommand((o) =>
                {
                    Vnc.SendSpecialKeys(SpecialKeys.CtrlEsc);
                }, o => Status == ProtocolHostStatus.Connected)
            });
            MenuItems.Add(new System.Windows.Controls.MenuItem()
            {
                Header = "Alt + F4",
                Command = new RelayCommand((o) =>
                {
                    Vnc.SendSpecialKeys(SpecialKeys.AltF4);
                }, o => Status == ProtocolHostStatus.Connected)
            });
            {
                var tb = new TextBlock();
                tb.SetResourceReference(TextBlock.TextProperty, "Reconnect");
                MenuItems.Add(new System.Windows.Controls.MenuItem()
                {
                    Header = tb,
                    Command = new RelayCommand((o) => { ReConn(); })
                });
            }
            {
                var tb = new TextBlock();
                tb.SetResourceReference(TextBlock.TextProperty, "Close");
                MenuItems.Add(new System.Windows.Controls.MenuItem()
                {
                    Header = tb,
                    Command = new RelayCommand((o) => { Close(); })
                });
            }
        }

        #region Base Interface

        private DateTime _connStart = DateTime.UtcNow;   // for the log: time from the requested connection to the successful one

        public override void Conn()
        {
            // the handshake of a previous attempt is still running (outside the UI thread): a second one would close it or clash with it
            // ("cannot be in Connected state", PC shown for a second and then gone); its outcome arrives anyway
            if (_handshaking) { Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): connection already in progress, new request ignored"); return; }
            _connStart = DateTime.UtcNow;
            Status = ProtocolHostStatus.Connecting;
            if (Vnc.IsConnected)
            {
                DisconnectNow();   // full socket shutdown (see DisconnectNow)
                _lastDisconnect = DateTime.UtcNow;
            }
            Status = ProtocolHostStatus.Connecting;
            _revealed = false;
            GridLoading.Visibility = Visibility.Visible;
            VncFormsHost.Visibility = Visibility.Collapsed;
            GridMessageBox.Visibility = Visibility.Collapsed;
            _ = ConnAsync();
        }

        /// <summary>
        /// First the server is checked OUTSIDE the UI thread (with timeouts), then the password is asked if needed,
        /// and only at the end Connect is called: it is blocking, and a server that does not answer (or wants an unsupported
        /// authentication, like some RealVNC) froze the whole app.
        /// </summary>
        private int _connGen;
        private bool _handshaking;

        private async System.Threading.Tasks.Task ConnAsync()
        {
            // only the latest request goes on: an older one that is still waiting (name, pause, preliminary check) stops at its next step
            var gen = ++_connGen;
            bool Stale() => gen != _connGen || _closing;
            var address = _vncBase.Address;
            var port = _vncBase.GetPort();
            if (port <= 0) port = 5900;
            Vnc.VncPort = port;

            // a name can resolve to several addresses (even an IPv6 link-local that does not answer): the first one that really answers is used,
            // found now and not cached
            if (!System.Net.IPAddress.TryParse(address, out _))
            {
                using var resolveCts = new System.Threading.CancellationTokenSource(20000);   // name resolution (cold it can exceed 3 s) + connection
                var reachable = await Lemora.Utils.TcpHelper.FindReachableAddressAsync(address, port, resolveCts.Token, 5000);
                if (reachable != null) address = reachable.ToString();
                if (Stale()) return;
            }

            // after a disconnect the server (e.g. TightVNC) needs a moment to clean up the session: reconnecting right away (reload)
            // is often closed mid-handshake ("Unable to read beyond the end of the stream")
            var wait = 1200 - (DateTime.UtcNow - _lastDisconnect).TotalMilliseconds;
            if (wait > 0) await System.Threading.Tasks.Task.Delay((int)wait);

            string? pwd = null;
            var authFailures = 0;
            var skipDelay = false;
            for (var attempt = 0; ; attempt++)
            {
                if (attempt > 0 && !skipDelay) await System.Threading.Tasks.Task.Delay(1500);   // same reason: retry after a short pause (at most 3 attempts)
                skipDelay = false;
                if (Stale()) return;
                var (failure, types) = await System.Threading.Tasks.Task.Run(() => VncPreflight.Check(address, port));
                if (Stale()) return;
                if (failure != null)
                {
                    if (attempt < 2 && (failure == IoC.Translate("vnc_server_silent") || failure == IoC.Translate("vnc_server_refused")))
                    {
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): preliminary check failed on {address}:{port} ({failure}), retrying");
                        continue;
                    }
                    ShowConnError(failure);
                    return;
                }

                if (pwd == null)
                {
                    pwd = DataVault.DecryptOrReturnOriginalString(_vncBase.Password);
                    if (string.IsNullOrEmpty(pwd)) pwd = _typedPwd;   // typed in this session: the reload does not ask for it again
                    if (string.IsNullOrEmpty(pwd) && types.Contains(2))
                    {
                        // password not saved: ask before connecting (an empty DES key would be rejected by .NET)
                        var dlg = new PasswordPopupDialogViewModel
                        {
                            Title = $"[VNC]({_vncBase.DisplayName}) -> {_vncBase.Address}:{_vncBase.Port}",
                            IsUserNameVisible = false,
                        };
                        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Password))
                        {
                            ShowConnError(IoC.Translate("vnc_password_cancelled"), fatal: true);
                            return;
                        }
                        pwd = dlg.Password;
                        _typedPwd = pwd;
                    }
                }
                var chosen = pwd ?? "";
                Vnc.GetPassword = () => chosen;

                try
                {
                    // A rejected password does NOT raise errors: the library only reports "connection lost" (which would close the session).
                    // So during connection the automatic close is suspended and we check whether we are really connected.
                    _invokeOnClosedWhenDisconnected = false;
                    // graphics quality: Automatic starts low and rises if the line holds; Low/High stay fixed
                    // (the choice made from the "Fit" menu during the session also holds after a reconnection)
                    Vnc.QualityMode = _qualityChosen ?? _vncBase.VncQuality switch
                    {
                        VNC.EVncQuality.Low => VncSharpCore.VncQualityMode.Low,
                        VNC.EVncQuality.High => VncSharpCore.VncQualityMode.High,
                        _ => VncSharpCore.VncQualityMode.Auto,
                    };
                    _qualityChosen ??= Vnc.QualityMode;
                    if (Stale()) return;
                    // the handshake runs outside the UI thread: a server that answers slowly no longer freezes the app (it took 21 s once)
                    _handshaking = true;
                    try { await Vnc.ConnectAsync(address, false, _vncBase.VncWindowResizeMode == VNC.EVncWindowResizeMode.Stretch); }
                    finally { _handshaking = false; }
                    if (_closing) { DisconnectNow(); return; }   // tab closed during the handshake: the connection just opened must not stay up
                    if (!Vnc.IsConnected)
                    {
                        // password rejected: not "Connected" (before, it was declared so and the wrong password was even remembered for later connections)
                        authFailures++;
                        _typedPwd = null;
                        QuickSessionFollowUp.ForgetSavedCredentials(ProtocolServer);
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): password rejected by the server (attempt {authFailures})");
                        Status = ProtocolHostStatus.Connecting;
                        VncFormsHost.Visibility = Visibility.Collapsed;
                        GridLoading.Visibility = Visibility.Visible;
                        GridMessageBox.Visibility = Visibility.Collapsed;
                        if (authFailures >= 3) { ShowConnError(IoC.Translate("host_bad_credentials"), fatal: true); return; }   // several attempts in a row risk locking out access on the server
                        var quick = ProtocolServer.IsTmpSession();
                        // the SAVED password was rejected: VNC is not encrypted and does not prove who the server is, so it may be a fake PC fishing for
                        // other passwords (each reaches it DES-encrypted, easy to brute-force). We say so before letting the user type another (security hardening)
                        var savedPwd = DataVault.DecryptOrReturnOriginalString(_vncBase.Password);
                        bool savedRejected = !string.IsNullOrEmpty(savedPwd) && chosen == savedPwd;
                        var again = new PasswordPopupDialogViewModel(false, quick && IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
                        {
                            Title = $"[VNC]({_vncBase.DisplayName}) -> {_vncBase.Address}:{_vncBase.Port}",
                            IsUserNameVisible = false,
                            ErrorText = IoC.Translate(savedRejected ? "vnc_saved_password_rejected" : "password_wrong"),   // red outline + text
                        };
                        if (again.ShowDialog() != true || string.IsNullOrEmpty(again.Password))
                        {
                            ShowConnError(IoC.Translate("vnc_password_cancelled"), fatal: true);
                            return;
                        }
                        pwd = again.Password;
                        _typedPwd = pwd;
                        QuickSessionFollowUp.UpdateCredentials(ProtocolServer, "", pwd, "", again.CanRememberInfo && again.IsRememberInfo);
                        skipDelay = true;
                        continue;
                    }
                    _invokeOnClosedWhenDisconnected = true;
                    // file transfer connects to the same server with the same password (separate connection)
                    _ftAddress = address; _ftPort = port; _ftPassword = chosen;
                    ApplyKeepAlive();
                    // the remote screen is shown (and the spinner disappears) on the first frame: see WaitFirstFrame
                    Status = ProtocolHostStatus.Connected;
                    return;
                }
                catch (Exception e) when (attempt < 2 && IsServerClosedEarly(e))
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): the server closed the connection during startup ({e.Message}), retrying");
                    try { FindSocket()?.Close(); } catch { /* already closed */ }
                }
                catch (Exception e)
                {
                    _typedPwd = null;   // if it was wrong it is not reused
                    ShowConnError(e.Message);
                    return;
                }
            }
        }

        private string? _typedPwd;

        #region File transfer
        private string? _ftAddress, _ftPassword;
        private int _ftPort;
        private Lemora.Service.FileTransfer.FileTransferManager? _fileTransfer;
        private Lemora.View.FileTransfer.FileTransferViewModel? _ftVm;
        /// <summary>file transfer UI of this session (popup, queue, browser, drag and drop, clipboard)</summary>
        public Lemora.View.FileTransfer.FileTransferViewModel FileTransferVm => _ftVm ??= new Lemora.View.FileTransfer.FileTransferViewModel(this);

        /// <summary>
        /// File transfer of this session (null until the session has connected). Works only with TightVNC 2.x:
        /// with other servers <c>CheckAvailableAsync()</c> returns false. It closes together with the session.
        /// </summary>
        public Lemora.Service.FileTransfer.FileTransferManager? FileTransfer
        {
            get
            {
                if (_fileTransfer != null) return _fileTransfer;
                if (_ftAddress == null || _ftPassword == null) return null;
                var cfg = IoC.Get<ConfigurationService>().FileTransfer;
                var opts = new Lemora.Service.FileTransfer.FileTransferOptions
                {
                    MaxFileBytes = cfg.MaxFileSizeMB * 1024L * 1024L,
                    MaxJobBytes = cfg.MaxJobSizeMB * 1024L * 1024L,
                    Conflict = (Lemora.Service.FileTransfer.ConflictPolicy)cfg.ConflictPolicy,
                    UseTemporaryNames = cfg.UseTemporaryNames,
                };
                var (addr, port, pwd) = (_ftAddress, _ftPort, _ftPassword);   // if the session reconnects with another password, the manager must be recreated
                Lemora.Service.FileTransfer.FileTransferManager.DiagLog ??= m => Shawn.Utils.SimpleLogHelper.Debug("File transfer: " + m);   // connections, retries, failed files: Medium level
                _fileTransfer = new Lemora.Service.FileTransfer.FileTransferManager(ct => Lemora.Service.FileTransfer.TightFtConnection.ConnectAsync(addr, port, pwd, ct), opts);
                return _fileTransfer;
            }
        }
        #endregion
        private DateTime _lastDisconnect = DateTime.MinValue;

        /// <summary>the server closed the connection mid-handshake (it is not a password rejection): worth retrying</summary>
        private static bool IsServerClosedEarly(Exception e)
            => e is System.IO.EndOfStreamException || e.InnerException is System.IO.EndOfStreamException
               || e.Message.Contains("beyond the end of the stream") || e.Message.Contains("inputCount");

        /// <param name="fatal">password rejected or cancelled: the automatic reconnection must not retry</param>
        private void ShowConnError(string message, bool fatal = false)
        {
            _connFailed = true; _connFatal = fatal;
            // the failure reason stays in the log even at the normal level (before it was only at Debug level and you could not tell what went wrong)
            Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase?.DisplayName}): connection failed{(fatal ? " (final)" : "")}: {message}");
            LogActivityEnd(message);   // Activity log: failed attempt (or drop, if it was connected) with the reason
            _invokeOnClosedWhenDisconnected = false;
            VncFormsHost.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Visible;
            GridMessageBox.Visibility = Visibility.Visible;

            TbMessageTitle.Visibility = Visibility.Collapsed;
            BtnReconn.Visibility = Visibility.Visible;
            TbMessage.Text = message;
        }

        /// <summary>
        /// Reload (button at top right, menu, "Reconnect"): with the automatic reconnection active it only makes it try now. Before, both ran: the next
        /// round of the automatic one closed the session just reopened by hand (PC shown for a second, then disconnected).
        /// </summary>
        public override void ReConn()
        {
            if (_autoCts != null) { _wakeNow = true; return; }
            ReConnNow();
        }

        private void ReConnNow()
        {
            RememberView();
            VncFormsHost.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Visible;
            GridMessageBox.Visibility = Visibility.Collapsed;
            _invokeOnClosedWhenDisconnected = false;
            Conn();
            _invokeOnClosedWhenDisconnected = true;
        }

        public override void Close()
        {
            _closing = true;
            _diagTimer?.Stop();
            StopAutoReconnect();
            ActivityReason ??= "closed by the user";   // compared by the MCP server (McpTools): keep this exact value
            Status = ProtocolHostStatus.Disconnected;
            DisconnectNow();
            try { _ftVm?.Dispose(); } catch { }
            try { _fileTransfer?.Dispose(); } catch { }   // also closes the transfers in progress
            _fileTransfer = null;

            // Unsubscribe from events to prevent memory leaks
            Vnc.ConnectComplete -= OnConnected;
            Vnc.ConnectionLost -= OnConnectionLost;

            base.Close();
        }

        private static readonly System.Reflection.FieldInfo? RfbField = typeof(VncClient).GetField("rfb", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>the socket to the server (internal library field): needed to close it by hand if the library close stops halfway</summary>
        private System.Net.Sockets.TcpClient? FindSocket()
        {
            try
            {
                var rfb = RfbField?.GetValue(VncZoomPolicy.GetClient(Vnc));
                return rfb?.GetType().GetField("tcp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(rfb) as System.Net.Sockets.TcpClient;
            }
            catch { return null; }
        }

        /// <summary>
        /// Closes the connection to the VNC server immediately and completely. The protocol has no goodbye message: the server notices the end
        /// only from the socket closing (TightVNC restores the normal screen only then). So the socket is always closed, by hand if needed,
        /// even if the library close fails halfway or the session was not "connected" yet.
        /// </summary>
        public void DisconnectNow()
        {
            var tcp = FindSocket();
            try { if (tcp != null) tcp.LingerState = new System.Net.Sockets.LingerOption(true, 2); } catch { /* socket already closed */ }
            try
            {
                if (Vnc.IsConnected) Vnc.Disconnect();
            }
            catch (Exception e)
            {
                Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): library close failed ({e.Message}), closing the socket");
            }
            finally
            {
                try { if (tcp?.Client is { Connected: true } sock) sock.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { /* already closed */ }
                try { tcp?.Close(); } catch { /* already closed */ }
            }
            Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): connection to the server closed");
        }

        /// <summary>Picture in picture: the remote screen shrinks to fit entirely in the window; on exit it returns to the mode chosen for the server</summary>
        public override void SetFitToWindow(bool on)
        {
            Execute.OnUIThread(() =>
            {
                try { _pip = on; ApplyView(); }
                catch (Exception e) { Shawn.Utils.SimpleLogHelper.Error(e); }
            });
        }

        #region fit to window (desktop with multiple monitors joined)

        private const double BarSize = 16;   // scroll bar thickness (DPI-independent px), as in the IslandScrollBar style

        /// <summary>null = the whole remote screen fits in the window (like "Stretch"); otherwise the chosen mode, with our own scrolling</summary>
        private VncFitMode? _fit;
        private VncZoomPolicy? _zoom;   // not null = fixed-scale mode active
        private bool _pip;
        private bool _updatingBars;

        /// <summary>image much wider (or taller) than a single screen: several monitors merged</summary>
        private bool IsMultiScreen => !_remoteSize.IsEmpty && _remoteSize.Height > 0
            && (_remoteSize.Width / _remoteSize.Height >= 2.5 || _remoteSize.Width / _remoteSize.Height <= 0.5);

        private void ApplyView()
        {
            ApplyViewCore();
            RaiseViewControlsChanged();
        }

        // always available once the remote screen is known (before: only with several monitors merged); with several monitors it also starts on "fit width"
        public override bool HasViewControls => !_remoteSize.IsEmpty && !_pip && VncFormsHost.Visibility == Visibility.Visible;
        public override string ZoomText => $"{Math.Round(_zoomFactor * 100)}%";
        public override void ZoomIn() { EnsureFit(); BtnZoomIn_OnClick(this, null!); }
        public override void ZoomOut() { EnsureFit(); BtnZoomOut_OnClick(this, null!); }

        /// <summary>zoom starts from a view: if none was chosen (whole screen shrunk) it starts from "fit width"</summary>
        private void EnsureFit()
        {
            if (_fit != null || !HasViewControls) return;
            _fit = VncFitMode.Width; _zoomFactor = 1; ApplyView();
        }

        public override bool SetFitMode(bool width)
        {
            if (!HasViewControls) return false;
            _fit = width ? VncFitMode.Width : VncFitMode.Height; _zoomFactor = 1; ApplyView();
            return true;
        }
        public override void ZoomReset() => SetZoomFactor(1);
        public override void ShowFitMenu(FrameworkElement anchor) => ShowFitMenuCore(anchor);

        private void ApplyViewCore()
        {
            if (_remoteSize.IsEmpty || VncFormsHost.Visibility != Visibility.Visible) return;
            if (_pip || _fit == null)
            {
                BarV.Visibility = BarH.Visibility = Visibility.Collapsed;
                if (_zoom != null || !Vnc.Scaled) { _zoom = null; Vnc.Scaled = true; }
                return;
            }

            // scale = (chosen fit, computed on the TOTAL space) x zoom. The bars are decided from the total space (bars included):
            // so a bar appearing/disappearing does not change the scale and does not trigger a loop
            var dpi = VisualTreeHelper.GetDpi(this);
            double wt = ViewArea.ActualWidth * dpi.DpiScaleX, ht = ViewArea.ActualHeight * dpi.DpiScaleY, bar = BarSize * dpi.DpiScaleX;
            if (wt <= 0 || ht <= 0) return;
            double fw = _remoteSize.Width, fh = _remoteSize.Height;
            double Base(double w, double h) => (_fit == VncFitMode.Width ? w / fw : _fit == VncFitMode.Height ? h / fh : 1) * _zoomFactor;
            var z0 = Base(wt, ht);
            bool needV = fh * z0 > ht, needH = fw * z0 > wt;
            if (needV && !needH) needH = fw * z0 > wt - bar;
            if (needH && !needV) needV = fh * z0 > ht - bar;
            BarV.Visibility = needV ? Visibility.Visible : Visibility.Collapsed;
            BarH.Visibility = needH ? Visibility.Visible : Visibility.Collapsed;
            if (_zoom == null || _zoom.Mode != _fit) _zoom = VncZoomPolicy.Install(Vnc, _fit.Value);
            _zoom.Zoom = Base(wt - (needV ? bar : 0), ht - (needH ? bar : 0));
            Vnc.Invalidate();
            UpdateBars();
        }

        /// <summary>1 = the chosen view (width/height/actual); 1.25 = 25% larger than that</summary>
        private double _zoomFactor = 1;
        private static readonly double[] ZoomSteps = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.25, 3.5 };   // Ctrl +/- keys: 25% steps up to 350% (the menu slider goes in 5% steps)

        private void SetZoomFactor(double f)
        {
            if (_zoom == null) return;
            // stay on the same point: the view center (in remote pixels) before and after
            var c = Vnc.ClientSize; var old = _zoom.Zoom; var sc = _zoom.ContentSize;
            var cx = (sc.Width > c.Width ? _zoom.ScrollX + c.Width / 2.0 : sc.Width / 2.0) / old;
            var cy = (sc.Height > c.Height ? _zoom.ScrollY + c.Height / 2.0 : sc.Height / 2.0) / old;
            _zoomFactor = f;
            ApplyView();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_zoom == null) return;
                var c2 = Vnc.ClientSize; var z = _zoom.Zoom;
                _zoom.ScrollTo((int)(cx * z - c2.Width / 2.0), (int)(cy * z - c2.Height / 2.0));
                UpdateBars();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BtnZoomIn_OnClick(object? sender, RoutedEventArgs? e) => SetZoomFactor(ZoomSteps.FirstOrDefault(x => x > _zoomFactor + 0.001, ZoomSteps[^1]));
        private void BtnZoomOut_OnClick(object? sender, RoutedEventArgs? e) => SetZoomFactor(ZoomSteps.LastOrDefault(x => x < _zoomFactor - 0.001, ZoomSteps[0]));

        private void UpdateBars()
        {
            if (_zoom == null) return;
            var c = _zoom.ContentSize; var v = Vnc.ClientSize;
            _updatingBars = true;
            try
            {
                BarH.Maximum = Math.Max(0, c.Width - v.Width); BarH.ViewportSize = v.Width; BarH.LargeChange = v.Width * 0.9; BarH.SmallChange = 60; BarH.Value = _zoom.ScrollX;
                BarV.Maximum = Math.Max(0, c.Height - v.Height); BarV.ViewportSize = v.Height; BarV.LargeChange = v.Height * 0.9; BarV.SmallChange = 60; BarV.Value = _zoom.ScrollY;
            }
            finally { _updatingBars = false; }
        }

        private void ViewArea_OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyView();

        private void Bar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_updatingBars) _zoom?.ScrollTo((int)BarH.Value, (int)BarV.Value);
        }

        /// <summary>keyboard focus is inside the remote screen</summary>
        public bool HasRemoteFocus => Vnc.ContainsFocus || Vnc.Focused;
        public void FocusRemote() { try { Vnc.Focus(); } catch { /* control not ready yet */ } }

        /// <summary>sends a key (X11 keysym) to the server, as if pressed or released on the remote computer</summary>
        public void SendKey(uint keysym, bool pressed)
        {
            try { VncZoomPolicy.GetClient(Vnc)?.WriteKeyboardEvent(keysym, pressed); }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): key not sent ({e.Message})"); }
        }

        internal ContextMenu? LastFitMenu;   // for tests (menu screenshot)

        /// <summary>quality chosen in this session ("Fit" menu); null until the first connection</summary>
        private VncSharpCore.VncQualityMode? _qualityChosen;

        /// <summary>changes the graphics quality while the session is open, without reconnecting</summary>
        private void SetQualityLive(VncSharpCore.VncQualityMode mode)
        {
            _qualityChosen = mode;
            Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): graphics quality chosen from the menu: {mode}");
            try { VncZoomPolicy.GetClient(Vnc)?.SetQualityMode(mode); }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): quality change not applied ({e.Message})"); }
        }

        private void ShowFitMenuCore(FrameworkElement anchor)
        {
            const double menuWidth = 340;   // aligned to the button's right edge: so the menu does not go outside the window
            var menu = new ContextMenu
            {
                PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Width = menuWidth, HorizontalOffset = anchor.ActualWidth - menuWidth, VerticalOffset = 2, Padding = new Thickness(0),
            };

            // ---- reused pieces: texts and clickable boxes (thin border, highlighted when selected)
            TextBlock Txt(string text, double size, string brush, FontWeight? weight = null)
            {
                var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, FontWeight = weight ?? FontWeights.Normal };
                t.SetResourceReference(TextBlock.ForegroundProperty, brush);
                return t;
            }
            void Paint(Border b, bool selected, bool hover)
            {
                b.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentMidBrush" : "IslandButtonBorderBrush");
                b.SetResourceReference(Border.BackgroundProperty, selected ? "IslandSelectedBrush" : hover ? "IslandHoverBrush" : "IslandSubCardBrush");
            }
            Border Box(UIElement content, Action onClick, string? tip = null)
            {
                var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = content, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip, Tag = false };
                Paint(b, false, false);
                b.MouseEnter += (_, _) => Paint(b, (bool)b.Tag, true);
                b.MouseLeave += (_, _) => Paint(b, (bool)b.Tag, false);
                b.MouseLeftButtonUp += (_, _) => onClick();
                return b;
            }
            void Select(Border b, bool selected) { b.Tag = selected; Paint(b, selected, b.IsMouseOver); }
            System.Windows.Shapes.Path Icon(string data)
            {
                var p = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 1.4, Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PrimaryTextBrush");
                return p;
            }

            var root = new StackPanel();

            // ---- header and the two views
            var head = Txt(IoC.Translate("vnc_menu_view").ToUpperInvariant(), 11, "AccentMidBrush", FontWeights.SemiBold);
            head.Margin = new Thickness(16, 14, 16, 10);
            root.Children.Add(head);
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(13, 0, 13, 14) };
            Border Tile(string key, VncFitMode mode, string icon)
            {
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(Icon(icon));
                var label = Txt(IoC.Translate(key), 13, "PrimaryTextBrush");
                label.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(label);
                var b = Box(sp, () => { _fit = mode; _zoomFactor = 1; ApplyView(); }, null);
                b.Height = 76; b.Margin = new Thickness(3, 0, 3, 0);
                Select(b, _fit == mode);
                return b;
            }
            var tileW = Tile("vnc_fit_width", VncFitMode.Width, "M2,4 H18 V16 H2 Z M5,10 H15 M5,10 L7.5,7.5 M5,10 L7.5,12.5 M15,10 L12.5,7.5 M15,10 L12.5,12.5");
            var tileH = Tile("vnc_fit_height", VncFitMode.Height, "M2,4 H18 V16 H2 Z M10,6.5 V13.5 M10,6.5 L7.5,9 M10,6.5 L12.5,9 M10,13.5 L7.5,11 M10,13.5 L12.5,11");
            tiles.Children.Add(tileW); tiles.Children.Add(tileH);
            root.Children.Add(tiles);

            var rule = new Border { Height = 1 };
            rule.SetResourceReference(Border.BackgroundProperty, "IslandBorderBrush");
            root.Children.Add(rule);

            // ---- zoom: title and value in accent, - slider + (25% step, 25-350%), presets
            var zoom = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
            var valueText = Txt(ZoomText, 22, "AccentMidBrush", FontWeights.SemiBold);
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(Txt(IoC.Translate("vnc_zoom"), 14, "IslandSecondaryTextBrush"));
            Grid.SetColumn(valueText, 1); top.Children.Add(valueText);
            zoom.Children.Add(top);

            var slider = new Slider { Minimum = 25, Maximum = 350, TickFrequency = 25, IsSnapToTickEnabled = true, SmallChange = 25, LargeChange = 25, Value = Math.Round(_zoomFactor * 100), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            slider.SetResourceReference(FrameworkElement.StyleProperty, "IslandSlider");
            var chips = new System.Collections.Generic.List<(Border box, int value)>();
            bool syncing = false;
            void Sync()
            {
                syncing = true;
                var v = Math.Round(_zoomFactor * 100);
                valueText.Text = ZoomText;
                if (Math.Abs(slider.Value - v) > 0.1) slider.Value = v;
                foreach (var (box, value) in chips) Select(box, Math.Abs(value - v) < 0.5);
                syncing = false;
            }
            void Apply(double v)
            {
                v = Math.Max(25, Math.Min(350, v));
                EnsureFit();
                SetZoomFactor(v / 100.0);
                Sync();
            }
            slider.ValueChanged += (_, e) => { if (!syncing) Apply(e.NewValue); };

            TextBlock Center(string t, double size) { var x = Txt(t, size, "PrimaryTextBrush"); x.HorizontalAlignment = HorizontalAlignment.Center; return x; }
            var minus = Box(Center("−", 18), () => Apply(Math.Round(_zoomFactor * 100) - 25), "−25%");
            var plus = Box(Center("+", 18), () => Apply(Math.Round(_zoomFactor * 100) + 25), "+25%");
            minus.Width = plus.Width = 36; minus.Height = plus.Height = 36;
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(slider, 1); Grid.SetColumn(plus, 2);
            row.Children.Add(minus); row.Children.Add(slider); row.Children.Add(plus);
            zoom.Children.Add(row);

            var presets = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(-3, 10, -3, 0) };
            foreach (var p in new[] { 50, 100, 150, 200, 350 })
            {
                var box = Box(Center(p + "%", 13), () => Apply(p));
                box.Height = 32; box.Margin = new Thickness(3, 0, 3, 0);
                chips.Add((box, p));
                presets.Children.Add(box);
            }
            zoom.Children.Add(presets);
            root.Children.Add(zoom);

            var rule2 = new Border { Height = 1 };
            rule2.SetResourceReference(Border.BackgroundProperty, "IslandBorderBrush");
            root.Children.Add(rule2);

            // ---- footer: range and "Reset 100%"
            var foot = new Grid { Margin = new Thickness(16, 10, 16, 12) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Children.Add(Txt(IoC.Translate("vnc_zoom_range"), 12, "IslandSecondaryTextBrush"));
            var reset = Txt(IoC.Translate("vnc_zoom_reset"), 12, "AccentMidBrush");
            reset.Cursor = System.Windows.Input.Cursors.Hand;
            reset.Margin = new Thickness(12, 0, 0, 0);
            reset.MouseLeftButtonUp += (_, _) => Apply(100);
            Grid.SetColumn(reset, 1); foot.Children.Add(reset);
            root.Children.Add(foot);

            // ---- graphics quality (changes while the session is open, as in RealVNC)
            var rule3 = new Border { Height = 1 };
            rule3.SetResourceReference(Border.BackgroundProperty, "IslandBorderBrush");
            root.Children.Add(rule3);
            var qHead = Txt(IoC.Translate("vnc_menu_quality").ToUpperInvariant(), 11, "AccentMidBrush", FontWeights.SemiBold);
            qHead.Margin = new Thickness(16, 12, 16, 8);
            root.Children.Add(qHead);
            var qTiles = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(13, 0, 13, 6) };
            var qBoxes = new System.Collections.Generic.List<(Border box, VncSharpCore.VncQualityMode mode)>();
            var qStatus = Txt("", 12, "IslandSecondaryTextBrush");
            qStatus.Margin = new Thickness(16, 0, 16, 12);
            void QSync()
            {
                foreach (var (box, mode) in qBoxes) Select(box, mode == _qualityChosen);
                var client = VncZoomPolicy.GetClient(Vnc);
                qStatus.Text = client == null ? "" : IoC.Translate(client.IsLowQuality ? "vnc_q_now_low" : "vnc_q_now_high");
            }
            foreach (var (key, mode) in new[] { ("vnc_q_auto", VncSharpCore.VncQualityMode.Auto), ("vnc_q_low", VncSharpCore.VncQualityMode.Low), ("vnc_q_high", VncSharpCore.VncQualityMode.High) })
            {
                var label = Txt(IoC.Translate(key), 13, "PrimaryTextBrush");
                label.HorizontalAlignment = HorizontalAlignment.Center;
                var qBox = Box(label, () =>
                {
                    SetQualityLive(mode);
                    QSync();
                    _ = System.Threading.Tasks.Task.Delay(900).ContinueWith(_ => Execute.OnUIThread(() => { if (menu.IsOpen) QSync(); }));   // the real change arrives as soon as the server answers
                });
                qBox.Height = 38; qBox.Margin = new Thickness(3, 0, 3, 0);
                qBoxes.Add((qBox, mode));
                qTiles.Children.Add(qBox);
            }
            root.Children.Add(qTiles);
            root.Children.Add(qStatus);
            QSync();

            Sync();
            // WPF wraps the content in a menu item (MenuItem) and the theme highlights it on hover: the item becomes a plain container, with no look of its own
            var plainItem = new Style(typeof(MenuItem));
            // a menu item's content lives in Header, not in Content
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            plainItem.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter }));
            plainItem.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            // a click inside a menu item closes the menu: the panel stays open while the user adjusts it, it closes on a click outside
            plainItem.Setters.Add(new Setter(MenuItem.StaysOpenOnClickProperty, true));
            menu.ItemContainerStyle = plainItem;
            menu.Items.Add(root);
            LastFitMenu = menu;
            menu.IsOpen = true;
        }

        #endregion

        public override void FocusOnMe()
        {
            Execute.OnUIThread(() =>
            {
                VncFormsHost.Focus();
                Vnc.Focus();
            });
        }

        public override ProtocolHostType GetProtocolHostType()
        {
            return ProtocolHostType.Native;
        }

        public override IntPtr GetHostHwnd()
        {
            return IntPtr.Zero;
        }

        #endregion Base Interface

        #region event handler

        #region connection

        private System.Windows.Size _remoteSize = System.Windows.Size.Empty;
        public override System.Windows.Size RemoteDesktopSize => _remoteSize;

        private void OnConnected(object sender, EventArgs e)
        {
            _lastClip = ""; _lastClipSeq = 0;   // new connection: the server does not have our clipboard yet, it must be sent again
            if (e is VncSharpCore.ConnectEventArgs ce && ce.DesktopWidth > 0 && ce.DesktopHeight > 0)
                _remoteSize = new System.Windows.Size(ce.DesktopWidth, ce.DesktopHeight);
            if (!Dispatcher.CheckAccess())
            {
                Execute.OnUIThread(() => OnConnected(sender, e));
                return;
            }
            Status = ProtocolHostStatus.Connected;
            ApplyKeepAlive();
            Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): connected to {_vncBase.Address} in {(DateTime.UtcNow - _connStart).TotalSeconds:0.0} s from the click, remote screen {_remoteSize.Width:0}x{_remoteSize.Height:0}");
            StartDiagTimer();
            WaitFirstFrame();
            _zoom = null;   // the connection created a new policy
            // a remote screen of another size (a monitor unplugged on the remote PC: 4480x1440 -> 1920x1080) gets the starting view:
            // the old "fit width" of two monitors on one monitor left a scroll bar, and without several monitors the "Fit" menu to change it is not there
            if (_hasSavedView && _savedSize != _remoteSize) _hasSavedView = false;
            if (_hasSavedView)
            {
                // reconnection: return to the previous view (fit height/width, zoom); scrolling is restored on the first frame
                _fit = _savedFit; _zoomFactor = _savedZoom;
            }
            else
            {
                // "Fixed" = actual size with scrolling; "Stretch" = everything in the window (until something is chosen from the "Fit" menu)
                _fit = _vncBase.VncWindowResizeMode == VNC.EVncWindowResizeMode.Fixed ? VncFitMode.Actual : null;
                // several monitors joined (the "Fit" menu is there): the view chosen in Settings › Key mapping
                if (_fit == null && IsMultiScreen)
                {
                    var d = Lemora.Service.KeyMap.Current.VncScreen;
                    _fit = d.Fit;
                    _zoomFactor = d.Fit == VncFitMode.Actual ? Math.Clamp(d.Zoom, 25, 350) / 100.0 : 1;
                }
            }
            ApplyView();
        }

        // ---- view (fit, zoom, scrolling) kept when the session reconnects
        private bool _hasSavedView;
        private VncFitMode? _savedFit;
        private double _savedZoom = 1;
        private int _savedSx, _savedSy;
        private System.Windows.Size _savedSize;

        private void RememberView()
        {
            if (!_revealed || _remoteSize.IsEmpty) return;   // only if the session was shown: a failed attempt does not overwrite the good view
            _savedSize = _remoteSize; _savedFit = _fit; _savedZoom = _zoomFactor; _savedSx = _zoom?.ScrollX ?? 0; _savedSy = _zoom?.ScrollY ?? 0; _hasSavedView = true;
        }

        private void RestoreScroll()
        {
            if (!_hasSavedView || _zoom == null) return;
            var sx = _savedSx; var sy = _savedSy;
            Dispatcher.BeginInvoke(new Action(() => { _zoom?.ScrollTo(sx, sy); UpdateBars(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- clipboard
        private string _lastClip = "";
        private uint _lastClipSeq;   // Windows clipboard sequence number at the last check: if it has not changed there is nothing to read
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        /// <summary>text copied on your PC goes to the remote PC (RFB: text only, Latin-1) when returning to the session, when the mouse enters the screen or on paste.
        /// A password copied from Lemora (ExcludeClipboardContentFromMonitorProcessing marker, see SecretClipboard) goes only with an intentional paste
        /// (<paramref name="explicitPaste"/>): otherwise just moving the mouse over a session would hand it to that PC.</summary>
        private void SyncClipboardToServer(bool explicitPaste)
        {
            if (Status != ProtocolHostStatus.Connected) return;
            try
            {
                uint seq = IsDemo ? 0 : GetClipboardSequenceNumber();
                if (!IsDemo && seq == _lastClipSeq) return;
                // Claude is pasting into another session (RDP): its text in the Windows clipboard must not reach this VNC server,
                // not even with Ctrl+V. The sequence number is recorded, so that text is not sent later either (security hardening)
                if (_globalMcpHold > 0 && _mcpClipHold == 0) { _lastClipSeq = seq; return; }
                if (!IsDemo && !System.Windows.Clipboard.ContainsText()) { _lastClipSeq = seq; return; }
                bool secret = IsDemo ? TestLocalClipSecret : System.Windows.Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing");
                if (secret && !explicitPaste)
                {
                    // _lastClipSeq is not updated: the following Ctrl+V must be able to send it
                    Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): clipboard not sent, protected password (it goes only with Ctrl+V or Shift+Ins)");
                    return;
                }
                var text = IsDemo ? TestLocalClip : System.Windows.Clipboard.GetText();   // in tests the real clipboard is not touched
                if (string.IsNullOrEmpty(text) || text == _lastClip || text.Length > 1_000_000) { _lastClipSeq = seq; return; }
                var client = VncZoomPolicy.GetClient(Vnc);
                if (client == null) return;
                client.WriteClientCutText(text.Replace("\r\n", "\n"));
                _lastClip = text;
                _lastClipSeq = seq;
                Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): clipboard sent to the remote PC ({text.Length} characters)");
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): clipboard not sent ({e.Message})"); }
        }

        /// <summary>text copied on the remote PC reaches this PC's clipboard (the library receives it on a thread that cannot use the clipboard)</summary>
        private void OnServerClipboard(object? sender, EventArgs e)
        {
            try
            {
                var text = VncZoomPolicy.GetClient(Vnc)?.LastServerCutText;
                // what the remote PC copied outside Claude's work (not the script nor the esegui_comando output): it is what leggi_appunti returns and what is restored afterwards
                if (_mcpClipHold == 0 && !string.IsNullOrEmpty(text) && !IsMcpText(text)) _remoteClipOutsideMcp = text;
                // "Copy files" waits for the text (the file path) and takes it itself, even if equal to the previous one: it does not go into this PC's clipboard
                if (_clipCapture != null && !string.IsNullOrEmpty(text) && (_clipMatch == null || _clipMatch(text)))
                {
                    if (_clipMatch == null && _mcpClipHold == 0) _remoteClipOutsideMcp = text;   // copy requested on purpose (leggi_appunti): it is remote PC text even if equal to one pasted before
                    _lastClip = text; _clipCapture.TrySetResult(text); return;
                }
                // Claude is using the remote PC clipboard (paste, esegui_comando): its texts do not end up in this PC's clipboard
                if (_mcpClipHold > 0) { if (!string.IsNullOrEmpty(text)) _lastClip = text; return; }
                // late echo of a text pasted by Claude (it arrives after the block ends, with \r\n instead of \n): it does not go into this PC's
                // clipboard, hence not into Win+V or the other sessions (security hardening)
                if (!string.IsNullOrEmpty(text) && IsMcpText(text)) { _lastClip = text; return; }
                if (string.IsNullOrEmpty(text) || text == _lastClip) return;
                if (IsDemo) { TestServerClip = text; _lastClip = text; return; }   // in tests the real clipboard is not touched
                for (var i = 0; i < 5; i++)
                {
                    try { System.Windows.Clipboard.SetText(text); _lastClip = text; return; }
                    catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(50); }   // clipboard busy with another program
                }
            }
            catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): remote PC clipboard not copied ({ex.Message})"); }
        }

        private System.Threading.Tasks.TaskCompletionSource<string?>? _clipCapture;
        private Func<string, bool>? _clipMatch;
        private int _mcpClipHold;

        /// <summary>
        /// Runs <paramref name="trigger"/> (keys sent to the remote PC) and returns the first text the remote PC copies to the clipboard within
        /// <paramref name="timeoutMs"/> (and that satisfies <paramref name="match"/>, if given), or null. The text does NOT go into this PC's clipboard.
        /// </summary>
        public async System.Threading.Tasks.Task<string?> CaptureServerClipboardAsync(Action trigger, int timeoutMs, Func<string, bool>? match = null)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _clipMatch = match;
            _clipCapture = tcs;
            try
            {
                trigger();
                var done = await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(timeoutMs));
                return done == tcs.Task ? tcs.Task.Result : null;
            }
            finally { _clipCapture = null; _clipMatch = null; }
        }

        /// <summary>Claude: text into the remote PC clipboard (RFB: Latin-1 only), without going through this PC's clipboard</summary>
        internal bool McpSetRemoteClipboard(string text)
        {
            var client = VncZoomPolicy.GetClient(Vnc);
            if (client == null || Status != ProtocolHostStatus.Connected) return false;
            _lastClip = text;   // the server echo must not come back into this PC's clipboard
            var n = text.Replace("\r\n", "\n");
            lock (_mcpPut) { _mcpPut.Add(n); if (_mcpPut.Count > 300) _mcpPut.RemoveAt(0); }
            client.WriteClientCutText(n);
            return true;
        }

        private string? _remoteClipOutsideMcp;
        private readonly System.Collections.Generic.List<string> _mcpPut = new();

        /// <summary>
        /// text put by Lemora shortly before (server echo): server messages can arrive late, without this check
        /// it would have looked like a copy by the remote user.
        /// </summary>
        internal bool IsMcpText(string t)
        {
            var n = t.Replace("\r\n", "\n");
            lock (_mcpPut) return _mcpPut.Contains(n);
        }

        /// <summary>Claude: the last text copied on the remote PC, excluding Claude's working texts (null = nothing since the session opened)</summary>
        internal string? McpLastRemoteClipboard() => _remoteClipOutsideMcp;

        /// <summary>while it is open, what the remote PC copies does not go into this PC's clipboard (Claude's working texts)</summary>
        internal IDisposable McpHoldClipboard()
        {
            _mcpClipHold++;
            return new Release(() => _mcpClipHold = Math.Max(0, _mcpClipHold - 1));
        }

        private static int _globalMcpHold;
        /// <summary>suspends automatic (and Ctrl+V) clipboard sending in ALL VNC sessions, except the one that has its own McpHoldClipboard</summary>
        internal static IDisposable McpHoldAllVnc()
        {
            System.Threading.Interlocked.Increment(ref _globalMcpHold);
            return new Release(() => System.Threading.Interlocked.Decrement(ref _globalMcpHold));
        }

        private sealed class Release : IDisposable
        {
            private Action? _a;
            public Release(Action a) => _a = a;
            public void Dispose() { _a?.Invoke(); _a = null; }
        }

        /// <summary>a shortcut: modifiers pressed, the key pressed and released, then the modifiers released (X11 keysym)</summary>
        public void SendChord(uint[] modifiers, uint key)
        {
            foreach (var m in modifiers) SendKey(m, true);
            SendKey(key, true); SendKey(key, false);
            foreach (var m in modifiers.Reverse()) SendKey(m, false);
        }

        // ---- Claude (MCP server): to be called on the UI thread, where the VNC control draws the screen

        /// <summary>copy of the remote screen at its real resolution; null if the session is not connected yet</summary>
        internal System.Drawing.Bitmap? McpCapture()
            => _revealed && Vnc.IsConnected && Vnc.Desktop is System.Drawing.Bitmap b ? new System.Drawing.Bitmap(b) : null;

        /// <summary>mouse in remote screen coordinates; mask: 1 left, 2 middle, 4 right, 8/16 wheel up/down</summary>
        internal void McpPointer(byte mask, int x, int y)
        {
            VncZoomPolicy.GetClient(Vnc)?.WritePointerEvent(mask, new System.Drawing.Point(x, y));
            Lemora.Service.Mcp.McpOverlay.Pointer(this, x, y);
        }

        /// <summary>remote screen point -> screen pixel of this PC (zoom and scrolling included)</summary>
        System.Drawing.Point? Lemora.Service.Mcp.IMcpScreen.McpToScreen(int x, int y)
        {
            if (!Vnc.IsHandleCreated || _remoteSize.IsEmpty) return null;
            if (_zoom != null) return Vnc.PointToScreen(_zoom.RemoteToClient(new System.Drawing.Point(x, y)));
            if (!Vnc.Scaled) return null;
            // VncSharp "fit to window": the whole remote screen in proportion, centered
            var c = Vnc.ClientSize;
            var k = Math.Min(c.Width / (double)_remoteSize.Width, c.Height / (double)_remoteSize.Height);
            return Vnc.PointToScreen(new System.Drawing.Point((int)Math.Round((c.Width - _remoteSize.Width * k) / 2 + x * k), (int)Math.Round((c.Height - _remoteSize.Height * k) / 2 + y * k)));
        }

        System.Drawing.Bitmap? Lemora.Service.Mcp.IMcpScreen.McpCapture() => McpCapture();
        System.Drawing.Size? Lemora.Service.Mcp.IMcpScreen.McpScreenSize()
            => _revealed && Vnc.IsConnected && Vnc.Desktop is System.Drawing.Bitmap b ? b.Size : null;
        void Lemora.Service.Mcp.IMcpScreen.McpPointer(byte mask, int x, int y) => McpPointer(mask, x, y);

        /// <summary>tests only</summary>
        private static bool IsDemo => Lemora.Utils.DevOnly.Env("LEMORA_DEMO") != null;
        internal string TestLocalClip = "";
        internal bool TestLocalClipSecret;
        internal string TestLastSentClip => _lastClip;
        internal string TestServerClip = "";
        internal void TestSyncClipboard(bool explicitPaste = false) => SyncClipboardToServer(explicitPaste);
        internal bool TestRevealed => _revealed;
        internal System.Windows.Controls.Primitives.ScrollBar TestBarH => BarH;
        internal System.Windows.Controls.Primitives.ScrollBar TestBarV => BarV;
        internal double TestSecondsSinceUpdate => (DateTime.UtcNow - _lastUpdateUtc).TotalSeconds;
        // the labels (adatta=...) are parsed by ui-map\tools\prova-vnc-cambio-schermo.ps1: keep them
        internal string TestViewState => $"adatta={_fit} zoom={_zoomFactor} scorrimentoX={_zoom?.ScrollX} Y={_zoom?.ScrollY} mostrata={_revealed} vistaSalvata={_hasSavedView} area={ViewArea.ActualWidth:0}x{ViewArea.ActualHeight:0}";
        internal void TestSetView(bool height, int scrollX) { SetFitMode(!height); _zoom?.ScrollTo(scrollX, 0); UpdateBars(); }
        /// <param name="delta">120 = one wheel notch; a touchpad sends many small events (e.g. 30)</param>
        internal void TestWheel(int notches, int delta = 120)
        {
            var m = typeof(System.Windows.Forms.Control).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var i = 0; i < notches; i++) m.Invoke(Vnc, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 100, 100, delta) });
        }

        private bool _revealed;
        private VncUpdateHandler? _firstFrameHandler;

        /// <summary>
        /// Until the first frame arrives the VNC control would show its own "Connecting to VNC host, please wait..." (English, small):
        /// it stays hidden and the spinner with our text is shown. If the frame does not arrive within 10 s it is shown anyway.
        /// </summary>
        private void WaitFirstFrame()
        {
            if (_revealed) return;
            var client = VncZoomPolicy.GetClient(Vnc);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            void Reveal()
            {
                Execute.OnUIThread(() =>
                {
                    if (_revealed) return;
                    _revealed = true;
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): first frame shown {(DateTime.UtcNow - _connStart).TotalSeconds:0.0} s after the click");
                    timer.Stop();
                    if (client != null && _firstFrameHandler != null) client.VncUpdate -= _firstFrameHandler;
                    _firstFrameHandler = null;
                    VncFormsHost.Visibility = Visibility.Visible;
                    GridLoading.Visibility = Visibility.Collapsed;
                    GridMessageBox.Visibility = Visibility.Collapsed;
                    ApplyView();
                    RestoreScroll();
                });
            }
            timer.Tick += (_, _) => Reveal();
            timer.Start();
            if (client != null)
            {
                _firstFrameHandler = (_, _) => Reveal();
                client.VncUpdate += _firstFrameHandler;
            }
        }

        private bool _invokeOnClosedWhenDisconnected = true;

        private void OnConnectionLost(object? sender, EventArgs e)
        {
            // can come from a worker thread (session close): controls are touched only from the UI thread
            if (!Dispatcher.CheckAccess())
            {
                Execute.OnUIThread(() => OnConnectionLost(sender, e));
                return;
            }
            RememberView();   // the previous view is restored on reconnection
            Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): connection lost (status {Status}, intentional close={_closing}, reconnection already running={_autoCts != null}, automatic close enabled={_invokeOnClosedWhenDisconnected})");
            if (_closing || _autoCts != null) return;   // intentional close, or the automatic reconnection is already running
            // Activity log: the reason given by the library (written even with diagnostics off)
            var why = VncZoomPolicy.GetClient(Vnc)?.LastError;
            ActivityReason = string.IsNullOrEmpty(why) ? null : why;
            Status = ProtocolHostStatus.Disconnected;
            ShowLostOverlay();
            TbMessage.Text = IoC.Translate("vnc_lost_msg");
            // before, the session closed on its own (even for a remote PC reboot): now it stays open and reconnects by itself
            if (_invokeOnClosedWhenDisconnected)
                StartAutoReconnect();
        }

        #region automatic reconnection

        private System.Threading.CancellationTokenSource? _autoCts;
        private bool _closing, _wakeNow, _connFailed, _connFatal;

        /// <summary>pause between checks: 3 s in the first 5 minutes (a typical reboot), then 10 s up to 30 minutes, then 30 s</summary>
        private static int RetryInterval(TimeSpan lostFor) => lostFor.TotalMinutes < 5 ? 3 : lostFor.TotalMinutes < 30 ? 10 : 30;

        private void ShowLostOverlay()
        {
            VncFormsHost.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Collapsed;
            GridMessageBox.Visibility = Visibility.Visible;
            TbMessageTitle.Visibility = Visibility.Visible;
            TbMessageTitle.Text = IoC.Translate("vnc_lost_title");
            BtnReconn.Visibility = Visibility.Visible;
        }

        private void StartAutoReconnect()
        {
            if (_autoCts != null || _closing) return;
            _autoCts = new System.Threading.CancellationTokenSource();
            _ = AutoReconnectLoop(_autoCts.Token);
        }

        private void StopAutoReconnect()
        {
            _autoCts?.Cancel();
            _autoCts = null;
        }

        internal enum Probe { Down, ServiceOff, Open, NameNotFound }

        private System.Threading.Tasks.Task<Probe> ProbeAsync(System.Threading.CancellationToken ct)
        {
            var port = _vncBase.GetPort();
            return ProbeAsync(_vncBase.Address, port <= 0 ? 5900 : port, ct);
        }

        /// <summary>
        /// light check: only the connection to the port is opened (2.5 s at most). Refused = the PC is on but the VNC service is not running yet; no answer = PC off or no network;
        /// name not found = the PC name does not resolve to an address (after a reboot the DNS can lose it for a while even if the PC is on)
        /// </summary>
        internal static async System.Threading.Tasks.Task<Probe> ProbeAsync(string address, int port, System.Threading.CancellationToken ct)
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(address, port);
            try
            {
                var done = await System.Threading.Tasks.Task.WhenAny(connect, System.Threading.Tasks.Task.Delay(2500, ct));
                if (done != connect)
                {
                    _ = connect.ContinueWith(t => { _ = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);   // the late outcome must not be lost as an unobserved exception
                    return Probe.Down;
                }
                await connect;
                return Probe.Open;
            }
            catch (System.Net.Sockets.SocketException se) { return se.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => Probe.ServiceOff,
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain => Probe.NameNotFound,
                _ => Probe.Down,
            }; }
            catch (OperationCanceledException) { throw; }
            catch { return Probe.Down; }
        }

        private async System.Threading.Tasks.Task CountdownAsync(int seconds, string state, DateTime since, int probes, System.Threading.CancellationToken ct)
        {
            _wakeNow = false;
            for (var s = seconds; s > 0 && !_wakeNow; s--)
            {
                ShowLostOverlay();
                var lostFor = DateTime.UtcNow - since;
                TbMessage.Text = string.Format(IoC.Translate("vnc_lost_countdown"), state, (int)lostFor.TotalMinutes, lostFor.Seconds, s, probes);
                await System.Threading.Tasks.Task.Delay(1000, ct);
            }
        }

        /// <summary>
        /// Stays active until we reconnect or the tab is closed (no time limit): if the remote PC is rebooting for
        /// updates, the session is there when it comes back. Each round is a light port check; the real connection starts only when
        /// the port answers. It stops only if the server rejects the password (several attempts in a row would lock the account).
        /// </summary>
        private async System.Threading.Tasks.Task AutoReconnectLoop(System.Threading.CancellationToken ct)
        {
            var since = DateTime.UtcNow;
            var name = _vncBase.DisplayName;
            var probes = 0;
            var wasDown = false;
            Shawn.Utils.SimpleLogHelper.Warning($"VNC({name}): connection lost with {_vncBase.Address}, automatic reconnection active");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var probe = await ProbeAsync(ct);
                    probes++;
                    if (probe != Probe.Open)
                    {
                        wasDown = true;
                        await CountdownAsync(RetryInterval(DateTime.UtcNow - since), IoC.Translate(probe switch { Probe.ServiceOff => "vnc_pc_on_vnc_off", Probe.NameNotFound => "vnc_name_not_found", _ => "vnc_pc_unreachable" }), since, probes, ct);
                        continue;
                    }
                    if (wasDown)
                    {
                        // the port has just reopened: the VNC service needs a moment to be ready
                        ShowLostOverlay();
                        TbMessage.Text = IoC.Translate("vnc_pc_back");
                        await System.Threading.Tasks.Task.Delay(2000, ct);
                        wasDown = false;
                    }
                    ShowLostOverlay();
                    TbMessage.Text = IoC.Translate("vnc_reconnecting");
                    _connFailed = false; _connFatal = false;
                    ReConnNow();
                    // the outcome: connected, or ShowConnError (the server closed halfway or does not answer)
                    for (var i = 0; i < 200 && Status != ProtocolHostStatus.Connected && !_connFailed; i++)
                        await System.Threading.Tasks.Task.Delay(300, ct);
                    if (Status == ProtocolHostStatus.Connected)
                    {
                        _autoCts = null;
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC({name}): reconnected after {(DateTime.UtcNow - since).TotalSeconds:0} s and {probes} checks");
                        NotifyReconnected(name);
                        return;
                    }
                    if (_connFatal)
                    {
                        _autoCts = null;
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC({name}): automatic reconnection stopped (password not accepted)");
                        return;
                    }
                    Shawn.Utils.SimpleLogHelper.Debug($"VNC({name}): connection failed, retrying");
                    wasDown = true;
                    await CountdownAsync(RetryInterval(DateTime.UtcNow - since), IoC.Translate("vnc_server_incomplete"), since, probes, ct);
                }
            }
            catch (OperationCanceledException) { /* tab closed */ }
        }

        /// <summary>session back: notice in the app and, if the window is not in the foreground, a taskbar flash</summary>
        private void NotifyReconnected(string name)
        {
            try
            {
                ToastService.Show(string.Format(IoC.Translate("vnc_reconnected_toast"), name));
                (IoC.Get<MainWindowViewModel>().View as Window)?.FlashIfNotActive(4, 400);
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Debug("VNC: reconnection notice not shown: " + e.Message); }
        }

        /// <summary>
        /// Connection check (TCP keepalive): after 5 s of silence a probe runs every 2 s, and after 3 without answer the connection
        /// is considered dropped (about 11 s) instead of the roughly 45 s of the read timeout alone.
        /// </summary>
        private void ApplyKeepAlive()
        {
            try
            {
                var s = FindSocket()?.Client;
                if (s == null) return;
                s.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
                s.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 5);
                s.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 2);
                s.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): keepalive not set ({e.Message})"); }
        }


        // ---- VNC-DIAG (temporary): if the screen gets no updates while connected, it logs it once per episode
        private System.Windows.Threading.DispatcherTimer? _diagTimer;
        private bool _stallLogged;

        private DateTime _lastUpdateUtc = DateTime.UtcNow;
        private DateTime _probeSentUtc = DateTime.MinValue;
        private bool _testIgnoreUpdates;
        // statistics of received updates (diagnostics): how many rectangles and pixels, to tell whether the server sends real changes or still images
        private long _updCount, _updPixels, _updMaxRect, _updOffUiThread;
        private int _diagTicks, _diagTotalTicks;
        /// <summary>tests only: simulates a stuck session (the server sends no more frames)</summary>
        internal void TestSimulateStall() { _testIgnoreUpdates = true; _lastUpdateUtc = DateTime.UtcNow.AddSeconds(-25); }
        private VncClient? _diagClient;

        private void StartDiagTimer()
        {
            _stallLogged = false;
            _diagTotalTicks = 0;
            _testIgnoreUpdates = false;
            _lastUpdateUtc = DateTime.UtcNow;
            _probeSentUtc = DateTime.MinValue;
            try
            {
                // the library creates a new client at each connection: we hook again to know when an updated screen arrives
                var client = VncZoomPolicy.GetClient(Vnc);
                if (client != null && !ReferenceEquals(client, _diagClient))
                {
                    _diagClient = client;
                    client.VncUpdate += (_, e) =>
                    {
                        if (!_testIgnoreUpdates) _lastUpdateUtc = DateTime.UtcNow;
                        if (!Dispatcher.CheckAccess()) _updOffUiThread++;
                        try { var r = e.DesktopUpdater.UpdateRectangle; _updCount++; _updPixels += (long)r.Width * r.Height; if ((long)r.Width * r.Height > _updMaxRect) _updMaxRect = (long)r.Width * r.Height; }
                        catch { /* statistics for diagnostics only */ }
                    };
                }
            }
            catch (Exception e) { Lemora.Utils.Diag.Log($"VNC-DIAG: hooking the updates failed ({e.Message})"); }
            if (_diagTimer == null)
            {
                _diagTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _diagTimer.Tick += (_, _) => DiagTick();
            }
            _diagTimer.Start();
        }

        private void DiagTick()
        {
            if (Status != ProtocolHostStatus.Connected || _closing) return;
            try
            {
                // every 30 s (6 rounds of 5 s): summary of the screen updates; in the first minute every 5 s, to see how the first load proceeds
                _diagTotalTicks++;
                if (++_diagTicks >= 6 || _diagTotalTicks <= 12)
                {
                    var window = _diagTicks >= 6 ? 30 : 5;
                    _diagTicks = 0;
                    var total = Math.Max(1L, (long)(_remoteSize.Width * _remoteSize.Height));
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): last {window} s: {_updCount} updates, {_updPixels / 1000} thousand pixels ({_updPixels * 100 / total}% of the remote screen), largest rectangle {_updMaxRect / 1000} thousand pixels, last {(DateTime.UtcNow - _lastUpdateUtc).TotalSeconds:0} s ago; processed OFF the UI thread: {_updOffUiThread}/{_updCount}; {_diagClient?.TakeStatsReport()}");
                    _updOffUiThread = 0; _updCount = 0; _updPixels = 0; _updMaxRect = 0;
                }
                var since = (DateTime.UtcNow - _lastUpdateUtc).TotalSeconds;
                // NO close for a "still screen": an idle remote desktop sends no updates and FullScreenUpdate() sends nothing until the server
                // answers (the library sends the request only after receiving a message). The check that closed after 30 s disconnected healthy sessions.
                // Real drops are detected by the TCP keepalive and the library read errors; here we only write to the log.
                if (since > 30 && !_stallLogged)
                {
                    _stallLogged = true;
                    var sock = FindSocket()?.Client;
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): no screen update for {since:0} s; socket connected={sock?.Connected}, tab visible={IsVisible}, VNC area={VncFormsHost.Visibility}, message={GridMessageBox.Visibility}, detected={Vnc.IsConnected}");
                }
                else if (since <= 30 && _stallLogged)
                {
                    _stallLogged = false;
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): screen updates resumed");
                }
            }
            catch (Exception e) { Lemora.Utils.Diag.Log($"VNC-DIAG: check failed ({e.Message})"); }
        }

        /// <summary>tests only: simulates a network drop by closing the socket under the library</summary>
        internal void TestDropConnection() { try { FindSocket()?.Close(); } catch { /* already closed */ } }
        internal string TestOverlayState => $"{Status} auto={_autoCts != null} msgBox={GridMessageBox.Visibility} text='{TbMessage.Text.Replace('\n', ' ')}'";

        #endregion automatic reconnection

        #endregion connection

        #endregion event handler

        private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnReconn_OnClick(object sender, RoutedEventArgs e)
        {
            ReConn();   // automatic reconnection active: it only skips the wait
        }
    }
}
