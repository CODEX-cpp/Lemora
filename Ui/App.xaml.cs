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
            var argss = args.ToList();
            Lemora.Utils.StartDiag.Mark("Main: inizio (avvio di .NET finito)");
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
            // il processo di installazione (permessi di amministratore) non passa dall'istanza unica: l'app dell'utente puo' essere aperta
            // anche il file lanciato con l'app gia' installata (scelta disinstalla/ripara/portatile) non va girato all'app aperta
            if (argss.Any(a => a.StartsWith("--setup-")) == false && Lemora.Setup.SetupFlow.NeedsMaintenanceChoice() == false)
            {
                Lemora.Utils.StartDiag.Mark("Main: controllo installazione (NeedsMaintenanceChoice)");
                Lemora.Setup.SetupFlow.IsolateIfPortableCopy(); // una copia portatile non e' l'app installata: istanza separata
                Lemora.Utils.StartDiag.Mark("Main: IsolateIfPortableCopy");
                AppStartupHelper.Init(argss); // in this method, it will call Environment.Exit() if needed
            }
            Lemora.Utils.StartDiag.Mark("Main: istanza unica controllata");
            // avvio normale (icona, nessun argomento): subito una finestra scheletro; con argomenti (file .mora, nome, minimizzato) non c'e' una finestra in vista
            if (argss.Count == 0) Lemora.Utils.StartupSplash.Start();
            Lemora.Utils.StartDiag.Mark("Main: scheletro avviato");
            Lemora.Utils.SmoothWheelScroll.Install();
            Lemora.Utils.StartDiag.Mark("Main: scheletro avviato, rotellina installata");
            // se il programma muore per un errore fuori dall'interfaccia (altro thread, attivita' in background) resta una riga nel log: prima non restava nulla
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { Shawn.Utils.SimpleLogHelper.Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "errore sconosciuto")); } catch { /* si sta chiudendo */ }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try { Shawn.Utils.SimpleLogHelper.Warning("Eccezione in un'attivita' in background non gestita: " + e.Exception); } catch { }
                e.SetObserved();
            };
            Lemora.Utils.FontPx.InstallWindowFontSize();
            Lemora.Utils.StartDiag.Mark("Main: grandezza caratteri installata");
            Lemora.Utils.HeadlessWindowGuard.InstallIfRequested(); // no-op senza LEMORA_HEADLESS=1
            Lemora.Utils.StartDiag.Mark("Main: guardia delle prove nascoste installata");
            Lemora.Service.AppShortcuts.Install();   // prima di qualsiasi finestra con controlli nativi: vedi il commento nella classe
            Lemora.Utils.StartDiag.Mark("Main: scorciatoie installate");
            Lemora.Utils.StartDiag.Mark("Main: prima di creare l'applicazione");
            var application = new App();
            application.InitializeComponent();
            Lemora.Utils.StartDiag.Mark("Main: risorse dell'applicazione caricate (App.xaml)");
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
            ExitDiag.Mark("OnExit: inizio");
            try { IoC.TryGet<SessionControlService>()?.DisconnectAllVncNow(); } catch { /* si sta chiudendo */ }
            ExitDiag.Mark("OnExit: VNC avvisati");
            try { Lemora.Service.FileTransfer.FileTransferShell.CleanStaging(); } catch { /* si sta chiudendo */ }
            try { IoC.TryGet<Lemora.Service.Locality.LocalityService>()?.Flush(); } catch { /* si sta chiudendo */ }   // posizione e grandezza delle finestre ancora da salvare
            try { Lemora.Service.Locality.LocalityListViewService.FlushWidths(); } catch { /* si sta chiudendo */ }   // larghezze delle colonne della lista
            ExitDiag.Mark("OnExit: salvataggi fatti");
            base.OnExit(e);
            ExitDiag.Mark("OnExit: fine");
        }

        public static bool ExitingFlag = false;
        public static void Close(int exitCode = 0)
        {
            ExitDiag.Mark($"Close({exitCode}): inizio");
            // workaround
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(5 * 1000);
                ExitDiag.Mark("Close: dopo 5 s il processo e' ancora vivo, Environment.Exit(1)");
                // Environment.Exit puo' restare appeso (aspetta i gestori di ProcessExit): dopo altri 5 s il processo si termina e basta
                new Thread(() =>
                {
                    Thread.Sleep(5 * 1000);
                    ExitDiag.Mark("Close: Environment.Exit appeso da 5 s, processo terminato con Kill");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }) { IsBackground = true }.Start();
                Environment.Exit(1);
            });
            ExitingFlag = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                // prima di chiudere si avvisano i server VNC: dopo, il processo potrebbe finire prima della chiusura asincrona delle sessioni
                try { IoC.TryGet<SessionControlService>()?.DisconnectAllVncNow(); } catch (Exception e) { Shawn.Utils.SimpleLogHelper.Warning(e.Message); }
                ExitDiag.Mark("Close: Shutdown chiesto");
                Application.Current.Shutdown(exitCode);
                ExitDiag.Mark("Close: Shutdown tornato");
            });
        }
    }
}
