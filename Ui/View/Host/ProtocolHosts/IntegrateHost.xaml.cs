using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Model.ProtocolRunner;
using Lemora.Model.ProtocolRunner.Default;
using Lemora.Service;
using Lemora.Utils;
using Lemora.Utils.PuTTY;
using Lemora.Utils.PuTTY.Model;
using Shawn.Utils;
using Shawn.Utils.Wpf.Controls;
using Stylet;
using Path = System.IO.Path;
using Timer = System.Timers.Timer;

/*
 * Note:

We should add <UseWindowsForms>true</UseWindowsForms> in the csproj.

<PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>netcoreapp3.0</TargetFramework>
    <UseWpf>true</UseWpf>
    <UseWindowsForms>true</UseWindowsForms>
</PropertyGroup>


 */

namespace Lemora.View.Host.ProtocolHosts
{
    /// <summary>container of the external program window: notifies when clicked inside (WM_PARENTNOTIFY also arrives from another process)</summary>
    internal sealed class ClickFocusPanel : System.Windows.Forms.Panel
    {
        public Action? ChildClicked;

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            const int WM_PARENTNOTIFY = 0x0210;
            if (m.Msg == WM_PARENTNOTIFY)
            {
                var evt = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (evt is 0x0201 or 0x0204 or 0x0207) ChildClicked?.Invoke();   // left, right, middle button
            }
            base.WndProc(ref m);
        }
    }

    public partial class IntegrateHost : HostBase, IDisposable
    {
        #region API

        [DllImport("User32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern int ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        public delegate bool WndEnumProc(IntPtr hWnd, int lParam);
        [DllImport("user32.dll")]
        public static extern int EnumWindows(WndEnumProc lpEnumFunc, int lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern int SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern IntPtr SetFocus(HandleRef hWnd);
        [DllImport("user32")]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32")]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // FreeRDP: Windows notification when windows of the FreeRDP process only are created, to attach them to the tab before they are visible
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        /// <summary>
        /// Ctrl+Alt+Del to the remote PC: keys simulated to the FreeRDP window (Windows does not treat them as the real Ctrl+Alt+Del,
        /// so they reach FreeRDP, which forwards them)
        /// </summary>
        private void SendCtrlAltDelToFreeRdp()
        {
            var h = McpWindow();
            if (h == IntPtr.Zero) h = _exeHandles.LastOrDefault();
            if (h == IntPtr.Zero) return;
            FocusOnMe();
            SetFocus(new HandleRef(this, h));
            const uint KEYUP = 0x2, EXTENDED = 0x1;
            keybd_event(0x11, 0x1D, 0, UIntPtr.Zero);              // Ctrl
            keybd_event(0x12, 0x38, 0, UIntPtr.Zero);              // Alt
            keybd_event(0x2E, 0x53, EXTENDED, UIntPtr.Zero);       // Del
            keybd_event(0x2E, 0x53, EXTENDED | KEYUP, UIntPtr.Zero);
            keybd_event(0x12, 0x38, KEYUP, UIntPtr.Zero);
            keybd_event(0x11, 0x1D, KEYUP, UIntPtr.Zero);
        }

        private static string WinInfo(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            return $"hwnd={h} class={sb} visible={IsWindowVisible(h)}";
        }

        // https://stackoverflow.com/a/57819801/8629624
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        public static string GetWindowTitle(IntPtr hWnd)
        {
            var length = GetWindowTextLength(hWnd) + 1;
            var title = new StringBuilder(length);
            GetWindowText(hWnd, title, length);
            return title.ToString();
        }

        internal enum GetWindowLongIndex
        {
            GWL_STYLE = -16,
            GWL_EXSTYLE = -20
        }
        internal enum ShowWindowStyles : short
        {
            SW_HIDE = 0,
            SW_SHOWNORMAL = 1,
            SW_NORMAL = 1,
            SW_SHOWMINIMIZED = 2,
            SW_SHOWMAXIMIZED = 3,
            SW_MAXIMIZE = 3,
            SW_SHOWNOACTIVATE = 4,
            SW_SHOW = 5,
            SW_MINIMIZE = 6,
            SW_SHOWMINNOACTIVE = 7,
            SW_SHOWNA = 8,
            SW_RESTORE = 9,
            SW_SHOWDEFAULT = 10,
            SW_FORCEMINIMIZE = 11,
            SW_MAX = 11
        }
        internal enum WindowStyles : uint
        {
            WS_OVERLAPPED = 0x00000000,
            WS_POPUP = 0x80000000,
            WS_CHILD = 0x40000000,
            WS_MINIMIZE = 0x20000000,
            WS_VISIBLE = 0x10000000,
            WS_DISABLED = 0x08000000,
            WS_CLIPSIBLINGS = 0x04000000,
            WS_CLIPCHILDREN = 0x02000000,
            WS_MAXIMIZE = 0x01000000,
            WS_CAPTION = 0x00C00000,      // 	creates a window with a title bar
            WS_BORDER = 0x00800000,       // 	creates a window with a thin-line border
            WS_DLGFRAME = 0x00400000,
            WS_VSCROLL = 0x00200000,      // creates a window with a vertical scroll bar.
            WS_HSCROLL = 0x00100000,
            WS_SYSMENU = 0x00080000,
            WS_THICKFRAME = 0x00040000,   // creates a window with a sizing border
            WS_GROUP = 0x00020000,
            WS_TABSTOP = 0x00010000,
            WS_MINIMIZEBOX = 0x00020000,
            WS_MAXIMIZEBOX = 0x00010000,
            WS_TILED = 0x00000000,
            WS_ICONIC = 0x20000000,
            WS_SIZEBOX = 0x00040000,
            WS_POPUPWINDOW = 0x80880000,
            WS_OVERLAPPEDWINDOW = 0x00CF0000,
            WS_TILEDWINDOW = 0x00CF0000,
            WS_CHILDWINDOW = 0x40000000
        }

        [Flags]
        internal enum WindowExStyles
        {
            WS_EX_DLGMODALFRAME = 0x00000001,
            WS_EX_NOPARENTNOTIFY = 0x00000004,
            WS_EX_TOPMOST = 0x00000008,
            WS_EX_ACCEPTFILES = 0x00000010,
            WS_EX_TRANSPARENT = 0x00000020,
            WS_EX_MDICHILD = 0x00000040,
            WS_EX_TOOLWINDOW = 0x00000080,
            WS_EX_WINDOWEDGE = 0x00000100,
            WS_EX_CLIENTEDGE = 0x00000200,
            WS_EX_CONTEXTHELP = 0x00000400,
            WS_EX_RIGHT = 0x00001000,
            WS_EX_LEFT = 0x00000000,
            WS_EX_RTLREADING = 0x00002000,
            WS_EX_LTRREADING = 0x00000000,
            WS_EX_LEFTSCROLLBAR = 0x00004000,
            WS_EX_RIGHTSCROLLBAR = 0x00000000,
            WS_EX_CONTROLPARENT = 0x00010000,
            WS_EX_STATICEDGE = 0x00020000,
            WS_EX_APPWINDOW = 0x00040000,
            WS_EX_OVERLAPPEDWINDOW = 0x00000300,
            WS_EX_PALETTEWINDOW = 0x00000188,
            WS_EX_LAYERED = 0x00080000,
            WS_EX_NOACTIVATE = 0x08000000
        }

        #endregion

        private Timer? _timer;
        private Process? _process;
        private readonly System.Windows.Forms.Panel _panel;
        private readonly HashSet<IntPtr> _exeHandles = new();
        public readonly string ExeFullName;
        public string ExeArguments { get; init; }
        private readonly Dictionary<string, string> _environmentVariables;
        private readonly Runner _runner;
        private readonly string _sessionId = "";
 
        public static IntegrateHost Create(ProtocolBase protocol, Runner runner, string exeFullName, string exeArguments, Dictionary<string, string>? environmentVariables = null)
        {
            IntegrateHost? view = null;
            Execute.OnUIThreadSync(() =>
            {
                view = new IntegrateHost(protocol, runner, exeFullName, exeArguments, environmentVariables);
            });
            return view!;
        }

        private IntegrateHost(ProtocolBase protocol, Runner runner, string exeFullName, string exeArguments, Dictionary<string, string>? environmentVariables = null) : base(protocol, false)
        {
            ExeFullName = exeFullName;
            ExeArguments = exeArguments;
            _sessionId = protocol.SessionId;
            _runner = runner;
            _environmentVariables = environmentVariables ?? new Dictionary<string, string>();
            InitializeComponent();

            _panel = new ClickFocusPanel
            {
                BackColor = System.Drawing.Color.Transparent,
                Dock = System.Windows.Forms.DockStyle.Fill,
                BorderStyle = BorderStyle.None,
            };
            _panel.SizeChanged += PanelOnSizeChanged;

            FormsHost.Child = _panel;
            // FreeRDP is another program: a click in its window does not give it the keyboard (it stayed with Lemora).
            // Click in the session or focus on the tab -> keyboard to the FreeRDP window
            ((ClickFocusPanel)_panel).ChildClicked = () =>
            {
                if (!IsFreeRdp) return;
                FocusOnMe();
                // the click also activates the Lemora window, which takes the foreground back right after: repeat once the click is done
                Dispatcher.BeginInvoke(new Action(FocusOnMe), System.Windows.Threading.DispatcherPriority.Background);
                SimpleLogHelper.Debug($"FOCUS-DIAG({ProtocolServer.DisplayName}): click in the session, focus after={Lemora.FocusDiag.Describe()}");
            };
            FormsHost.GotKeyboardFocus += (_, _) => { if (IsFreeRdp) FocusOnMe(); };


            // FreeRDP: same bar as the Microsoft component (full screen if the server allows it, Ctrl+Alt+Del)
            if (runner is FreeRdpRunner && protocol is RDP freeRdp)
            {
                CanFullScreen = freeRdp.RdpFullScreenFlag != ERdpFullScreenFlag.Disable;
                GlobalEventHelper.OnScreenResolutionChanged += OnScreensChangedFreeRdp;
                MenuItems.Add(new System.Windows.Controls.Separator());
                MenuItems.Add(new System.Windows.Controls.MenuItem
                {
                    Header = "Ctrl + Alt + Del",
                    Command = new Shawn.Utils.Wpf.RelayCommand(_ => SendCtrlAltDelToFreeRdp(), _ => Status == ProtocolHostStatus.Connected),
                });
            }

            if (runner is PuttyRunner)
            {
                RunAfterConnected += () =>
                {
                    try
                    {
                        PuttyConfig.CleanUpOldConfig();
                        var path = PuttyRunner.GetAutoCommandFilePath(ProtocolServer);
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                };
            }
        }

        #region Resize

        private void SetToPanelSize()
        {
            if (_process != null)
            {
                CleanupClosedHandle();
                foreach (var exeHandle in _exeHandles)
                {
                    // PuTTY/KiTTY: frame outside the tab (see PuttyTarget)
                    if (IsPuttyFamily && PuttyWindowClasses.Contains(ClassOf(exeHandle))) { PuttyFit(exeHandle); continue; }
                    PlaceFreeRdpWindow(exeHandle, (int)(_panel.Width), (int)(_panel.Height), true);
                }
            }
        }

        // not work with UHD + scaling, e.g. 4k+150%, the ActualWidth will return 1500 / 150% = 1000pix while the real width is 1500pix.
        //protected override void OnRender(DrawingContext drawingContext)
        //{
        //    if (_process != null)
        //    {
        //        CleanupClosedHandle();
        //        SimpleLogHelper.Debug($"ActualWidth = {(int)(FormsHost.ActualWidth)}, ActualHeight = {(int)(FormsHost.ActualHeight)}");
        //        SimpleLogHelper.Debug($"GridActualWidth = {(int)(Grid.ActualWidth)}, GridActualHeight = {(int)(Grid.ActualHeight)}");
        //        foreach (var exeHandle in _exeHandles)
        //        {
        //            MoveWindow(exeHandle, 0, 0, (int)(Grid.ActualWidth), (int)(Grid.ActualHeight), true);
        //        }
        //        //MoveWindow(_exeHandle, 0, 0, (int)(FormsHost.ActualWidth), (int)(FormsHost.ActualHeight), true);
        //    }
        //    base.OnRender(drawingContext);
        //}

        private void PanelOnSizeChanged(object? sender, EventArgs e)
        {
            SetToPanelSize();
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            this.InvalidateVisual();
            base.OnRenderSizeChanged(sizeInfo);
        }
        #endregion

        /// <summary>
        /// remove the handles in _exeHandles which is not  window
        /// </summary>
        private void CleanupClosedHandle()
        {
            foreach (var handle in _exeHandles.ToArray())
            {
                if (IsWindow(handle) == false)
                {
                    SimpleLogHelper.Debug($"_exeHandles remove {handle}");
                    _exeHandles.Remove(handle);
                }
            }
        }

        /// <summary>
        /// remove title border frame scroll of the process
        /// </summary>
        private void SetExeWindowStyle()
        {
            CleanupClosedHandle();
            Dispatcher.Invoke(() =>
            {
                foreach (var exeHandle in _exeHandles)
                {
                    // must be set or exe will be shown out of panel
                    SetParent(exeHandle, _panel.Handle);
                    ShowWindow(exeHandle, (int)ShowWindowStyles.SW_SHOWMAXIMIZED);
                    int lStyle = GetWindowLong(exeHandle, (int)GetWindowLongIndex.GWL_STYLE);
                    lStyle &= ~(int)WindowStyles.WS_CAPTION; // no title
                    lStyle &= ~(int)WindowStyles.WS_BORDER; // no border
                    lStyle &= ~(int)WindowStyles.WS_THICKFRAME;
                    //lStyle &= ~(int)WindowStyles.WS_VSCROLL;
                    //lStyle |= (int)WindowExStyles.WS_EX_TOOLWINDOW;
                    SetWindowLong(exeHandle, (int)GetWindowLongIndex.GWL_STYLE, lStyle);
                }
                SetToPanelSize();
            });
        }

        public override void Conn()
        {
            Status = ProtocolHostStatus.Connecting;
            Debug.Assert(ParentWindow != null);

            var tsk = new Task(Start);
            // Observe the task: a failed Process.Start() used to leave the tab spinning on Connecting.
            tsk.ContinueWith(t =>
            {
                SimpleLogHelper.Fatal(t.Exception!);
                Execute.OnUIThread(() =>
                {
                    ActivityReason = "program start failed: " + t.Exception!.GetBaseException().Message;
                    Status = ProtocolHostStatus.Disconnected;
                    MessageBoxHelper.ErrorAlert($"Connect failed: {t.Exception!.GetBaseException().Message}");
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
            tsk.Start();
        }

        /// <summary>FreeRDP: the "Display settings" and "Advanced settings" buttons here too (before only with the Microsoft component).
        /// FreeRDP reads the settings only at startup: the panel applies them by reconnecting (see AppliesDisplayLive)</summary>
        public override bool CanChangeDisplayModeLive => IsFreeRdp && ProtocolServer is RDP;

        /// <summary>tests: how many times FreeRDP started in this tab (each reconnection +1)</summary>
        internal int TestFreeRdpStarts;
        /// <summary>tests (demo freerdp-impostazioni): the parameters of the last FreeRDP launch, without password</summary>
        internal string TestFreeRdpArgs => _environmentVariables.TryGetValue(FreeRdpRunner.ArgsEnvName, out var v)
            ? string.Join(" ", v.Split('\n').Where(x => !x.StartsWith("/p:"))) : "";

        public override void ReConn()
        {
            // tab already closed (e.g. the server refused and the session ended, but the settings panel was still open): restarting
            // here launched FreeRDP without being able to embed it, with its window on the desktop outside Lemora and an error (sandbox test)
            if (_disposed) { SimpleLogHelper.Warning($"{nameof(IntegrateHost)}({ProtocolServer.DisplayName}): reconnection requested on a tab already closed, ignored"); return; }
            CloseIntegrate();
            Conn();
        }

        public override void Close()
        {
            Dispose();
            base.Close();
        }

        public void ShowWindow(bool isShow)
        {
            foreach (var exeHandle in _exeHandles)
            {
                ShowWindow(exeHandle, (int)(isShow ? ShowWindowStyles.SW_SHOWMAXIMIZED : ShowWindowStyles.SW_HIDE));
            }
        }


        public void Dispose()
        {
            // Optimize dispose pattern to avoid UI thread blocking
            if (_disposed) return;
            _disposed = true;

            UnhookFreeRdpWindows();
            GlobalEventHelper.OnScreenResolutionChanged -= OnScreensChangedFreeRdp;
            Execute.OnUIThread(() => { _fullScreenBar?.Close(); _fullScreenBar = null; });
            // Stop and dispose timer first to prevent further operations
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;

            // Handle process cleanup
            if (_process != null)
            {
                try
                {
                    _process.Exited -= ProcessOnExited;
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    SimpleLogHelper.Debug($"Error disposing process: {ex.Message}");
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                }
            }
            FreeRdpLogDone();   // close from the tab: ProcessOnExited does not arrive (unhooked above), the FreeRDP log must be removed anyway

            // UI cleanup should be done on UI thread if needed
            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                Execute.OnUIThread(() =>
                {
                    CloseIntegrate();
                    _panel?.Dispose();
                    FormsHost?.Dispose();
                });
            }
            else
            {
                CloseIntegrate();
                _panel?.Dispose();
                FormsHost?.Dispose();
            }
            
            GC.SuppressFinalize(this);
        }

        private volatile bool _disposed = false;

        private void CloseIntegrate()
        {
            UnhookFreeRdpWindows();
            Execute.OnUIThread(() =>
            {
                _timer?.Stop();
                _timer?.Dispose();
                _timer = null;
                if (_process != null)
                {
                    try
                    {
                        _process.Exited -= ProcessOnExited;
                        _process.Kill();
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }

                Status = ProtocolHostStatus.Disconnected;
            });
        }

        public void Start()
        {
            RunBeforeConnect?.Invoke();
            var exeFullName = ExeFullName;

            if (ProtocolServer is IPuttyConnectable kittyConnectable && _runner is PuttyRunner putty)
            {
                if (PuttyRunner.GetAutoCommandFilePath(ProtocolServer) != "")
                    putty.SaveAutoCommandFile(ProtocolServer, 30); // save auto command file
                var sshPrivateKeyPath = putty.GetPrivateKeyPath(ProtocolServer); // prepare ssh key path
                putty.ConfigPutty(kittyConnectable, _sessionId, sshPrivateKeyPath);
            }

            if (Path.IsPathRooted(exeFullName)
                && File.Exists(ExeFullName) == false)
            {
                // Reset the status or the tab keeps spinning on Connecting forever.
                ActivityReason = $"program not found: {ExeFullName}";
                Status = ProtocolHostStatus.Disconnected;
                MessageBoxHelper.ErrorAlert($"Exe file '{ExeFullName}' does not existed! We can not start the connection!");
                return;
            }

            if (_runner is FreeRdpRunner freeRdpRunner && ProtocolServer is RDP freeRdp)
            {
                // real size of the session area in pixels: wait for the tab to be laid out (just opened it measures 0, seen in practice:
                // FreeRDP started at 1024x768 and then adapted, with black flashes). Up to 3 s, then start anyway
                var size = System.Drawing.Size.Empty;
                for (int i = 0; i < 30 && (size.Width < 200 || size.Height < 150); i++)
                {
                    Execute.OnUIThreadSync(() =>
                    {
                        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                        size = new System.Drawing.Size((int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round((ActualHeight - 1) * dpi.DpiScaleY));
                    });
                    if (size.Width < 200 || size.Height < 150) Thread.Sleep(100);
                }
                SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): starts with size {size.Width}x{size.Height}");
                // "Full screen" can be changed from the panel while the session is open (reconnection): the button follows the choice
                CanFullScreen = freeRdp.RdpFullScreenFlag != ERdpFullScreenFlag.Disable;
                _freeRdpAttachSize = size;
                // parameters recomputed at every launch: after "Cambia credenziali" (change credentials) the new user and password apply
                // PC identity: certificate read and compared with the remembered one before launching FreeRDP (security hardening)
                var certArgs = Lemora.Service.RdpCertPin.CheckForFreeRdp(freeRdp.Address, freeRdp.GetPort(), freeRdp.DisplayName, out var certError,
                    viaGateway: freeRdp.GatewayMode == EGatewayMode.UseTheseGatewayServerSettings && !string.IsNullOrWhiteSpace(freeRdp.GatewayHostName),
                    entryId: PinEntryId());
                if (certArgs == null)
                {
                    ActivityReason = certError;
                    Status = ProtocolHostStatus.Disconnected;
                    Lemora.Utils.ActivityLog.Notify(ProtocolServer, "annullata", certError);
                    Execute.OnUIThread(() => IoC.Get<SessionControlService>().CloseProtocolHostAsync(ConnectionId));
                    return;
                }
                // reduced protection already chosen "always" for this server, and the certificate is still the same one (checked just above)
                if (_freeRdpTlsLevel == null && LowTlsChoices.Get(ProtocolServer.Id, FreeRdpPinnedFingerprint()) is { } saved)
                {
                    _freeRdpTlsLevel = saved; _freeRdpTlsAgreed = true; _freeRdpTlsFromSaved = true;
                }
                var multimon = false;
                Execute.OnUIThreadSync(() => multimon = WantsFreeRdpMultimon());
                _freeRdpMultimon = multimon;
                if (multimon) Execute.OnUIThread(() => { if (ParentWindow is FullScreenWindowView f && f.IsLoaded && !f.IsClosed) FreeRdpGoFullScreen(f, true); });
                lock (_freeRdpWindowRects) _freeRdpWindowRects.Clear();
                if (_freeRdpMultimon) SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): full screen on several monitors, starts with /multimon:force");
                _environmentVariables[FreeRdpRunner.ArgsEnvName] = freeRdpRunner.BuildEnvValue(freeRdp, size, certArgs, _freeRdpMultimon) + (_freeRdpTlsLevel is { } tlsLevel ? $"\n/tls:seclevel:{tlsLevel}" : "");
                // image of the session in shared memory (patch freerdp-zzz-schermo-condiviso): Claude sees the screen even with the tab in the background
                // or Lemora minimized. An older FreeRDP ignores it (capture from the window as before)
                _environmentVariables["LEMORA_FRAME_SHARE"] = "1";
                // FreeRDP log (warnings and errors, no passwords) in the Lemora log folder: if the connection fails its errors
                // are copied into the Lemora log (the exit code alone is not enough: 131 is "unclassified error")
                _environmentVariables["WLOG_LEVEL"] = "WARN";
                // connection phases with timestamps (chosen security, licensing, activation): they tell WHERE a connection that times out stops.
                // Seen on a server where with the domain user "Timeout waiting for activation", with the local one you get in. These modules do not write passwords.
                _environmentVariables["WLOG_FILTER"] = "com.freerdp.core.connection:DEBUG,com.freerdp.core.nego:DEBUG,com.freerdp.core.license:DEBUG,com.freerdp.client.SDL:INFO" +
                    // clipboard trace, off by default: only format names and numbers and when they pass, never the content (checked in sdl_clip.cpp).
                    // To investigate the HTML pasted at random on some PCs: enable it with the environment variable LEMORA_FREERDP_CLIP_TRACE=1
                    (Lemora.Utils.DevOnly.Env("LEMORA_FREERDP_CLIP_TRACE") == "1" ? ",com.freerdp.client.sdl.cliprdr:TRACE" : "");
                _environmentVariables["WLOG_APPENDER"] = "FILE";
                _environmentVariables["WLOG_FILEAPPENDER_OUTPUT_FILE_PATH"] = Path.GetDirectoryName(AppPathHelper.Instance.LogFilePath)!;
                // one file per connection, deleted at session end (FreeRdpLogDone): FreeRDP writes addresses and users there without going through the
                // Lemora log filter (security hardening). Useful errors are copied, filtered, into the Lemora log
                FreeRdpLogDone();
                _freeRdpLogName = $"freerdp-{Guid.NewGuid():N}.log";
                _environmentVariables["WLOG_FILEAPPENDER_OUTPUT_FILE_NAME"] = _freeRdpLogName;
                CleanOldFreeRdpLogs();
                ShowFreeRdpState(loading: true, message: null);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exeFullName,
                WorkingDirectory = new FileInfo(exeFullName).DirectoryName,
                Arguments = ExeArguments,
                // PuTTY starts hidden (it honors the request, FreeRDP does not): PuttyTick shows it only after putting it in the tab, no flash on the desktop
                WindowStyle = IsPuttyFamily ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                CreateNoWindow = _runner is FreeRdpRunner,   // FreeRDP: no console window
            };

            // Set environment variables
            if (this._environmentVariables?.Count > 0)
            {
                startInfo.UseShellExecute = false;
                foreach (var kv in this._environmentVariables)
                    startInfo.EnvironmentVariables[kv.Key] = RunnerHelper.NoNul(kv.Value);
            }

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            _process.Exited += ProcessOnExited;
            if (_runner is FreeRdpRunner) { _freeRdpWindowSeen = false; _freeRdpShown = false; _exeHandles.Clear(); }
            _process.Start();
            _freeRdpClock.Restart();
            if (_runner is FreeRdpRunner) TestFreeRdpStarts++;
            if (_runner is FreeRdpRunner) FreeRdpTime($"FreeRDP started{(_freeRdpTlsLevel is { } l ? $" (TLS level {l})" : "")}");
            if (_runner is FreeRdpRunner || IsPuttyFamily)
            {
                ChildProcessJob.Add(_process);   // FreeRDP and PuTTY close with Lemora, even if Lemora is killed abruptly
                var pid = _process.Id;
                Execute.OnUIThreadSync(() => HookFreeRdpWindows(pid));   // window attached at creation: never visible outside the tab
            }

            SimpleLogHelper.Debug($"{nameof(IntegrateHost)}: Start process {exeFullName}");

            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(10 * 1000);
                RunAfterConnected?.Invoke();
            });

            // keep detect MainWindowHandle in next 10 seconds.
            var endTime = DateTime.Now.AddSeconds(10);
            _timer?.Dispose();
            _timer = new Timer { Interval = 100, AutoReset = false };
            _timer.Elapsed += (sender, args) =>
            {
                if (_process == null)
                {
                    return;
                }

                try
                {
                    _process.Refresh();
                    if (_runner is FreeRdpRunner)
                    {
                        if (_freeRdpShown) EnsureFreeRdpWindowShown();
                        // several monitors: the windows still outside go in only through AttachMultimonWindow (it knows their monitor)
                        if (_freeRdpMultimon)
                        {
                            if (_process.MainWindowHandle != IntPtr.Zero) AttachMultimonWindow(_process.MainWindowHandle, _panelHandle);
                        }
                        // the FreeRDP window is attached by the Windows notification (HookFreeRdpWindows); if the notification is missed, it is taken here like the others
                        else if (!_freeRdpWindowSeen && _exeHandles.Count == 0 && _process.MainWindowHandle != IntPtr.Zero && _exeHandles.Add(_process.MainWindowHandle))
                        {
                            SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): attached by the periodic check {WinInfo(_process.MainWindowHandle)}");
                            SetExeWindowStyle();
                        }
                        // FreeRDP main window still outside the tab (a second window not seen at creation): attach it again
                        else if (_freeRdpWindowSeen && _process.MainWindowHandle != IntPtr.Zero && GetAncestor(_process.MainWindowHandle, 2) == _process.MainWindowHandle)
                        {
                            var h = _process.MainWindowHandle;
                            SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): window outside the tab, attached again {WinInfo(h)}");
                            _exeHandles.Add(h);
                            SetExeWindowStyle();
                        }
                        // "connected" only when the session window (SDL_app) exists: FreeRDP creates it once connected. Before, any process window
                        // was enough and, on slow servers (e.g. 5 s), the loading screen was removed and black remained
                        if (!_freeRdpWindowSeen && HasSessionWindow())
                        {
                            _freeRdpWindowSeen = true;
                            FreeRdpTime("session window (connected to the server)");
                            SetToPanelSize();
                            // "connected" only if FreeRDP is still running after a moment (otherwise the error comes from ProcessOnExited)
                            var p = _process;
                            // 400 ms: with 1500 the loading screen stayed 1.5 s longer than with Microsoft (log: SDL window at +0.3 s from start);
                            // an error arriving later is shown by ProcessOnExited anyway
                            Task.Delay(400).ContinueWith(_ =>
                            {
                                try { if (p == null || p.HasExited || !ReferenceEquals(p, _process)) return; } catch { return; }
                                _freeRdpEverConnected = true;
                                if (_freeRdpTlsLevel != null) OnConnectedWithLowTls();
                                Execute.OnUIThread(() => { if (Status != ProtocolHostStatus.Connected) Status = ProtocolHostStatus.Connected; });
                                // NB: waiting for FreeRDP to make the window visible itself does NOT work (tried: it stays hidden, the whole
                                // 20 s limit was waited). It is shown as soon as the session window exists
                                _freeRdpShown = true;
                                ShowFreeRdpState(loading: false, message: null);
                                EnsureFreeRdpWindowShown();
                                FreeRdpTime("session shown in the tab");
                                FocusIfActiveSession();   // the keyboard goes to the remote computer without having to click
                                WatchFirstImage(p);
                                // shortcut: FreeRDP sends the new resolution to the server only when the display channel is ready (after sign-in);
                                // a resize done while connecting was lost. The size is sent again, but ONLY if the tab changed
                                // since it was attached: each repeat changes the remote resolution twice = a black flash.
                                // Better would be knowing from FreeRDP when the channel is ready (possible only with FreeRDP as a library)
                                foreach (var s in new[] { 4, 12 })
                                    Task.Delay(s * 1000).ContinueWith(_ => { if (ReferenceEquals(p, _process)) Execute.OnUIThread(NudgeFreeRdpSizeIfChanged); });
                            });
                        }
                    }
                    else if (IsPuttyFamily)
                    {
                        PuttyTick();
                    }
                    else if (_process.MainWindowHandle != IntPtr.Zero
                        && _exeHandles.Add(_process.MainWindowHandle) != false)
                    {
                        SimpleLogHelper.Debug($"new _process.MainWindowHandle = {_process.MainWindowHandle}");
                        SetExeWindowStyle();
                        // the program window appeared: the session is connected (before, it stayed "in progress" and the remembered credentials were never saved)
                        Execute.OnUIThread(() => { if (Status != ProtocolHostStatus.Connected) Status = ProtocolHostStatus.Connected; });
                    }
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Warning($"Error in timer callback: {e.Message}");
                }
                // FreeRDP and PuTTY: the check goes on for the whole session (a window left outside is attached again, restored borders are removed), other programs 10 s
                if (DateTime.Now > endTime && _exeHandles.Count > 0 && _runner is not FreeRdpRunner && !IsPuttyFamily)
                    return;
                if (_timer != null && _runner is FreeRdpRunner && _freeRdpWindowSeen) _timer.Interval = 1000;   // once the session is running one check per second is enough
                if (_timer != null && IsPuttyFamily && Status == ProtocolHostStatus.Connected) _timer.Interval = 500;   // PuTTY: borders put back are removed within half a second
                _timer?.Start();
            };
            _timer.Start();
        }

        private bool _freeRdpEverConnected;
        /// <summary>old server whose TLS OpenSSL rejects (code 143): reduced TLS level for this session (1, then 0); null = normal</summary>
        private int? _freeRdpTlsLevel;
        /// <summary>the user agreed to the reduced protection for this tab (asked once, also covers the step from level 1 to 0)</summary>
        private bool _freeRdpTlsAgreed;
        /// <summary>"always for this server": the level that works is saved once connected</summary>
        private bool _freeRdpTlsRemember;
        /// <summary>the level comes from a saved choice (the notice offers "Don't remember anymore")</summary>
        private bool _freeRdpTlsFromSaved;        /// <summary>the FreeRDP window is shown in the tab (until then the Lemora loading screen stays)</summary>
        private bool _freeRdpShown;
        /// <summary>
        /// Reduced TLS for old servers. Before (security review): on code 143 Lemora silently retried with /tls:seclevel:0 and remembered it forever,
        /// so a man in the middle making the first attempt fail could lower the protection for good. Now the user is asked; "always" is saved in the
        /// encrypted database bound to the PC certificate fingerprint (key "freerdp.tlslow.&lt;id&gt;" = "&lt;level&gt;|&lt;fingerprint&gt;"): with a
        /// different certificate the question comes back. The old key "freerdp.tls0.&lt;id&gt;" (no fingerprint) is no longer trusted and is deleted.
        /// </summary>
        internal static class LowTlsChoices
        {
            private const string Prefix = "freerdp.tlslow.";

            public static int? Get(string id, string? fingerprint)
            {
                try
                {
                    if (Lemora.Service.Locality.AppDbStore.TryGet("freerdp.tls0." + id, out var old) && !string.IsNullOrEmpty(old))
                        Lemora.Service.Locality.AppDbStore.Set("freerdp.tls0." + id, null);
                    return Lemora.Service.Locality.AppDbStore.TryGet(Prefix + id, out var v) ? Parse(v, fingerprint) : null;
                }
                catch (Exception e) { SimpleLogHelper.Warning(e.Message); return null; }
            }

            /// <summary>saved "&lt;level&gt;|&lt;fingerprint&gt;": the level only if the certificate is the same one (and the level is 0 or 1)</summary>
            internal static int? Parse(string? stored, string? fingerprint)
            {
                if (string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(stored)) return null;
                var p = stored.Split('|');
                return p.Length == 2 && int.TryParse(p[0], out var level) && level is 0 or 1 && string.Equals(p[1], fingerprint, StringComparison.OrdinalIgnoreCase) ? level : null;
            }

            public static void Save(string id, int level, string fingerprint)
            {
                try { Lemora.Service.Locality.AppDbStore.Set(Prefix + id, $"{level}|{fingerprint}"); } catch (Exception e) { SimpleLogHelper.Warning(e.Message); }
            }

            public static void Forget(string id)
            {
                try { Lemora.Service.Locality.AppDbStore.Set(Prefix + id, null); } catch (Exception e) { SimpleLogHelper.Warning(e.Message); }
            }
        }

        /// <summary>fingerprint of the PC certificate checked by RdpCertPin (null with a gateway or when it could not be read)</summary>
        private string? FreeRdpPinnedFingerprint() => ProtocolServer is RDP r
            ? Lemora.Service.RdpCertPin.Get(r.Address, r.GetPort(), PinEntryId()) ?? Lemora.Service.RdpCertPin.Get(r.Address, r.GetPort())
            : null;

        /// <summary>saved entry: its certificate is remembered on its own (not per address); quick connection: null, per address</summary>
        private string? PinEntryId() => ProtocolServer.IsTmpSession() || string.IsNullOrEmpty(ProtocolServer.Id) ? null : ProtocolServer.Id;

        /// <summary>
        /// code 143 at the normal level: ask before lowering the protection. false = the user refused (the error is shown as usual)
        /// </summary>
        private bool AskLowTls()
        {
            var fp = FreeRdpPinnedFingerprint();
            var text = string.Format(IoC.Translate("rdp_lowtls_text"), ProtocolServer.DisplayName);
            var yes = IoC.Translate(fp != null ? "rdp_lowtls_always" : "rdp_lowtls_yes");
            System.Windows.MessageBoxResult r;
            // called from the process-exit callback: an error here must not crash Lemora; it counts as "Cancel" (the usual error is shown)
            try { r = Lemora.Utils.MessageBoxHelper.Ask3(text, yes, IoC.Translate("rdp_lowtls_once"), IoC.Translate("Cancel"), IoC.Translate("rdp_lowtls_title")); }
            catch (Exception e) { SimpleLogHelper.Error(e); return false; }
            if (r == System.Windows.MessageBoxResult.Cancel) return false;
            _freeRdpTlsAgreed = true;
            _freeRdpTlsRemember = r == System.Windows.MessageBoxResult.Yes && fp != null;
            return true;
        }

        /// <summary>connected with reduced protection: saved if asked ("always"), noted in the activity log and shown with a notice</summary>
        private void OnConnectedWithLowTls()
        {
            if (_freeRdpTlsLevel is not { } level) return;
            var fp = FreeRdpPinnedFingerprint();
            // "always": the level that works is saved (also when a saved level 1 was no longer enough and 0 was needed)
            if ((_freeRdpTlsRemember || _freeRdpTlsFromSaved) && fp != null) { LowTlsChoices.Save(ProtocolServer.Id, level, fp); _freeRdpTlsRemember = false; _freeRdpTlsFromSaved = true; }
            SimpleLogHelper.Warning($"{nameof(IntegrateHost)}({ProtocolServer.DisplayName}): connected with reduced protection (TLS level {level})");
            Lemora.Utils.ActivityLog.Notify(ProtocolServer, "protezione ridotta", $"TLS level {level}, server with old security");   // the kind is an identifier for the MCP server: keep it
            var id = ProtocolServer.Id;
            if (_freeRdpTlsFromSaved)
                Lemora.Service.ToastService.ShowAction(string.Format(IoC.Translate("rdp_lowtls_toast_title"), ProtocolServer.DisplayName), IoC.Translate("rdp_lowtls_toast_saved"),
                    IoC.Translate("rdp_lowtls_forget"), () => { LowTlsChoices.Forget(id); Lemora.Service.ToastService.Show(IoC.Translate("rdp_lowtls_forgotten")); },
                    IoC.Translate("OK"), null, 15, Lemora.Service.ToastKind.Warning);
            else
                Lemora.Service.ToastService.Show(string.Format(IoC.Translate("rdp_lowtls_toast"), ProtocolServer.DisplayName), Lemora.Service.ToastKind.Warning);
        }
        private bool _freeRdpWindowSeen;
        /// <summary>Windows service windows created inside the FreeRDP process (seen in the log): they are not the session</summary>
        private static readonly HashSet<string> WindowsServiceClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "IME", "MSCTFIME UI", "OleMainThreadWndClass", "CicMarshalWndClass", "SystemUserAdapterWindowClass",
        };

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        private static string ClassOf(IntPtr h) { var c = new StringBuilder(64); GetClassName(h, c, c.Capacity); return c.ToString(); }

        /// <summary>
        /// PuTTY in the tab (called by the periodic check): Windows no longer sees the terminal window as "main" once attached,
        /// so here we work only on the process windows of class PuTTY/KiTTY. They are attached if the creation notification missed them, title bar
        /// and borders are removed when PuTTY restores them (happens right after creation, seen in the sandbox), and the session is "connected" when
        /// no PuTTY dialog is open (new server key, password): dialogs stay normal windows, never in the tab
        /// </summary>
        private void PuttyTick()
        {
            if (_process == null) return;
            uint pid;
            try { pid = (uint)_process.Id; } catch { return; }
            bool dialogOpen = false;
            var found = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var p);
                if (p != pid) return true;
                // the terminal even if hidden (it starts hidden on purpose); for dialogs only visible ones count
                if (PuttyWindowClasses.Contains(ClassOf(h))) found.Add(h); else if (IsWindowVisible(h)) dialogOpen = true;
                return true;
            }, IntPtr.Zero);
            // terminal windows still top-level (creation notification missed): into the tab
            if (found.Count > 0)
            {
                lock (_exeHandles) foreach (var h in found) _exeHandles.Add(h);
                SimpleLogHelper.Warning($"PUTTY-WIN({ProtocolServer.DisplayName}): attached by the periodic check");
            }
            IntPtr[] handles;
            lock (_exeHandles) handles = _exeHandles.Where(h => IsWindow(h) && PuttyWindowClasses.Contains(ClassOf(h))).ToArray();
            if (handles.Length == 0) return;
            foreach (var h in handles)
            {
                if (GetAncestor(h, 2) != h && IsWindowVisible(h) && PuttyFitted(h)) continue;
                Execute.OnUIThreadSync(() =>
                {
                    if (GetAncestor(h, 2) == h) SetParent(h, _panel.Handle);
                    int style = GetWindowLong(h, (int)GetWindowLongIndex.GWL_STYLE);
                    int child = (style & ~unchecked((int)WindowStyles.WS_POPUP)) | (int)WindowStyles.WS_CHILD;
                    if (child != style) SetWindowLong(h, (int)GetWindowLongIndex.GWL_STYLE, child);
                    PuttyFit(h, show: true);
                });
            }
            if (!dialogOpen && Status != ProtocolHostStatus.Connected)
            {
                Execute.OnUIThread(() =>
                {
                    if (Status == ProtocolHostStatus.Connected) return;
                    Status = ProtocolHostStatus.Connected;
                    FocusIfActiveSession();   // the keyboard goes straight to the terminal
                });
            }
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);   // GetWindowRect, ClientToScreen and POINT live in IntegrateHost.Mcp.cs

        /// <summary>
        /// position that hides the PuTTY frame: title and borders stay outside the tab (clipped), only the terminal with its
        /// scroll bar is visible. Removing the style is not enough: PuTTY and KiTTY restore title bar and borders at every resize (seen in the sandbox)
        /// </summary>
        private System.Drawing.Rectangle PuttyTarget(IntPtr h)
        {
            GetWindowRect(h, out var wr);
            var p = new POINT();
            ClientToScreen(h, ref p);
            GetClientRect(h, out var cr);
            int left = p.X - wr.Left, top = p.Y - wr.Top;
            int right = wr.Right - (p.X + cr.Right), bottom = wr.Bottom - (p.Y + cr.Bottom);
            const int WS_VSCROLL = 0x00200000, SM_CXVSCROLL = 2;
            if ((GetWindowLong(h, (int)GetWindowLongIndex.GWL_STYLE) & WS_VSCROLL) != 0) right = Math.Max(0, right - GetSystemMetrics(SM_CXVSCROLL));   // the scroll bar stays inside
            return new System.Drawing.Rectangle(-left, -top, _panel.Width + left + right, _panel.Height + top + bottom);
        }

        /// <summary>true if the window is already in the position that hides the frame (no moves at every check)</summary>
        private bool PuttyFitted(IntPtr h)
        {
            var t = PuttyTarget(h);
            GetWindowRect(h, out var wr);
            var origin = new POINT();
            ClientToScreen(_panel.Handle, ref origin);
            return wr.Left - origin.X == t.X && wr.Top - origin.Y == t.Y && wr.Right - wr.Left == t.Width && wr.Bottom - wr.Top == t.Height;
        }

        private void PuttyFit(IntPtr h, bool show = false)
        {
            if (_panel.Width < 10 || _panel.Height < 10) return;
            var t = PuttyTarget(h);
            const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
            SetWindowPos(h, IntPtr.Zero, t.X, t.Y, t.Width, t.Height, SWP_NOZORDER | SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0));
        }

        /// <summary>terminal window classes of PuTTY and KiTTY (dialogs are "#32770")</summary>
        private static readonly HashSet<string> PuttyWindowClasses = new(StringComparer.OrdinalIgnoreCase) { "PuTTY", "KiTTY" };

#pragma warning disable CS0612 // KittyRunner is obsolete but whoever still has it configured must get the same behavior
        /// <summary>PuTTY (or KiTTY): external program attached in the main window tab, like FreeRDP</summary>
        private bool IsPuttyFamily => _runner is Lemora.Model.ProtocolRunner.Default.PuttyRunner or Lemora.Model.ProtocolRunner.Default.KittyRunner;
#pragma warning restore CS0612

        private IntPtr _panelHandle;
        private WinEventDelegate? _winEventProc;   // kept in a field: the garbage collector must not free it while the notification is active
        private uint _hookThreadId;

        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// FreeRDP (SDL) ignores /parent-window, /window-position and the hidden window, and immediately shows its window at the top left with a
        /// taskbar button. Windows notifies as soon as it is created; the notification is received on a dedicated thread (the UI thread, busy during
        /// connection, read it late and the window was visible for half a second) and the window goes straight into the tab.
        /// </summary>
        private void HookFreeRdpWindows(int pid)
        {
            UnhookFreeRdpWindows();
            var panel = _panel.Handle;   // read here, on the UI thread
            _panelHandle = panel;
            const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, GA_ROOT = 2;
            _winEventProc = (_, _, hwnd, idObject, idChild, _, _) =>
            {
                if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;   // only the window, not its elements
                if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;                  // top-level windows only (not those already inside the tab)
                // the process's Windows service windows (keyboard, text, OLE) are left alone: put in the tab at full size
                // they flashed over the remote desktop. We do not keep ONLY "SDL_app": a window without a readable class name is needed
                // (without it, black screen)
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, cls.Capacity);
                if (WindowsServiceClasses.Contains(cls.ToString())) return;
                // PuTTY/KiTTY: only the terminal window. Its dialogs (new server key, errors, password) stay normal windows,
                // above Lemora: attached in the tab at full size they could be neither read nor closed
                if (IsPuttyFamily && !PuttyWindowClasses.Contains(cls.ToString())) return;
                // several monitors: FreeRDP measures each monitor with a small hidden window ("SdlWindow::query") placed on it. Moved into
                // the tab it measured 64x64 monitors and the connection failed (code 136): those windows are left alone.
                // A window whose class is not readable yet is looked at again a moment later (the probes are gone by then)
                // At creation the title is not set yet, so every window is looked at again a moment later, when the probes are gone and the
                // session windows have their title (they are created hidden: Lemora shows them, so nothing appears on the desktop meanwhile)
                if (_freeRdpMultimon && !IsPuttyFamily)
                {
                    Task.Delay(400).ContinueWith(_ => AttachMultimonWindow(hwnd, panel));
                    return;
                }
                AttachFreeRdpWindow(hwnd, panel);
            };
            var proc = _winEventProc;
            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                _hookThreadId = GetCurrentThreadId();
                var hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, proc, (uint)pid, 0, WINEVENT_OUTOFCONTEXT);
                ready.Set();
                if (hook == IntPtr.Zero) { SimpleLogHelper.Warning($"{nameof(IntegrateHost)}: window creation notice not enabled, FreeRDP is attached by the periodic check"); return; }
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }   // notifications arrive while waiting for messages; WM_QUIT ends it
                UnhookWinEvent(hook);
            }) { IsBackground = true, Name = "FreeRDP-aggancio" };
            thread.Start();
            ready.Wait(2000);
        }

        /// <summary>
        /// several monitors: a FreeRDP session window goes into the tab only by this way (creation notice or periodic check), so the
        /// monitor it shows is always known. Before, the periodic check took windows on its own and two ended up on the same monitor
        /// (one screen black)
        /// </summary>
        private void AttachMultimonWindow(IntPtr hwnd, IntPtr panel)
        {
            if (!IsWindow(hwnd) || !IsSessionWindow(hwnd) || GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
            lock (_exeHandles) if (_exeHandles.Contains(hwnd)) return;
            // FreeRDP creates each window on its own monitor (centered): the monitor under the window's center is the one it shows
            if (GetWindowRect(hwnd, out var wr))
            {
                var center = new System.Drawing.Point((wr.Left + wr.Right) / 2, (wr.Top + wr.Bottom) / 2);
                var mon = Lemora.Utils.PhysicalScreens.All().Select(m => m.Bounds).FirstOrDefault(b => b.Contains(center));
                if (!mon.IsEmpty)
                    lock (_freeRdpWindowRects)
                    {
                        _freeRdpWindowRects[hwnd] = mon;
                        SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): session window {hwnd} ({wr.Left},{wr.Top}) on monitor {mon}");
                    }
            }
            AttachFreeRdpWindow(hwnd, panel);
        }

        private void AttachFreeRdpWindow(IntPtr hwnd, IntPtr panel)
        {
            {
                lock (_exeHandles) { if (!_exeHandles.Add(hwnd)) return; }
                SetParent(hwnd, panel);
                int style = GetWindowLong(hwnd, (int)GetWindowLongIndex.GWL_STYLE);
                style &= ~((int)WindowStyles.WS_CAPTION | (int)WindowStyles.WS_BORDER | (int)WindowStyles.WS_THICKFRAME | unchecked((int)WindowStyles.WS_POPUP));
                style |= (int)WindowStyles.WS_CHILD;
                SetWindowLong(hwnd, (int)GetWindowLongIndex.GWL_STYLE, style);
                // the tab is hidden during loading: if its size is not valid yet the one given to FreeRDP is used
                GetClientRect(panel, out var rc);
                var w = rc.Right >= 200 ? rc.Right : _freeRdpAttachSize.Width;
                var h = rc.Bottom >= 150 ? rc.Bottom : _freeRdpAttachSize.Height;
                if (w > 0 && h > 0) PlaceFreeRdpWindow(hwnd, w, h, true);
                SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): attached at creation {WinInfo(hwnd)}");
            }
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private FreeRdpFullScreenBar? _fullScreenBar;

        /// <summary>
        /// FreeRDP: the Lemora full screen window starts small and the session container brings it to full screen
        /// (as the Microsoft component does); here also the exit bar, which FreeRDP lacks
        /// </summary>
        public override void SetParentWindow(Shawn.Utils.WpfResources.Theme.Styles.WindowBase? value)
        {
            base.SetParentWindow(value);
            if (_runner is not FreeRdpRunner) return;
            if (value is FullScreenWindowView fw && fw.IsLoaded && !fw.IsClosed)
                Execute.OnUIThread(() =>
                {
                    // several monitors: FreeRDP must know the layout from the start, so the session reconnects (as the Microsoft component does)
                    var reconnect = _process != null && !_disposed && !_freeRdpMultimon && WantsFreeRdpMultimon();
                    FreeRdpGoFullScreen(fw, reconnect);
                    if (reconnect)
                    {
                        // marked at once: SetParentWindow arrives twice and two reconnections started two FreeRDP ("Remote Desktop Services busy")
                        _freeRdpMultimon = true;
                        SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): full screen on several monitors, reconnecting");
                        ReConn();
                    }
                });
            else
                Execute.OnUIThread(() =>
                {
                    _fullScreenBar?.Disarm();
                    // back to a normal window: the remote desktop as wide as all monitors is replaced by one that fits the tab
                    if (_process != null && !_disposed && _freeRdpMultimon && !WantsFreeRdpMultimon())
                    {
                        _freeRdpMultimon = false;   // marked at once: only one reconnection even if SetParentWindow arrives twice
                        SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): end of full screen on several monitors, reconnecting");
                        // after the layout: started at once, FreeRDP still measured the full screen size (3840x1343 instead of the tab)
                        Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) ReConn(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                });
        }

        /// <summary>the session was started with the layout of all the monitors (one FreeRDP window per monitor)</summary>
        private bool _freeRdpMultimon;
        /// <summary>FreeRDP windows of a multi-monitor session: where each one was created on the screen (physical pixels)</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, System.Drawing.Rectangle> _freeRdpWindowRects = new();

        /// <summary>
        /// Full screen over several monitors, same rule as the Microsoft component: several monitors chosen or "all monitors", but not
        /// with different Windows scales (the monitors would come out enlarged and shifted: then the current monitor is used).
        /// FreeRDP cannot take a subset of the monitors, so a choice of several monitors uses all of them.
        /// </summary>
        private bool WantsFreeRdpMultimon()
        {
            if (ProtocolServer is not RDP rdp || rdp.RdpFullScreenFlag == ERdpFullScreenFlag.Disable) return false;
            if (ParentWindow != null && ParentWindow is not FullScreenWindowView) return false;
            if (System.Windows.Forms.Screen.AllScreens.Length < 2 || Lemora.Utils.PhysicalScreens.HasMixedScaling()) return false;
            var selected = Lemora.Utils.PhysicalScreens.ParseSelection(rdp.RdpFullScreenMonitors);
            return selected.Count > 1 || (selected.Count == 0 && rdp.RdpFullScreenFlag == ERdpFullScreenFlag.EnableFullAllScreens);
        }

        /// <summary>origin (top left) of the area made by all the monitors, in physical pixels: the windows of a multi-monitor session are placed from here</summary>
        private static System.Drawing.Point AllMonitorsOrigin()
        {
            var all = Lemora.Utils.PhysicalScreens.All();
            return all.Count == 0 ? System.Drawing.Point.Empty : new System.Drawing.Point(all.Min(m => m.Bounds.Left), all.Min(m => m.Bounds.Top));
        }

        /// <summary>puts a FreeRDP window in the tab: the whole tab, or (several monitors) the part that belongs to its monitor</summary>
        private void PlaceFreeRdpWindow(IntPtr h, int panelWidth, int panelHeight, bool repaint)
        {
            if (_freeRdpMultimon && MonitorOfWindow(h) is { } r)
            {
                var o = AllMonitorsOrigin();
                MoveWindow(h, r.Left - o.X, r.Top - o.Y, r.Width, r.Height, repaint);
            }
            else MoveWindow(h, 0, 0, panelWidth, panelHeight, repaint);
        }

        /// <summary>
        /// Which monitor a FreeRDP session window shows (physical rectangle). FreeRDP creates one window per monitor in the order of its
        /// monitor list: the primary monitor first, then the others. The window's own position cannot be used: FreeRDP places it later.
        /// </summary>
        private System.Drawing.Rectangle? MonitorOfWindow(IntPtr h)
        {
            lock (_freeRdpWindowRects)
            {
                if (_freeRdpWindowRects.TryGetValue(h, out var known)) return known;
                if (!IsSessionWindow(h)) return null;
                var all = Lemora.Utils.PhysicalScreens.All().Select(m => m.Bounds).ToList();
                var primary = all.FindIndex(b => b.Contains(0, 0));
                if (primary > 0) { var p = all[primary]; all.RemoveAt(primary); all.Insert(0, p); }
                if (all.Count == 0) return null;
                // unknown position: a monitor that has no window yet (never two windows on the same monitor)
                var free = all.Where(b => !_freeRdpWindowRects.Values.Contains(b)).ToList();
                var r = free.Count > 0 ? free[0] : all[0];
                _freeRdpWindowRects[h] = r;
                SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): session window {h} on monitor {r}");
                return r;
            }
        }

        /// <summary>
        /// monitors plugged or unplugged with the FreeRDP session in full screen, like the Microsoft component: with "follow monitors" the window
        /// moves onto the present monitors (FreeRDP adapts the remote desktop on its own, +dynamic-resolution), otherwise it goes back to windowed
        /// </summary>
        private void OnScreensChangedFreeRdp()
        {
            Execute.OnUIThread(() =>
            {
                if (_disposed || ParentWindow is not FullScreenWindowView fw || fw.IsClosed || ProtocolServer is not RDP rdp) return;
                SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): monitors changed in full screen, {(rdp.RdpFullScreenFollowMonitors == true ? "following them" : "back to window")}");
                if (rdp.RdpFullScreenFollowMonitors == true)
                {
                    // several monitors: the layout sent to the server changes, so the session reconnects
                    var multi = _freeRdpMultimon || WantsFreeRdpMultimon();
                    FreeRdpGoFullScreen(fw, multi);
                    if (multi) ReConn();
                }
                else OnFullScreen2Window?.Invoke(ConnectionId);
            });
        }

        /// <summary>tests: the full screen bar is visible</summary>
        internal bool TestFullScreenBarVisible => _fullScreenBar?.IsVisible == true;

        /// <summary>tests: as if the monitors had changed</summary>
        internal void TestScreensChanged() => OnScreensChangedFreeRdp();

        private void FreeRdpGoFullScreen(FullScreenWindowView fw, bool multimon = false)
        {
            if (ProtocolServer is not RDP rdp) return;
            // same monitors as the Microsoft component: the chosen ones, all, or the one where the session was
            var selected = Lemora.Utils.PhysicalScreens.ParseSelection(rdp.RdpFullScreenMonitors);
            System.Drawing.Rectangle rect;
            if (multimon || _freeRdpMultimon) rect = Shawn.Utils.Wpf.ScreenInfoEx.GetAllScreensSize();   // FreeRDP uses all the monitors
            else if (selected.Count > 0) rect = Lemora.Utils.PhysicalScreens.UnionOf(selected);
            else if (rdp.RdpFullScreenFlag == ERdpFullScreenFlag.EnableFullAllScreens) rect = Shawn.Utils.Wpf.ScreenInfoEx.GetAllScreensSize();
            else
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                var i = SourceScreenIndex >= 0 && SourceScreenIndex < screens.Length ? SourceScreenIndex : Shawn.Utils.Wpf.ScreenInfoEx.GetCurrentScreen(fw).Index;
                rect = screens[Math.Max(0, Math.Min(i, screens.Length - 1))].Bounds;
            }
            fw.WindowState = WindowState.Normal;
            fw.WindowStyle = WindowStyle.None;
            fw.ResizeMode = ResizeMode.NoResize;
            const uint SWP_NOZORDER = 0x0004;
            SetWindowPos(new System.Windows.Interop.WindowInteropHelper(fw).EnsureHandle(), IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOZORDER);
            SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): full screen on {rect}");
            Dispatcher.BeginInvoke(new Action(() => { if (GridLoading.Visibility == Visibility.Visible) CenterLoadingOnPrimaryScreen(); }), System.Windows.Threading.DispatcherPriority.Loaded);
            _fullScreenBar ??= new FreeRdpFullScreenBar(ProtocolServer.DisplayName,
                exitFullScreen: () => OnFullScreen2Window?.Invoke(ConnectionId),
                close: Close);
            // several monitors: the bar (and the edge that shows it) on the primary monitor, not in the middle of the whole area, cut between two screens
            var barRect = rect;
            var primary = Lemora.Utils.PhysicalScreens.All().Select(m => m.Bounds).FirstOrDefault(b => b.Contains(0, 0));
            if (!primary.IsEmpty && System.Drawing.Rectangle.Intersect(rect, primary) is { IsEmpty: false } onPrimary && onPrimary.Width < rect.Width) barRect = onPrimary;
            _fullScreenBar.Arm(fw, barRect, enabled: rdp.IsFullScreenWithConnectionBar != false, pinned: rdp.IsPinTheConnectionBarByDefault == true,
                exitByKeys: () => OnFullScreen2Window?.Invoke(ConnectionId));
            FocusOnMe();
        }

        /// <summary>
        /// The FreeRDP window (SDL_app) attached at creation stays hidden (FreeRDP shows it while it is still its own) and FreeRDP
        /// restores its title bar and borders: seen in practice (visible=False, style 0xC6CF0000 = black screen).
        /// It is shown and they are removed again, only when needed (no useless redraws).
        /// </summary>
        private void EnsureFreeRdpWindowShown()
        {
            IntPtr[] handles;
            lock (_exeHandles) handles = _exeHandles.ToArray();
            foreach (var h in handles)
            {
                if (!IsSessionWindow(h)) continue;   // not the SDL probe window ("SdlWindow::query")
                int style = GetWindowLong(h, (int)GetWindowLongIndex.GWL_STYLE);
                int clean = (style & ~((int)WindowStyles.WS_CAPTION | (int)WindowStyles.WS_THICKFRAME | unchecked((int)WindowStyles.WS_POPUP))) | (int)WindowStyles.WS_CHILD;
                var visible = IsWindowVisible(h);
                if (clean == style && visible) continue;
                SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): adjusted {WinInfo(h)} style=0x{style:X8}");
                if (clean != style) SetWindowLong(h, (int)GetWindowLongIndex.GWL_STYLE, clean);
                const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
                GetClientRect(_panelHandle, out var rc);
                var (px, py, pw, ph) = (0, 0, rc.Right, rc.Bottom);
                if (_freeRdpMultimon && MonitorOfWindow(h) is { } mr) { var o = AllMonitorsOrigin(); (px, py, pw, ph) = (mr.Left - o.X, mr.Top - o.Y, mr.Width, mr.Height); }
                SetWindowPos(h, IntPtr.Zero, px, py, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            }
        }

        /// <summary>tab size when the FreeRDP window was attached (the one FreeRDP connects with)</summary>
        private System.Drawing.Size _freeRdpAttachSize;

        private void NudgeFreeRdpSizeIfChanged()
        {
            if (_panel.Width == _freeRdpAttachSize.Width && _panel.Height == _freeRdpAttachSize.Height) return;
            SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): tab changed while connecting ({_freeRdpAttachSize.Width}x{_freeRdpAttachSize.Height} -> {_panel.Width}x{_panel.Height}), resolution sent again");
            NudgeFreeRdpSize();
        }

        /// <summary>same tab size, but going through one pixel less: FreeRDP sees a change and resends the resolution to the server</summary>
        private void NudgeFreeRdpSize()
        {
            if (_disposed || _panel.Width < 10 || _panel.Height < 10 || _freeRdpMultimon) return;   // several monitors: the size is the one of the monitors
            var handles = _exeHandles.ToArray();
            foreach (var h in handles) MoveWindow(h, 0, 0, _panel.Width - 1, _panel.Height, false);
            // a moment later the right size: FreeRDP groups close changes and with two immediate moves it would see nothing
            Task.Delay(400).ContinueWith(_ => Execute.OnUIThread(() => { if (!_disposed) SetToPanelSize(); }));
        }

        private void UnhookFreeRdpWindows()
        {
            var id = _hookThreadId;
            if (id == 0) return;
            _hookThreadId = 0;
            PostThreadMessage(id, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Full screen over several monitors: the spinner would sit in the middle of the whole area, cut between two screens.
        /// It is moved to the center of the part of the area that lies on the primary monitor.
        /// </summary>
        private void CenterLoadingOnPrimaryScreen()
        {
            try
            {
                var margin = new Thickness(0);
                var monitors = Lemora.Utils.PhysicalScreens.All();
                if (IsLoaded && ActualWidth > 0 && ActualHeight > 0 && monitors.Count > 1)
                {
                    // physical pixels: PointToScreen and the monitor list agree whatever the Windows scale of each monitor
                    var primary = monitors.Select(m => m.Bounds).FirstOrDefault(b => b.Contains(0, 0));
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                    var tl = PointToScreen(new Point(0, 0));
                    var area = new System.Drawing.Rectangle((int)tl.X, (int)tl.Y, (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY));
                    var target = System.Drawing.Rectangle.Intersect(area, primary);
                    if (!target.IsEmpty && target.Width < area.Width - 2)
                        margin = new Thickness((target.Left - area.Left) / dpi.DpiScaleX, (target.Top - area.Top) / dpi.DpiScaleY,
                            (area.Right - target.Right) / dpi.DpiScaleX, (area.Bottom - target.Bottom) / dpi.DpiScaleY);
                    SimpleLogHelper.Warning($"FREERDP-WIN({ProtocolServer.DisplayName}): spinner, area {area}, primary monitor {primary}, margin {margin}");
                }
                GridLoading.Margin = margin;
            }
            catch { GridLoading.Margin = new Thickness(0); }
        }

        /// <summary>FreeRDP: loading, message or session (the program window is hidden while loading or a message is shown)</summary>
        private void ShowFreeRdpState(bool loading, string? message, bool askCredentials = false)
        {
            Execute.OnUIThreadSync(() =>
            {
                GridLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
                if (loading)
                {
                    CenterLoadingOnPrimaryScreen();
                    // the window may still be taking its full screen size: measured again once the layout has settled
                    Dispatcher.BeginInvoke(new Action(() => { if (GridLoading.Visibility == Visibility.Visible) CenterLoadingOnPrimaryScreen(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                GridMessageBox.Visibility = message != null ? Visibility.Visible : Visibility.Collapsed;
                FormsHost.Visibility = loading || message != null ? Visibility.Collapsed : Visibility.Visible;
                if (message != null)
                {
                    TbMessage.Text = message;
                    BtnCreds.Visibility = askCredentials || !_freeRdpEverConnected ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        }

        /// <summary>FreeRDP exited with an error: message in the tab (it does not close); credentials rejected before connecting = Lemora prompt</summary>
        private void OnFreeRdpFailed(int exitCode)
        {
            ActivityReason = $"{FreeRdpRunner.DescribeExit(exitCode)} (FreeRDP, code {exitCode})";
            SimpleLogHelper.Warning($"{nameof(IntegrateHost)}({ProtocolServer.DisplayName}): {ActivityReason}");
            try
            {
                Execute.OnUIThreadSync(() =>
                {
                    _timer?.Stop();
                    _timer?.Dispose();
                    _timer = null;
                    _process = null;
                    _exeHandles.Clear();
                    Status = ProtocolHostStatus.Disconnected;
                });
            }
            catch (Exception ex) { SimpleLogHelper.Warning(ex); return; }   // program closing
            var credentials = FreeRdpRunner.IsCredentialFailure(exitCode) && !_freeRdpEverConnected;
            ShowFreeRdpState(loading: false, message: FreeRdpRunner.DescribeExit(exitCode), askCredentials: credentials);
            Execute.OnUIThread(() => ParentWindow?.FlashIfNotActive());
            if (credentials) Execute.OnUIThread(AskFreeRdpCredentials);
        }

        /// <summary>same credentials prompt as the Microsoft component: the password saved from the history is removed and we retry with the new ones</summary>
        private void AskFreeRdpCredentials()
        {
            if (ProtocolServer is not RDP rdp) return;
            try
            {
                QuickSessionFollowUp.ForgetSavedCredentials(ProtocolServer);
                var quick = ProtocolServer.IsTmpSession();
                var dlg = new Lemora.View.Editor.PasswordPopupDialogViewModel(false, quick && IoC.Get<Lemora.Service.ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
                {
                    Title = $"[RDP]{rdp.Address}",
                    UserName = rdp.UserName,
                    ErrorText = IoC.Translate("password_wrong"),
                    CanSaveToConnection = !quick,   // saved connection: the working credential is saved inside it
                };
                FolderCredentialHints.Apply(dlg, rdp.Address, ProtocolServer.TreeNodes, useDomain: true);
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Password))
                {
                    rdp.UserName = dlg.UserName;
                    rdp.Password = dlg.Password;
                    QuickSessionFollowUp.UpdateCredentials(ProtocolServer, dlg.UserName, dlg.Password, "", dlg.CanRememberInfo && dlg.IsRememberInfo);
                    QuickSessionFollowUp.RegisterRetry(ProtocolServer, dlg, dlg.Password);
                    SimpleLogHelper.Warning($"IntegrateHost({ProtocolServer.DisplayName}): new credentials, retrying as {dlg.UserName}");
                    ReConn();
                }
            }
            catch (Exception ex) { SimpleLogHelper.Error(ex); }
        }

        private void BtnReconn_OnClick(object sender, RoutedEventArgs e) => ReConn();
        private void BtnCreds_OnClick(object sender, RoutedEventArgs e) => AskFreeRdpCredentials();
        private void BtnCancel_OnClick(object sender, RoutedEventArgs e) => Close();

        private string? _freeRdpLogName;
        private static string FreeRdpLogDir => Path.GetDirectoryName(AppPathHelper.Instance.LogFilePath)!;

        /// <summary>
        /// this connection's FreeRDP log is no longer needed: remove it from disk. A just-closed FreeRDP can keep it open for a moment:
        /// retry for a few seconds (in the background); if it remains, the cleanup at the next connection removes it
        /// </summary>
        private void FreeRdpLogDone()
        {
            var name = _freeRdpLogName;
            _freeRdpLogName = null;
            if (name == null) return;
            var path = Path.Combine(FreeRdpLogDir, name);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                for (var i = 0; i < 20 && File.Exists(path); i++)
                {
                    try { File.Delete(path); } catch { await System.Threading.Tasks.Task.Delay(500); }
                }
            });
        }

        /// <summary>
        /// leftover FreeRDP logs (Lemora closed abruptly) and the old shared freerdp.log. Those of sessions still open are in use
        /// by FreeRDP and Windows does not let them be deleted: they stay.
        /// </summary>
        private static void CleanOldFreeRdpLogs()
        {
            try
            {
                foreach (var f in new DirectoryInfo(FreeRdpLogDir).GetFiles("freerdp*.log"))
                    try { f.Delete(); } catch { /* in use by another open session */ }
            }
            catch { /* log folder not readable: nothing to clean */ }
        }

        /// <summary>errors written by FreeRDP for this process ("[pid:thread] [ERROR]" lines) copied into the Lemora log; without errors, the last useful warnings</summary>
        private void LogFreeRdpErrors(int pid, int code)
        {
            if (pid == 0) return;
            try
            {
                if (_freeRdpLogName == null) return;
                var path = Path.Combine(FreeRdpLogDir, _freeRdpLogName);
                if (!File.Exists(path)) return;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(Math.Max(0, fs.Length - 64 * 1024), SeekOrigin.Begin);   // only the tail: the file grows at every connection
                    text = new StreamReader(fs).ReadToEnd();
                }
                var mine = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains($"[{pid}:")).ToList();
                var errors = mine.Where(l => l.Contains("[ERROR]") || l.Contains("[FATAL]")).TakeLast(8).ToList();
                if (errors.Count == 0) errors = mine.Where(l => !l.Contains("log_build_warn") && !l.Contains("[DANGER]")).TakeLast(5).ToList();
                SimpleLogHelper.Warning($"FREERDP-ERRORI({ProtocolServer.DisplayName}): code {code}, {(errors.Count == 0 ? "no line in the FreeRDP log" : "\n" + string.Join("\n", errors))}");
                // the last connection phases (WLOG_FILTER): where it stopped
                var phases = mine.Where(l => l.Contains("[DEBUG]")).TakeLast(20).ToList();
                if (phases.Count > 0) SimpleLogHelper.Warning($"FREERDP-FASI({ProtocolServer.DisplayName}):\n" + string.Join("\n", phases));
            }
            catch (Exception ex) { SimpleLogHelper.Warning($"FREERDP-ERRORI({ProtocolServer.DisplayName}): FreeRDP log not read ({ex.Message})"); }
        }

        private void ProcessOnExited(object? sender, EventArgs e)
        {
            if (_runner is FreeRdpRunner)
            {
                UnhookFreeRdpWindows();
                int code, pid;
                try { code = (sender as Process)?.ExitCode ?? 0; } catch { code = 0; }
                try { pid = (sender as Process)?.Id ?? 0; } catch { pid = 0; }
                if (!FreeRdpRunner.IsNormalEnd(code)) LogFreeRdpErrors(pid, code);
                FreeRdpLogDone();   // errors already copied (filtered) into the Lemora log
                // TLS rejected by OpenSSL: old server (SBS 2011 / 2008 R2, SHA-1 signed certificate) that Windows accepts.
                // New attempts with a lower TLS security level (1, then 0), as the Microsoft client accepts them; if they fail too, the error message
                // Never silent (security review): the user is asked first; level 1 (old but not the weakest methods) is tried before 0 (everything).
                // 143 is also "certificate different from the checked one": the question says so, a PC that always worked must not continue
                if (code == 143 && _freeRdpTlsLevel != 0 && !_disposed && (_freeRdpTlsAgreed || AskLowTls()))
                {
                    var from = _freeRdpTlsLevel;
                    _freeRdpTlsLevel = from == null ? 1 : 0;
                    // the first attempt had reached "connected": without this the new attempt immediately showed the black FreeRDP window
                    // instead of the Lemora loading screen (~10 s of black)
                    _freeRdpEverConnected = false;
                    SimpleLogHelper.Warning($"{nameof(IntegrateHost)}({ProtocolServer.DisplayName}): TLS refused (code 143{(from != null ? $" with level {from}" : "")}), retrying with /tls:seclevel:{_freeRdpTlsLevel} (accepted by the user)");
                    try { Execute.OnUIThread(ReConn); } catch (Exception ex) { SimpleLogHelper.Warning(ex); }
                    return;
                }
                if (!FreeRdpRunner.IsNormalEnd(code)) { OnFreeRdpFailed(code); return; }
            }
            if (IsPuttyFamily) UnhookFreeRdpWindows();   // the window creation notification stays active only while the process exists
            // Activity log: the session ends because the external program (PuTTY, KiTTY, ...) closed
            try { ActivityReason = $"the program {Path.GetFileName(ExeFullName)} closed (exit code {(sender as Process)?.ExitCode})"; } catch { ActivityReason = "the external program closed"; }
            SimpleLogHelper.Info($"{nameof(IntegrateHost)}({ProtocolServer.DisplayName}): {ActivityReason}");
            // Pool-thread callback: sync Dispatcher.Invoke throws once the dispatcher is shutting down.
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _timer?.Stop();
                    _timer?.Dispose();
                    _timer = null;
                    _process = null;
                    FormsHost.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                // e.g. TaskCanceledException on exit: the UI updates are moot, teardown below still runs.
                SimpleLogHelper.Warning(ex);
            }
            _process = null;
            Close();
        }

        public override void FocusOnMe()
        {
            // FreeRDP: the session window is a child of the tab (not foreground by itself): it is given the keyboard focus.
            // Before, the last attached window was used, which could be a FreeRDP service window
            if (IsFreeRdp)
            {
                var s = McpWindow();
                if (s != IntPtr.Zero)
                {
                    // the window belongs to another process: SetFocus works only with the keyboard queues of the two threads attached
                    // (without, in the sandbox the keyboard stayed with Lemora or nobody). They stay attached while the window is our child
                    var sdlThread = GetWindowThreadProcessId(s, out _);
                    var attached = sdlThread != 0 && AttachThreadInput(GetCurrentThreadId(), sdlThread, true);
                    var prev = SetFocus(new HandleRef(this, s));
                    SimpleLogHelper.Debug($"FOCUS-DIAG({ProtocolServer.DisplayName}): focus to FreeRDP attached={attached} keyboard={(prev != IntPtr.Zero ? "ok" : "failed")}");
                    return;
                }
            }
            SetForegroundWindow(this.GetHostHwnd());
        }

        public override ProtocolHostType GetProtocolHostType()
        {
            return ProtocolHostType.Integrate;
        }

        public override IntPtr GetHostHwnd()
        {
            if (_exeHandles.Count > 0)
                return _exeHandles.Last();
            
            try
            {
                return _process?.MainWindowHandle ?? IntPtr.Zero;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error($"Error when _process?.MainWindowHandle: {e}");
                // Process has exited or been disposed
                return IntPtr.Zero;
            }
        }

        public Action? RunBeforeConnect { get; set; }
        public Action? RunAfterConnected { get; set; }
    }
}
