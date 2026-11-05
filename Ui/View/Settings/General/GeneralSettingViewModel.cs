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


        // ---- file .mora: ripara l'associazione se Windows l'ha persa (l'installazione e l'aggiornamento la fanno gia')
        public string MoraTitle => Lemora.Setup.SetupText.T("mora_set_title");
        public string MoraInfo => Lemora.Setup.SetupText.T("mora_set_desc");
        public string MoraButton => Lemora.Setup.SetupText.T("mora_set_btn");
        public RelayCommand CmdRegisterMora => new RelayCommand(_ =>
        {
            var ok = MoraFile.Register(Environment.ProcessPath ?? "", false);
            ToastService.Show(Lemora.Setup.SetupText.T(ok ? "mora_set_done" : "mora_set_fail"), ok ? ToastKind.Success : ToastKind.Error);
        });

        // ---- Teams: connessioni salvate di questo PC (ERRORI 96), stesso valore della finestra di stato (TeamsLink.SharePersonal) ----
        private static Lemora.Setup.TeamsLink TmLink => Lemora.Setup.TeamsLink.Load();
        private static bool TmCanToggle(Lemora.Setup.TeamsLink l) => !l.NoCopy && l.Permission is "write" or "requests";
        /// <summary>sezione visibile solo sul PC abbinato; con sola lettura (o "non copiare") solo il testo, senza interruttore</summary>
        public bool TeamsShareVisible => TmLink.State == "approved"; // approvato = Teams, anche se installato Locale e abbinato dopo da qui
        public bool TeamsShareToggleVisible => TeamsShareVisible && TmCanToggle(TmLink);
        /// <summary>sola lettura: al posto della riga con titolo e paragrafo, una sola riga breve (tm_share_ro)</summary>
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
        // ---- Teams: punto di accesso per collegare un PC non abbinato (anche installato in modo Locale); con richiesta in attesa apre la stessa finestra per vederla o annullarla
        public bool TeamsJoinVisible => TmLink.State != "approved";
        private bool TmPending => TmLink.State == "pending";
        public string TeamsJoinTitle => Lemora.Setup.SetupText.T(TmPending ? "tm_join_wait" : "tm_title");
        public string TeamsJoinInfo => TmPending ? string.Format(Lemora.Setup.SetupText.T("tm_join_wait_desc"), TmLink.Server) : Lemora.Setup.SetupText.T("tm_join_desc");
        public string TeamsJoinButton => Lemora.Setup.SetupText.T(TmPending ? "tm_join_open" : "tm_join_btn");
        public RelayCommand CmdTeamsJoin => new RelayCommand(_ => { IoC.Get<Lemora.View.MainWindowView>().OpenTeamsConnect(); RefreshTeams(); });

        /// <summary>all'apertura della pagina: il collegamento puo' essere cambiato (abbinamento, permesso, finestra di stato)</summary>
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

        /// <summary>la scelta "sessioni attive in background" ha senso solo se la X manda l'app in background</summary>
        public bool ShowKeepSessionsOption => _configurationService.General.CloseButtonBehavior == (int)GeneralConfig.EnumCloseButtonBehavior.Minimize;

        // ---- trasferimento file
        private FileTransferConfig Ft => _configurationService.FileTransfer;
        // in impostazioni si scrivono GB interi; nel file di configurazione resta in MB
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

        /// <summary>0 Base, 1 Medio, 2 Approfondito: e' anche l'indice della scelta nella pagina</summary>
        public int LogDetail
        {
            get => Math.Max(0, _configurationService.General.LogDetail);
            set
            {
                if (SetAndNotifyIfChanged(ref _configurationService.General.LogDetail, value))
                {
                    Lemora.Utils.Diag.ApplyLogDetail(value);
                    SimpleLogHelper.Warning($"Livello del log cambiato: {Lemora.Utils.Diag.LogDetailName(value)}");
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

        // "Mantieni sbloccata": scelte del menu, in minuti (0 = chiedi sempre)
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

        // accendere (o allungare) "Mantieni sbloccata" allenta la protezione: con la verifica attiva serve prima impronta, volto o PIN
        private async System.Threading.Tasks.Task ApplyKeepUnlockedAsync(int minutes)
        {
            if (minutes > _configurationService.General.VaultKeepUnlockedMinutes && await SecondaryVerificationHelper.VerifyAsyncUi() != true)
            {
                RaisePropertyChanged(nameof(VaultKeepUnlockedIndex));   // il menu torna alla scelta di prima
                return;
            }
            _configurationService.General.VaultKeepUnlockedMinutes = minutes;
            _configurationService.Save();
            VaultUnlock.SetKeepMinutes(minutes);
            SimpleLogHelper.Warning($"Mantieni sbloccata: {minutes} minuti");
            RaisePropertyChanged(nameof(VaultKeepUnlockedIndex));
        }

        private RelayCommand? _cmdLockAndClose;
        public RelayCommand CmdLockAndClose => _cmdLockAndClose ??= new RelayCommand((o) => { VaultUnlock.Forget(); App.Close(); });

        // nuova password principale: stessa chiave dei dati, i lucchetti Windows Hello restano validi
        private RelayCommand? _cmdChangeVaultPassword;
        public RelayCommand CmdChangeVaultPassword => _cmdChangeVaultPassword ??= new RelayCommand(async (o) =>
        {
            if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;   // con la verifica attiva: prima impronta, volto o PIN
            var w = new Lemora.View.Utils.VaultWindow(Lemora.View.Utils.VaultWindow.Mode.Change, false) { Owner = System.Windows.Application.Current.MainWindow };
            if (w.ShowDialog() != true) return;
            var db = IoC.Get<Lemora.Service.DataSource.DataSourceService>().LocalDataSource?.GetDataBase();
            if (db != null && VaultUnlock.ChangePassword(db, w.Password))
            {
                SimpleLogHelper.Warning("Cassaforte: password principale cambiata");
                MessageBoxHelper.Info(IoC.Translate("vault_change_done"));
            }
            else MessageBoxHelper.ErrorAlert(IoC.Translate("vault_change_failed"));
        });
    }
}
