using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lemora.Service;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.View.Settings.General
{
    public class GeneralSettingViewModel : NotifyPropertyChangedBase
    {
        private readonly ConfigurationService _configurationService;
        private readonly LanguageService _languageService;

        public GeneralSettingViewModel(ConfigurationService configurationService, LanguageService languageService)
        {
            _configurationService = configurationService;
            _languageService = languageService;
        }


        // ---- .mora files: repairs the file association if Windows lost it (install and update already set it)
        public string MoraTitle => Lemora.Setup.SetupText.T("mora_set_title");
        public string MoraInfo => Lemora.Setup.SetupText.T("mora_set_desc");
        public string MoraButton => Lemora.Setup.SetupText.T("mora_set_btn");
        public RelayCommand CmdRegisterMora => new RelayCommand(_ =>
        {
            var ok = MoraFile.Register(Environment.ProcessPath ?? "", false);
            ToastService.Show(Lemora.Setup.SetupText.T(ok ? "mora_set_done" : "mora_set_fail"), ok ? ToastKind.Success : ToastKind.Error);
        });

        // ---- Teams: saved connections of this PC (ERRORI 96), same value as the status window (TeamsLink.SharePersonal) ----
        private static Lemora.Setup.TeamsLink TmLink => Lemora.Setup.TeamsLink.Load();
        private static bool TmCanToggle(Lemora.Setup.TeamsLink l) => !l.NoCopy && l.Permission is "write" or "requests";
        /// <summary>section visible only on the paired PC; with read-only (or "do not copy") only the text, without the switch</summary>
        public bool TeamsShareVisible => TmLink.State == "approved"; // approved = Teams, even if installed as Local and paired later from here
        public bool TeamsShareToggleVisible => TeamsShareVisible && TmCanToggle(TmLink);
        /// <summary>read-only: instead of the row with title and paragraph, a single short line (tm_share_ro)</summary>
        public bool TeamsShareRoVisible => TeamsShareVisible && !TmLink.NoCopy && !TmCanToggle(TmLink);
        public bool TeamsShareRowVisible => TeamsShareVisible && !TeamsShareRoVisible;
        public string TeamsShareTitle => Lemora.Setup.SetupText.T(TmCanToggle(TmLink) ? "tm_share_toggle" : "tm_share_lbl");
        public string TeamsShareInfo
        {
            get
            {
                var l = TmLink;
                if (l.NoCopy) return Lemora.Setup.SetupText.T("tm_share_off");
                if (!TmCanToggle(l)) return Lemora.Setup.SetupText.T("tm_share_ro");
                return Lemora.Setup.SetupText.T(l.Permission == "write" ? "tm_share_hint_rw" : "tm_share_hint_req") + "\n" + Lemora.Setup.SetupText.T("tm_share_kept");
            }
        }
        public bool TeamsSharePersonal
        {
            get => TmLink.SharePersonal;
            set { _ = Lemora.Setup.TeamsSync.SetSharePersonalAsync(value); RaisePropertyChanged(); }
        }
        // ---- Teams: entry point to connect an unpaired PC (even one installed as Local); with a pending request it opens the same window to view or cancel it
        public bool TeamsJoinVisible => TmLink.State != "approved";
        private bool TmPending => TmLink.State == "pending";
        public string TeamsJoinTitle => Lemora.Setup.SetupText.T(TmPending ? "tm_join_wait" : "tm_title");
        public string TeamsJoinInfo => TmPending ? string.Format(Lemora.Setup.SetupText.T("tm_join_wait_desc"), TmLink.Server) : Lemora.Setup.SetupText.T("tm_join_desc");
        public string TeamsJoinButton => Lemora.Setup.SetupText.T(TmPending ? "tm_join_open" : "tm_join_btn");
        public RelayCommand CmdTeamsJoin => new RelayCommand(_ => { IoC.Get<Lemora.View.MainWindowView>().OpenTeamsConnect(); RefreshTeams(); });

        /// <summary>when the page opens: the link may have changed (pairing, permission, status window)</summary>
        public void RefreshTeams()
        {
            foreach (var p in new[] { nameof(TeamsShareVisible), nameof(TeamsShareToggleVisible), nameof(TeamsShareRoVisible), nameof(TeamsShareRowVisible), nameof(TeamsShareTitle), nameof(TeamsShareInfo), nameof(TeamsSharePersonal),
                         nameof(TeamsJoinVisible), nameof(TeamsJoinTitle), nameof(TeamsJoinInfo), nameof(TeamsJoinButton) }) RaisePropertyChanged(p);
        }

        public Dictionary<string, string> Languages => _languageService.LanguageCode2Name;
        public string Language
        {
            get => _configurationService.General.CurrentLanguageCode;
            set
            {
                Debug.Assert(Languages.ContainsKey(value));
                if (SetAndNotifyIfChanged(ref _configurationService.General.CurrentLanguageCode, value))
                {
                    // reset lang service
                    _languageService.SetLanguage(value);
                    _configurationService.Save();
                }
            }
        }

        private bool _appStartAutomatically = false;
        public bool AppStartAutomatically
        {
            get => _appStartAutomatically;
            set
            {
                ConfigurationService.SetSelfStart(value);
                _appStartAutomatically = value;
                RaisePropertyChanged();
            }
        }

        public int CloseButtonBehavior
        {
            get => _configurationService.General.CloseButtonBehavior;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.CloseButtonBehavior, value))
                {
                    _configurationService.Save();
                    RaisePropertyChanged(nameof(ShowKeepSessionsOption));
                }
            }
        }

        /// <summary>the "keep sessions alive in background" choice only makes sense if the X sends the app to the background</summary>
        public bool ShowKeepSessionsOption => _configurationService.General.CloseButtonBehavior == (int)GeneralConfig.EnumCloseButtonBehavior.Minimize;

        // ---- file transfer
        private FileTransferConfig Ft => _configurationService.FileTransfer;
        // settings take whole GB; the configuration file keeps MB
        public int FtMaxFileSizeGB { get => (Ft.MaxFileSizeMB + 1023) / 1024; set { var mb = Math.Max(0, value) * 1024; if (Ft.MaxFileSizeMB != mb) { Ft.MaxFileSizeMB = mb; _configurationService.Save(); } RaisePropertyChanged(); } }
        public int FtMaxJobSizeGB { get => (Ft.MaxJobSizeMB + 1023) / 1024; set { var mb = Math.Max(0, value) * 1024; if (Ft.MaxJobSizeMB != mb) { Ft.MaxJobSizeMB = mb; _configurationService.Save(); } RaisePropertyChanged(); } }
        public int FtConflictPolicy { get => Ft.ConflictPolicy; set { if (value >= 0 && Ft.ConflictPolicy != value) { Ft.ConflictPolicy = value; _configurationService.Save(); } RaisePropertyChanged(); } }
        public bool FtUseTemporaryNames { get => Ft.UseTemporaryNames; set { if (Ft.UseTemporaryNames != value) { Ft.UseTemporaryNames = value; _configurationService.Save(); } RaisePropertyChanged(); } }
        public string FtDefaultRemoteDir { get => Ft.DefaultRemoteDir; set { value = (value ?? "").Trim(); if (Ft.DefaultRemoteDir != value) { Ft.DefaultRemoteDir = value; _configurationService.Save(); } RaisePropertyChanged(); } }
        public string FtDefaultLocalFolder { get => Ft.DefaultLocalFolder; set { value = (value ?? "").Trim(); if (Ft.DefaultLocalFolder != value) { Ft.DefaultLocalFolder = value; _configurationService.Save(); } RaisePropertyChanged(); } }
        public RelayCommand CmdBrowseLocalFolder => new RelayCommand(_ =>
        {
            using var fbd = new System.Windows.Forms.FolderBrowserDialog { ShowNewFolderButton = true, SelectedPath = System.IO.Directory.Exists(FtDefaultLocalFolder) ? FtDefaultLocalFolder : "" };
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) FtDefaultLocalFolder = fbd.SelectedPath;
        });
        public bool FtConfirmRemoteDelete { get => Ft.ConfirmRemoteDelete; set { if (Ft.ConfirmRemoteDelete != value) { Ft.ConfirmRemoteDelete = value; _configurationService.Save(); } RaisePropertyChanged(); } }

        public bool SendAltTabToVnc
        {
            get => _configurationService.General.SendAltTabToVnc;
            set { if (SetAndNotifyIfChanged(ref _configurationService.General.SendAltTabToVnc, value)) _configurationService.Save(); }
        }

        public bool KeepSessionsInBackground
        {
            get => _configurationService.General.KeepSessionsInBackground;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.KeepSessionsInBackground, value))
                {
                    _configurationService.Save();
                }
            }
        }

        public bool ConfirmBeforeClosingSession
        {
            get => _configurationService.General.ConfirmBeforeClosingSession;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.ConfirmBeforeClosingSession, value))
                {
                    _configurationService.Save();
                }
            }
        }


        public bool ShowSessionIconInSessionWindow
        {
            get => _configurationService.General.ShowSessionIconInSessionWindow;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.ShowSessionIconInSessionWindow, value))
                {
                    _configurationService.Save();
                }
            }
        }

        public string LogPath => SimpleLogHelper.GetFileFullName();

        /// <summary>0 Basic, 1 Medium, 2 Detailed: it is also the index of the choice on the page</summary>
        public int LogDetail
        {
            get => Math.Max(0, _configurationService.General.LogDetail);
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.LogDetail, value))
                {
                    Lemora.Utils.Diag.ApplyLogDetail(value);
                    SimpleLogHelper.Warning($"Log level changed: {Lemora.Utils.Diag.LogDetailName(value)}");
                    RaisePropertyChanged(nameof(LogDetailDesc));
                    _configurationService.Save();
                }
            }
        }

        public string LogDetailDesc => IoC.Translate(LogDetail switch { 0 => "settings_log_base_desc", 2 => "settings_log_deep_desc", _ => "settings_log_medium_desc" });

        //public bool TabAutoFocusContent
        //{
        //    get => _configurationService.General.TabAutoFocusContent;
        //    set
        //    {
        //        if (SetAndNotifyIfChanged(ref _configurationService.General.TabAutoFocusContent, value))
        //        {
        //            _configurationService.Save();
        //        }
        //    }
        //}

        public bool CopyPortWhenCopyAddress
        {
            get => _configurationService.General.CopyPortWhenCopyAddress;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.CopyPortWhenCopyAddress, value))
                {
                    _configurationService.Save();
                }
            }
        }

        public bool TabWindowCloseButtonOnLeft
        {
            get => _configurationService.General.TabWindowCloseButtonOnLeft;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.TabWindowCloseButtonOnLeft, value))
                {
                    _configurationService.Save();
                }
            }
        }

        public bool TabWindowSetFocusToLocalDesktopOnMouseLeaveRdpWindow
        {
            get => _configurationService.General.TabWindowSetFocusToLocalDesktopOnMouseLeaveRdpWindow;
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.TabWindowSetFocusToLocalDesktopOnMouseLeaveRdpWindow, value))
                {
                    _configurationService.Save();
                }
            }
        }

        private RelayCommand? _cmdExportReadableLog;
        /// <summary>saves the last 7 days of log with the real names instead of the codes (the user picks the file; with verification enabled it asks for it)</summary>
        public RelayCommand CmdExportReadableLog => _cmdExportReadableLog ??= new RelayCommand(async _ =>
        {
            try
            {
                if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;   // the readable log reveals customers, addresses and users
                var path = SelectFileHelper.SaveFile(IoC.Translate("settings_log_readable"), $"{IoC.Translate("settings_log_file_name")}-{DateTime.Now:yyyyMMdd-HHmm}.txt", filter: "txt|*.txt");
                if (string.IsNullOrEmpty(path)) return;
                System.IO.File.WriteAllText(path, Lemora.Utils.LogRedactor.ReadableLog(LogPath, 7), System.Text.Encoding.UTF8);
                SimpleLogHelper.Warning("Readable log exported (last 7 days)");
                SelectFileHelper.OpenInExplorerAndSelect(path);
            }
            catch (Exception e) { UnifyTracing.Error(e); MessageBoxHelper.ErrorAlert(e.Message); }
        });

        private RelayCommand? _cmdExploreTo = null;
        public RelayCommand CmdExploreTo
        {
            get
            {
                return _cmdExploreTo ??= new RelayCommand((o) =>
                {
                    try
                    {
                        SelectFileHelper.OpenInExplorerAndSelect(LogPath);
                    }
                    catch (Exception e)
                    {
                        UnifyTracing.Error(e);
                    }
                });
            }
        }

        // "Keep unlocked": menu choices, in minutes (0 = always ask)
        public static readonly int[] KeepUnlockedChoices = { 0, 15, 30, 60, 120, 240, 480 };
        public int VaultKeepUnlockedIndex
        {
            get => Math.Max(0, Array.IndexOf(KeepUnlockedChoices, _configurationService.General.VaultKeepUnlockedMinutes));
            set
            {
                var minutes = KeepUnlockedChoices[Math.Clamp(value, 0, KeepUnlockedChoices.Length - 1)];
                if (minutes == _configurationService.General.VaultKeepUnlockedMinutes) return;
                _ = ApplyKeepUnlockedAsync(minutes);
            }
        }

        // turning on (or lengthening) "Keep unlocked" loosens protection: with verification enabled, fingerprint, face or PIN is required first
        private async System.Threading.Tasks.Task ApplyKeepUnlockedAsync(int minutes)
        {
            if (minutes > _configurationService.General.VaultKeepUnlockedMinutes && await SecondaryVerificationHelper.VerifyAsyncUi() != true)
            {
                RaisePropertyChanged(nameof(VaultKeepUnlockedIndex));   // the menu goes back to the previous choice
                return;
            }
            _configurationService.General.VaultKeepUnlockedMinutes = minutes;
            VaultUnlock.SetKeepMinutes(minutes);
            _configurationService.General.VaultKeepUnlockedMac = VaultUnlock.CurrentKeepMac;
            _configurationService.Save();
            SimpleLogHelper.Warning($"Keep unlocked: {minutes} minutes");
            RaisePropertyChanged(nameof(VaultKeepUnlockedIndex));
        }

        private RelayCommand? _cmdLockAndClose;
        public RelayCommand CmdLockAndClose => _cmdLockAndClose ??= new RelayCommand((o) => { VaultUnlock.Forget(); App.Close(); });

        // new master password with a new data key: the whole database is re-encrypted, then Lemora closes (values with the old key remain in memory)
        private RelayCommand? _cmdChangeVaultPassword;
        public RelayCommand CmdChangeVaultPassword => _cmdChangeVaultPassword ??= new RelayCommand(async (o) =>
        {
            if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;   // with verification enabled: fingerprint, face or PIN first
            var dss = IoC.Get<Lemora.Service.DataSource.DataSourceService>();
            // other stores (SQLite, MySQL, PostgreSQL) encrypted with the same key: re-encryption only covers the local database, so the change is refused
            var others = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(dss.AdditionalSources.Values, s => s is not Lemora.Service.DataSource.Model.TeamsSource), s => s.DataSourceName));
            if (others.Count > 0) { MessageBoxHelper.ErrorAlert(string.Format(IoC.Translate("vault_change_other_sources"), string.Join(", ", others))); return; }
            if (dss.LocalDataSource is not Lemora.Service.DataSource.Model.SqliteSource local) { MessageBoxHelper.ErrorAlert(IoC.Translate("vault_change_failed")); return; }
            var w = new Lemora.View.Utils.VaultWindow(Lemora.View.Utils.VaultWindow.Mode.Change, false) { Owner = System.Windows.Application.Current.MainWindow };
            if (w.ShowDialog() != true) return;
            var password = w.Password;
            var mask = Lemora.View.Utils.MaskLayerController.ShowProcessingRing(IoC.Translate("vault_change_working"));
            VaultUnlock.ChangeResult r;
            try { r = await System.Threading.Tasks.Task.Run(() => VaultUnlock.ChangePassword(local.Path, password)); }
            finally { Lemora.View.Utils.MaskLayerController.HideMask(mask); }
            if (r.Ok)
            {
                // "Keep unlocked" MAC recomputed with the new key
                _configurationService.General.VaultKeepUnlockedMac = VaultUnlock.CurrentKeepMac;
                _configurationService.Save();
                SimpleLogHelper.Warning($"Vault: master password and data key changed ({r.Rows} rows, {r.Stats.Whole} values, {r.Stats.Inner} inner, {r.Stats.Unreadable} already unreadable; Windows Hello of other PCs removed: {r.HelloRemoved}, of this PC redone: {r.HelloKept})");
                MessageBoxHelper.Info(string.Format(IoC.Translate("vault_change_done_restart"), r.HelloRemoved > 0 ? " " + string.Format(IoC.Translate("vault_change_hello_removed"), r.HelloRemoved) : ""));
                App.Close();   // the key in memory has changed: Lemora must be reopened
            }
            else
            {
                SimpleLogHelper.Warning("Vault: password change failed, nothing changed: " + r.Error);
                MessageBoxHelper.ErrorAlert(IoC.Translate("vault_change_failed") + "\n" + r.Error);
            }
        });
    }
}
