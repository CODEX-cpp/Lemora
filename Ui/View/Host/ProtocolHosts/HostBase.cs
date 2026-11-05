using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.View.Settings;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.Host.ProtocolHosts
{
    public enum ProtocolHostStatus
    {
        NotInit,
        Initializing,
        Initialized,
        Connecting,
        WaitingForReconnect,
        Connected,
        Disconnected,
    }

    public enum ProtocolHostType
    {
        Native,
        Integrate
    }

    public abstract class HostBase : UserControl
    {
        public ProtocolBase ProtocolServer { get; }

        private WindowBase? _parentWindow;
        public WindowBase? ParentWindow => _parentWindow;

        public virtual void SetParentWindow(WindowBase? value)
        {
            if (_parentWindow == value) return;
            _parentWindow = value;
            ParentWindowHandle = IntPtr.Zero;

            if (null == value) return;
            var window = Window.GetWindow(value);
            if (window != null)
            {
                var wih = new WindowInteropHelper(window);
                ParentWindowHandle = wih.Handle;
            }
        }

        public IntPtr ParentWindowHandle { get; private set; } = IntPtr.Zero;

        /// <summary>
        /// a flag to id if ProtocolServer can open session successfully.
        /// </summary>
        public bool HasConnected = false;

        /// <summary>quando la sessione si e' connessa (serve al tempo trascorso nella barra sessione)</summary>
        public DateTime? ConnectedAt { get; private set; }

        /// <summary>la sessione e' cambiata di stato (collegata, scollegata, in riconnessione): barra e schede aggiornano subito colore e scritta</summary>
        public event Action? StatusChanged;

        private ProtocolHostStatus _status = ProtocolHostStatus.NotInit;
        public ProtocolHostStatus Status
        {
            get => _status;
            protected set
            {
                if (_status != value)
                {
                    if (value == ProtocolHostStatus.Connected)
                    {
                        var first = !HasConnected;
                        HasConnected = true;
                        ConnectedAt = DateTime.Now;
                        // connessione rapida riuscita: solo ora si possono ricordare le credenziali (prima si salvavano anche se sbagliate)
                        if (first) Lemora.Service.QuickSessionFollowUp.OnSessionConnected(ProtocolServer);
                        Lemora.Utils.ActivityLog.Connected(ProtocolServer, !first);
                        _activityOpen = true;
                        _activityFailLogged = false;
                        ActivityReason = null;
                    }
                    else if (value == ProtocolHostStatus.Disconnected)
                        LogActivityEnd(ActivityReason);

                    SimpleLogHelper.Debug(this.GetType().Name + ": Status => " + value);
                    _status = value;
                    OnCanResizeNowChanged?.Invoke();
                    StatusChanged?.Invoke();
                }
            }
        }

        /// <summary>Registro attivita': motivo della prossima caduta, da impostare PRIMA di passare a Disconnected (es. codice RDP, errore della libreria VNC)</summary>
        public string? ActivityReason { get; set; }
        private bool _activityOpen;      // c'e' un "Connesso" scritto senza ancora la sua chiusura
        private bool _activityFailLogged; // tentativo fallito gia' scritto (la riconnessione automatica ne fa tanti: una riga sola finche' non si ricollega)

        /// <summary>
        /// Scrive la fine della sessione nel Registro attivita' (una volta sola): caduta o chiusura se era collegata, tentativo non riuscito se non lo e' mai stata.
        /// La chiama lo stato Disconnected e anche la chiusura della scheda (SessionControlService), con motivo "chiusa dall'utente".
        /// </summary>
        public void LogActivityEnd(string? reason)
        {
            if (_activityOpen)
            {
                _activityOpen = false;
                _activityFailLogged = false;
                Lemora.Utils.ActivityLog.Disconnected(ProtocolServer, reason, ConnectedAt is { } at ? DateTime.Now - at : null);
            }
            else if (!_activityFailLogged && (_status is ProtocolHostStatus.Connecting or ProtocolHostStatus.WaitingForReconnect or ProtocolHostStatus.Initializing or ProtocolHostStatus.Initialized))
            {
                _activityFailLogged = true;
                Lemora.Utils.ActivityLog.Failed(ProtocolServer, reason);
            }
            ActivityReason = null;
        }

        protected HostBase(ProtocolBase protocolServer, bool canFullScreen = false)
        {
            ProtocolServer = protocolServer;
            CanFullScreen = canFullScreen;

            // gestore password della cartella (linguetta sullo schermo): solo RDP e VNC salvati, lo decide DevicePasswordDrawer
            Loaded += (_, _) => DevicePasswordDrawer.Attach(this);

            // Add right click menu
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
                    Command = new RelayCommand((o) => { Close(); }),
                });
            }

            MenuItems.Add(new System.Windows.Controls.Separator());

            {
	            var subMenu = new System.Windows.Controls.MenuItem()
	            {
		            Header = IoC.Translate("Custom"),
	            };


	            {
		            var cb = new CheckBox
		            {
			            Content = IoC.Translate("Show XXX button", IoC.Translate("Reconnect") ?? ""),
			            IsHitTestVisible = false,
		            };

		            var binding = new Binding("TabHeaderShowReConnectButton")
		            {
			            Source = SettingsPage,
			            Mode = BindingMode.TwoWay,
			            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
		            };
		            cb.SetBinding(CheckBox.IsCheckedProperty, binding);
		            subMenu.Items.Add(new System.Windows.Controls.MenuItem()
		            {
			            Header = cb,
			            Command = new RelayCommand((_) => { SettingsPage.TabHeaderShowReConnectButton = !SettingsPage.TabHeaderShowReConnectButton; }),
		            });
				}

	            {
		            var cb = new CheckBox
		            {
			            Content = IoC.Translate("Show XXX button", IoC.Translate("Close") ?? ""),
			            IsHitTestVisible = false,
		            };

		            var binding = new Binding("TabHeaderShowCloseButton")
		            {
			            Source = SettingsPage,
			            Mode = BindingMode.TwoWay,
			            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
		            };
		            cb.SetBinding(CheckBox.IsCheckedProperty, binding);
		            subMenu.Items.Add(new System.Windows.Controls.MenuItem()
		            {
			            Header = cb,
			            Command = new RelayCommand((_) => { SettingsPage.TabHeaderShowCloseButton = !SettingsPage.TabHeaderShowCloseButton; }),
		            });
				}


	            {
		            var cb = new CheckBox
		            {
			            Content = IoC.Translate("Show XXX button", "Icon"),
			            IsHitTestVisible = false,
		            };

		            var binding = new Binding("TabHeaderShowIconButton")
		            {
			            Source = SettingsPage,
			            Mode = BindingMode.TwoWay,
			            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
		            };
		            cb.SetBinding(CheckBox.IsCheckedProperty, binding);
		            subMenu.Items.Add(new System.Windows.Controls.MenuItem()
		            {
			            Header = cb,
			            Command = new RelayCommand((_) => { SettingsPage.TabHeaderShowIconButton = !SettingsPage.TabHeaderShowIconButton; }),
		            });
	            }

				MenuItems.Add(subMenu);
			}

			MenuItems.Add(new Separator());

			var actions = protocolServer.GetActions(true);
            foreach (var action in actions)
            {
                var tb = new TextBlock()
                {
                    Text = action.ActionName,
                };
                MenuItems.Add(new System.Windows.Controls.MenuItem()
                {
                    Header = tb,
                    Command = new RelayCommand((o) => { action.Run(); }),
                });
            }
        }

        public string ConnectionId
        {
            get
            {
                if (ProtocolServer.IsOnlyOneInstance())
                {
                    return ProtocolServer.BuildConnectionId();
                }
                else
                {
                    return ProtocolServer.BuildConnectionId() + "_" + this.GetHashCode();
                }
            }
        }

        public SettingsPageViewModel SettingsPage => IoC.Get<SettingsPageViewModel>();

        public bool CanFullScreen { get; protected set; }

        /// <summary>indice del monitor su cui stava la finestra della sessione quando si e' chiesto lo schermo intero (-1 = non noto): lo schermo intero parte da li</summary>
        public int SourceScreenIndex { get; set; } = -1;

        /// <summary>
        /// special menu for tab
        /// </summary>
        public List<System.Windows.Controls.Control> MenuItems { get; set; } = new List<System.Windows.Controls.Control>();

        /// <summary>
        /// since resizing when rdp is connecting would not tiger the rdp size change event
        /// then I let rdp host return false when rdp is on connecting to prevent TabWindow resize or maximize.
        /// </summary>
        /// <returns></returns>
        public virtual bool CanResizeNow()
        {
            return true;
        }

        /// <summary>
        /// in rdp, tab window cannot resize until rdp is connected. or rdp will not fit window size.
        /// </summary>
        public Action? OnCanResizeNowChanged { get; set; } = null;

        /// <summary>true se il client sa cambiare le impostazioni dello schermo a sessione aperta (RDP integrato)</summary>
        public virtual bool CanChangeDisplayModeLive => false;

        /// <summary>false se il server remoto non sa cambiare risoluzione a sessione aperta (RDP prima di Windows Server 2012 R2)</summary>
        public virtual bool RemoteResizeSupported => true;

        /// <summary>copre subito la sessione con il caricamento: serve prima di un cambio che sta per ridisegnare la finestra (l'immagine vecchia non deve restare a vista)</summary>
        public virtual void ShowSwitchCover() { }
        /// <summary>applica alla sessione aperta le impostazioni dello schermo dell'oggetto server della sessione (adattamento, risoluzione, scala)</summary>
        public virtual void ApplyDisplaySettings() { }

        /// <summary>
        /// true = lo schermo remoto si riduce per stare tutto nella finestra (Picture in picture), senza cambiare la risoluzione remota;
        /// false = torna al comportamento scelto per la sessione
        /// </summary>
        public virtual void SetFitToWindow(bool on) { }

        /// <summary>dimensione dello schermo remoto in pixel (serve a tenere le proporzioni nel Picture in picture); vuota se non nota</summary>
        public virtual System.Windows.Size RemoteDesktopSize => System.Windows.Size.Empty;

        // ---- controlli della vista (VNC con desktop molto larghi/alti = piu' monitor uniti): "Adatta" e zoom, mostrati nella barra della sessione
        public virtual bool HasViewControls => false;
        public virtual string ZoomText => "100%";
        /// <summary>apre il menu "Adatta larghezza / Adatta altezza / Dimensioni reali" sotto al pulsante dato</summary>
        public virtual void ShowFitMenu(System.Windows.FrameworkElement anchor) { }
        /// <summary>Ctrl+3 / Ctrl+4: adatta la vista alla larghezza (true) o all'altezza (false); false = non applicabile</summary>
        public virtual bool SetFitMode(bool width) => false;
        public virtual void ZoomIn() { }
        public virtual void ZoomOut() { }
        public virtual void ZoomReset() { }
        public event Action? ViewControlsChanged;
        protected void RaiseViewControlsChanged() => ViewControlsChanged?.Invoke();

        public virtual void ToggleAutoResize(bool isEnable)
        {
        }

        public abstract void Conn();

        public abstract void ReConn();

        /// <summary>
        /// disconnect the session and close host window
        /// </summary>
        public virtual void Close()
        {
            DevicePasswordDrawer.Detach(this);
            this.OnClosed?.Invoke(ConnectionId);
        }

        protected virtual void GoFullScreen()
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// call to focus the AxRdp or putty
        /// </summary>
        public virtual void FocusOnMe()
        {
            // do nothing
        }

        public abstract ProtocolHostType GetProtocolHostType();

        /// <summary>
        /// if it is a Integrate host, then return process's hwnd.
        /// </summary>
        /// <returns></returns>
        public abstract IntPtr GetHostHwnd();

        public Action<string>? OnClosed { get; set; } = null;
        public Action<string>? OnFullScreen2Window { get; set; } = null;
    }
}