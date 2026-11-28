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

        /// <summary>when the session connected (used for the elapsed time in the session bar)</summary>
        public DateTime? ConnectedAt { get; private set; }

        /// <summary>the session changed state (connected, disconnected, reconnecting): bar and tabs update color and text immediately</summary>
        public event Action? StatusChanged;

        /// <summary>true = the "Connected" state arrives before sign-in (RDP: right after Connect()): the host itself calls QuickSessionFollowUp.OnSessionConnected when the login succeeded</summary>
        protected virtual bool NotifyConnectedFollowUpManually => false;

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
                        // quick connection succeeded: only now can the credentials be remembered (before, they were saved even when wrong)
                        if (first && !NotifyConnectedFollowUpManually) Lemora.Service.QuickSessionFollowUp.OnSessionConnected(ProtocolServer);
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

        /// <summary>Activity log: reason for the next drop, to be set BEFORE switching to Disconnected (e.g. RDP code, VNC library error)</summary>
        public string? ActivityReason { get; set; }
        private bool _activityOpen;      // a "Connected" has been written without its closing yet
        private bool _activityFailLogged; // failed attempt already written (automatic reconnection makes many: a single line until it reconnects)

        /// <summary>
        /// Writes the end of the session to the Activity log (only once): drop or close if it was connected, failed attempt if it never was.
        /// Called by the Disconnected state and also by the tab close (SessionControlService), with a "closed by the user" reason.
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

            // folder password manager (handle on the screen): saved RDP and VNC only, DevicePasswordDrawer decides
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

        private string? _connectionId;
        /// <summary>
        /// Fixed from the first request (the session registration). It contains the hash of user and password: if it were recomputed after the user
        /// re-enters the credentials (password rejected) it would change, and the tab's X would no longer find the registered session.
        /// </summary>
        public string ConnectionId => _connectionId ??= ProtocolServer.IsOnlyOneInstance()
            ? ProtocolServer.BuildConnectionId()
            : ProtocolServer.BuildConnectionId() + "_" + this.GetHashCode();

        public SettingsPageViewModel SettingsPage => IoC.Get<SettingsPageViewModel>();

        public bool CanFullScreen { get; protected set; }

        /// <summary>index of the monitor the session window was on when full screen was requested (-1 = unknown): full screen starts there</summary>
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

        /// <summary>true if the client can change the display settings while the session is open (built-in RDP)</summary>
        public virtual bool CanChangeDisplayModeLive => false;

        /// <summary>true if fit, resolution and scale change without reconnecting (Microsoft component); FreeRDP reads them only at startup</summary>
        public virtual bool AppliesDisplayLive => false;

        /// <summary>false if the remote server cannot change resolution while the session is open (RDP before Windows Server 2012 R2)</summary>
        public virtual bool RemoteResizeSupported => true;

        /// <summary>covers the session with the loading screen right away: needed before a change that is about to redraw the window (the old image must not stay visible)</summary>
        public virtual void ShowSwitchCover() { }
        /// <summary>applies to the open session the display settings of the session's server object (fit, resolution, scale)</summary>
        public virtual void ApplyDisplaySettings() { }

        /// <summary>
        /// true = the remote screen shrinks to fit entirely in the window (Picture in picture), without changing the remote resolution;
        /// false = back to the behavior chosen for the session
        /// </summary>
        public virtual void SetFitToWindow(bool on) { }

        /// <summary>remote screen size in pixels (used to keep the aspect ratio in Picture in picture); empty if unknown</summary>
        public virtual System.Windows.Size RemoteDesktopSize => System.Windows.Size.Empty;

        // ---- view controls (VNC with very wide/tall desktops = several merged monitors): "Fit" and zoom, shown in the session bar
        public virtual bool HasViewControls => false;
        public virtual string ZoomText => "100%";
        /// <summary>opens the "Fit width / Fit height / Actual size" menu below the given button</summary>
        public virtual void ShowFitMenu(System.Windows.FrameworkElement anchor) { }
        /// <summary>Ctrl+3 / Ctrl+4: fits the view to the width (true) or the height (false); returns false = not applicable</summary>
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

        /// <summary>
        /// the session became visible (connected): if it is the one on screen and the window is active it takes the focus. When the
        /// tab opened, the focus arrived while the loading screen was still there and got lost: typing went into the Home search
        /// </summary>
        protected void FocusIfActiveSession()
        {
            Stylet.Execute.OnUIThread(() =>
            {
                try
                {
                    var main = IoC.TryGet<Lemora.View.MainWindowViewModel>();
                    // "on screen" = visible: only the shown session (or its partner in split view) is.
                    // The comparison with ActiveSession was false with the session on screen
                    var active = IsVisible;
                    var winActive = ParentWindow is System.Windows.Window { IsActive: true };
                    SimpleLogHelper.Debug($"FOCUS-DIAG({ProtocolServer.DisplayName}): session ready, on screen={active}, active for Lemora={(main == null ? "no vm" : (main.ActiveSession == this).ToString())}, window active={winActive}, focus before={FocusDiag.Describe()}");
                    if (active && winActive) FocusOnMe();
                    // after a moment: where the focus really ended up
                    System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => Stylet.Execute.OnUIThread(() =>
                        SimpleLogHelper.Debug($"FOCUS-DIAG({ProtocolServer.DisplayName}): focus after={FocusDiag.Describe()}")));
                }
                catch { /* session closing */ }
            });
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