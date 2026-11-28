using System;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lemora.Service;
using Lemora.Utils.Tracing;
using ExitDiag = Lemora.Utils.ExitDiag;
#if FOR_MICROSOFT_STORE_ONLY
#if DEV
using System.IO;
#endif
using Windows.ApplicationModel.Activation;
using Lemora.Utils;
#endif


namespace Lemora
{
    static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // started by the browser for the extension (native messaging): only the bridge to the running Lemora, no windows and no single-instance check
            if (Lemora.Service.Browser.BrowserHost.IsHostLaunch(args)) { Environment.Exit(Lemora.Service.Browser.BrowserHost.Run()); return; }
            // started by Lemora itself to run one script of Claude in a separate process with a memory ceiling (McpScript): no window, no database
            if (Lemora.Service.Mcp.ScriptChild.IsLaunch(args)) { Environment.Exit(Lemora.Service.Mcp.ScriptChild.Run()); return; }
            var argss = args.ToList();
            Lemora.Utils.StartDiag.Mark("Main: start (.NET startup done)");
            Lemora.Setup.TaskbarIdentity.ApplyToProcess();
            AppInitHelper.Init();
            Lemora.Utils.StartDiag.Mark("Main: AppInitHelper.Init");
#if FOR_MICROSOFT_STORE_ONLY
            // see: https://stackoverflow.com/questions/57755792/how-can-i-handle-file-activation-from-a-wpf-app-which-is-running-as-uwp
            try
            {
                var aea = Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
                if (aea?.Kind == ActivationKind.StartupTask)
                {
                    // ref: https://blogs.windows.com/windowsdeveloper/2017/08/01/configure-app-start-log/
                    // If your app is enabled for startup activation, you should handle this case in your
                    // App class by overriding the OnActivated method.Check the IActivatedEventArgs.Kind
                    // to see if it is ActivationKind.StartupTask, and if so, case the IActivatedEventArgs
                    // to a StartupTaskActivatedEventArgs.
                    argss.Add(AppStartupHelper.APP_START_MINIMIZED);
#if DEBUG
                    string kind = aea?.Kind.ToString() ?? "null";
                    if (File.Exists(@"D:\1remtoe_arg_Kind.txt")) kind = File.ReadAllText(@"D:\1remtoe_arg_Kind.txt") + "\r\n" + kind;
                    File.WriteAllText(@"D:\1remtoe_arg_Kind.txt", kind);
                    if (File.Exists(@"D:\1remtoe_arg_data.txt")) File.Delete(@"D:\1remtoe_arg_data.txt");
                    File.WriteAllText(@"D:\1remtoe_arg_data.txt", string.Join("\r\n", argss));
#endif
                }
            }
            catch (Exception e)
            {
                UnifyTracing.Error(e);
            }
#endif
            // the setup process (administrator rights) skips the single-instance check: the user's app may be open
            // likewise, the file launched while the app is already installed (uninstall/repair/portable choice) must not be handed to the open app
            if (argss.Any(a => a.StartsWith("--setup-")) == false && Lemora.Setup.SetupFlow.NeedsMaintenanceChoice() == false)
            {
                Lemora.Utils.StartDiag.Mark("Main: installation check (NeedsMaintenanceChoice)");
                Lemora.Setup.SetupFlow.IsolateIfPortableCopy(); // a portable copy is not the installed app: separate instance
                Lemora.Utils.StartDiag.Mark("Main: IsolateIfPortableCopy");
                AppStartupHelper.Init(argss); // in this method, it will call Environment.Exit() if needed
            }
            Lemora.Utils.StartDiag.Mark("Main: single instance checked");
            // normal start (icon, no arguments): a skeleton window right away; with arguments (.mora file, name, minimized) there is no visible window
            if (argss.Count == 0) Lemora.Utils.StartupSplash.Start();
            Lemora.Utils.StartDiag.Mark("Main: skeleton started");
            Lemora.Utils.SmoothWheelScroll.Install();
            Lemora.Utils.StartDiag.Mark("Main: skeleton started, mouse wheel installed");
            // if the program dies from an error outside the UI (another thread, background task) a line stays in the log: before, nothing was left
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { Shawn.Utils.SimpleLogHelper.Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown error")); } catch { /* shutting down */ }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try { Shawn.Utils.SimpleLogHelper.Warning("Unhandled exception in a background task: " + e.Exception); } catch { }
                e.SetObserved();
            };
            Lemora.Utils.FontPx.InstallWindowFontSize();
            Lemora.Utils.StartDiag.Mark("Main: font size installed");
            Lemora.Utils.HeadlessWindowGuard.InstallIfRequested(); // no-op without LEMORA_HEADLESS=1
            Lemora.Utils.StartDiag.Mark("Main: hidden test guard installed");
            Lemora.Service.AppShortcuts.Install();   // before any window with native controls: see the comment in the class
            Lemora.Utils.StartDiag.Mark("Main: shortcuts installed");
            Lemora.Utils.UiStallProbe.Install();   // logs UI freezes (UI-STALL)
            Lemora.Utils.StartDiag.Mark("Main: before creating the application");
            var application = new App();
            application.InitializeComponent();
            Lemora.Utils.StartDiag.Mark("Main: application resources loaded (App.xaml)");
            application.Run();
        }
    }

    public partial class App : Application
    {
        public static ResourceDictionary? ResourceDictionary { get; private set; } = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            Lemora.Utils.StartDiag.Mark("App: OnStartup");
            ResourceDictionary = this.Resources;
            base.OnStartup(e);

            // First, make a sound (one second of silence) in the main window
            // so that the Volume Mixer and others will recognize Lemora as
            // an application that outputs sound.
            //
            // Otherwise, Lemora is only be detected as a sound application
            // when an RDP session is started. However, it seemed odd that it
            // remained in this state even after all RDP sessions were
            // terminated.
            //
            // So while this application is running, from start to finish,
            // it's better to be visible as a sound application in the Volume
            // Mixer and others.
            try
            {
                var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/dummy.wav"));
                if (sri != null)
                {
                    using var s = sri.Stream;
                    System.Media.SoundPlayer player = new System.Media.SoundPlayer(s);
                    player.Load();
                    player.Play();
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ExitDiag.Mark("OnExit: start");
            try { IoC.TryGet<SessionControlService>()?.DisconnectAllVncNow(); } catch { /* shutting down */ }
            ExitDiag.Mark("OnExit: VNC notified");
            try { Lemora.Service.FileTransfer.FileTransferShell.CleanStaging(); } catch { /* shutting down */ }
            try { IoC.TryGet<Lemora.Service.Locality.LocalityService>()?.Flush(); } catch { /* shutting down */ }   // window position and size still to be saved
            try { Lemora.Service.Locality.LocalityListViewService.FlushWidths(); } catch { /* shutting down */ }   // list column widths
            ExitDiag.Mark("OnExit: saves done");
            base.OnExit(e);
            ExitDiag.Mark("OnExit: end");
        }

        public static bool ExitingFlag = false;
        public static void Close(int exitCode = 0)
        {
            ExitDiag.Mark($"Close({exitCode}): start");
            // workaround
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(5 * 1000);
                ExitDiag.Mark("Close: after 5 s the process is still alive, Environment.Exit(1)");
                // Environment.Exit can hang (it waits for ProcessExit handlers): after another 5 s the process is simply killed
                new Thread(() =>
                {
                    Thread.Sleep(5 * 1000);
                    ExitDiag.Mark("Close: Environment.Exit hung for 5 s, process terminated with Kill");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }) { IsBackground = true }.Start();
                Environment.Exit(1);
            });
            ExitingFlag = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                // notify the VNC servers before closing: afterwards the process might end before the sessions finish closing asynchronously
                try { IoC.TryGet<SessionControlService>()?.DisconnectAllVncNow(); } catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning(e.Message); }
                ExitDiag.Mark("Close: Shutdown requested");
                Application.Current.Shutdown(exitCode);
                ExitDiag.Mark("Close: Shutdown returned");
            });
        }
    }
}
