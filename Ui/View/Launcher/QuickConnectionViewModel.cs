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
        /// Connessione rapida verso un host (anche dalla barra di ricerca della finestra principale), con il tipo scelto.
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
        if (i >= 0 && host.IndexOf(':') == i)   // IPv4:porta o nome:porta (un solo due punti); un IPv6 ("::1") non ha porta
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

        // se l'host non risponde sulla porta, inutile chiedere la password
        if (server is ProtocolBaseWithAddressPort reach && !string.IsNullOrWhiteSpace(reach.Port))
        {
            var hideBusy = ToastService.ShowBusy(string.Format(IoC.Translate("toast_connecting"), reach.Address));
            bool? reachable;
            try { reachable = await TcpHelper.TestConnectionAsync(reach.Address, reach.Port, null, 3000); }
            finally { hideBusy(); }
            // VNC senza porta indicata e la porta standard (5900) non risponde: si cerca il server sulle altre porte VNC usuali (5901-5910).
            // Se la 5900 risponde non si fa nessuna prova in piu': alcuni server VNC reagiscono male a connessioni ravvicinate che si chiudono subito.
            if (reachable != true && protocol == "VNC" && string.IsNullOrEmpty(port))
            {
                var hideFind = ToastService.ShowBusy(string.Format(IoC.Translate("toast_connecting"), reach.Address));
                int? found;
                try { found = await Lemora.View.Host.ProtocolHosts.VncPortFinder.FindAsync(reach.Address, System.Linq.Enumerable.Range(5901, 10).ToArray()); }
                finally { hideFind(); }
                if (found is { } p)
                {
                    SimpleLogHelper.Debug($"VNC: nessun server sulla 5900, trovato sulla porta {p}");
                    port = p.ToString();
                    reach.Port = port;
                    if (!address.Contains(':')) host = $"{address}:{port}";   // la cronologia ricorda l'indirizzo con la porta trovata (non per IPv6: "::1:5902" sarebbe ambiguo)
                    server.DisplayName = host;
                    reachable = true;
                }
            }
            if (reachable != true)
            {
                // il perche' resta nel registro (prima non c'era traccia e non si poteva capire un "impossibile raggiungere")
                try
                {
                    var detail = int.TryParse(reach.Port, out var pn) ? await TcpHelper.DescribeAsync(reach.Address, pn) : "porta non numerica";
                    SimpleLogHelper.Warning($"Collegamento rapido a {reach.Address}:{reach.Port} non riuscito (verifica = {(reachable == null ? "scaduta o annullata" : "non raggiungibile")}). Diagnosi: {detail}");
                }
                catch (Exception e) { SimpleLogHelper.Warning("Collegamento rapido non riuscito, diagnosi impossibile: " + e.Message); }
                MessageBoxHelper.ErrorAlert(string.Format(IoC.Translate("host_unreachable"), $"{reach.Address}:{reach.Port}"));
                return;
            }
        }

        // pop password window if needed
        // credenziali ricordate in un collegamento precedente ("Ricorda le informazioni"): collega direttamente, senza finestra.
        // Con Maiusc + Invio dalla ricerca la finestra viene mostrata comunque.
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
            // VNC usa solo la password: niente campo utente (come nel client integrato)
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

            // dominio e utenti della cartella la cui rete contiene l'IP (solo il dominio vale per RDP)
            if (server is not VNC) FolderCredentialHints.Apply(pwdDlg, address, null, useDomain: server is RDP);   // il VNC usa solo la password

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
        // collegamento diretto con credenziali salvate: aggiorna solo la data d'uso (la voce sale in cima alla cronologia)
        if (usedSavedCredential && refreshItem != null)
        {
            var (ru, rp, rk) = refreshItem.GetUserPassword();
            LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(host, protocol, ru, rp, rk);
        }

        if (!usedSavedCredential && IoC.Get<ConfigurationService>().Launcher.AllowSaveInfoInQuickConnect)
        {
            // salva utente e password solo se la casella "Ricorda le informazioni" e' spuntata (prima bastava che fosse visibile)
            // la voce entra subito nella cronologia, le credenziali ricordate solo dopo un collegamento riuscito (QuickSessionFollowUp.OnSessionConnected):
            // prima si salvava anche una password sbagliata e ogni connessione successiva usava quella
            LocalityConnectRecorder.QuickConnectionHistoryAddOrUpdate(host, protocol, "", "", "");
        }

        // a sessione chiusa si proporra' di salvarla nella cartella della sua rete (con le credenziali solo se "ricordate")
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
