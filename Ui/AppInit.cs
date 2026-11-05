using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Lemora.Model;
using Lemora.Service;
using Lemora.View;
using Lemora.View.Guidance;
using Shawn.Utils;
using Shawn.Utils.Wpf.FileSystem;
using Lemora.Service.DataSource;
using Lemora.Utils;
using Lemora.Service.DataSource.DAO;
using Lemora.View.ServerView;
using Lemora.View.Settings.General;
using Lemora.View.Utils;
using System.Collections.Generic;
using Lemora.Utils.PuTTY.Model;
using Lemora.Utils.Tracing;

namespace Lemora
{
    public enum ProfileStorage
    {
        AppData,
        Portable
    }

    internal static class AppInitHelper
    {
        private static bool WritePermissionCheck(string path, bool isFile, bool alert, bool exitIfError)
        {
            Debug.Assert(LanguageServiceObj != null);
            // un file gia' aperto da un'altra istanza di Lemora (database condiviso tra app installata e di sviluppo) non e' un errore di permessi
            var flag = isFile == false ? IoPermissionHelper.HasWritePermissionOnDir(path) : DbFileAccess.HasWritePermissionShared(path);
            if (flag)
            {
                // check by write txt
                var di = new DirectoryInfo(isFile ? new FileInfo(path).DirectoryName! : path);
                if (di.Exists)
                {
                    try
                    {
                        var txt = Path.Combine(di.FullName, $"PermissionCheck.txt");
                        File.WriteAllText(txt, txt);
                        File.Delete(txt);
                    }
                    catch (Exception)
                    {
                        flag = false;
                    }
                }
            }

            if (!flag)
            {
                if (alert)
                    MessageBoxHelper.ErrorAlert(LanguageServiceObj?.Translate("write permissions alert", path) ?? "write permissions error:" + path);
                if (exitIfError)
                    Environment.Exit(1);
            }
            return flag;
        }

        /// <summary>
        /// Dà al gruppo Utenti il permesso di modificare la cartella (e quanto contiene): serve per tenere i dati nella cartella del programma.
        /// Si lancia icacls con i permessi di amministratore (finestra UAC); false se si rifiuta o fallisce.
        /// </summary>
        private static bool TryGrantWriteAccess(string dir)
        {
            try
            {
                var psi = new ProcessStartInfo("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(30000);
                return p?.ExitCode == 0;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning($"permesso di scrittura su '{dir}' non concesso: {e.Message}");
                return false;
            }
        }

        private static bool CheckAllPathsPermission(AppPathHelper appPathHelper, bool alert = false, bool exitIfError = false)
        {
            var dirPaths = new List<string>
            {
                appPathHelper.BaseDirPath,
                appPathHelper.PuttyDirPath,
                appPathHelper.ProtocolRunnerDirPath,
                appPathHelper.LocalityDirPath,
                appPathHelper.LocalityIconDirPath,
            };
            var filePaths = new List<string>()
            {
                appPathHelper.LogFilePath,
                appPathHelper.ProfileJsonPath,
                appPathHelper.ProfileAdditionalDataSourceJsonPath,
                appPathHelper.SqliteDbDefaultPath,
                appPathHelper.LogFilePath,
            };

            foreach (var dirPath in dirPaths)
            {
                if (!WritePermissionCheck(dirPath, false, alert, exitIfError))
                {
                    return false;
                }
            }

            foreach (var filePath in filePaths)
            {
                if (!WritePermissionCheck(filePath, true, alert, exitIfError))
                {
                    return false;
                }
            }
            return true;
        }


        public static void Init()
        {
            SimpleLogHelper.WriteLogLevel = SimpleLogHelper.EnumLogLevel.Disabled;
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory); // in case user start app in a different working dictionary.
            UnifyTracing.Init();
        }

        public static LanguageService? LanguageServiceObj;
        public static KeywordMatchService? KeywordMatchServiceObj;
        public static ConfigurationService? ConfigurationServiceObj;
        public static ThemeService? ThemeServiceObj;
        public static GlobalData GlobalDataObj = null!;

        private static bool _isNewUser = false;
        private static DatabaseStatus _localDataConnectionStatus;

        public static void InitOnStart()
        {
            Debug.Assert(App.ResourceDictionary != null);

            Configuration newConfiguration = new();
            LanguageServiceObj = new LanguageService(App.ResourceDictionary!);
            LanguageServiceObj.SetLanguage(CultureInfo.CurrentCulture.Name.ToLower());
            if (Lemora.Setup.SetupFlow.HandleSetupCommand()) Environment.Exit(0); // processo di installazione con permessi di amministratore
            #region Portable or not
            {
                // modo portatile: i dati stanno in una sola cartella "Lemora-data" accanto all'exe, non sparsi nella cartella dell'exe;
                // chi ha gia' i dati accanto all'exe (versioni precedenti) li tiene dove sono
                var legacyPortablePaths = new AppPathHelper(Environment.CurrentDirectory, Environment.CurrentDirectory);
                var portableDataDir = File.Exists(legacyPortablePaths.ProfileJsonPath) ? Environment.CurrentDirectory : Path.Combine(Environment.CurrentDirectory, "Lemora-data");
                var portablePaths = new AppPathHelper(portableDataDir, portableDataDir);
                var appDataPaths = new AppPathHelper(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Assert.APP_NAME), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Assert.APP_NAME));

                // primo avvio: si sceglie come usare l'app (Portatile, Locale, Teams). Locale e Teams si installano e questo processo termina.
                if (Lemora.Setup.SetupFlow.NeedsMaintenanceChoice())
                {
                    // app gia' installata e questo e' un altro exe: disinstalla / ripara / avvia come portatile
                    if (Lemora.Setup.SetupFlow.RunMaintenance() == false)
                        Environment.Exit(0);
                }
                else if (Lemora.Setup.SetupFlow.ShouldShowChooser(portablePaths, appDataPaths) && Lemora.Setup.SetupFlow.RunChooser() == false)
                    Environment.Exit(0);
                bool installedCopy = Lemora.Setup.SetupEngine.IsInstalledCopy();

                ProfileStorage selectedMode = ProfileStorage.Portable;
                {
                    _isNewUser = false;
                    bool portableProfilePathExisted = File.Exists(portablePaths.ProfileJsonPath);
                    bool appDataProfilePathExisted = File.Exists(appDataPaths.ProfileJsonPath);
                    bool forcePortable = File.Exists(AppPathHelper.FORCE_INTO_PORTABLE_MODE);
                    bool forceAppData = File.Exists(AppPathHelper.FORCE_INTO_APPDATA_MODE);
                    bool permissionForPortable = CheckAllPathsPermission(portablePaths, portableProfilePathExisted || forcePortable, forcePortable);
                    // la cartella dati non esiste ancora: serve poterla creare accanto all'exe
                    if (permissionForPortable && Directory.Exists(portablePaths.BaseDirPath) == false)
                        permissionForPortable = WritePermissionCheck(Environment.CurrentDirectory, false, false, false);
#if FOR_MICROSOFT_STORE_ONLY
                    forceAppData = true;
                    forcePortable = false;
                    permissionForPortable = false;
#endif


                    ProfileStorage? defaultStorage = null; // default mode, if is null, user can select `portable` or `app data` in guidance view.
                    if (permissionForPortable)
                    {
                        // 当前文件夹可写
                        if (forcePortable == true)
                        {
                            defaultStorage = selectedMode = ProfileStorage.Portable;
                            if (portableProfilePathExisted == false)
                            {
                                _isNewUser = true;
                            }
                        }
                        else if (forceAppData == true)    // 标记了强制 AppData 模式
                        {
                            defaultStorage = selectedMode = ProfileStorage.AppData;
                            if (appDataProfilePathExisted == false)
                            {
                                _isNewUser = true;
                            }
                        }
                        else // 标志文件都不存在时
                        {
                            if (portableProfilePathExisted == false) // 当前文件夹可写但却没有标志文件，也没配置文件，(不管appDataProfilePath是存在都)认为是新用户
                            {
                                _isNewUser = true;
                            }
                            else
                            {
                                defaultStorage = selectedMode = ProfileStorage.Portable;
                            }
                        }
                    }
                    else
                    {
                        // cartella del programma non scrivibile (es. installato in Programmi)
                        defaultStorage = selectedMode = ProfileStorage.AppData;
                        if (appDataProfilePathExisted == false) // nessun profilo in AppData: utente nuovo
                        {
                            _isNewUser = true;
#if !FOR_MICROSOFT_STORE_ONLY
                            // utente nuovo: la finestra iniziale offre sempre le due scelte (predefinita: cartella del programma);
                            // se sceglie quella, dopo la conferma si chiedono i permessi di amministratore per poterci scrivere
                            if (portableProfilePathExisted == false && forceAppData == false && installedCopy == false)
                                defaultStorage = null;
#endif
                        }
                    }

                    // scelto "Portatile" nella finestra iniziale: la scelta dei dati e' gia' fatta
                    if (_isNewUser && Lemora.Setup.SetupFlow.ChosenPortable)
                        defaultStorage = selectedMode = ProfileStorage.Portable;

                    if (_isNewUser)
                    {
                        SecondaryVerificationHelper.Init();
                        // Ensure LanguageServiceObj is initialized
                        if (LanguageServiceObj == null)
                        {
                            throw new InvalidOperationException("LanguageServiceObj must be initialized before showing guidance window");
                        }

                        // copia installata (Locale/Teams) o Portatile scelto nella finestra iniziale: scelte gia' fatte, niente finestra di benvenuto
                        if (installedCopy == false && Lemora.Setup.SetupFlow.ChosenPortable == false)
                        {
                            var guidanceWindowViewModel = new GuidanceWindowViewModel(LanguageServiceObj, newConfiguration, defaultStorage);
                            var guidanceWindow = new GuidanceWindow(guidanceWindowViewModel);
                            guidanceWindow.ShowDialog();
                            selectedMode = guidanceWindowViewModel.ProfileModeIsPortable ? ProfileStorage.Portable : ProfileStorage.AppData;
                            AppStartupHelper.InstallDesktopShortcut(guidanceWindowViewModel.CreateDesktopShortcut);
                        }

                        // scelta "cartella del programma" ma la cartella non e' scrivibile: UAC una sola volta per dare il permesso di scrittura
                        if (selectedMode == ProfileStorage.Portable && permissionForPortable == false)
                        {
                            if (TryGrantWriteAccess(Environment.CurrentDirectory))
                                permissionForPortable = CheckAllPathsPermission(portablePaths, false, false);
                            if (permissionForPortable == false)
                            {
                                selectedMode = ProfileStorage.AppData;
                                System.Windows.MessageBox.Show(
                                    "Non e' stato possibile dare il permesso di scrittura sulla cartella del programma.\r\nI dati verranno salvati nella cartella AppData dell'utente.",
                                    Assert.APP_DISPLAY_NAME, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                            }
                        }
                    }

                    // 在当前文件夹自动创建标志文件
                    if (permissionForPortable)
                    {
                        try
                        {
                            switch (selectedMode)
                            {
                                case ProfileStorage.AppData:
                                    if (File.Exists(AppPathHelper.FORCE_INTO_APPDATA_MODE) == false)
                                        File.WriteAllText(AppPathHelper.FORCE_INTO_APPDATA_MODE, $"rename to '{AppPathHelper.FORCE_INTO_PORTABLE_MODE}' can make it portable");
                                    if (File.Exists(AppPathHelper.FORCE_INTO_PORTABLE_MODE))
                                        File.Delete(AppPathHelper.FORCE_INTO_PORTABLE_MODE);
                                    break;
                                case ProfileStorage.Portable:
                                    // con la cartella "Lemora-data" il marcatore non serve: il profilo li' dentro basta a riconoscere il modo
                                    if (portablePaths.BaseDirPath == Environment.CurrentDirectory && File.Exists(AppPathHelper.FORCE_INTO_PORTABLE_MODE) == false)
                                        File.WriteAllText(AppPathHelper.FORCE_INTO_PORTABLE_MODE, $"rename to '{AppPathHelper.FORCE_INTO_APPDATA_MODE}' can save to AppData");
                                    if (File.Exists(AppPathHelper.FORCE_INTO_APPDATA_MODE))
                                        File.Delete(AppPathHelper.FORCE_INTO_APPDATA_MODE);
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                }
                AppPathHelper.Instance = selectedMode == ProfileStorage.Portable ? portablePaths : appDataPaths;

                // 最终文件权限检查
                {
                    var paths = AppPathHelper.Instance;
                    CheckAllPathsPermission(paths, true, true);
                }

                // 文件夹创建
                {
                    var paths = AppPathHelper.Instance;
                    AppPathHelper.CreateDirIfNotExist(paths.BaseDirPath, false);
                    AppPathHelper.CreateDirIfNotExist(paths.ProtocolRunnerDirPath, false);
                    AppPathHelper.CreateDirIfNotExist(paths.ProfileJsonPath, true);
                    AppPathHelper.CreateDirIfNotExist(paths.LogFilePath, true);
                    AppPathHelper.CreateDirIfNotExist(paths.SqliteDbDefaultPath, true);
                    AppPathHelper.CreateDirIfNotExist(paths.PuttyDirPath, false);
                    AppPathHelper.CreateDirIfNotExist(paths.LocalityDirPath, false);
                }
            }
            #endregion

            // logger init
            {
                SimpleLogHelper.WriteLogLevel = SimpleLogHelper.EnumLogLevel.Info;
                SimpleLogHelper.PrintLogLevel = SimpleLogHelper.EnumLogLevel.Debug;
#if DEBUG
                SimpleLogHelper.WriteLogLevel = SimpleLogHelper.EnumLogLevel.Debug;
                ConsoleManager.Show();
#endif
                // init log file placement
                SimpleLogHelper.LogFileName = AppPathHelper.Instance.LogFilePath;
                Lemora.Utils.ExitDiag.Init(Path.GetDirectoryName(AppPathHelper.Instance.LogFilePath)!);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Lemora.Utils.ExitDiag.Mark("ProcessExit (il processo sta finendo)");
            }


            KeywordMatchServiceObj = new KeywordMatchService();
            // read profile
            try
            {
                if (File.Exists(AppPathHelper.Instance.ProfileJsonPath) == true)
                {
                    ConfigurationServiceObj = ConfigurationService.LoadFromAppPath(KeywordMatchServiceObj);
                }
                else
                {
                    newConfiguration.SqliteDatabasePath = AppPathHelper.Instance.SqliteDbDefaultPath;
                    ConfigurationServiceObj = new ConfigurationService(KeywordMatchServiceObj, newConfiguration);
                }
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                newConfiguration.SqliteDatabasePath = AppPathHelper.Instance.SqliteDbDefaultPath;
                ConfigurationServiceObj = new ConfigurationService(KeywordMatchServiceObj, newConfiguration);
            }

            if (ConfigurationServiceObj.General.LogDetail < 0)
                ConfigurationServiceObj.General.LogDetail = File.Exists(AppPathHelper.Instance.ProfileJsonPath)
                    ? Lemora.Utils.Diag.LogDetailFromOld(ConfigurationServiceObj.General.LogLevel, ConfigurationServiceObj.General.DiagnosticMode)
                    : 1;   // utente nuovo: Medio
            Lemora.Utils.Diag.ApplyLogDetail(ConfigurationServiceObj.General.LogDetail);
            // riga d'apertura visibile a ogni livello: versione e livello del log aiutano a leggere tutto il resto
            SimpleLogHelper.Warning($"Avvio {Assert.APP_DISPLAY_NAME} {AppVersion.Version} ({AppVersion.BuildDate}), Windows {Environment.OSVersion.Version}, livello del log {Lemora.Utils.Diag.LogDetailName(ConfigurationServiceObj.General.LogDetail)}");
            Lemora.Utils.UiHangWatchdog.Start();   // UI-DIAG: diagnosi temporanea dei blocchi
            System.Threading.Tasks.Task.Run(Lemora.Service.FileTransfer.FileTransferShell.CleanStaging);   // file temporanei lasciati da un trasferimento precedente

            // make sure path is not empty
            if (string.IsNullOrWhiteSpace(ConfigurationServiceObj.LocalDataSource.Path))
            {
                ConfigurationServiceObj.LocalDataSource.Path = AppPathHelper.Instance.SqliteDbDefaultPath;
            }

            ThemeServiceObj = new ThemeService(App.ResourceDictionary!, ConfigurationServiceObj.Theme);
            GlobalDataObj = new GlobalData(ConfigurationServiceObj);
        }

        public static void InitOnConfigure()
        {
            IoC.Get<LanguageService>().SetLanguage(IoC.Get<ConfigurationService>().General.CurrentLanguageCode);

            // Init data sources controller
            var dataSourceService = IoC.Get<DataSourceService>();
            GlobalDataObj.SetDataSourceService(dataSourceService);

            // read from configs and find where db is.
            {
                var local = ConfigurationServiceObj!.LocalDataSource;
                if (string.IsNullOrWhiteSpace(local.Path))
                    local.Path = AppPathHelper.Instance.SqliteDbDefaultPath;
                var fi = new FileInfo(local.Path);
                try
                {
                    if (fi?.Directory?.Exists == false)
                        fi.Directory.Create();
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                }

                // la cassaforte si apre prima di leggere il database: senza la chiave dei dati non si legge nulla
                try
                {
                    if (!VaultUnlock.Run(local.Path, ConfigurationServiceObj.General.VaultKeepUnlockedMinutes)) Environment.Exit(0);
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);   // database non apribile: lo dira' InitLocalDataSource qui sotto
                }
                StartDiag.Mark("  Init: cassaforte aperta");
                _localDataConnectionStatus = dataSourceService.InitLocalDataSource(local);
            }

            // init session controller
            IoC.Get<SessionControlService>();
            IoC.Get<MainWindowViewModel>();
        }


        public static void InitOnLaunch()
        {
            // impostazioni di vista e tag: si leggono subito dal database (importando i vecchi file locali la prima volta)
            Lemora.Service.Locality.LocalityTagService.Load();
            Lemora.Service.Locality.LocalityTreeViewService.Load();
            Lemora.Service.Locality.LocalityListViewService.Load();
            StartDiag.Mark("  Init: Locality caricate");
            StartupSplash.Pump();
            // la Home chiede utente/password di ogni connessione rapida: la decifratura e' lenta, si scalda in parallelo al resto dell'avvio
            Task.Run(() => { try { foreach (var q in Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryGetAll()) q.GetUserPassword(); } catch { } });
            // impostazioni dei programmi: dal database (importando i vecchi .json) gia' all'avvio
            _ = IoC.Get<ProtocolConfigurationService>();
            StartDiag.Mark("  Init: ProtocolConfigurationService");
            StartupSplash.Pump();
            IoC.Get<GlobalData>().ReloadAll(true);
            StartDiag.Mark("  Init: GlobalData.ReloadAll");
            if (ConfigurationServiceObj?.General.ShowRecentlySessionInTray != false)
                IoC.Get<TaskTrayService>().ReloadTaskTrayContextMenu();
            StartDiag.Mark("  Init: menu tray");
            if (_isNewUser == false && ConfigurationServiceObj != null)
            {
                Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        var kys = new Dictionary<string, string>
                        {
                            { $"App start with - ServerViewStatus", $"{ConfigurationServiceObj.General.ServerViewStatus}" },
                            { $"App start with - ConfirmBeforeClosingSession", $"{ConfigurationServiceObj.General.ConfirmBeforeClosingSession}" },
                            { $"App start with - LauncherEnabled", $"{ConfigurationServiceObj.Launcher.LauncherEnabled}" },
                            { $"App start with - Theme", $"{ConfigurationServiceObj.Theme.ThemeName}" },
                            { $"App start with - Tray + ShowRecentlySessionInTray", $"{ConfigurationServiceObj.General.ShowRecentlySessionInTray}" },
                            { $"App start with - Windows Hello Enabled", $"{await SecondaryVerificationHelper.GetEnabled()}" },
                            { $"App start with - Language", $"{ConfigurationServiceObj.General.CurrentLanguageCode}" },
                        };
#if FOR_MICROSOFT_STORE_ONLY
                kys.Add("Distributor", $"{Assert.APP_NAME} MS Store");
#else
                        kys.Add("Distributor", $"{Assert.APP_NAME} Exe");
#endif

#if NETFRAMEWORK
                kys.Add($"App start with - Net", $"4.8");
#else
                        kys.Add($"App start with - Net", $"6.x");
#endif
                        UnifyTracing.TraceSpecial(kys);
                    }
                    catch (Exception ex)
                    {
                        UnifyTracing.Error(ex);
                    }
                });
            }

            KittyConfig.CleanUpOldConfig();
            PuttyConfig.CleanUpOldConfig();

            StartDiag.Mark("  Init: pulizia vecchie config");
            StartupSplash.Pump();
            var mvm = IoC.Get<MainWindowViewModel>();
            if (AppStartupHelper.IsStartMinimized == false
                || _localDataConnectionStatus.Status != EnumDatabaseStatus.OK
                || _isNewUser)
            {
                if (_localDataConnectionStatus.Status != EnumDatabaseStatus.OK)
                {
                    string error = _localDataConnectionStatus.GetErrorMessage;
                    mvm.OnMainWindowViewLoaded += () =>
                    {
                        mvm.ShowMe(goPage: EnumMainWindowPage.SettingsData);
                        MessageBoxHelper.ErrorAlert(error);
                    };
                }
                StartupSplash.ShowReal();   // dati pronti: il contenuto vero prende il posto dello scheletro nella stessa finestra
                mvm.ShowMe();
            }
            StartDiag.Mark("  Init: ShowMe");

            MaskLayerController.ShowProcessingRing("", mvm);

            {
                foreach (var ds in ConfigurationServiceObj!.AdditionalDataSource)
                {
                    IoC.Get<DataSourceService>().AddOrUpdateDataSource(ds, connectTimeOutSeconds: 0, doReload: false);
                }

                if (_isNewUser == false)
                    IoC.Get<ServerListPageViewModel>().VmServerListDummyNode();

                StartDiag.Mark("  Init: sorgenti dati aggiunte");
                GlobalDataObj.StartTick();
                IoC.Get<ServerListPageViewModel>().CmdRefreshDataSource.Execute();
                StartDiag.Mark("  Init: CmdRefreshDataSource");
            }

            MaskLayerController.HideMask(mvm);

            AppStartupHelper.ProcessWhenDataLoaded(IoC.Get<GeneralSettingViewModel>());
            StartDiag.Mark("  Init: ProcessWhenDataLoaded");
            if (ConfigurationServiceObj.General.ShowRecentlySessionInTray)
                IoC.Get<TaskTrayService>().ReloadTaskTrayContextMenu();
            IoC.Get<LauncherWindowViewModel>().SetHotKey();
        }
    }
}
