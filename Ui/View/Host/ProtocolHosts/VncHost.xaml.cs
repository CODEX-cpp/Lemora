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
    public sealed partial class VncHost : HostBase
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
            // clic nella corsia della barra (fuori dalla maniglia): una pagina nella direzione del clic.
            // Un ScrollBar fuori da uno ScrollViewer esegue questi comandi senza cambiare il valore: lo si fa qui.
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
                Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase?.DisplayName}): controllo ridimensionato dal layout a {Vnc.Width}x{Vnc.Height} px (non e' un cambio di risoluzione del PC remoto)");
                _zoom?.ScrollTo(_zoom.ScrollX, _zoom.ScrollY); Execute.OnUIThread(UpdateBars);
            };

            // la libreria scrive qui cosa va storto nel flusso VNC (errori di lettura, flusso sfasato, lunghezze assurde)
            // righe della libreria VNC: cambi di qualita' ed errori di lettura a livello Medio (spiegano lentezze e cadute), il resto (TRACE, tempi) solo in Approfondito
            VncClient.DiagLog ??= m =>
            {
                if (m.StartsWith("qualita'") || m.StartsWith("lettura dal server") || m.StartsWith("flusso sfasato") || m.StartsWith("errore nel") || m.StartsWith("appunti del server"))
                    Shawn.Utils.SimpleLogHelper.Debug("VNC libreria: " + m);
                else Lemora.Utils.Diag.Log("VNC-DIAG libreria: " + m);
            };

            // appunti: dal server (la libreria non puo' scriverli dal suo thread) e verso il server quando si torna sulla sessione o si incolla
            Vnc.ClipboardChanged += OnServerClipboard;
            Vnc.GotFocus += (_, _) => SyncClipboardToServer();
            // il tasto destro (e il menu del terminale) incollano dagli appunti del PC remoto: devono essere gia' aggiornati PRIMA del clic, quindi si manda il testo
            // appena il mouse entra nello schermo e al primo tasto Ctrl (cosi' il server ha il tempo di riceverlo prima del V di Ctrl+V)
            Vnc.MouseEnter += (_, _) => SyncClipboardToServer();
            Vnc.PreviewKeyDown += (_, e) =>
            {
                if (e.Control || (e.Shift && e.KeyCode == System.Windows.Forms.Keys.Insert)) SyncClipboardToServer();
            };

            // trascinamento di file sullo schermo remoto: si caricano nella destinazione predefinita (il disegno sta fuori dallo schermo, nella barra sessione)
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

        private DateTime _connStart = DateTime.UtcNow;   // per il registro: quanto passa dal collegamento richiesto a quello riuscito

        public override void Conn()
        {
            _connStart = DateTime.UtcNow;
            Status = ProtocolHostStatus.Connecting;
            if (Vnc.IsConnected)
            {
                DisconnectNow();   // chiusura completa del socket (vedi DisconnectNow)
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
        /// Prima si controlla il server FUORI dal thread dell'interfaccia (con tempi massimi), poi si chiede l'eventuale password,
        /// e solo alla fine si chiama Connect: quello e' bloccante e un server che non risponde (o che vuole un'autenticazione
        /// non supportata, come alcuni RealVNC) congelava l'intera app.
        /// </summary>
        private async System.Threading.Tasks.Task ConnAsync()
        {
            var address = _vncBase.Address;
            var port = _vncBase.GetPort();
            if (port <= 0) port = 5900;
            Vnc.VncPort = port;

            // un nome puo' dare piu' indirizzi (anche un IPv6 link-local che non risponde): si usa il primo che risponde davvero,
            // trovato adesso e non memorizzato
            if (!System.Net.IPAddress.TryParse(address, out _))
            {
                using var resolveCts = new System.Threading.CancellationTokenSource(20000);   // risoluzione del nome (a freddo puo' superare 3 s) + connessione
                var reachable = await Lemora.Utils.TcpHelper.FindReachableAddressAsync(address, port, resolveCts.Token, 5000);
                if (reachable != null) address = reachable.ToString();
            }

            // dopo una chiusura il server (es. TightVNC) ha bisogno di un attimo per ripulire la sessione: ricollegarsi subito (ricarica)
            // viene spesso chiuso a meta' handshake ("Unable to read beyond the end of the stream")
            var wait = 1200 - (DateTime.UtcNow - _lastDisconnect).TotalMilliseconds;
            if (wait > 0) await System.Threading.Tasks.Task.Delay((int)wait);

            string? pwd = null;
            var authFailures = 0;
            var skipDelay = false;
            for (var attempt = 0; ; attempt++)
            {
                if (attempt > 0 && !skipDelay) await System.Threading.Tasks.Task.Delay(1500);   // stesso motivo: si riprova dopo una breve pausa (al massimo 3 tentativi)
                skipDelay = false;
                var (failure, types) = await System.Threading.Tasks.Task.Run(() => VncPreflight.Check(address, port));
                if (failure != null)
                {
                    if (attempt < 2 && (failure == IoC.Translate("vnc_server_silent") || failure == IoC.Translate("vnc_server_refused")))
                    {
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): controllo preliminare non riuscito su {address}:{port} ({failure}), nuovo tentativo");
                        continue;
                    }
                    ShowConnError(failure);
                    return;
                }

                if (pwd == null)
                {
                    pwd = DataVault.DecryptOrReturnOriginalString(_vncBase.Password);
                    if (string.IsNullOrEmpty(pwd)) pwd = _typedPwd;   // digitata in questa sessione: la ricarica non la richiede di nuovo
                    if (string.IsNullOrEmpty(pwd) && types.Contains(2))
                    {
                        // password non salvata: si chiede prima di collegarsi (una chiave DES vuota verrebbe rifiutata da .NET)
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
                    // Una password rifiutata NON genera errori: la libreria segnala solo "connessione persa" (che chiuderebbe la sessione).
                    // Per questo durante il collegamento la chiusura automatica e' sospesa e si controlla se si e' davvero collegati.
                    _invokeOnClosedWhenDisconnected = false;
                    // qualita' grafica: Automatica parte bassa e sale se la linea regge; Bassa/Alta restano fisse
                    // (la scelta fatta dal menu "Adatta" durante la sessione vale anche dopo una riconnessione)
                    Vnc.QualityMode = _qualityChosen ?? _vncBase.VncQuality switch
                    {
                        VNC.EVncQuality.Low => VncSharpCore.VncQualityMode.Low,
                        VNC.EVncQuality.High => VncSharpCore.VncQualityMode.High,
                        _ => VncSharpCore.VncQualityMode.Auto,
                    };
                    _qualityChosen ??= Vnc.QualityMode;
                    Vnc.Connect(address, false, _vncBase.VncWindowResizeMode == VNC.EVncWindowResizeMode.Stretch);
                    if (!Vnc.IsConnected)
                    {
                        // password rifiutata: non e' "Connesso" (prima lo si dichiarava e la password sbagliata veniva anche ricordata per i collegamenti dopo)
                        authFailures++;
                        _typedPwd = null;
                        QuickSessionFollowUp.ForgetSavedCredentials(ProtocolServer);
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): password rifiutata dal server (tentativo {authFailures})");
                        Status = ProtocolHostStatus.Connecting;
                        VncFormsHost.Visibility = Visibility.Collapsed;
                        GridLoading.Visibility = Visibility.Visible;
                        GridMessageBox.Visibility = Visibility.Collapsed;
                        if (authFailures >= 3) { ShowConnError(IoC.Translate("host_bad_credentials"), fatal: true); return; }   // piu' prove di fila rischiano di bloccare l'accesso sul server
                        var quick = ProtocolServer.IsTmpSession();
                        var again = new PasswordPopupDialogViewModel(false, quick && IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
                        {
                            Title = $"[VNC]({_vncBase.DisplayName}) -> {_vncBase.Address}:{_vncBase.Port}",
                            IsUserNameVisible = false,
                            ErrorText = IoC.Translate("password_wrong"),   // contorno rosso + "Password errata"
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
                    // il trasferimento file si collega allo stesso server con la stessa password (connessione separata)
                    _ftAddress = address; _ftPort = port; _ftPassword = chosen;
                    ApplyKeepAlive();
                    // lo schermo remoto si mostra (e la rotellina sparisce) al primo fotogramma: vedi WaitFirstFrame
                    Status = ProtocolHostStatus.Connected;
                    return;
                }
                catch (Exception e) when (attempt < 2 && IsServerClosedEarly(e))
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): il server ha chiuso la connessione durante l'avvio ({e.Message}), nuovo tentativo");
                    try { FindSocket()?.Close(); } catch { /* gia' chiuso */ }
                }
                catch (Exception e)
                {
                    _typedPwd = null;   // se era sbagliata non si riusa
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
        /// <summary>interfaccia del trasferimento file di questa sessione (popup, coda, navigatore, trascinamento, appunti)</summary>
        public Lemora.View.FileTransfer.FileTransferViewModel FileTransferVm => _ftVm ??= new Lemora.View.FileTransfer.FileTransferViewModel(this);

        /// <summary>
        /// Trasferimento file di questa sessione (null finche' la sessione non si e' collegata). Funziona solo con TightVNC 2.x:
        /// con altri server <c>CheckAvailableAsync()</c> risponde false. Si chiude insieme alla sessione.
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
                var (addr, port, pwd) = (_ftAddress, _ftPort, _ftPassword);   // se la sessione si ricollega con un'altra password, il gestore va ricreato
                Lemora.Service.FileTransfer.FileTransferManager.DiagLog ??= m => Shawn.Utils.SimpleLogHelper.Debug("Trasferimento file: " + m);   // connessioni, nuovi tentativi, file non riusciti: livello Medio
                _fileTransfer = new Lemora.Service.FileTransfer.FileTransferManager(ct => Lemora.Service.FileTransfer.TightFtConnection.ConnectAsync(addr, port, pwd, ct), opts);
                return _fileTransfer;
            }
        }
        #endregion
        private DateTime _lastDisconnect = DateTime.MinValue;

        /// <summary>il server ha chiuso il collegamento a meta' handshake (non e' un rifiuto della password): vale la pena riprovare</summary>
        private static bool IsServerClosedEarly(Exception e)
            => e is System.IO.EndOfStreamException || e.InnerException is System.IO.EndOfStreamException
               || e.Message.Contains("beyond the end of the stream") || e.Message.Contains("inputCount");

        /// <param name="fatal">password rifiutata o annullata: la riconnessione automatica non deve riprovare</param>
        private void ShowConnError(string message, bool fatal = false)
        {
            _connFailed = true; _connFatal = fatal;
            // il motivo del fallimento resta nel registro anche col livello normale (prima c'era solo a livello Debug e non si poteva capire cosa fosse andato storto)
            Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase?.DisplayName}): collegamento fallito{(fatal ? " (definitivo)" : "")}: {message}");
            LogActivityEnd(message);   // Registro attivita': tentativo non riuscito (o caduta, se era collegata) col motivo
            _invokeOnClosedWhenDisconnected = false;
            VncFormsHost.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Visible;
            GridMessageBox.Visibility = Visibility.Visible;

            TbMessageTitle.Visibility = Visibility.Collapsed;
            BtnReconn.Visibility = Visibility.Visible;
            TbMessage.Text = message;
        }

        public override void ReConn()
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
            ActivityReason ??= "chiusa dall'utente";
            Status = ProtocolHostStatus.Disconnected;
            DisconnectNow();
            try { _ftVm?.Dispose(); } catch { }
            try { _fileTransfer?.Dispose(); } catch { }   // chiude anche i trasferimenti in corso
            _fileTransfer = null;

            // Unsubscribe from events to prevent memory leaks
            Vnc.ConnectComplete -= OnConnected;
            Vnc.ConnectionLost -= OnConnectionLost;

            base.Close();
        }

        private static readonly System.Reflection.FieldInfo? RfbField = typeof(VncClient).GetField("rfb", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>il socket verso il server (campo interno della libreria): serve per chiuderlo a mano se la chiusura della libreria si ferma a meta'</summary>
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
        /// Chiude subito e per intero la connessione verso il server VNC. Il protocollo non ha un messaggio di congedo: il server si accorge della fine
        /// solo dalla chiusura del socket (TightVNC rimette lo schermo normale solo allora). Il socket si chiude quindi sempre, anche a mano,
        /// pure se la chiusura della libreria fallisce a meta' o se la sessione non era ancora "connessa".
        /// </summary>
        public void DisconnectNow()
        {
            var tcp = FindSocket();
            try { if (tcp != null) tcp.LingerState = new System.Net.Sockets.LingerOption(true, 2); } catch { /* socket gia' chiuso */ }
            try
            {
                if (Vnc.IsConnected) Vnc.Disconnect();
            }
            catch (Exception e)
            {
                Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): chiusura della libreria non riuscita ({e.Message}), si chiude il socket");
            }
            finally
            {
                try { if (tcp?.Client is { Connected: true } sock) sock.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { /* gia' chiuso */ }
                try { tcp?.Close(); } catch { /* gia' chiuso */ }
            }
            Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): connessione verso il server chiusa");
        }

        /// <summary>Picture in picture: lo schermo remoto si riduce per stare tutto nella finestra; all'uscita torna alla modalita' scelta per il server</summary>
        public override void SetFitToWindow(bool on)
        {
            Execute.OnUIThread(() =>
            {
                try { _pip = on; ApplyView(); }
                catch (Exception e) { Shawn.Utils.SimpleLogHelper.Error(e); }
            });
        }

        #region adattamento alla finestra (desktop con piu' monitor uniti)

        private const double BarSize = 16;   // spessore delle barre di scorrimento (px indipendenti dal DPI), come nello stile IslandScrollBar

        /// <summary>null = tutto lo schermo remoto entra nella finestra (come "Stretch"); altrimenti la modalita' scelta, con scorrimento nostro</summary>
        private VncFitMode? _fit;
        private VncZoomPolicy? _zoom;   // non null = modalita' a scala fissa attiva
        private bool _pip;
        private bool _updatingBars;

        /// <summary>immagine molto piu' larga (o alta) di uno schermo singolo: l'unione di piu' monitor</summary>
        private bool IsMultiScreen => !_remoteSize.IsEmpty && _remoteSize.Height > 0
            && (_remoteSize.Width / _remoteSize.Height >= 2.5 || _remoteSize.Width / _remoteSize.Height <= 0.5);

        private void ApplyView()
        {
            ApplyViewCore();
            RaiseViewControlsChanged();
        }

        public override bool HasViewControls => IsMultiScreen && !_pip && VncFormsHost.Visibility == Visibility.Visible;
        public override string ZoomText => $"{Math.Round(_zoomFactor * 100)}%";
        public override void ZoomIn() { EnsureFit(); BtnZoomIn_OnClick(this, null!); }
        public override void ZoomOut() { EnsureFit(); BtnZoomOut_OnClick(this, null!); }

        /// <summary>lo zoom parte da una vista: se non ne e' stata scelta una (schermo intero ridotto) si parte da "adatta larghezza"</summary>
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

            // scala = (adattamento scelto, calcolato sullo spazio TOTALE) x zoom. Le barre si decidono dallo spazio totale (barre comprese):
            // cosi' comparire/sparire di una barra non cambia la scala e non si innesca un ciclo
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

        /// <summary>1 = la vista scelta (larghezza/altezza/reale); 1.25 = ingrandita del 25% rispetto a quella</summary>
        private double _zoomFactor = 1;
        private static readonly double[] ZoomSteps = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.25, 3.5 };   // tasti Ctrl +/-: scalini del 25% fino al 350% (il cursore del menu va a passi del 5%)

        private void SetZoomFactor(double f)
        {
            if (_zoom == null) return;
            // si resta sullo stesso punto: il centro della vista (in pixel remoti) prima e dopo
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

        /// <summary>il fuoco della tastiera e' dentro lo schermo remoto</summary>
        public bool HasRemoteFocus => Vnc.ContainsFocus || Vnc.Focused;
        public void FocusRemote() { try { Vnc.Focus(); } catch { /* controllo non ancora pronto */ } }

        /// <summary>invia un tasto (keysym X11) al server, come se fosse premuto o rilasciato sul computer remoto</summary>
        public void SendKey(uint keysym, bool pressed)
        {
            try { VncZoomPolicy.GetClient(Vnc)?.WriteKeyboardEvent(keysym, pressed); }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): tasto non inviato ({e.Message})"); }
        }

        internal ContextMenu? LastFitMenu;   // per le prove (foto del menu)

        /// <summary>qualita' scelta in questa sessione (menu "Adatta"); null finche' non si collega la prima volta</summary>
        private VncSharpCore.VncQualityMode? _qualityChosen;

        /// <summary>cambia la qualita' grafica a sessione aperta, senza ricollegarsi</summary>
        private void SetQualityLive(VncSharpCore.VncQualityMode mode)
        {
            _qualityChosen = mode;
            Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): qualita' grafica scelta dal menu: {mode}");
            try { VncZoomPolicy.GetClient(Vnc)?.SetQualityMode(mode); }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): cambio di qualita' non applicato ({e.Message})"); }
        }

        private void ShowFitMenuCore(FrameworkElement anchor)
        {
            const double menuWidth = 340;   // allineato al bordo destro del pulsante: cosi' il menu non esce dalla finestra
            var menu = new ContextMenu
            {
                PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Width = menuWidth, HorizontalOffset = anchor.ActualWidth - menuWidth, VerticalOffset = 2, Padding = new Thickness(0),
            };

            // ---- pezzi riusati: testi e riquadri cliccabili (bordo sottile, evidenziati se scelti)
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

            // ---- intestazione e le due viste
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

            // ---- zoom: titolo e valore in accent, - cursore + (passo 25%, 25-350%), preset
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

            // ---- piede: intervallo e "Ripristina 100%"
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

            // ---- qualita' grafica (cambia a sessione aperta, come in RealVNC)
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
                    _ = System.Threading.Tasks.Task.Delay(900).ContinueWith(_ => Execute.OnUIThread(() => { if (menu.IsOpen) QSync(); }));   // il cambio vero arriva appena il server risponde
                });
                qBox.Height = 38; qBox.Margin = new Thickness(3, 0, 3, 0);
                qBoxes.Add((qBox, mode));
                qTiles.Children.Add(qBox);
            }
            root.Children.Add(qTiles);
            root.Children.Add(qStatus);
            QSync();

            Sync();
            // WPF avvolge il contenuto in una voce di menu (MenuItem) e il tema la evidenzia al passaggio del mouse: la voce diventa un semplice contenitore, senza aspetto proprio
            var plainItem = new Style(typeof(MenuItem));
            // il contenuto di una voce di menu sta nell'intestazione (Header), non in Content
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            plainItem.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter }));
            plainItem.Setters.Add(new Setter(UIElement.FocusableProperty, false));
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
            _lastClip = ""; _lastClipSeq = 0;   // nuova connessione: il server non ha ancora i nostri appunti, vanno rimandati
            if (e is VncSharpCore.ConnectEventArgs ce && ce.DesktopWidth > 0 && ce.DesktopHeight > 0)
                _remoteSize = new System.Windows.Size(ce.DesktopWidth, ce.DesktopHeight);
            if (!Dispatcher.CheckAccess())
            {
                Execute.OnUIThread(() => OnConnected(sender, e));
                return;
            }
            Status = ProtocolHostStatus.Connected;
            ApplyKeepAlive();
            Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): collegato a {_vncBase.Address} in {(DateTime.UtcNow - _connStart).TotalSeconds:0.0} s dal clic, schermo remoto {_remoteSize.Width:0}x{_remoteSize.Height:0}");
            StartDiagTimer();
            WaitFirstFrame();
            _zoom = null;   // la connessione ha creato una policy nuova
            if (_hasSavedView)
            {
                // ricollegamento: si torna alla vista di prima (adatta altezza/larghezza, zoom); lo scorrimento si rimette al primo fotogramma
                _fit = _savedFit; _zoomFactor = _savedZoom;
            }
            else
            {
                // "Fixed" = dimensioni reali con scorrimento; "Stretch" = tutto nella finestra (finche' non si sceglie dal menu "Adatta")
                _fit = _vncBase.VncWindowResizeMode == VNC.EVncWindowResizeMode.Fixed ? VncFitMode.Actual : null;
            }
            ApplyView();
        }

        // ---- vista (adattamento, zoom, scorrimento) mantenuta quando la sessione si ricollega
        private bool _hasSavedView;
        private VncFitMode? _savedFit;
        private double _savedZoom = 1;
        private int _savedSx, _savedSy;

        private void RememberView()
        {
            if (!_revealed || _remoteSize.IsEmpty) return;   // solo se la sessione era mostrata: un tentativo fallito non sovrascrive la vista buona
            _savedFit = _fit; _savedZoom = _zoomFactor; _savedSx = _zoom?.ScrollX ?? 0; _savedSy = _zoom?.ScrollY ?? 0; _hasSavedView = true;
        }

        private void RestoreScroll()
        {
            if (!_hasSavedView || _zoom == null) return;
            var sx = _savedSx; var sy = _savedSy;
            Dispatcher.BeginInvoke(new Action(() => { _zoom?.ScrollTo(sx, sy); UpdateBars(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- appunti
        private string _lastClip = "";
        private uint _lastClipSeq;   // numero di sequenza degli appunti di Windows all'ultimo controllo: se non e' cambiato non c'e' niente da leggere
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        /// <summary>il testo copiato sul tuo PC passa al PC remoto (RFB: solo testo, Latin-1) quando si torna sulla sessione, il mouse entra nello schermo o si incolla</summary>
        private void SyncClipboardToServer()
        {
            if (Status != ProtocolHostStatus.Connected) return;
            try
            {
                uint seq = IsDemo ? 0 : GetClipboardSequenceNumber();
                if (!IsDemo && seq == _lastClipSeq) return;
                if (!IsDemo && !System.Windows.Clipboard.ContainsText()) { _lastClipSeq = seq; return; }
                var text = IsDemo ? TestLocalClip : System.Windows.Clipboard.GetText();   // nelle prove non si toccano gli appunti veri
                if (string.IsNullOrEmpty(text) || text == _lastClip || text.Length > 1_000_000) { _lastClipSeq = seq; return; }
                var client = VncZoomPolicy.GetClient(Vnc);
                if (client == null) return;
                client.WriteClientCutText(text.Replace("\r\n", "\n"));
                _lastClip = text;
                _lastClipSeq = seq;
                Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): appunti inviati al PC remoto ({text.Length} caratteri)");
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): appunti non inviati ({e.Message})"); }
        }

        /// <summary>il testo copiato sul PC remoto arriva negli appunti di questo PC (la libreria lo riceve su un thread che non puo' usare gli appunti)</summary>
        private void OnServerClipboard(object? sender, EventArgs e)
        {
            try
            {
                var text = VncZoomPolicy.GetClient(Vnc)?.LastServerCutText;
                // "Copia file" aspetta il testo (il percorso dei file) e lo prende lui, anche se uguale al precedente: non va negli appunti di questo PC
                if (_clipCapture != null && !string.IsNullOrEmpty(text)) { _lastClip = text; _clipCapture.TrySetResult(text); return; }
                if (string.IsNullOrEmpty(text) || text == _lastClip) return;
                if (IsDemo) { TestServerClip = text; _lastClip = text; return; }   // nelle prove non si toccano gli appunti veri
                for (var i = 0; i < 5; i++)
                {
                    try { System.Windows.Clipboard.SetText(text); _lastClip = text; return; }
                    catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(50); }   // appunti occupati da un altro programma
                }
            }
            catch (Exception ex) { Shawn.Utils.SimpleLogHelper.Warning($"VNC({_vncBase.DisplayName}): appunti del PC remoto non copiati ({ex.Message})"); }
        }

        private System.Threading.Tasks.TaskCompletionSource<string?>? _clipCapture;

        /// <summary>
        /// Esegue <paramref name="trigger"/> (tasti mandati al PC remoto) e restituisce il primo testo che il PC remoto copia negli appunti entro
        /// <paramref name="timeoutMs"/>, oppure null. Il testo NON va negli appunti di questo PC.
        /// </summary>
        public async System.Threading.Tasks.Task<string?> CaptureServerClipboardAsync(Action trigger, int timeoutMs)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _clipCapture = tcs;
            try
            {
                trigger();
                var done = await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(timeoutMs));
                return done == tcs.Task ? tcs.Task.Result : null;
            }
            finally { _clipCapture = null; }
        }

        /// <summary>una scorciatoia: modificatori premuti, il tasto premuto e rilasciato, poi i modificatori rilasciati (keysym X11)</summary>
        public void SendChord(uint[] modifiers, uint key)
        {
            foreach (var m in modifiers) SendKey(m, true);
            SendKey(key, true); SendKey(key, false);
            foreach (var m in modifiers.Reverse()) SendKey(m, false);
        }

        /// <summary>solo per le prove</summary>
        private static bool IsDemo => Environment.GetEnvironmentVariable("LEMORA_DEMO") != null;
        internal string TestLocalClip = "";
        internal string TestServerClip = "";
        internal void TestSyncClipboard() => SyncClipboardToServer();
        internal bool TestRevealed => _revealed;
        internal System.Windows.Controls.Primitives.ScrollBar TestBarH => BarH;
        internal System.Windows.Controls.Primitives.ScrollBar TestBarV => BarV;
        internal double TestSecondsSinceUpdate => (DateTime.UtcNow - _lastUpdateUtc).TotalSeconds;
        internal string TestViewState => $"adatta={_fit} zoom={_zoomFactor} scorrimentoX={_zoom?.ScrollX} Y={_zoom?.ScrollY}";
        internal void TestSetView(bool height, int scrollX) { SetFitMode(!height); _zoom?.ScrollTo(scrollX, 0); UpdateBars(); }
        /// <param name="delta">120 = uno scatto di rotellina; un touchpad manda tanti eventi piccoli (es. 30)</param>
        internal void TestWheel(int notches, int delta = 120)
        {
            var m = typeof(System.Windows.Forms.Control).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var i = 0; i < notches; i++) m.Invoke(Vnc, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 100, 100, delta) });
        }

        private bool _revealed;
        private VncUpdateHandler? _firstFrameHandler;

        /// <summary>
        /// Finche' non arriva il primo fotogramma il controllo VNC mostrerebbe da solo il suo "Connecting to VNC host, please wait..." (inglese, piccolo):
        /// resta nascosto e si vede la rotellina con la scritta nostra. Se il fotogramma non arriva entro 10 s si mostra comunque.
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
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): primo fotogramma mostrato {(DateTime.UtcNow - _connStart).TotalSeconds:0.0} s dopo il clic");
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
            // puo' arrivare da un thread di lavoro (chiusura della sessione): i controlli si toccano solo dal thread dell'interfaccia
            if (!Dispatcher.CheckAccess())
            {
                Execute.OnUIThread(() => OnConnectionLost(sender, e));
                return;
            }
            RememberView();   // la vista di prima si ripristina al ricollegamento
            Shawn.Utils.SimpleLogHelper.Warning($"VNC-DIAG({_vncBase.DisplayName}): connessione persa (stato {Status}, chiusura voluta={_closing}, riconnessione gia' attiva={_autoCts != null}, chiusura automatica abilitata={_invokeOnClosedWhenDisconnected})");
            if (_closing || _autoCts != null) return;   // chiusura voluta, oppure la riconnessione automatica e' gia' in corso
            // Registro attivita': il motivo detto dalla libreria (scritto anche con la diagnosi spenta)
            var why = VncZoomPolicy.GetClient(Vnc)?.LastError;
            ActivityReason = string.IsNullOrEmpty(why) ? null : why;
            Status = ProtocolHostStatus.Disconnected;
            ShowLostOverlay();
            TbMessage.Text = "Connessione persa...";
            // prima la sessione si chiudeva da sola (anche per un riavvio del PC remoto): ora resta aperta e si riconnette da sola
            if (_invokeOnClosedWhenDisconnected)
                StartAutoReconnect();
        }

        #region riconnessione automatica

        private System.Threading.CancellationTokenSource? _autoCts;
        private bool _closing, _wakeNow, _connFailed, _connFatal;

        /// <summary>pausa tra un controllo e l'altro: 3 s nei primi 5 minuti (un riavvio tipico), poi 10 s fino a 30 minuti, poi 30 s</summary>
        private static int RetryInterval(TimeSpan lostFor) => lostFor.TotalMinutes < 5 ? 3 : lostFor.TotalMinutes < 30 ? 10 : 30;

        private void ShowLostOverlay()
        {
            VncFormsHost.Visibility = Visibility.Collapsed;
            GridLoading.Visibility = Visibility.Collapsed;
            GridMessageBox.Visibility = Visibility.Visible;
            TbMessageTitle.Visibility = Visibility.Visible;
            TbMessageTitle.Text = "Connessione persa";
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

        private enum Probe { Down, ServiceOff, Open }

        /// <summary>controllo leggero: si apre solo la connessione alla porta (2,5 s al massimo). Rifiutata = il PC e' acceso ma il servizio VNC non e' ancora attivo; nessuna risposta = PC spento o rete assente</summary>
        private async System.Threading.Tasks.Task<Probe> ProbeAsync(System.Threading.CancellationToken ct)
        {
            var port = _vncBase.GetPort();
            if (port <= 0) port = 5900;
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(_vncBase.Address, port);
            try
            {
                var done = await System.Threading.Tasks.Task.WhenAny(connect, System.Threading.Tasks.Task.Delay(2500, ct));
                if (done != connect)
                {
                    _ = connect.ContinueWith(t => { _ = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);   // l'esito tardivo non va perso come eccezione non osservata
                    return Probe.Down;
                }
                await connect;
                return Probe.Open;
            }
            catch (System.Net.Sockets.SocketException se) { return se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ? Probe.ServiceOff : Probe.Down; }
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
                TbMessage.Text = $"{state}\nPersa da {(int)lostFor.TotalMinutes:00}:{lostFor.Seconds:00}  ·  nuovo controllo tra {s} s  ·  controlli: {probes}\nRiprovo da solo finché non chiudi la scheda (se il PC si sta riavviando, aspetto).";
                await System.Threading.Tasks.Task.Delay(1000, ct);
            }
        }

        /// <summary>
        /// Resta attiva finche' non ci si ricollega o si chiude la scheda (nessun limite di tempo): se il PC remoto si sta riavviando per gli
        /// aggiornamenti, la sessione e' li' quando torna. Ogni giro e' un controllo leggero della porta; il collegamento vero parte solo quando
        /// la porta risponde. Si ferma solo se il server rifiuta la password (piu' prove di fila bloccherebbero l'account).
        /// </summary>
        private async System.Threading.Tasks.Task AutoReconnectLoop(System.Threading.CancellationToken ct)
        {
            var since = DateTime.UtcNow;
            var name = _vncBase.DisplayName;
            var probes = 0;
            var wasDown = false;
            Shawn.Utils.SimpleLogHelper.Warning($"VNC({name}): connessione persa con {_vncBase.Address}, riconnessione automatica attiva");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var probe = await ProbeAsync(ct);
                    probes++;
                    if (probe != Probe.Open)
                    {
                        wasDown = true;
                        await CountdownAsync(RetryInterval(DateTime.UtcNow - since), probe == Probe.ServiceOff ? "PC acceso, VNC non ancora attivo" : "PC non raggiungibile", since, probes, ct);
                        continue;
                    }
                    if (wasDown)
                    {
                        // la porta si e' appena riaperta: il servizio VNC ha bisogno di un attimo per essere pronto
                        ShowLostOverlay();
                        TbMessage.Text = "PC di nuovo raggiungibile, preparo il collegamento…";
                        await System.Threading.Tasks.Task.Delay(2000, ct);
                        wasDown = false;
                    }
                    ShowLostOverlay();
                    TbMessage.Text = "Connessione in corso…";
                    _connFailed = false; _connFatal = false;
                    ReConn();
                    // l'esito: collegato, oppure ShowConnError (il server ha chiuso a meta' o non risponde)
                    for (var i = 0; i < 200 && Status != ProtocolHostStatus.Connected && !_connFailed; i++)
                        await System.Threading.Tasks.Task.Delay(300, ct);
                    if (Status == ProtocolHostStatus.Connected)
                    {
                        _autoCts = null;
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC({name}): riconnesso dopo {(DateTime.UtcNow - since).TotalSeconds:0} s e {probes} controlli");
                        NotifyReconnected(name);
                        return;
                    }
                    if (_connFatal)
                    {
                        _autoCts = null;
                        Shawn.Utils.SimpleLogHelper.Warning($"VNC({name}): riconnessione automatica interrotta (password non accettata)");
                        return;
                    }
                    Shawn.Utils.SimpleLogHelper.Debug($"VNC({name}): collegamento non riuscito, si riprova");
                    wasDown = true;
                    await CountdownAsync(RetryInterval(DateTime.UtcNow - since), "Il server non ha completato il collegamento", since, probes, ct);
                }
            }
            catch (OperationCanceledException) { /* scheda chiusa */ }
        }

        /// <summary>sessione tornata: avviso nell'app e, se la finestra non e' in primo piano, lampeggio nella barra delle applicazioni</summary>
        private void NotifyReconnected(string name)
        {
            try
            {
                ToastService.Show($"{name}: riconnesso");
                (IoC.Get<MainWindowViewModel>().View as Window)?.FlashIfNotActive(4, 400);
            }
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Debug("VNC: avviso di riconnessione non mostrato: " + e.Message); }
        }

        /// <summary>
        /// Controllo di connessione (keepalive TCP): dopo 5 s di silenzio parte un controllo ogni 2 s, e dopo 3 senza risposta il collegamento
        /// risulta caduto (circa 11 s) invece dei circa 45 s del solo timeout di lettura.
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
            catch (Exception e) { Shawn.Utils.SimpleLogHelper.Debug($"VNC({_vncBase.DisplayName}): keepalive non impostato ({e.Message})"); }
        }


        // ---- VNC-DIAG (temporaneo): se lo schermo non riceve aggiornamenti mentre risulta collegato, lo scrive nel log una volta per episodio
        private System.Windows.Threading.DispatcherTimer? _diagTimer;
        private bool _stallLogged;

        private DateTime _lastUpdateUtc = DateTime.UtcNow;
        private DateTime _probeSentUtc = DateTime.MinValue;
        private bool _testIgnoreUpdates;
        // statistiche degli aggiornamenti ricevuti (diagnosi): quanti rettangoli e quanti pixel, per capire se il server manda cambiamenti veri o immagini ferme
        private long _updCount, _updPixels, _updMaxRect, _updOffUiThread;
        private int _diagTicks, _diagTotalTicks;
        /// <summary>solo per le prove: simula una sessione bloccata (il server non manda piu' fotogrammi)</summary>
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
                // la libreria crea un client nuovo a ogni collegamento: ci si riaggancia per sapere quando arriva uno schermo aggiornato
                var client = VncZoomPolicy.GetClient(Vnc);
                if (client != null && !ReferenceEquals(client, _diagClient))
                {
                    _diagClient = client;
                    client.VncUpdate += (_, e) =>
                    {
                        if (!_testIgnoreUpdates) _lastUpdateUtc = DateTime.UtcNow;
                        if (!Dispatcher.CheckAccess()) _updOffUiThread++;
                        try { var r = e.DesktopUpdater.UpdateRectangle; _updCount++; _updPixels += (long)r.Width * r.Height; if ((long)r.Width * r.Height > _updMaxRect) _updMaxRect = (long)r.Width * r.Height; }
                        catch { /* statistiche solo per la diagnosi */ }
                    };
                }
            }
            catch (Exception e) { Lemora.Utils.Diag.Log($"VNC-DIAG: aggancio agli aggiornamenti non riuscito ({e.Message})"); }
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
                // ogni 30 s (6 giri da 5 s): riepilogo degli aggiornamenti dello schermo; nel primo minuto ogni 5 s, per vedere come procede il primo caricamento
                _diagTotalTicks++;
                if (++_diagTicks >= 6 || _diagTotalTicks <= 12)
                {
                    var window = _diagTicks >= 6 ? 30 : 5;
                    _diagTicks = 0;
                    var total = Math.Max(1L, (long)(_remoteSize.Width * _remoteSize.Height));
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): ultimi {window} s: {_updCount} aggiornamenti, {_updPixels / 1000} migliaia di pixel ({_updPixels * 100 / total}% dello schermo remoto), rettangolo piu' grande {_updMaxRect / 1000} migliaia di pixel, ultimo {(DateTime.UtcNow - _lastUpdateUtc).TotalSeconds:0} s fa; elaborati FUORI dal thread dell'interfaccia: {_updOffUiThread}/{_updCount}; {_diagClient?.TakeStatsReport()}");
                    _updOffUiThread = 0; _updCount = 0; _updPixels = 0; _updMaxRect = 0;
                }
                var since = (DateTime.UtcNow - _lastUpdateUtc).TotalSeconds;
                // NIENTE chiusura per "schermo fermo": un desktop remoto immobile non manda aggiornamenti e FullScreenUpdate() non spedisce nulla finche' il server
                // non risponde (la libreria manda la richiesta solo dopo aver ricevuto un messaggio). Il controllo che chiudeva dopo 30 s ha scollegato sessioni sane
                // (11:46 e 11:50 del 29/10). Le cadute vere le rilevano il keepalive TCP e gli errori di lettura della libreria; qui si scrive solo nel log.
                if (since > 30 && !_stallLogged)
                {
                    _stallLogged = true;
                    var sock = FindSocket()?.Client;
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): nessun aggiornamento dello schermo da {since:0} s; socket collegato={sock?.Connected}, scheda visibile={IsVisible}, area VNC={VncFormsHost.Visibility}, messaggio={GridMessageBox.Visibility}, rilevato={Vnc.IsConnected}");
                }
                else if (since <= 30 && _stallLogged)
                {
                    _stallLogged = false;
                    Lemora.Utils.Diag.Log($"VNC-DIAG({_vncBase.DisplayName}): aggiornamenti dello schermo ripresi");
                }
            }
            catch (Exception e) { Lemora.Utils.Diag.Log($"VNC-DIAG: controllo non riuscito ({e.Message})"); }
        }

        /// <summary>solo per le prove: simula la caduta della rete chiudendo il socket sotto la libreria</summary>
        internal void TestDropConnection() { try { FindSocket()?.Close(); } catch { /* gia' chiuso */ } }
        internal string TestOverlayState => $"{Status} auto={_autoCts != null} msgBox={GridMessageBox.Visibility} testo='{TbMessage.Text.Replace('\n', ' ')}'";

        #endregion riconnessione automatica

        #endregion connection

        #endregion event handler

        private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnReconn_OnClick(object sender, RoutedEventArgs e)
        {
            if (_autoCts != null) { _wakeNow = true; return; }   // riconnessione automatica attiva: "Riconnetti" salta l'attesa
            ReConn();
        }
    }
}
