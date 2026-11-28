using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Model.ProtocolRunner;
using Lemora.Model.ProtocolRunner.Default;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View;
using Lemora.View.Editor;
using Lemora.View.Host;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace Lemora.Service
{
    public partial class SessionControlService
    {
        #region Open Via Different
        private static void ConnectRdpByMstsc(in RDP rdp)
        {
            var tmp = Path.GetTempPath();
            var rdpFileName = $"{rdp.DisplayName}_{rdp.Port}_{MD5Helper.GetMd5Hash16BitString(rdp.UserName)}";
            var invalid = new string(Path.GetInvalidFileNameChars()) +
                          new string(Path.GetInvalidPathChars());
            rdpFileName = invalid.Aggregate(rdpFileName, (current, c) => current.Replace(c.ToString(), ""));
            var rdpFile = Path.Combine(tmp, rdpFileName + ".rdp");
            var text = rdp.ToRdpConfig().ToString();

            // write a .rdp file for mstsc.exe
            if (RetryHelper.Try(() =>
                {
                    File.WriteAllText(rdpFile, text);
                }, actionOnError: exception => UnifyTracing.Error(exception)))
            {
                // delete tmp rdp file, ETA 30s
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        Thread.Sleep(1000 * 30);
                        if (File.Exists(rdpFile))
                            File.Delete(rdpFile);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Error(e);
                    }
                });

                try
                {
                    string admin = rdp.IsAdministrativePurposes == true ? " /admin " : "";
                    var p = new Process
                    {
                        StartInfo =
                        {
                            FileName = "mstsc.exe",
                            Arguments = $"{admin} \"" + rdpFile + "\""
                        },
                    };
                    var protocol = rdp;
                    AddUnHostingWatch(p, protocol);
                    p.EnableRaisingEvents = true;
                    p.Start();
                }
                catch (Exception e)
                {
                    UnifyTracing.Error(e);
                    MessageBoxHelper.ErrorAlert(e.Message + "\r\n while Run mstsc.exe");
                }
            }
        }

        private static void ConnectRemoteApp(in RdpApp remoteApp)
        {
            var tmp = Path.GetTempPath();
            var rdpFileName = $"{remoteApp.DisplayName}_{remoteApp.Port}_{remoteApp.UserName}";
            var invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            rdpFileName = invalid.Aggregate(rdpFileName, (current, c) => current.Replace(c.ToString(), ""));
            var rdpFile = Path.Combine(tmp, rdpFileName + ".rdp");

            // write a .rdp file for mstsc.exet.Replace(c.ToString(), ""));
            var text = remoteApp.ToRdpConfig().ToString();
            // write a .rdp file for mstsc.exe
            if (RetryHelper.Try(() =>
            {
                File.WriteAllText(rdpFile, text);
            }, actionOnError: exception => UnifyTracing.Error(exception)))
            {
                var p = new Process
                {
                    StartInfo =
                    {
                        FileName = "cmd.exe",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                var protocol = remoteApp;
                AddUnHostingWatch(p, protocol);
                p.Start();
                p.StandardInput.WriteLine($"mstsc \"" + rdpFile + "\"");
                p.StandardInput.WriteLine("exit");

                // delete tmp rdp file, ETA 10s
                var t = new Task(async () =>
                {
                    try
                    {
                        await Task.Delay(1000 * 10);
                        if (File.Exists(rdpFile))
                            File.Delete(rdpFile);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Error(e);
                    }
                });
                t.Start();
            }
        }

        private void ConnectWithFullScreen(in ProtocolBase server, in Runner runner)
        {
            // fullscreen normally
            var host = runner.GetHost(server);
            if (host == null)
                return;

            lock (_dictLock)
            {
                Debug.Assert(!_connectionId2Hosts.ContainsKey(host.ConnectionId));
                _connectionId2Hosts.TryAdd(host.ConnectionId, host);
            }
            host.OnClosed += OnRequestCloseConnection;
            host.OnFullScreen2Window += this.MoveSessionToTabWindow;
            Lemora.Setup.TeamsSessionLog.NoteOpen(server, host.ConnectionId); // connection duration: only for Teams entries
            this.MoveSessionToFullScreen(host.ConnectionId);
            // opened straight in full screen: "back to window" makes it a main window tab, as if it had been opened in a tab
            // (before, a separate tabbed window was created)
            if (OpensInMainWindow(server, runner)) lock (_dictLock) _mainWindowSessions.Add(host.ConnectionId);
            host.Conn();
            SimpleLogHelper.Debug($@"Start Conn: {server.DisplayName}({server.GetHashCode()}) by host({host.GetHashCode()}) with full");
        }

        /// <summary>the built-in VNC client opens as a tab of the main window (at the top, browser style), with focus</summary>
        private string ConnectInMainWindow(ProtocolBase protocol, Runner runner)
        {
            Execute.OnUIThreadSync(() =>
            {
                lock (_dictLock)
                {
                    var main = IoC.Get<MainWindowViewModel>();
                    var area = (main.View as MainWindowView)?.SessionAreaSize;
                    var host = runner.GetHost(protocol, null, area is { Width: > 0, Height: > 0 } ? area : null);
                    if (main.View is Shawn.Utils.WpfResources.Theme.Styles.WindowBase win)
                        host.SetParentWindow(win);
                    Debug.Assert(!_connectionId2Hosts.ContainsKey(host.ConnectionId));
                    host.OnClosed += OnRequestCloseConnection;
                    host.OnFullScreen2Window += this.MoveSessionToTabWindow;
                    Lemora.Setup.TeamsSessionLog.NoteOpen(protocol, host.ConnectionId); // connection duration: only for Teams entries
                    _connectionId2Hosts.TryAdd(host.ConnectionId, host);
                    _mainWindowSessions.Add(host.ConnectionId);
                    main.AddSessionTab(host);
                    host.Conn();
                }
            });
            return "";
        }

        /// <summary>
        /// built-in clients (VNC, RDP, SFTP, FTP): main window tab; embedded external programs stay in the tabbed windows.
        /// FreeRDP like the Microsoft component, and PuTTY (KiTTY for old configurations) like the built-in terminal: main window tab
        /// </summary>
        private static bool OpensInMainWindow(ProtocolBase protocol, Runner runner)
        {
#pragma warning disable CS0612 // KittyRunner is obsolete but whoever still has it configured must get the same behavior
            bool embeddedExe = runner is FreeRdpRunner or Lemora.Model.ProtocolRunner.Default.PuttyRunner or Lemora.Model.ProtocolRunner.Default.KittyRunner;
#pragma warning restore CS0612
            return (protocol is VNC or RDP or SFTP or FTP or SSH or Telnet or Serial)
                && ((runner is not ExternalRunner && runner is not InternalExeRunner) || embeddedExe)
                && protocol.AlwaysOpenInNewTabWindow != true;
        }

        public string ConnectWithTab(in ProtocolBase protocolIn, in Runner runnerIn, string assignTabToken)
        {
            TabWindowView? tab = null;
            ProtocolBase protocol = protocolIn;
            Runner runner = runnerIn;
            if (OpensInMainWindow(protocol, runner))
                return ConnectInMainWindow(protocol, runner);
            Execute.OnUIThreadSync(() =>
            {
                lock (_dictLock)
                {
                    if (protocol.AlwaysOpenInNewTabWindow == true && string.IsNullOrEmpty(assignTabToken))
                    {
                        assignTabToken = DateTime.Now.Ticks.ToString();
                    }
                    tab = this.GetOrCreateTabWindow(assignTabToken);
                    if (tab == null) return;
                    if (tab.IsClosing) return;
                    tab.Show();
                    tab.ShowInTaskbar = true;

                    var host = runner.GetHost(protocol, tab);
                    // get display area size for host
                    Debug.Assert(!_connectionId2Hosts.ContainsKey(host.ConnectionId));
                    host.OnClosed += OnRequestCloseConnection;
                    host.OnFullScreen2Window += this.MoveSessionToTabWindow;
                    Lemora.Setup.TeamsSessionLog.NoteOpen(protocol, host.ConnectionId); // connection duration: only for Teams entries
                    tab.GetViewModel().AddItem(new TabItemViewModel(host, protocol.DisplayName));
                    _connectionId2Hosts.TryAdd(host.ConnectionId, host);
                    host.Conn();
                    tab.WindowState = tab.WindowState == WindowState.Minimized ? WindowState.Normal : tab.WindowState;
                    tab.Activate();
                }
            });
            return tab?.Token ?? "";
        }
        #endregion

        /// <summary>unsaved session (quick connection, apri_indirizzo): address, user and name are in no entry, the log hides them anyway</summary>
        internal static void RememberForLog(ProtocolBase protocol)
        {
            if (!protocol.IsTmpSession()) return;
            Lemora.Utils.LogRedactor.Remember(protocol.DisplayName, (protocol as ProtocolBaseWithAddressPort)?.Address,
                (protocol as ProtocolBaseWithAddressPortUserPwd)?.UserName);
        }

        private async Task<string> Connect(ProtocolBase protocol, string fromView, string assignTabToken = "", string assignRunnerName = "", string assignCredentialName = "")
        {
            RememberForLog(protocol);

            #region prepare

            //// trace source view
            //if (string.IsNullOrEmpty(fromView) == false)
            //    MsAppCenterHelper.TraceSessionOpen(protocol.Protocol, fromView);

            // connect count save to config
            _configurationService.Engagement.ConnectCount++;
            _configurationService.Save();


            // update the last conn time
            {
                var vmServer = _appData.GetItemById(protocol.DataSource?.DataSourceName ?? "", protocol.Id);
                vmServer?.ConnectTimeAddOrUpdate();
                if (IoC.Get<ConfigurationService>().General.ShowRecentlySessionInTray)
                    IoC.Get<TaskTrayService>().ReloadTaskTrayContextMenu();
            }

            // clone and decrypt!
            var protocolClone = protocol.Clone();
            protocolClone.DecryptToConnectLevel();
            protocolClone.GenerateSessionId();
            // RDP settings changed during the session (quality, display, advanced) are saved in the server
            if (protocolClone is Lemora.Model.Protocol.RDP rdpSession) RdpSessionSettingsSaver.Attach(rdpSession, protocol);


            // apply alternate credential
            {
                if (protocolClone is ProtocolBaseWithAddressPort p)
                {
                    // FreeRDP: no port check before the tab (with the name "lab" Windows took 4-7 s to resolve it,
                    // with the "Connecting" notification stuck): the tab opens right away and, if the PC does not answer, the tab shows the error (FreeRDP codes)
                    // With an IP address the check stays instead: it takes a few milliseconds and, if the PC does not answer, shows the
                    // "Connecting" notification and then the "unreachable" warning as with the Microsoft component, without opening a useless tab
                    var skipReachCheck = protocolClone is RDP
                        && !System.Net.IPAddress.TryParse(p.Address?.Trim(), out _)
                        && RunnerHelper.GetRunner(IoC.Get<ProtocolConfigurationService>(), protocolClone, protocolClone.Protocol, assignRunnerName) is FreeRdpRunner;
                    var c = await GetCredential(p, assignCredentialName, skipReachCheck);
                    if (c == null)
                    {
                        return "";
                    }

                    p.SetCredential(c, true);
                    p.DisplayName = c.Name;
                }
            }



            // multiple users: shared folder credentials + local users. The default one is used, or the user is asked which.
            if (protocolClone is ProtocolBaseWithAddressPortUserPwd multiUser and not VNC)   // VNC uses only the password
            {
                var candidates = UserResolver.GetCandidates(multiUser);
                if (candidates.Count > 0)
                {
                    UserCandidate? chosen;
                    if (multiUser.AskWhichUser == true && candidates.Count > 1)
                    {
                        var chooser = new UserChooserDialogViewModel
                        {
                            Title = $"[{multiUser.ProtocolDisplayName}]({multiUser.DisplayName}) -> {multiUser.Address}:{multiUser.Port}",
                            Users = candidates,
                        };
                        Execute.OnUIThreadSync(() => MaskLayerController.ShowWindowWithMask(chooser));
                        if (await chooser.WaitDialogResult() != true || chooser.Chosen == null)
                        {
                            Lemora.Utils.ActivityLog.Notify(protocolClone, "annullata", "the user closed the user choice without connecting");
                            return "";
                        }
                        chosen = chooser.Chosen;
                    }
                    else
                    {
                        chosen = UserResolver.Default(multiUser, candidates);
                    }
                    if (chosen != null)
                    {
                        UserResolver.Apply(multiUser, chosen);
                        // user without password and without key: asked at connection time
                        if (string.IsNullOrEmpty(chosen.Password) && !chosen.UsePrivateKey)
                            multiUser.AskPasswordWhenConnect = true;
                    }
                }
                else if (string.IsNullOrEmpty(multiUser.Password) && string.IsNullOrEmpty(multiUser.PrivateKey))
                {
                    // the server has no credential, or has the user but not the password (nor a key): it does NOT connect "blind" (Windows would show its own window,
                    // with its own user; SSH would fail with "No suitable authentication method"). Lemora's prompt appears with the user already filled in, which also offers
                    // the domain credentials available in the folder (FolderCredentialHints) and lets the user type a user by hand
                    multiUser.AskPasswordWhenConnect = true;
                }
            }

            // check if it needs password
            if (protocolClone is ProtocolBaseWithAddressPortUserPwd { AskPasswordWhenConnect: true } pb)
            {
                bool flag = false;
                var pwdDlg = new PasswordPopupDialogViewModel(protocolClone is SSH or SFTP)
                {
                    Title = $"[{pb.ProtocolDisplayName}]({pb.DisplayName}) -> {pb.Address}:{pb.Port}",
                    UserName = pb.UserName,
                    IsUserNameVisible = pb is not VNC,   // VNC asks only for the password
                    CanSaveToConnection = pb is not VNC && !protocol.IsTmpSession(),   // already saved connection: the typed credentials can be saved inside it
                };
                if (pb.UsePrivateKeyForConnect == true)
                {
                    pwdDlg.CanUsePrivateKeyForConnect = true;
                    pwdDlg.UsePrivateKeyForConnect = true;
                    pwdDlg.PrivateKey = pb.PrivateKey;
                }
                else
                {
                    pwdDlg.UsePrivateKeyForConnect = false;
                    pwdDlg.Password = pb.Password;
                }

                // shared folder credentials apply to RDP/SSH/SFTP, not to VNC
                if (pb is not VNC) FolderCredentialHints.Apply(pwdDlg, pb.Address, pb.TreeNodes, useDomain: protocolClone is RDP);

                Execute.OnUIThreadSync(() =>
                {
                    MaskLayerController.ShowWindowWithMask(pwdDlg);
                });

                if (await pwdDlg.WaitDialogResult() == true)
                {
                    flag = true;
                    pb.UserName = pwdDlg.UserName;
                    if (pwdDlg.UsePrivateKeyForConnect)
                    {
                        pb.UsePrivateKeyForConnect = true;
                        pb.Password = "";
                        pb.PrivateKey = pwdDlg.PrivateKey;
                    }
                    else
                    {
                        pb.UsePrivateKeyForConnect = false;
                        pb.PrivateKey = "";
                        pb.Password = pwdDlg.Password;
                    }
                    // "Use and link": the chosen folder credential is linked to the server only if the connection succeeds (see QuickSessionFollowUp.OnSessionConnected)
                    if (!string.IsNullOrEmpty(pwdDlg.LinkCredentialId)) QuickSessionFollowUp.RegisterLink(protocolClone.Id, protocol, pwdDlg.LinkCredentialId);
                    // "Save in the connection": hand-typed user and password (or key) become a user of the connection, after a successful connection
                    else if (pwdDlg.CanSaveToConnection && pwdDlg.SaveToConnection)
                        QuickSessionFollowUp.RegisterSave(protocolClone.Id, protocol, pwdDlg.UserName, pb.Password, pb.PrivateKey, pb.UsePrivateKeyForConnect == true);
                    pwdDlg.PrivateKey = "";
                    pwdDlg.Password = "";
                }
                else
                {
                    pwdDlg.Password = "";
                }


                if (flag == false)
                {
                    SimpleLogHelper.Info($"Opening of {Lemora.Utils.ActivityLog.Describe(protocolClone)} cancelled: password not entered");
                    Lemora.Utils.ActivityLog.Notify(protocolClone, "annullata", "the user closed the credential prompt without connecting");
                    return "";
                }
            }

            #endregion


            // if is OnlyOneInstance server, and it is connected now, activate it and return.
            if (this.ActivateOrReConnIfServerSessionIsOpened(protocolClone))
                return "";


            // run script before connected
            {
                int code = protocolClone.RunScriptBeforeConnect();
                if (0 != code)
                {
                    Lemora.Utils.ActivityLog.Failed(protocolClone, $"the script to run before connecting exited with code {code}");
                    MessageBoxHelper.ErrorAlert($"Script ExitCode = {code}, connection abort!");
                    return "";
                }
            }

            // dispatch for specified protocol
            if (protocolClone is RdpApp rdpApp)
            {
                ConnectRemoteApp(rdpApp);
                NoteLaunchedWithoutHost(rdpApp);
                return "";
            }
            else if (protocolClone is RDP freeRdp
                     && RunnerHelper.GetRunner(IoC.Get<ProtocolConfigurationService>(), protocolClone, protocolClone.Protocol, assignRunnerName) is FreeRdpRunner freeRdpRunner)
            {
                // FreeRDP: no Microsoft component. With explicit "mstsc.exe mode" mstsc wins; in full screen FreeRDP
                // opens its own window (/f) outside the tabs, otherwise it attaches to a tab like the other external programs
                if (freeRdp.MstscModeEnabled)
                {
                    ConnectRdpByMstsc(freeRdp);
                    NoteLaunchedWithoutHost(freeRdp);
                    return "";
                }
                SimpleLogHelper.Info($"Opening {Lemora.Utils.ActivityLog.Describe(protocolClone)} with {freeRdpRunner.Name}, from {(string.IsNullOrEmpty(fromView) ? "?" : fromView)}");
                // missing program: only the message (before, it silently fell back to the Microsoft component)
                if (!freeRdpRunner.TryResolveExe(out _))
                {
                    Lemora.Utils.ActivityLog.Failed(protocolClone, "FreeRDP not found");
                    MessageBoxHelper.ErrorAlert(IoC.Translate("session_freerdp_not_found", freeRdpRunner.ExePath));
                    return "";
                }
                // always inside Lemora: full screen in Lemora's full screen window, like the Microsoft component
                if (freeRdp.IsThisTimeConnWithFullScreen())
                {
                    this.ConnectWithFullScreen(protocolClone, freeRdpRunner);
                    return "";
                }
                return ConnectWithTab(protocolClone, freeRdpRunner, assignTabToken);
            }
            else if (protocolClone is RDP rdp)
            {
                if (rdp.IsNeedRunWithMstsc())
                {
                    ConnectRdpByMstsc(rdp);
                    NoteLaunchedWithoutHost(rdp);
                    return "";
                }
                // rdp full screen
                if (protocolClone.IsThisTimeConnWithFullScreen())
                {
                    this.ConnectWithFullScreen(protocolClone, new InternalDefaultRunner(RDP.ProtocolName));
                    return "";
                }
            }
            else if (protocolClone is SSH { OpenSftpOnConnected: true } ssh)
            {
                // open SFTP when SSH is connected.
                var tmpRunner = RunnerHelper.GetRunner(IoC.Get<ProtocolConfigurationService>(), protocolClone, SFTP.ProtocolName);
                var sftp = new SFTP
                {
                    ColorHex = ssh.ColorHex,
                    IconBase64 = ssh.IconBase64,
                    DisplayName = ssh.DisplayName + " (SFTP)",
                    Address = ssh.Address,
                    Port = ssh.Port,
                    UserName = ssh.UserName,
                    Password = ssh.Password,
                    PrivateKey = ssh.PrivateKey
                };
                assignTabToken = await Connect(sftp, fromView, assignTabToken, tmpRunner.Name, assignCredentialName);
            }
            else if (protocolClone is LocalApp { RunWithHosting: false } localApp)
            {
                var tmp = WinCmdRunner.CheckFileExistsAndFullName(localApp.GetExePath());
                if (tmp.Item1)
                {
                    var process = Process.Start(tmp.Item2, localApp.GetArguments(false));
                    AddUnHostingWatch(process, localApp);
                    NoteLaunchedWithoutHost(localApp);
                }
                return "";
            }


            string tabToken = "";
            var s = IoC.Get<ProtocolConfigurationService>();
            var runner = RunnerHelper.GetRunner(s, protocolClone, protocolClone.Protocol, assignRunnerName)!;
            SimpleLogHelper.Info($"Opening {Lemora.Utils.ActivityLog.Describe(protocolClone)} with {runner.Name}{(runner.IsRunWithoutHosting() ? " (external program)" : "")}, from {(string.IsNullOrEmpty(fromView) ? "?" : fromView)}");
            if (runner.IsRunWithoutHosting())
            {
                runner.RunWithoutHosting(protocolClone);
                NoteLaunchedWithoutHost(protocolClone);
            }
            else
            {
                tabToken = ConnectWithTab(protocolClone, runner, assignTabToken);
            }
            return tabToken;
        }

        /// <summary>
        /// Connections launched without a tab (external program like Windows Terminal, mstsc, RemoteApp, local app): there is no session that becomes "Connected",
        /// so the credentials the user asked to remember (quick connection) are saved at launch. Also removes the pending item.
        /// </summary>
        internal static void NoteLaunchedWithoutHost(ProtocolBase p)
        {
            try
            {
                // an external program does not report whether login succeeded: "Use and link" and "Save in the connection" do not apply (security hardening).
                // The quick connection history is still saved at launch
                QuickSessionFollowUp.DropLinkAndSave(p);
                QuickSessionFollowUp.OnSessionConnected(p);
                QuickSessionFollowUp.OnSessionClosed(p, false);
            }
            catch (Exception e) { SimpleLogHelper.Error(e); }
            Lemora.Utils.ActivityLog.Write($"Started {Lemora.Utils.ActivityLog.Describe(p)} with an external program (the session is not followed by the app: end and drops are not logged)");
        }
    }
}