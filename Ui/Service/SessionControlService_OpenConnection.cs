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
            Lemora.Setup.TeamsSessionLog.NoteOpen(server, host.ConnectionId); // durata dei collegamenti: solo per le voci Teams
            this.MoveSessionToFullScreen(host.ConnectionId);
            host.Conn();
            SimpleLogHelper.Debug($@"Start Conn: {server.DisplayName}({server.GetHashCode()}) by host({host.GetHashCode()}) with full");
        }

        /// <summary>il client VNC integrato si apre come scheda della finestra principale (in alto, stile browser), con il focus</summary>
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
                    Lemora.Setup.TeamsSessionLog.NoteOpen(protocol, host.ConnectionId); // durata dei collegamenti: solo per le voci Teams
                    _connectionId2Hosts.TryAdd(host.ConnectionId, host);
                    _mainWindowSessions.Add(host.ConnectionId);
                    main.AddSessionTab(host);
                    host.Conn();
                }
            });
            return "";
        }

        public string ConnectWithTab(in ProtocolBase protocolIn, in Runner runnerIn, string assignTabToken)
        {
            TabWindowView? tab = null;
            ProtocolBase protocol = protocolIn;
            Runner runner = runnerIn;
            // client integrati (VNC, RDP, SFTP, FTP): scheda della finestra principale; i programmi esterni incorporati restano nelle finestre a schede
            if ((protocol is VNC or RDP or SFTP or FTP or SSH)
                && runner is not ExternalRunner && runner is not InternalExeRunner
                && protocol.AlwaysOpenInNewTabWindow != true)
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
                    Lemora.Setup.TeamsSessionLog.NoteOpen(protocol, host.ConnectionId); // durata dei collegamenti: solo per le voci Teams
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

        private async Task<string> Connect(ProtocolBase protocol, string fromView, string assignTabToken = "", string assignRunnerName = "", string assignCredentialName = "")
        {

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
            // le impostazioni RDP cambiate durante la sessione (qualita', schermo, avanzate) si salvano nel server
            if (protocolClone is Lemora.Model.Protocol.RDP rdpSession) RdpSessionSettingsSaver.Attach(rdpSession, protocol);


            // apply alternate credential
            {
                if (protocolClone is ProtocolBaseWithAddressPort p)
                {
                    var c = await GetCredential(p, assignCredentialName);
                    if (c == null)
                    {
                        return "";
                    }

                    p.SetCredential(c, true);
                    p.DisplayName = c.Name;
                }
            }



            // piu' utenti: credenziali condivise della cartella + utenti locali. Si usa il predefinito, oppure si chiede quale.
            if (protocolClone is ProtocolBaseWithAddressPortUserPwd multiUser and not VNC)   // il VNC usa solo la password
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
                            return "";
                        chosen = chooser.Chosen;
                    }
                    else
                    {
                        chosen = UserResolver.Default(multiUser, candidates);
                    }
                    if (chosen != null)
                    {
                        UserResolver.Apply(multiUser, chosen);
                        // utente senza password e senza chiave: si chiede alla connessione
                        if (string.IsNullOrEmpty(chosen.Password) && !chosen.UsePrivateKey)
                            multiUser.AskPasswordWhenConnect = true;
                    }
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
                    IsUserNameVisible = pb is not VNC,   // il VNC chiede solo la password
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

                // le credenziali condivise delle cartelle valgono per RDP/SSH/SFTP, non per il VNC
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
                    pwdDlg.PrivateKey = "";
                    pwdDlg.Password = "";
                }
                else
                {
                    pwdDlg.Password = "";
                }


                if (flag == false)
                {
                    SimpleLogHelper.Info($"Apertura di {Lemora.Utils.ActivityLog.Describe(protocolClone)} annullata: password non inserita");
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
                    Lemora.Utils.ActivityLog.Failed(protocolClone, $"lo script da eseguire prima del collegamento è uscito con codice {code}");
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
            SimpleLogHelper.Info($"Apertura di {Lemora.Utils.ActivityLog.Describe(protocolClone)} con {runner.Name}{(runner.IsRunWithoutHosting() ? " (programma esterno)" : "")}, da {(string.IsNullOrEmpty(fromView) ? "?" : fromView)}");
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
        /// Collegamenti lanciati senza scheda (programma esterno come Windows Terminal, mstsc, RemoteApp, app locale): non c'e' una sessione che diventi "Connessa",
        /// quindi le credenziali che l'utente ha chiesto di ricordare (connessione rapida) si salvano al lancio. Toglie anche la voce in sospeso.
        /// </summary>
        internal static void NoteLaunchedWithoutHost(ProtocolBase p)
        {
            try
            {
                QuickSessionFollowUp.OnSessionConnected(p);
                QuickSessionFollowUp.OnSessionClosed(p, false);
            }
            catch (Exception e) { SimpleLogHelper.Error(e); }
            Lemora.Utils.ActivityLog.Write($"Avviato {Lemora.Utils.ActivityLog.Describe(p)} con un programma esterno (la sessione non è seguita dall'app: fine e cadute non si registrano)");
        }
    }
}