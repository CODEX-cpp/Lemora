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
            // a file already open in another Lemora instance (database shared between the installed and the dev app) is not a permission error
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
        /// Grants the REQUESTING USER (not the whole Users group) permission to modify the folder: needed to keep the data in the program folder.
        /// With the Users group any other account on the PC could replace the exe or the DLLs used by whoever launches it (security hardening).
        /// Runs icacls with administrator rights (UAC prompt); false if refused or failed.
        /// </summary>
        private static bool TryGrantWriteAccess(string dir)
        {
            try
            {
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(me)) return false;
                // only on the data folder (Lemora-data), not on the exe folder: with modify rights on the exe folder the requester could
                // replace the Lemora.exe used by other users and by the administrator (security hardening)
                var data = Path.Combine(dir, "Lemora-data");
                var psi = new ProcessStartInfo("cmd.exe", $"/c if not exist \"{data}\" mkdir \"{data}\" & icacls \"{data}\" /grant *{me}:(OI)(CI)M")
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
                SimpleLogHelper.Warning($"write permission on '{dir}' not granted: {e.Message}");
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
            // Windows language in the new profile too: installed copies skip the welcome window (where it was chosen) and stayed in English
            if (LanguageServiceObj.SetLanguage(CultureInfo.CurrentCulture.Name.ToLower())) newConfiguration.General.CurrentLanguageCode = CultureInfo.CurrentCulture.Name.ToLower();
            if (Lemora.Setup.SetupFlow.HandleSetupCommand()) Environment.Exit(0); // setup process with administrator rights
            #region Portable or not
            {
                // portable mode: the data lives in a single "Lemora-data" folder next to the exe, not scattered in the exe folder;
                // users who already have data next to the exe (previous versions) keep it where it is
                var legacyPortablePaths = new AppPathHelper(Environment.CurrentDirectory, Environment.CurrentDirectory);
                var portableDataDir = File.Exists(legacyPortablePaths.ProfileJsonPath) ? Environment.CurrentDirectory : Path.Combine(Environment.CurrentDirectory, "Lemora-data");
                var portablePaths = new AppPathHelper(portableDataDir, portableDataDir);
                var appDataPaths = new AppPathHelper(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Assert.APP_NAME), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Assert.APP_NAME));

                // first start: choose how to use the app (Portable, Local, Teams). Local and Teams get installed and this process exits.
                if (Lemora.Setup.SetupFlow.NeedsMaintenanceChoice())
                {
                    // app already installed and this is another exe: uninstall / repair / run as portable
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
                    // installed copy (Local/Teams): data always in AppData, as the installer promises. With a writable program folder
                    // (administrator without UAC, Windows Sandbox) it ended up in Program Files\Lemora\versions\<v>\Lemora-data and the first update lost it
                    if (installedCopy && !forcePortable) forceAppData = true;
                    bool permissionForPortable = CheckAllPathsPermission(portablePaths, portableProfilePathExisted || forcePortable, forcePortable);
                    // the data folder does not exist yet: we must be able to create it next to the exe
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
                        // current folder is writable
                        if (forcePortable == true)
                        {
                            defaultStorage = selectedMode = ProfileStorage.Portable;
                            if (portableProfilePathExisted == false)
                            {
                                _isNewUser = true;
                            }
                        }
                        else if (forceAppData == true)    // forced AppData mode is flagged
                        {
                            defaultStorage = selectedMode = ProfileStorage.AppData;
                            if (appDataProfilePathExisted == false)
                            {
                                _isNewUser = true;
                            }
                        }
                        else // when neither flag file exists
                        {
                            if (portableProfilePathExisted == false) // current folder is writable but has no flag file and no profile file: treat as a new user (whether or not appDataProfilePath exists)
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
                        // program folder not writable (e.g. installed in Program Files)
                        defaultStorage = selectedMode = ProfileStorage.AppData;
                        if (appDataProfilePathExisted == false) // no profile in AppData: new user
                        {
                            _isNewUser = true;
#if !FOR_MICROSOFT_STORE_ONLY
                            // new user: the initial window always offers both choices (default: program folder);
                            // if that one is chosen, administrator rights are requested after confirmation so we can write there
                            if (portableProfilePathExisted == false && forceAppData == false && installedCopy == false)
                                defaultStorage = null;
#endif
                        }
                    }

                    // "Portable" chosen in the initial window: the data location is already decided
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

                        // installed copy (Local/Teams) or Portable chosen in the initial window: choices already made, no welcome window
                        // hidden tests (LEMORA_HEADLESS): nobody would answer the window, keep the mode already decided
                        if (installedCopy == false && Lemora.Setup.SetupFlow.ChosenPortable == false && Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") != "1")
                        {
                            var guidanceWindowViewModel = new GuidanceWindowViewModel(LanguageServiceObj, newConfiguration, defaultStorage);
                            var guidanceWindow = new GuidanceWindow(guidanceWindowViewModel);
                            guidanceWindow.ShowDialog();
                            selectedMode = guidanceWindowViewModel.ProfileModeIsPortable ? ProfileStorage.Portable : ProfileStorage.AppData;
                            AppStartupHelper.InstallDesktopShortcut(guidanceWindowViewModel.CreateDesktopShortcut);
                        }

                        // "program folder" chosen but the folder is not writable: one UAC prompt to grant write permission
                        if (selectedMode == ProfileStorage.Portable && permissionForPortable == false)
                        {
                            if (TryGrantWriteAccess(Environment.CurrentDirectory))
                                permissionForPortable = CheckAllPathsPermission(portablePaths, false, false);
                            if (permissionForPortable == false)
                            {
                                selectedMode = ProfileStorage.AppData;
                                System.Windows.MessageBox.Show(
                                    LanguageServiceObj.Translate("startup_write_permission_fallback_appdata").Replace("\n", "\r\n"),
                                    Assert.APP_DISPLAY_NAME, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                            }
                        }
                    }

                    // automatically create the flag file in the current folder
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
                                    // with the "Lemora-data" folder the marker is not needed: the profile inside it is enough to recognize the mode
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

                // final file permission check
                {
                    var paths = AppPathHelper.Instance;
                    CheckAllPathsPermission(paths, true, true);
                }

                // folder creation
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
                Lemora.Utils.LogRedactor.Install();   // in the log file, entry names, addresses and users become codes
                Lemora.Utils.ExitDiag.Init(Path.GetDirectoryName(AppPathHelper.Instance.LogFilePath)!);
                Lemora.Utils.StartDiag.OpenFile(Path.GetDirectoryName(AppPathHelper.Instance.LogFilePath)!);   // avvio.log: timeline of this startup
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Lemora.Utils.ExitDiag.Mark("ProcessExit (the process is ending)");
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
                    : 1;   // new user: Medium
            Lemora.Utils.Diag.ApplyLogDetail(ConfigurationServiceObj.General.LogDetail);
            // opening line visible at every level: version and log level help read everything else
            SimpleLogHelper.Warning($"Starting {Assert.APP_DISPLAY_NAME} {AppVersion.Version} ({AppVersion.BuildDate}), Windows {Environment.OSVersion.Version}, log level {Lemora.Utils.Diag.LogDetailName(ConfigurationServiceObj.General.LogDetail)}");
            Lemora.Utils.UiHangWatchdog.Start();   // UI-DIAG: temporary diagnosis of freezes
            System.Threading.Tasks.Task.Run(Lemora.Service.FileTransfer.FileTransferShell.CleanStaging);   // temporary files left by a previous transfer
            System.Threading.Tasks.Task.Run(Lemora.Utils.PasswordFile.CleanLeftovers);   // PuTTY password files left by an abrupt exit

            // make sure path is not empty
            if (string.IsNullOrWhiteSpace(ConfigurationServiceObj.LocalDataSource.Path))
            {
                ConfigurationServiceObj.LocalDataSource.Path = AppPathHelper.Instance.SqliteDbDefaultPath;
            }

            ThemeServiceObj = new ThemeService(App.ResourceDictionary!, ConfigurationServiceObj.Theme);
            Lemora.Utils.StartupSplash.UseTheme(ThemeServiceObj.CurrentTheme);   // the loading skeleton in the colors of the theme saved last time
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

                // one database, one Lemora copy: another copy (installed, portable, dev, or another PC on the share) already has it open
                // retry for a few seconds: after an update the old copy starts the new one and is still closing
                var lockedOk = DbInstanceLock.TryAcquire(local.Path, out var owner);
                for (var i = 0; i < 40 && !lockedOk; i++) { System.Threading.Thread.Sleep(250); lockedOk = DbInstanceLock.TryAcquire(local.Path, out owner); }
                if (!lockedOk)
                {
                    SimpleLogHelper.Warning("Database already open in another copy of Lemora: this one closes");
                    if (Lemora.Utils.DevOnly.Env("LEMORA_HEADLESS") != "1")
                        System.Windows.MessageBox.Show(string.Format(IoC.Translate("startup_db_open_elsewhere"), owner.Length > 0 ? $" ({owner})" : "", local.Path),
                            Assert.APP_DISPLAY_NAME, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    Environment.Exit(0);
                }

                // the vault opens before reading the database: without the data key nothing can be read
                try
                {
                    if (!VaultUnlock.Run(local.Path, ConfigurationServiceObj.General.VaultKeepUnlockedMinutes, ConfigurationServiceObj.General.VaultKeepUnlockedMac)) Environment.Exit(0);
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);   // database cannot be opened: InitLocalDataSource below will report it
                }
                StartDiag.Mark("  Init: vault opened");
                _localDataConnectionStatus = dataSourceService.InitLocalDataSource(local);
            }

            // init session controller
            IoC.Get<SessionControlService>();
            IoC.Get<MainWindowViewModel>();
        }


        public static void InitOnLaunch()
        {
            // view and tag settings: read right away from the database (importing the old local files the first time)
            Lemora.Service.Locality.LocalityTagService.Load();
            Lemora.Service.Locality.LocalityTreeViewService.Load();
            Lemora.Service.Locality.LocalityListViewService.Load();
            StartDiag.Mark("  Init: Locality loaded");
            StartupSplash.Pump();
            // the Home asks for user/password of every quick connection: decryption is slow, so it is warmed up in parallel with the rest of startup
            Task.Run(() => { try { foreach (var q in Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryGetAll()) q.GetUserPassword(); } catch { } });
            // client program settings: from the database (importing the old .json files) already at startup
            _ = IoC.Get<ProtocolConfigurationService>();
            StartDiag.Mark("  Init: ProtocolConfigurationService");
            StartupSplash.Pump();
            IoC.Get<GlobalData>().ReloadAll(true);
            StartDiag.Mark("  Init: GlobalData.ReloadAll");
            if (ConfigurationServiceObj?.General.ShowRecentlySessionInTray != false)
                IoC.Get<TaskTrayService>().ReloadTaskTrayContextMenu();
            StartDiag.Mark("  Init: tray menu");
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

            StartDiag.Mark("  Init: old config cleanup");
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
                StartupSplash.ShowReal();   // data ready: the real content replaces the skeleton in the same window
                mvm.ShowMe();
            }
            StartDiag.Mark("  Init: ShowMe");

            MaskLayerController.ShowProcessingRing("", mvm);

            {
                foreach (var ds in ConfigurationServiceObj!.AdditionalDataSource)
                {
                    IoC.Get<DataSourceService>().AddOrUpdateDataSource(ds, connectTimeOutSeconds: 0, doReload: false, fromProfile: true);
                }

                if (_isNewUser == false)
                    IoC.Get<ServerListPageViewModel>().VmServerListDummyNode();

                StartDiag.Mark("  Init: data sources added");
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
