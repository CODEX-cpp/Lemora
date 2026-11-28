using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Shawn.Utils.WpfResources.Theme.Styles;
using static Lemora.Setup.SetupText;

namespace Lemora.Setup
{
    internal enum SetupOutcome { None, Portable, InstallStarted }

    /// <summary>parameters of the process (with administrator rights) that runs the uninstall</summary>
    internal class UninstallRun
    {
        public string Dir = "";
        public bool Purge;
        public SetupMode Mode = SetupMode.Local;
    }

    /// <summary>
    /// Mode choice, options, progress and end of the installation, plus the uninstall (blueprint installer).
    /// Without parameters it is the first-start choice; with <c>runOpts</c> (process with administrator rights) the installation starts right away.
    /// With <c>uninstall</c> it is the uninstall: first the confirmation, then (with <c>unRun</c>, process with administrator rights) the progress.
    /// </summary>
    internal partial class SetupWindow : WindowBase
    {
        private const int PgMode = 0, PgOptions = 1, PgProgress = 2, PgDone = 3, PgConfirm = 4, PgMaintain = 5, PgTmConnect = 6, PgTmWait = 7, PgTmOk = 8, PgTmNo = 9;
        private DateTime _tmSent = DateTime.Now;

        public SetupOutcome Outcome { get; private set; } = SetupOutcome.None;

        private int _page;
        private bool _busy;
        private bool _failed;
        private bool _uninstall;
        private bool _cleanupOnClose;
        private string _unDir = "";
        private SetupMode _unMode = SetupMode.Local;
        private string _launchTarget = "";

        private bool _appMode; // opened from the already installed app: the link state is saved in the database

        public SetupWindow(SetupOptions? runOpts = null, UninstallRun? unRun = null, bool uninstall = false, bool maintenance = false, bool teams = false)
        {
            InitializeComponent();
            SetColors();
            SetTexts();
            RbPurge.Checked += (_, _) => NotePurge.Visibility = Visibility.Visible;
            RbPurge.Unchecked += (_, _) => NotePurge.Visibility = Visibility.Collapsed;
            Closed += (_, _) => { if (_cleanupOnClose) SetupEngine.ScheduleSelfCleanup(); };

            if (teams)
            {
                _appMode = true;
                var link = TeamsLink.Load();
                TxtServer.Text = link.Server;
                _tmSent = link.SentAt;
                _tmFp = link.Fingerprint;
                EnterTeams();
                if (link.State == "pending") ShowTeamsState(PgTmWait);
                else if (link.State == "approved") ShowTeamsState(PgTmOk);
                else if (link.State == "rejected") ShowTeamsState(PgTmNo);
            }
            else if (maintenance)
            {
                EnterMaintenance();
            }
            else if (uninstall || unRun != null)
            {
                if (unRun != null)
                {
                    EnterUninstall(unRun.Mode, unRun.Dir);
                    _cleanupOnClose = true;
                    Loaded += (_, _) => RunUninstall(unRun);
                    ShowPage(PgProgress);
                }
                else
                {
                    // the installation folder, not versions\<version> where the app runs: the launcher and everything to remove are there
                    EnterUninstall(SetupEngine.InstalledMode(), SetupEngine.ThisRoot);
                    ShowPage(PgConfirm);
                }
            }
            else if (runOpts != null)
            {
                Loaded += (_, _) => RunInstall(runOpts);
                ShowPage(PgProgress);
            }
            else
            {
                ShowPage(PgMode);
            }
        }

        /// <summary>only for appearance tests (DevDemo): brings the window to a page without installing anything</summary>
        internal void DemoShow(string what)
        {
            switch (what)
            {
                case "options": ShowPage(PgOptions); break;
                case "teams": RbTeams.IsChecked = true; ShowPage(PgOptions); break;
                case "progress": ShowPage(PgProgress); UpdateProgress(2); break;
                case "done": Finish(null, new SetupOptions()); break;
                case "error": Finish(new InvalidOperationException("Access denied: C:\\Program Files\\Lemora\\Lemora.exe"), new SetupOptions()); break;
                case "portable": RbPortable.IsChecked = true; break;
                case "maintain": EnterMaintenance(); break;
                case "tmconnect": TxtServer.Text = "192.168.1.10:25353"; TxtCode.Text = "4821-7730"; EnterTeams(); break;
                case "tmwait": TxtServer.Text = "192.168.1.10:25353"; _tmFp = "A0883F0CF2A2FEE4FA658713 1B5DE692BCA62B8DAF36468F9478FDEBAA04CB84".Replace(" ", ""); EnterTeams(); ShowTeamsState(PgTmWait); break;
                case "tmok": TxtServer.Text = "192.168.1.10:25353"; EnterTeams(); ShowTeamsState(PgTmOk); break;
                case "tmno": TxtServer.Text = "192.168.1.10:25353"; EnterTeams(); ShowTeamsState(PgTmNo); break;
                case "unlocal": EnterUninstall(SetupMode.Local, SetupEngine.DefaultDir); ShowPage(PgConfirm); break;
                case "unteams": EnterUninstall(SetupMode.Teams, SetupEngine.DefaultDir); ShowPage(PgConfirm); break;
                case "unpurge": EnterUninstall(SetupMode.Teams, SetupEngine.DefaultDir); RbPurge.IsChecked = true; ShowPage(PgConfirm); break;
                case "unprogress": EnterUninstall(SetupMode.Teams, SetupEngine.DefaultDir); SetUninstallProgressTexts(false); ShowPage(PgProgress); UpdateProgress(1); break;
                case "undone": EnterUninstall(SetupMode.Local, SetupEngine.DefaultDir); FinishUninstall(null, false); break;
                default: ShowPage(PgMode); break;
            }
        }

        /// <summary>green / yellow / red readable on both the dark and the light theme</summary>
        private void SetColors()
        {
            bool dark = true;
            if (Application.Current.TryFindResource("BackgroundBrush") is SolidColorBrush bg)
                dark = (bg.Color.R * 0.299 + bg.Color.G * 0.587 + bg.Color.B * 0.114) < 128;
            SolidColorBrush B(string c) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return b; }
            // the installer always has its own light blue (blueprint), whatever the app theme: on first start the default theme, red, is still active
            void Accent(string name, string c) { var col = (Color)ColorConverter.ConvertFromString(c); Resources[name + "Color"] = col; Resources[name + "Brush"] = B(c); }
            string accent = dark ? "#60CDFF" : "#005FB8";
            Accent("AccentMid", accent);
            Accent("AccentLight", dark ? "#8EDCFF" : "#2B7BC9");
            Accent("AccentText", dark ? "#00283B" : "#FFFFFF");
            Resources["IslandSelectedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent)) { Opacity = 0.17 };
            Resources["WarnBrush"] = dark ? B("#FDD663") : B("#9D5D00");
            Resources["OkBrush"] = dark ? B("#6CCB5F") : B("#0F7B0F");
            Resources["ErrBrush"] = dark ? B("#FF9B94") : B("#C42B1C");
        }

        private void SetTexts()
        {
            TxtTitle.Text = T("title");
            TxtModeTitle.Text = T("mode_title");
            TxtModeSub.Text = T("mode_sub");
            TxtPortable.Text = T("portable");
            TxtPortableDesc.Text = T("portable_desc");
            TxtPortablePerm.Text = T("no_admin");
            TxtLocal.Text = T("local");
            TxtLocalDesc.Text = T("local_desc");
            TxtLocalPerm.Text = T("need_admin");
            TxtTeams.Text = T("teams");
            TxtTeamsDesc.Text = T("teams_desc");
            TxtTeamsPerm.Text = T("need_admin");
            TxtFolder.Text = T("folder");
            TxtDir.Text = SetupEngine.DefaultDir;
            BtnBrowse.Content = T("browse");
            CbDesktop.Content = T("opt_desktop");
            CbStart.Content = T("opt_start");
            CbAuto.Content = T("opt_auto");
            TxtAutoHint.Text = T("opt_auto_hint");
            CbBrowserExt.Content = T("opt_browserext");
            TxtBrowserExtHint.Text = T("opt_browserext_hint");
            TxtProgTitle.Text = T("progress_title");
            St0.Text = T("step_copy");
            St1.Text = T("step_links");
            St2.Text = T("step_reg");
            St3.Text = T("step_data");
            CbLaunch.Content = T("done_launch");
            BtnBack.Content = T("back");
            BtnCancel.Content = T("cancel");
        }

        /// <summary>exe launched with the app already installed: choice between uninstalling, repairing or using this file as portable</summary>
        private void EnterMaintenance()
        {
            TxtMtTitle.Text = T("mt_title");
            TxtMtSub.Text = string.Format(T("mt_sub"), SetupEngine.InstalledLocation() ?? SetupEngine.DefaultDir);
            TxtMtUninstall.Text = T("mt_uninstall");
            TxtMtUninstallDesc.Text = T("mt_uninstall_desc");
            TxtMtRepair.Text = T("mt_repair");
            TxtMtRepairDesc.Text = T("mt_repair_desc");
            TxtMtPortable.Text = T("mt_portable");
            TxtMtPortableDesc.Text = T("mt_portable_desc");
            ShowPage(PgMaintain);
        }

        private void Maintain_OnChecked(object sender, RoutedEventArgs e)
        {
            if (TxtFooterNote == null || BtnNext == null) return; // while the XAML is loading
            if (_page == PgMaintain) ShowPage(PgMaintain);
        }

        /// <summary>switches the window to uninstall mode: texts, steps and Teams notice</summary>
        private void EnterUninstall(SetupMode mode, string dir)
        {
            _uninstall = true;
            _unMode = mode;
            _unDir = dir;
            TxtTitle.Text = T("un_title");
            TxtUnTitle.Text = T("un_q");
            TxtUnSub.Text = T("un_sub");
            TxtKeep.Text = T("un_keep");
            TxtKeepDesc.Text = T("un_keep_desc");
            TxtPurge.Text = T("un_purge");
            TxtPurgeDesc.Text = T("un_purge_desc");
            TxtNotePurge.Text = T("un_note_purge");
            TxtNoteTeams.Text = T("un_note_teams");
            NoteTeams.Visibility = mode == SetupMode.Teams ? Visibility.Visible : Visibility.Collapsed;
            SetUninstallProgressTexts(RbPurge.IsChecked == true);
        }

        private void SetUninstallProgressTexts(bool purge)
        {
            TxtProgTitle.Text = T("un_progress");
            St0.Text = T("un_s_close");
            St1.Text = T("un_s_disc");
            St2.Text = T("un_s_files");
            St3.Text = T("un_s_reg");
            St4.Text = T(purge ? "un_s_data_del" : "un_s_data_keep");
            Row4.Visibility = Visibility.Visible;
            Row1.Visibility = _unMode == SetupMode.Teams ? Visibility.Visible : Visibility.Collapsed; // disconnecting from the server only exists with Teams
        }

        private bool TeamsSelected => RbTeams.IsChecked == true;
        private bool PortableSelected => RbPortable.IsChecked == true;

        private void ShowPage(int page)
        {
            _page = page;
            PageMode.Visibility = page == PgMode ? Visibility.Visible : Visibility.Collapsed;
            PageOptions.Visibility = page == PgOptions ? Visibility.Visible : Visibility.Collapsed;
            PageProgress.Visibility = page == PgProgress ? Visibility.Visible : Visibility.Collapsed;
            PageDone.Visibility = page == PgDone ? Visibility.Visible : Visibility.Collapsed;
            PageConfirm.Visibility = page == PgConfirm ? Visibility.Visible : Visibility.Collapsed;
            PageMaintain.Visibility = page == PgMaintain ? Visibility.Visible : Visibility.Collapsed;
            PageTmConnect.Visibility = page == PgTmConnect ? Visibility.Visible : Visibility.Collapsed;
            PageTmState.Visibility = page >= PgTmWait ? Visibility.Visible : Visibility.Collapsed;

            BtnBack.Visibility = page == PgOptions || page == PgTmConnect || page == PgTmWait || page == PgTmNo ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Visibility = page == PgMode || page == PgConfirm || page == PgMaintain ? Visibility.Visible : Visibility.Collapsed;
            BtnNext.Visibility = page == PgProgress ? Visibility.Collapsed : Visibility.Visible;
            BtnNext.Style = (Style)FindResource(page == PgConfirm ? "BtnDanger" : "BtnPrimary");

            switch (page)
            {
                case PgMode:
                    BtnNext.Content = PortableSelected ? T("start") : T("next");
                    SetFooterNote(PortableSelected ? null : T("need_admin"));
                    break;
                case PgOptions:
                    TxtOptTitle.Text = TeamsSelected ? T("opt_title_teams") : T("opt_title_local");
                    TxtOptSub.Text = T("opt_sub");
                    TxtOptNote.Text = TeamsSelected ? T("opt_note_teams") : T("opt_note_local");
                    BtnBack.Content = T("back");
                    BtnNext.Content = T("install");
                    SetFooterNote(SetupEngine.IsAdmin() ? null : T("uac_hint"));
                    break;
                case PgConfirm:
                    BtnNext.Content = T("un_btn");
                    // the Windows prompt appears only when needed (it does not appear with an account without UAC or already elevated)
                    SetFooterNote(SetupEngine.IsAdmin() ? null : T("uac_hint"));
                    break;
                case PgMaintain:
                    BtnNext.Content = T("next");
                    SetFooterNote(RbMtPortable.IsChecked == true || SetupEngine.IsAdmin() ? null : T("uac_hint"));
                    break;
                case PgTmConnect:
                    BtnBack.Content = T("tm_skip"); BtnNext.Content = T("tm_send"); SetFooterNote(null);
                    break;
                case PgTmWait:
                    BtnBack.Content = T("tm_cancel_req"); BtnNext.Content = T("tm_continue"); SetFooterNote(null);
                    break;
                case PgTmOk:
                    BtnNext.Content = T("tm_open"); SetFooterNote(null);
                    break;
                case PgTmNo:
                    BtnBack.Content = T("tm_local"); BtnNext.Content = T("tm_retry"); SetFooterNote(null);
                    break;
                case PgProgress:
                    SetFooterNote(null);
                    break;
                case PgDone:
                    BtnNext.Content = _failed || _uninstall ? T("close") : T("finish");
                    SetFooterNote(null);
                    break;
            }
        }

        private void SetFooterNote(string? text)
        {
            FooterNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            TxtFooterNote.Text = text ?? "";
            TxtFooterNote.SetResourceReference(ForegroundProperty, "WarnBrush");
        }

        private void Mode_OnChecked(object sender, RoutedEventArgs e)
        {
            if (TxtFooterNote == null || BtnNext == null) return; // while the XAML is loading
            if (_page == PgMode) ShowPage(PgMode);
        }

        private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) DragMove();
        }

        private void BtnX_OnClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Close();
        }

        private void BtnBack_OnClick(object sender, RoutedEventArgs e)
        {
            switch (_page)
            {
                case PgTmConnect: SaveLink("none", skipped: true); LaunchAndClose(); break;   // skip for now
                case PgTmWait: { var l = TeamsLink.Load(); SaveLink("none"); _ = TeamsLinkService.CancelAsync(l); ShowPage(PgTmConnect); break; } // cancel request (on the server too)
                case PgTmNo: SaveLink("none", skipped: true); LaunchAndClose(); break;        // continue locally
                default: ShowPage(PgMode); break;
            }
        }

        // ---- Teams: client screens. For now without a server: the request is not sent, only the flow is shown ----

        private void EnterTeams()
        {
            TxtTitle.Text = T("tm_wintitle");
            TxtTmTitle.Text = T("tm_title");
            TxtTmSub.Text = T("tm_sub");
            TxtTmAddr.Text = T("tm_addr");
            TxtTmCode.Text = T("tm_code");
            TxtTmInfo.Text = T("tm_info");
            TxtTmWarn.Text = T("tm_warn");
            ShowPage(PgTmConnect);
        }

        private string _tmFp = ""; // server certificate fingerprint, seen on the first connection
        private System.Windows.Threading.DispatcherTimer? _tmTimer;

        private async void SendTeamsRequest()
        {
            var server = TxtServer.Text.Trim();
            var code = TxtCode.Text.Trim();
            if (server.Length == 0 || code.Length == 0)
            {
                SetFooterNote(T("tm_need"));
                return;
            }
            BtnNext.IsEnabled = false; BtnBack.IsEnabled = false;
            // once paired, the PC sends all saved passwords to the server: same verification as when copying one (security hardening)
            if (_appMode && await Lemora.Service.SecondaryVerificationHelper.VerifyAsyncUi() != true)
            {
                BtnNext.IsEnabled = true; BtnBack.IsEnabled = true;
                return;
            }
            SetFooterNote(T("tm_sending"));
            try
            {
                using var client = new TeamsServerClient(server);
                var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";
                var req = await client.RequestAsync(code, Environment.MachineName, Environment.UserName, version);
                _tmSent = DateTime.Now;
                _tmFp = req.Fingerprint;
                SaveLink("pending", req: req);
                SetFooterNote(null);
                ShowTeamsState(PgTmWait);
            }
            catch (TeamsException ex)
            {
                SetFooterNote(TeamsErrorText(ex));
            }
            catch (Exception ex)
            {
                Shawn.Utils.SimpleLogHelper.Warning("Teams: pairing request failed: " + ex.Message);
                SetFooterNote(T("tm_err_server"));
            }
            finally
            {
                BtnNext.IsEnabled = true; BtnBack.IsEnabled = true;
            }
        }

        private string TeamsErrorText(TeamsException ex) => ex.Kind switch
        {
            "address" => T("tm_err_address"),
            "unreachable" => string.Format(T("tm_err_unreachable"), TxtServer.Text.Trim(), TeamsServerClient.DefaultPort),
            "certificate" => T("tm_err_certificate"),
            "code" => string.Format(T("tm_err_code"), ex.Remaining),
            "blocked" => string.Format(T("tm_err_blocked"), ex.Until?.ToString("HH:mm") ?? ""),
            "input" => T("tm_err_input"),
            _ => T("tm_err_server"),
        };

        private void SaveLink(string state, bool skipped = false, TeamsServerClient.PairRequest? req = null)
        {
            if (!_appMode) return;
            new TeamsLink { State = state, Server = TxtServer.Text.Trim(), SentAt = _tmSent, Skipped = skipped, Id = req?.Id ?? "", Token = req?.Token ?? "", Fingerprint = req?.Fingerprint ?? "" }.Save();
        }

        /// <summary>on the waiting page the server is checked every few seconds: as soon as the administrator decides, the page changes by itself</summary>
        private void WatchTeamsApproval(bool on)
        {
            if (!on || !_appMode)
            {
                _tmTimer?.Stop();
                return;
            }
            if (_tmTimer == null)
            {
                bool busy = false;
                _tmTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _tmTimer.Tick += async (_, _) =>
                {
                    if (busy || _page != PgTmWait) return;
                    busy = true;
                    try
                    {
                        if (!await TeamsLinkService.PollAsync()) return;
                        var l = TeamsLink.Load();
                        if (l.State == "approved") ShowTeamsState(PgTmOk);
                        else if (l.State is "rejected" or "revoked") ShowTeamsState(PgTmNo);
                    }
                    finally { busy = false; }
                };
                Closed += (_, _) => _tmTimer?.Stop();
            }
            _tmTimer.Start();
        }

        private void ShowTeamsState(int page)
        {
            string srv = TxtServer.Text.Trim().Length > 0 ? TxtServer.Text.Trim() : "192.168.1.10:25353";
            string at = _tmSent.ToString("HH:mm");
            TmRows.Children.Clear();
            void Row((string, string) r, bool small = false)
            {
                if (TmRows.Children.Count > 0) TmRows.Children.Add(new System.Windows.Controls.Border { Height = 1, Background = (Brush)FindResource("LineBrush") });
                var g = new System.Windows.Controls.Grid { MinHeight = small ? 60 : 38 }; // 38: with the toggle and wrapped texts the "PC approved" page fits in the window without cutting the icon
                g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(240) });
                g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var l = new System.Windows.Controls.TextBlock { Text = r.Item1, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("SecondaryBrush"), FontSize = 14 };
                // the fingerprint is long: small monospaced font with wrapping, so it can be read and compared with the one in the server console
                var v = new System.Windows.Controls.TextBlock { Text = r.Item2, VerticalAlignment = VerticalAlignment.Center, FontSize = small ? 12 : 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 12, 6) };
                if (small) v.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                v.SetResourceReference(ForegroundProperty, "BackgroundTextBrush");
                System.Windows.Controls.Grid.SetColumn(v, 1);
                g.Children.Add(l); g.Children.Add(v);
                TmRows.Children.Add(g);
            }
            string iconBrush, glyph;
            switch (page)
            {
                case PgTmWait:
                    iconBrush = "AccentMidBrush"; glyph = "i";
                    TxtTmStTitle.Text = T("tm_wait_title"); TxtTmStText.Text = T("tm_wait_text");
                    Row((T("tm_pc"), Environment.MachineName)); Row((T("tm_server"), srv)); Row((T("tm_sent"), string.Format(T("tm_today"), at)));
                    if (_tmFp.Length > 0) Row((T("tm_fp"), string.Join(":", Enumerable.Range(0, _tmFp.Length / 2).Select(i => _tmFp.Substring(i * 2, 2)))), small: true);
                    break;
                case PgTmOk:
                    iconBrush = "OkBrush"; glyph = "✓";
                    TxtTmStTitle.Text = T("tm_ok_title"); TxtTmStText.Text = T("tm_ok_text");
                    var perm = _appMode ? TeamsLink.Load().Permission : "write"; // outside the app (preview) the example with write access is shown
                    if (perm is "write" or "requests") Row((T("tm_ok_recv"), _appMode ? TeamsSync.ReceivedText() : T("tm_ok_recv_v"))); // read only: no rows about connections (only indication in Options)
                    Row((T("tm_ok_perm"), T(perm == "write" ? "tm_ok_perm_rw" : perm == "requests" ? "tm_ok_perm_req" : "tm_ok_perm_ro")));
                    Row((T("tm_ok_sync"), T("tm_ok_sync_v"))); Row((T("tm_ok_lock"), T("tm_ok_lock_v")));
                    if (_appMode) SharePersonalRow(TeamsLink.Load(), Row);
                    break;
                default:
                    iconBrush = "ErrBrush"; glyph = "✕";
                    TxtTmStTitle.Text = T("tm_no_title"); TxtTmStText.Text = T("tm_no_text");
                    Row((T("tm_server"), srv)); Row((T("tm_outcome"), string.Format(T("tm_no_at"), at)));
                    break;
            }
            TmIcon.SetResourceReference(BackgroundProperty, iconBrush);
            TmGlyph.Text = glyph;
            if (page == PgTmWait) TmGlyph.SetResourceReference(ForegroundProperty, "AccentTextBrush"); else TmGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
            ShowPage(page);
            WatchTeamsApproval(page == PgTmWait);
        }

        /// <summary>
        /// this PC's saved connections towards the server (TEAMS-DESIGN.md section 3): with requests/write the toggle (on by default),
        /// with read only nothing (the row is in Options), with "do not copy" a note that the server does not copy them. Changing it syncs right away.
        /// </summary>
        private void SharePersonalRow(TeamsLink link, Action<(string, string), bool> row)
        {
            if (link.NoCopy) { row((T("tm_share_lbl"), T("tm_share_off")), false); return; }
            if (link.Permission is not ("write" or "requests")) return; // read only: the only indication is in Options > General > Teams
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = new System.Windows.Controls.TextBlock { Text = T("tm_share_toggle") + "\n" + T(link.Permission == "write" ? "tm_share_hint_rw" : "tm_share_hint_req") + "\n" + T("tm_share_kept"), TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                IsChecked = link.SharePersonal, Margin = new Thickness(20, 6, 12, 6), VerticalContentAlignment = VerticalAlignment.Top,
            };
            cb.SetResourceReference(ForegroundProperty, "BackgroundTextBrush");
            System.Windows.Automation.AutomationProperties.SetAutomationId(cb, "TeamsSharePersonal");
            cb.Click += async (_, _) => await TeamsSync.SetSharePersonalAsync(cb.IsChecked == true);
            TmRows.Children.Add(new System.Windows.Controls.Border { Height = 1, Background = (Brush)FindResource("LineBrush") });
            TmRows.Children.Add(cb);
        }

        private void LaunchAndClose()
        {
            if (_launchTarget.Length > 0)
            {
                try
                {
                    // explorer launches the app with the user's normal rights, not the administrator rights of this process
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_launchTarget}\"") { UseShellExecute = true });
                }
                catch { /* the app is installed: it opens from the Start menu */ }
            }
            Close();
        }

        private void BtnBrowse_OnClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(TxtDir.Text) ? TxtDir.Text : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            if (dlg.ShowDialog(this) != true) return;
            var dir = dlg.FolderName.TrimEnd('\\');
            // the app always goes into a folder of its own: the uninstall deletes it entirely
            TxtDir.Text = SetupEngine.IsOurFolderName(dir) ? dir : Path.Combine(dir, "Lemora");
        }

        private void BtnNext_OnClick(object sender, RoutedEventArgs e)
        {
            switch (_page)
            {
                case PgMode:
                    if (PortableSelected)
                    {
                        Outcome = SetupOutcome.Portable;
                        Close();
                    }
                    else
                    {
                        ShowPage(PgOptions);
                    }
                    break;
                case PgOptions:
                    StartInstallFromOptions();
                    break;
                case PgConfirm:
                    StartUninstallFromConfirm();
                    break;
                case PgMaintain:
                    if (RbMtPortable.IsChecked == true)
                    {
                        Outcome = SetupOutcome.Portable;
                        Close();
                    }
                    else if (RbMtUninstall.IsChecked == true)
                    {
                        EnterUninstall(SetupEngine.InstalledMode(), SetupEngine.InstalledLocation() ?? SetupEngine.DefaultDir);
                        ShowPage(PgConfirm);
                    }
                    else
                    {
                        RepairInstalled();
                    }
                    break;
                case PgTmConnect:
                    SendTeamsRequest();
                    break;
                case PgTmWait:
                case PgTmOk:
                    LaunchAndClose();
                    break;
                case PgTmNo:
                    SaveLink("none");
                    ShowPage(PgTmConnect);
                    break;
                case PgDone:
                    if (_failed == false && _uninstall == false && CbLaunch.IsChecked == true)
                    {
                        try
                        {
                            // explorer launches the app with the user's normal rights, not the administrator rights of this process
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_launchTarget}\"") { UseShellExecute = true });
                        }
                        catch { /* the app is installed: it opens from the Start menu */ }
                    }
                    Close();
                    break;
            }
        }

        private void StartInstallFromOptions()
        {
            var dir = TxtDir.Text.Trim().TrimEnd('\\');
            if (dir.Length < 4 || Path.IsPathRooted(dir) == false)
            {
                SetFooterNote(T("folder"));
                return;
            }
            var o = new SetupOptions
            {
                Dir = dir,
                Mode = TeamsSelected ? SetupMode.Teams : SetupMode.Local,
                Desktop = CbDesktop.IsChecked == true,
                StartMenu = CbStart.IsChecked == true,
                AutoStart = CbAuto.IsChecked == true,
                BrowserExtension = CbBrowserExt.IsChecked == true,
            };
            if (SetupEngine.IsAdmin())
            {
                RunInstall(o);
            }
            else if (SetupEngine.RelaunchElevated(o.ToArguments()))
            {
                Outcome = SetupOutcome.InstallStarted;
                Close();
            }
            else
            {
                SetFooterNote(T("uac_refused"));
            }
        }

        /// <summary>repair/update: reinstalls with the same choices as the current installation, using this exe</summary>
        private void RepairInstalled()
        {
            var o = SetupEngine.CurrentInstallOptions();
            if (SetupEngine.IsAdmin())
            {
                RunInstall(o);
            }
            else if (SetupEngine.RelaunchElevated(o.ToArguments()))
            {
                Outcome = SetupOutcome.InstallStarted;
                Close();
            }
            else
            {
                SetFooterNote(T("uac_refused"));
            }
        }

        private void StartUninstallFromConfirm()
        {
            bool purge = RbPurge.IsChecked == true;
            bool started;
            try { started = SetupEngine.StartUninstallElevated(_unDir, purge, _unMode); }
            catch (Exception ex) { SetFooterNote(ex.Message); return; }
            if (started)
            {
                Outcome = SetupOutcome.InstallStarted;
                Close();
            }
            else
            {
                SetFooterNote(T("uac_refused"));
            }
        }

        private void RunInstall(SetupOptions o)
        {
            _busy = true;
            _failed = false;
            _launchTarget = Path.Combine(o.Dir, SetupEngine.ExeName);
            ShowPage(PgProgress);
            UpdateProgress(0);
            Task.Run(() =>
            {
                try
                {
                    SetupEngine.Install(o, step =>
                    {
                        Dispatcher.Invoke(() => UpdateProgress(step));
                        System.Threading.Thread.Sleep(280); // the steps are fast: this way the progress is readable
                    });
                    Dispatcher.Invoke(() => Finish(null, o));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Finish(ex, o));
                }
            });
        }

        private void RunUninstall(UninstallRun run)
        {
            _busy = true;
            _failed = false;
            SetUninstallProgressTexts(run.Purge);
            ShowPage(PgProgress);
            UpdateProgress(0);
            Task.Run(() =>
            {
                try
                {
                    SetupEngine.Uninstall(run.Dir, run.Purge, step =>
                    {
                        Dispatcher.Invoke(() => UpdateProgress(step));
                        System.Threading.Thread.Sleep(280);
                    });
                    Dispatcher.Invoke(() => FinishUninstall(null, run.Purge));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => FinishUninstall(ex, run.Purge));
                }
            });
        }

        private void UpdateProgress(int step)
        {
            var icons = new[] { Ic0, Ic1, Ic2, Ic3, Ic4 };
            var texts = new[] { St0, St1, St2, St3, St4 };
            int rows = _uninstall ? 5 : 4;
            Bar.Value = 100.0 * step / rows;
            TxtProgPct.Text = $"{(int)Bar.Value}%";
            string[] now = _uninstall
                ? new[] { T("un_now_close"), T("un_now_disc"), T("un_now_files"), T("un_now_reg"), T("un_now_data"), T("un_now_data") }
                : new[] { T("step_copy_now"), T("step_links_now"), T("step_reg_now"), T("step_data_now"), T("step_data_now") };
            TxtProgNow.Text = now[Math.Min(step, now.Length - 1)];
            for (int i = 0; i < rows; i++)
            {
                if (i < step) { icons[i].Text = "\u2713"; icons[i].SetResourceReference(ForegroundProperty, "OkBrush"); texts[i].SetResourceReference(ForegroundProperty, "BackgroundTextBrush"); }
                else if (i == step) { icons[i].Text = "\u25CF"; icons[i].SetResourceReference(ForegroundProperty, "AccentMidBrush"); texts[i].SetResourceReference(ForegroundProperty, "BackgroundTextBrush"); }
                else { icons[i].Text = "\u25CB"; icons[i].Foreground = (Brush)FindResource("SecondaryBrush"); texts[i].Foreground = (Brush)FindResource("SecondaryBrush"); }
            }
        }

        private void ShowResult(bool ok, string title, string detail)
        {
            TxtDoneTitle.Text = title;
            TxtDonePath.Text = detail;
            DoneIcon.SetResourceReference(BackgroundProperty, ok ? "OkBrush" : "ErrBrush");
            DoneGlyph.Text = ok ? "\u2713" : "\u2715";
            ShowPage(PgDone);
        }

        private void Finish(Exception? ex, SetupOptions o)
        {
            _busy = false;
            _failed = ex != null;
            if (ex == null)
            {
                Outcome = SetupOutcome.InstallStarted;
                UpdateProgress(SetupEngine.StepCount);
                CbLaunch.Visibility = Visibility.Visible; // with Teams the server connection is offered inside the app, on first start
                ShowResult(true, T("done_title"), string.Format(T("done_path"), o.Dir));
            }
            else
            {
                CbLaunch.Visibility = Visibility.Collapsed;
                ShowResult(false, T("error_title"), ex.Message);
            }
        }

        private void FinishUninstall(Exception? ex, bool purge)
        {
            _busy = false;
            _failed = ex != null;
            CbLaunch.Visibility = Visibility.Collapsed;
            if (ex == null)
            {
                UpdateProgress(SetupEngine.UninstallStepCount);
                ShowResult(true, T("un_done"), T(purge ? "un_done_del" : "un_done_keep"));
            }
            else
            {
                ShowResult(false, T("un_error_title"), ex.Message);
            }
        }
    }
}
