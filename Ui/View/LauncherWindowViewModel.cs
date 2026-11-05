using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lemora.Service;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;
using Lemora.View.Launcher;


namespace Lemora.View
{
    public class LauncherWindowViewModel : NotifyPropertyChangedBaseScreen
    {
        public const double LAUNCHER_LIST_AREA_WIDTH = 640;
        public const double LAUNCHER_GRID_KEYWORD_HEIGHT = 56;
        public const double LAUNCHER_SERVER_LIST_ITEM_HEIGHT = 56;
        public const double LAUNCHER_ACTION_LIST_ITEM_HEIGHT = 40;
        public const double LAUNCHER_OUTLINE_CORNER_RADIUS = 4;
        public static readonly CornerRadius LauncherOutlineCornerRadiusObj = new CornerRadius(LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS);


        public const double LAUNCHER_FOOTER_HEIGHT = 34;
        public const int MAX_SERVER_COUNT = 8;
        public const double MAX_SELECTION_HEIGHT = LauncherWindowViewModel.LAUNCHER_SERVER_LIST_ITEM_HEIGHT * MAX_SERVER_COUNT;
        public const double MAX_WINDOW_HEIGHT = LauncherWindowViewModel.LAUNCHER_GRID_KEYWORD_HEIGHT + MAX_SELECTION_HEIGHT;

        private Visibility _serverSelectionsViewVisibility = Visibility.Visible;
        public Visibility ServerSelectionsViewVisibility
        {
            get => _serverSelectionsViewVisibility;
            set { if (SetAndNotifyIfChanged(ref _serverSelectionsViewVisibility, value)) RaisePropertyChanged(nameof(FooterText)); }
        }

        private bool _actionsMode;
        /// <summary>true mentre e' aperto l'elenco delle azioni di un server (cambia i suggerimenti del piè di pagina)</summary>
        public bool ActionsMode
        {
            get => _actionsMode;
            set { if (SetAndNotifyIfChanged(ref _actionsMode, value)) RaisePropertyChanged(nameof(FooterText)); }
        }

        public string FooterText => IoC.Translate(ServerSelectionsViewVisibility != Visibility.Visible ? "launcher_footer_quick" : ActionsMode ? "launcher_footer_actions" : "launcher_footer_list");

        public ServerSelectionsViewModel ServerSelectionsViewModel { get; }
        public QuickConnectionViewModel QuickConnectionViewModel { get; }
        private readonly ConfigurationService _configurationService;

        // Event handler delegates to prevent memory leaks
        private EventHandler? _deactivatedHandler;
        private KeyEventHandler? _keyDownHandler;

        #region properties


        private double _gridMainHeight;
        public double GridMainHeight
        {
            get => _gridMainHeight;
            set
            {
                if (SetAndNotifyIfChanged(ref _gridMainHeight, value))
                {
                    GridMainClip = new RectangleGeometry(new Rect(new Size(LAUNCHER_LIST_AREA_WIDTH, GridMainHeight)), LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS);
                }
            }
        }


        private RectangleGeometry? _gridMainClip = null;
        public RectangleGeometry? GridMainClip
        {
            get => _gridMainClip;
            set => SetAndNotifyIfChanged(ref _gridMainClip, value);
        }


        public double GridNoteHeight { get; }

        private double _noteWidth = 500;

        public double NoteWidth
        {
            get => _noteWidth;
            private set => SetAndNotifyIfChanged(ref _noteWidth, value);
        }

        #endregion

        public LauncherWindowViewModel(ServerSelectionsViewModel serverSelectionsViewModel, QuickConnectionViewModel quickConnectionViewModel, ConfigurationService configurationService)
        {
            ServerSelectionsViewModel = serverSelectionsViewModel;
            QuickConnectionViewModel = quickConnectionViewModel;
            _configurationService = configurationService;
            GridNoteHeight = MAX_WINDOW_HEIGHT;
        }

        protected override void OnViewLoaded()
        {
            HideMe();
            if (this.View is not LauncherWindowView {IsClosing: false} window) return;
            ServerSelectionsViewVisibility = Visibility.Visible;
            ReSetWindowHeight();
        }

        public void SetHotKey()
        {
            if (this.View is LauncherWindowView { IsClosing: false } window)
            {
                Execute.OnUIThreadSync(() =>
                {
                    // Unsubscribe from previous event handlers to prevent memory leaks
                    if (_deactivatedHandler != null)
                        window.Deactivated -= _deactivatedHandler;
                    if (_keyDownHandler != null)
                        window.KeyDown -= _keyDownHandler;

                    SetHotKey(_configurationService.Launcher.LauncherEnabled,
                        _configurationService.Launcher.HotKeyModifiers, _configurationService.Launcher.HotKeyKey);

                    // Create and subscribe new event handlers
                    // istanze di mappatura (LEMORA_HEADLESS=1): il Launcher resta aperto anche se perde l'attivazione
                    _deactivatedHandler = (s, a) => { if (Environment.GetEnvironmentVariable("LEMORA_HEADLESS") != "1") HideMe(); };
                    _keyDownHandler = (s, a) => { if (a.Key == Key.Escape) HideMe(); };

                    window.Deactivated += _deactivatedHandler;
                    window.KeyDown += _keyDownHandler;

                    ServerSelectionsViewModel.CalcNoteFieldVisibility();
                });
            }
        }


        public void ReSetWindowHeight()
        {
            if (IoC.TryGet<LauncherWindowView>()?.IsClosing != false) return;
            Execute.OnUIThread(() =>
            {
                var height = ServerSelectionsViewVisibility == Visibility.Visible ? ServerSelectionsViewModel.ReCalcGridMainHeight() : QuickConnectionViewModel.ReCalcGridMainHeight();
                GridMainHeight = height;
            });
        }


        public void ShowMe()
        {
            if (this.View is LauncherWindowView { IsClosing: false } window)
            {
                SimpleLogHelper.Debug($"Call shortcut to invoke launcher Visibility = {window.Visibility}");
                if (IoC.Get<MainWindowViewModel>().TopLevelViewModel != null) return;
                if (IoC.Get<ConfigurationService>().Launcher.LauncherEnabled == false) return;

                lock (this)
                {
                    //if (window.Visibility != Visibility.Visible)
                    //{
                    //    MsAppCenterHelper.TraceView(nameof(LauncherWindowView), true);
                    //}
                    window.WindowState = WindowState.Normal;
                    QuickConnectionViewModel.SelectedProtocol = QuickConnectionViewModel.Protocols.First();
                    ReSetWindowHeight();

                    // show position
                    var p = ScreenInfoEx.GetMouseSystemPosition();
                    var screenEx = ScreenInfoEx.GetCurrentScreenBySystemPosition(p);
                    PlaceCenteredInPixels(window, screenEx.Screen);

                    var noteWidth = (screenEx.VirtualWorkingArea.Width - window.BorderMainContent.ActualWidth - 100) / 2;
                    if (noteWidth < 100)
                        noteWidth = 100;
                    NoteWidth = Math.Min(noteWidth, NoteWidth);

                    window.Show();
                    window.Visibility = Visibility.Visible;
                    window.Activate();
                    window.Topmost = true; // important
                    window.Topmost = false; // important
                    window.Topmost = true; // important
                    window.Focus(); // important
                    ServerSelectionsViewModel.Show();
                }
            }
            else if (this.View is not LauncherWindowView)
            {
                IoC.Get<IWindowManager>().ShowWindow(this);
            }
        }


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>
        /// Centra il pannello sul monitor scelto, in pixel veri. Con la scala per monitor Left/Top in unita' WPF dipendono dal monitor
        /// su cui finisce la finestra: prima la si porta sul monitor (cosi' prende la sua scala), poi si calcola il centro con quella.
        /// </summary>
        private void PlaceCenteredInPixels(LauncherWindowView window, System.Windows.Forms.Screen screen)
        {
            const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var wa = screen.WorkingArea;
            var cx = wa.Left + wa.Width / 2;
            var cy = wa.Top + wa.Height / 2;
            SetWindowPos(hwnd, IntPtr.Zero, cx - 100, cy - 100, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);   // sul monitor giusto
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            var panelWidth = window.BorderMainContent.ActualWidth * scale;
            var panelHeight = GridMainHeight * scale;
            var x = (int)Math.Round(cx - panelWidth / 2);
            var y = (int)Math.Round(cy - panelHeight / 2 - 40 * scale);   // 40: margine di BorderMainContent
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public void HideMe()
        {
            if (this.View is not LauncherWindowView {IsClosing: false} window) return;
            lock (this)
            {
                Execute.OnUIThread(() =>
                {
                    //if (window.Visibility == Visibility.Visible)
                    //{
                    //    MsAppCenterHelper.TraceView(nameof(LauncherWindowView), false);
                    //}
                    window.Hide();
                    ActionsMode = false;
                    QuickConnectionViewModel.Filter = "";
                    QuickConnectionViewModel.Show();
                    ServerSelectionsViewModel.Show();
                    // After startup and initalizing our application and when closing our window and minimize the application to tray we free memory with the following line:
                    System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
                });
            }
        }


        public bool SetHotKey(bool launcherEnabled, HotkeyModifierKeys hotKeyModifierKeys, Key hotKeyKey)
        {
            if (this.View is not LauncherWindowView window) return false;
            GlobalHotkeyHooker.Instance.Unregist(window);
            if (launcherEnabled == false)
                return false;
            // istanze di mappatura (LEMORA_HEADLESS=1): niente scorciatoia globale, così non si ruba quella dell'app dell'utente
            if (Environment.GetEnvironmentVariable("LEMORA_HEADLESS") == "1")
                return false;
            // LEMORA_NO_HOTKEY=1: l'app di sviluppo accanto a quella installata non registra la scorciatoia globale (e' gia' della versione installata)
            if (Environment.GetEnvironmentVariable("LEMORA_NO_HOTKEY") == "1")
                return false;
            var r = GlobalHotkeyHooker.Instance.Register(window, (uint)hotKeyModifierKeys, hotKeyKey, this.ShowMe);
            switch (r.Item1)
            {
                case GlobalHotkeyHooker.RetCode.Success:
                    return true;
                case GlobalHotkeyHooker.RetCode.ERROR_HOTKEY_NOT_REGISTERED:
                {
                    var msg = $"{IoC.Translate("hotkey_registered_fail")}: {r.Item2}";
                    SimpleLogHelper.Warning(msg);
                    MessageBoxHelper.Warning(msg);
                    break;
                }
                case GlobalHotkeyHooker.RetCode.ERROR_HOTKEY_ALREADY_REGISTERED:
                {
                    var msg = $"{IoC.Translate("hotkey_already_registered")}: {r.Item2}";
                    SimpleLogHelper.Warning(msg);
                    MessageBoxHelper.Warning(msg);
                    break;
                }
                default:
                {
                    var msg = $"Failed to register hotkey {hotKeyModifierKeys} + {hotKeyKey}, error code: {r.Item1}";
                    SimpleLogHelper.Warning(msg);
                    MessageBoxHelper.Warning(msg);
                    UnifyTracing.Error(new Exception(msg));
                    break;
                }
            }
            return false;
        }

        public void ToggleQuickConnection()
        {
            if (ServerSelectionsViewVisibility == Visibility.Collapsed)
            {
                ServerSelectionsViewModel.Show();
            }
            else
            {
                QuickConnectionViewModel.Show();
            }
            ServerSelectionsViewModel.CalcNoteFieldVisibility();
            ReSetWindowHeight();
        }
    }
}