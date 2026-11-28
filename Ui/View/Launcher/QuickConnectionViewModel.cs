using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View.Editor;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;
using File = System.IO.File;

namespace Lemora.View.Launcher
{
    public class QuickConnectionViewModel : NotifyPropertyChangedBaseScreen
    {
        public readonly QuickConnectionItem OpenConnectActionItem; // A placeholder for the "Connect" button
        public List<ProtocolBaseWithAddressPort> Protocols { get; } // A list of protocols that can be used for quick connection
        public QuickConnectionViewModel()
        {
            Protocols = new List<ProtocolBaseWithAddressPort>();
            var protocols = ProtocolBase.GetAllSubInstance().Select(x => x as ProtocolBaseWithAddressPort).Where(x => x != null).ToList();
            foreach (var protocol in protocols)
            {
                if (protocol != null
                    && protocol.Protocol != RdpApp.ProtocolName)
                {
                    Protocols.Add(protocol);
                }
            }
            _selectedProtocol = Protocols.First(x => x.Protocol == RDP.ProtocolName);

            // Placeholder for the "Connect" button
            OpenConnectActionItem = new QuickConnectionItem()
            {
                Host = IoC.Translate("Connect"),
            };
        }

        protected override void OnViewLoaded()
        {
            if (this.View is QuickConnectionView view)
            {
                Execute.OnUIThreadSync(() => { view.TbKeyWord.Focus(); });
                RebuildConnectionHistory();
            }
        }

        private ProtocolBase? _serverSelectionsViewSelected = null;
        public void Show()
        {
            if (this.View is not QuickConnectionView view) return;
            if (IoC.TryGet<LauncherWindowView>()?.IsClosing != false) return;

            IoC.Get<LauncherWindowViewModel>().ServerSelectionsViewVisibility = Visibility.Collapsed;
            RebuildConnectionHistory();

            // clear info
            Filter = "";
            if (SelectedProtocol is ProtocolBaseWithAddressPortUserPwd sp)
            {
                sp.Address = "";
                sp.UserName = "";
                sp.Password = "";
            }
            _serverSelectionsViewSelected = null;


            // if selected server in launcher list, and it is writable, then auto fill the server info into quick connect view
            if (IoC.Get<LauncherWindowViewModel>().ServerSelectionsViewModel is { AnyKeyExceptTabPressAfterShow: true, SelectedItem: { DataSource: { IsWritable: true }, Server: { } serverSelectionsViewSelected } })
            {
                _serverSelectionsViewSelected = serverSelectionsViewSelected;
                SelectedProtocol = Protocols.FirstOrDefault(x => x.Protocol == _serverSelectionsViewSelected.Protocol) ?? Protocols.First();
                // fill address and port
                if (_serverSelectionsViewSelected is ProtocolBaseWithAddressPort pap)
                {
                    Filter = pap.Address + ":" + pap.Port;
                }
            }


            Execute.OnUIThread(() =>
            {
                view.TbKeyWord.Focus();
                view.TbKeyWord.CaretIndex = view.TbKeyWord.Text.Length;
            });
        }


        private readonly DebounceDispatcher _debounceDispatcher = new();
        private string _filter = "";
        public string Filter
        {
            get => _filter;
            set
            {
                if (SetAndNotifyIfChanged(ref _filter, value))
                {
                    _debounceDispatcher.Debounce(100, (obj) =>
                    {
                        if (value == _filter)
                        {
                            CalcVisibleByFilter();
                        }
                    });
                }
            }
        }

        private ProtocolBaseWithAddressPort _selectedProtocol;
        public ProtocolBaseWithAddressPort SelectedProtocol
        {
            get => _selectedProtocol;
            set => SetAndNotifyIfChanged(ref _selectedProtocol, value);
        }


        private int _selectedIndex = 0;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (SetAndNotifyIfChanged(ref _selectedIndex, value))
                {
                    if (this.View is not QuickConnectionView view) return;
                    if (IoC.TryGet<LauncherWindowView>()?.IsClosing != false) return;
                    Execute.OnUIThread(() => { view.ListBoxHistory.ScrollIntoView(view.ListBoxHistory.SelectedItem); });
                    if (SelectedIndex >= 0 && SelectedIndex < ConnectHistory.Count && ConnectHistory.Count > 0 && ConnectHistory[SelectedIndex] != OpenConnectActionItem)
                    {
                        var p = ConnectHistory[SelectedIndex].Protocol;
                        SelectedProtocol = Protocols.FirstOrDefault(x => x.Protocol == p) ?? SelectedProtocol;
                    }
                }
            }
        }

        private ObservableCollection<QuickConnectionItem> _connectHistory = new ObservableCollection<QuickConnectionItem>();
        public ObservableCollection<QuickConnectionItem> ConnectHistory
        {
            get => _connectHistory;
            set
            {
                if (SetAndNotifyIfChanged(ref _connectHistory, value))
                {
                    SelectedIndex = 0;
                }
            }
        }


        public void RebuildConnectionHistory()
        {
            if (!IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
            {
                LocalityConnectRecorder.QuickConnectionHistoryRemoveAll();
            }
            var list = LocalityConnectRecorder.QuickConnectionHistoryGetAll();
            list.Insert(0, OpenConnectActionItem);
            ConnectHistory = new ObservableCollection<QuickConnectionItem>(list);
            IoC.Get<LauncherWindowViewModel>().ReSetWindowHeight();
        }


        public double ReCalcGridMainHeight()
        {
            var tmp = LauncherWindowViewModel.LAUNCHER_SERVER_LIST_ITEM_HEIGHT * ConnectHistory.Count;
            var ret = LauncherWindowViewModel.LAUNCHER_GRID_KEYWORD_HEIGHT + tmp;
            return ret;
        }


        private string _lastKeyword = string.Empty;
        public void CalcVisibleByFilter()
        {
            if (this.View is not QuickConnectionView view) return;
            if (IoC.TryGet<LauncherWindowView>()?.IsClosing != false) return;
            if (string.IsNullOrEmpty(_filter) == false && _lastKeyword == _filter) return;

            _lastKeyword = _filter;

            var keyword = _filter.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                RebuildConnectionHistory();
                return;
            }

            var newList = LocalityConnectRecorder.QuickConnectionHistoryGetAll().Where(x => x.Host.StartsWith(keyword, StringComparison.OrdinalIgnoreCase));
            var list = newList?.ToList() ?? new List<QuickConnectionItem>();
            list.Insert(0, OpenConnectActionItem);
            ConnectHistory = new ObservableCollection<QuickConnectionItem>(list);
            IoC.Get<LauncherWindowViewModel>().ReSetWindowHeight();
        }


        public async void OpenConnection()
        {
            try
            {
                var serverSelectionsViewSelected = _serverSelectionsViewSelected;
                _serverSelectionsViewSelected = null; // release the reference to avoid memory leak

                if (this.View is not QuickConnectionView) return;
                if (IoC.TryGet<LauncherWindowView>()?.IsClosing != false) return;

                if (ConnectHistory.Count > 0
                    && SelectedIndex >= 0
                    && SelectedIndex < ConnectHistory.Count)
                {
                    var host = ConnectHistory[SelectedIndex] != OpenConnectActionItem ? ConnectHistory[SelectedIndex].Host.Trim() : Filter.Trim();
                    if (string.IsNullOrWhiteSpace(host))
                        return;

                    await ConnectCore(host, SelectedProtocol, serverSelectionsViewSelected);
                }
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                UnifyTracing.Error(e, new Dictionary<string, string>()
                {
                    {"Action", "QuickConnectionViewModel.OpenConnection"}
                });
            }
        }


        /// <summary>
        /// Quick connection to a host (also from the main window search bar), with the chosen type.
        /// </summary>
        public async void ConnectToHost(string host, ProtocolBaseWithAddressPort selectedProtocol, bool forceAskCredentials = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(host)) return;
                await ConnectCore(host.Trim(), selectedProtocol, null, forceAskCredentials);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Error(e);
                UnifyTracing.Error(e, new Dictionary<string, string>()
                {
                    {"Action", "QuickConnectionViewModel.ConnectToHost"}
                });
            }
        }

        private async System.Threading.Tasks.Task ConnectCore(string host, ProtocolBaseWithAddressPort selectedProtocol, ProtocolBase? serverSelectionsViewSelected, bool forceAskCredentials = false)
        {
        string protocol = selectedProtocol.Protocol;
        string address = host;
        string port = "";
        var i = host.LastIndexOf(":", StringComparison.Ordinal);
        if (i >= 0 && host.IndexOf(':') == i)   // IPv4:port or name:port (a single colon); an IPv6 ("::1") has no port
        {
            if (int.TryParse(host.Substring(i + 1), out var intPort))
            {
                address = host.Substring(0, i).Trim();
                port = intPort.ToString();
            }
            else
            {
                // invalid port, reset address to let it be invalid
                address = "";
            }
        }

        // stop if address is empty
        if (string.IsNullOrWhiteSpace(address))
            return;

        // Hide Ui
        Filter = "";
        IoC.Get<LauncherWindowViewModel>().HideMe();

        // create protocol
        var server = (Protocols.FirstOrDefault(x => x.Protocol == protocol) ?? selectedProtocol).Clone();
        Lemora.Service.KeyMap.ApplyNewConnectionDefaults(server);   // screen settings of Settings › Associazione tasti
        server.DisplayName = host;
        if (server is ProtocolBaseWithAddressPort protocolBaseWithAddressPort)
        {
            protocolBaseWithAddressPort.Address = address;
            if (string.IsNullOrWhiteSpace(port) == false)
            {
                protocolBaseWithAddressPort.Port = port;
            }
        }

        if (serverSelectionsViewSelected != null)
        {
            server.IconBase64 = serverSelectionsViewSelected.IconBase64;
        }

        // if the host does not answer on the port, there is no point asking for the password
        if (server is ProtocolBaseWithAddressPort reach && !string.IsNullOrWhiteSpace(reach.Port))
        {
            using var userCancel = new System.Threading.CancellationTokenSource();   // the X of the "Connecting to ..." notification cancels the connection
            var hideBusy = ToastService.ShowBusy(string.Format(IoC.Translate("toast_connecting"), reach.Address), userCancel);
            bool? reachable;
            try { reachable = await TcpHelper.TestConnectionAsync(reach.Address, reach.Port, userCancel.Token, 3000); }
            finally { hideBusy(); }
            if (userCancel.IsCancellationRequested) return;
            // VNC with no port given and the standard port (5900) does not answer: look for the server on the other usual VNC ports (5901-5910).
            // If 5900 answers, no extra probe is made: some VNC servers react badly to rapid connections that close right away.
            if (reachable != true && protocol == "VNC" && string.IsNullOrEmpty(port))
            {
                using var findCancel = new System.Threading.CancellationTokenSource();
                var hideFind = ToastService.ShowBusy(string.Format(IoC.Translate("toast_connecting"), reach.Address), findCancel);
                int? found;
                try { found = await Lemora.View.Host.ProtocolHosts.VncPortFinder.FindAsync(reach.Address, System.Linq.Enumerable.Range(5901, 10).ToArray()); }
                finally { hideFind(); }
                if (findCancel.IsCancellationRequested) return;
                if (found is { } p)
                {
                    SimpleLogHelper.Debug($"VNC: no server on 5900, found on port {p}");
                    port = p.ToString();
                    reach.Port = port;
                    if (!address.Contains(':')) host = $"{address}:{port}";   // the history remembers the address with the port found (not for IPv6: "::1:5902" would be ambiguous)
                    server.DisplayName = host;
                    reachable = true;
                }
            }
            if (reachable != true)
            {
                // the reason stays in the log (before there was no trace and an "unable to reach" could not be understood)
                try
                {
                    var detail = int.TryParse(reach.Port, out var pn) ? await TcpHelper.DescribeAsync(reach.Address, pn) : "port not numeric";
                    SimpleLogHelper.Warning($"Quick connection to {reach.Address}:{reach.Port} failed (check = {(reachable == null ? "timed out or cancelled" : "unreachable")}). Diagnosis: {detail}");
                }
                catch (Exception e) { SimpleLogHelper.Warning("Quick connection failed, diagnosis impossible: " + e.Message); }
                MessageBoxHelper.ErrorAlert(string.Format(IoC.Translate("host_unreachable"), $"{reach.Address}:{reach.Port}"));
                return;
            }
        }

        // pop password window if needed
        // credentials remembered from a previous connection ("Remember the information"): connect directly, without the window.
        // With Shift + Enter from the search the window is shown anyway.
        bool usedSavedCredential = false;
        QuickConnectionItem? refreshItem = null;
        if (!forceAskCredentials
            && server is ProtocolBaseWithAddressPortUserPwd savedTarget
            && IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
        {
            var savedItem = LocalityConnectRecorder.QuickConnectionHistoryGetAll().FirstOrDefault(x => x.Host == host && x.Protocol == protocol);
            if (savedItem != null)
            {
                var (savedUser, savedPwd, savedKey) = savedItem.GetUserPassword();
                var hasKey = !string.IsNullOrEmpty(savedKey) && File.Exists(savedKey);
                var hasSecret = !string.IsNullOrEmpty(savedPwd) || hasKey;
                if (hasSecret && (savedTarget is VNC || !string.IsNullOrEmpty(savedUser)))
                {
                    savedTarget.UserName = savedUser;
                    if (hasKey)
                    {
                        savedTarget.UsePrivateKeyForConnect = true;
                        savedTarget.Password = "";
                        savedTarget.PrivateKey = savedKey;
                    }
                    else
                    {
                        savedTarget.UsePrivateKeyForConnect = false;
                        savedTarget.PrivateKey = "";
                        savedTarget.Password = savedPwd;
                    }
                    usedSavedCredential = true;
                    refreshItem = savedItem;
                }
            }
        }

        var pwdDlg = new PasswordPopupDialogViewModel();
        if (!usedSavedCredential && server is ProtocolBaseWithAddressPortUserPwd protocolBaseWithAddressPortUserPwd)
        {
            pwdDlg = new PasswordPopupDialogViewModel(protocolBaseWithAddressPortUserPwd is SSH or SFTP, IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
            {
                Title = $"[{server.ProtocolDisplayName}]{host}"
            };
            // VNC uses only the password: no user field (as in the built-in client)
            if (server is VNC) pwdDlg.IsUserNameVisible = false;

            // if selected server in launcher list, and it is writable, then auto fill the server password into quick connect view
            // fill user and password
            if (serverSelectionsViewSelected is ProtocolBaseWithAddressPortUserPwd pup)
            {
                pwdDlg.UserName = pup.UserName;
                pwdDlg.Password = DataVault.DecryptOrReturnOriginalString(pup.Password);
                pwdDlg.PrivateKey = DataVault.DecryptOrReturnOriginalString(pup.PrivateKey);
            }
            // otherwise, fill in the last used username and password
            else
            {
                if (IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
                {
                    // find saved username and password then fill in
                    var history = LocalityConnectRecorder.QuickConnectionHistoryGetAll().FirstOrDefault(x => x.Host == host && x.Protocol == protocol);
                    if (history != null)
                    {
                        (pwdDlg.UserName, pwdDlg.Password, pwdDlg.PrivateKey) = history.GetUserPassword();
                    }
                }
            }
            if (!string.IsNullOrEmpty(pwdDlg.PrivateKey) && File.Exists(pwdDlg.PrivateKey))
            {
                pwdDlg.UsePrivateKeyForConnect = true;
            }

            // domain and users of the folder whose network contains the IP (the domain only applies to RDP)
            if (server is not VNC) FolderCredentialHints.Apply(pwdDlg, address, null, useDomain: server is RDP);   // VNC uses only the password

            MaskLayerController.ShowWindowWithMask(pwdDlg);

            if (await pwdDlg.WaitDialogResult() == true)
            {
                protocolBaseWithAddressPortUserPwd.UserName = pwdDlg.UserName;
                if (pwdDlg.UsePrivateKeyForConnect)
                {
                    protocolBaseWithAddressPortUserPwd.UsePrivateKeyForConnect = true;
                    protocolBaseWithAddressPortUserPwd.Password = "";
                    protocolBaseWithAddressPortUserPwd.PrivateKey = pwdDlg.PrivateKey;
                }
                else
                {
                    protocolBaseWithAddressPortUserPwd.UsePrivateKeyForConnect = false;
                    protocolBaseWithAddressPortUserPwd.PrivateKey = "";
                    protocolBaseWithAddressPortUserPwd.Password = pwdDlg.Password;
                }
            }
            else
            {
                return;
            }
        }


        // save history
        // direct connection with saved credentials: only update the last-used date (the entry moves to the top of the history)
        if (usedSavedCredential && refreshItem != null)
        {
            var (ru, rp, rk) = refreshItem.GetUserPassword();
            LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(host, protocol, ru, rp, rk);
        }

        if (!usedSavedCredential && IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
        {
            // save user and password only if the "Remember the information" box is checked (before, it was enough for it to be visible)
            // the entry goes into the history right away, the remembered credentials only after a successful connection (QuickSessionFollowUp.OnSessionConnected):
            // before, a wrong password was saved too and every later connection used it
            LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(host, protocol, "", "", "");
        }

        // once the session is closed, it will be offered for saving in the folder of its network (with the credentials only if "remembered")
        {
            string user = "", pwd = "", key = "";
            if (server is ProtocolBaseWithAddressPortUserPwd cred)
            {
                var remember = usedSavedCredential || (pwdDlg.CanRememberInfo && pwdDlg.IsRememberInfo == true);
                if (remember) { user = cred.UserName; pwd = cred.Password; key = cred.UsePrivateKeyForConnect == true ? cred.PrivateKey : ""; }
            }
            QuickSessionFollowUp.Register(server, host, user, pwd, key);
        }

        // connect
        GlobalEventHelper.OnRequestQuickConnect?.Invoke(server, fromView: $"{nameof(LauncherWindowView)} - {nameof(QuickConnectionView)}");
        }
    }
}
