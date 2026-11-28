using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.Model;
using Lemora.Utils;
using Lemora.View.Editor;
using Lemora.View.Editor.Forms.AlternativeCredential;
using Lemora.View.Utils;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace Lemora.View.Settings.CredentialVault
{
    public class CredentialItem
    {
        public CredentialItem(DataSourceBase dataSource, Credential credential)
        {
            DataSource = dataSource;
            Credential = credential;
        }

        public DataSourceBase DataSource { get; }
        public Credential Credential { get; }
    }

    public class CredentialVaultViewModel : NotifyPropertyChangedBase
    {
        private readonly DataSourceService _sourceService;

        private ObservableCollection<CredentialItem> _credentials = new ObservableCollection<CredentialItem>();
        public ObservableCollection<CredentialItem> Credentials
        {
            get => _credentials;
            set => SetAndNotifyIfChanged(ref _credentials, value);
        }

        public CredentialVaultViewModel(DataSourceService sourceService, GlobalData appData)
        {
            _sourceService = sourceService;
            InitCredentials();
            IoC.Get<GlobalData>().OnReloadAll -= InitCredentials;
            IoC.Get<GlobalData>().OnReloadAll += InitCredentials;
        }

        private void InitCredentials()
        {
            Execute.OnUIThreadSync(() =>
            {
                var tuples = _sourceService.GetSourceCredentials(false);
                // folder credentials live in the same table but are managed only from the folder page (FolderCredentialService):
                // editing or deleting them from here bypassed the Teams permissions and did not notify the server
                Credentials = new ObservableCollection<CredentialItem>(tuples.Where(t => string.IsNullOrEmpty(t.Item2.FolderPath)).Select(tuple => new CredentialItem(tuple.Item1, tuple.Item2)));
            });
        }


        // ---- browser extension: who is paired and "unpair all" (extensions will have to ask for confirmation again)
        public sealed class BrowserLink
        {
            public string Id { get; init; } = "";
            public string Name { get; init; } = "";
            public string Detail { get; init; } = "";
            public string AccessibleName => string.Format(IoC.Translate("settings_browser_unpair_name"), Name);
        }
        public IReadOnlyList<BrowserLink> Browsers => Lemora.Service.Browser.BrowserBridge.PairedList.Select(p => new BrowserLink
        {
            Id = p.Id,
            Name = p.Browser,
            Detail = string.Format(IoC.Translate("settings_browser_detail"), p.Exe, p.Created.ToLocalTime(), p.LastUsed.ToLocalTime(), Lemora.Service.Browser.BrowserBridge.ExpireDays),
        }).ToList();
        public bool BrowserHasPaired => Browsers.Count > 0;
        public string BrowserPairedText => BrowserHasPaired ? "" : IoC.Translate("settings_browser_none");

        private RelayCommand? _cmdForgetBrowser;
        public RelayCommand CmdForgetBrowser => _cmdForgetBrowser ??= new RelayCommand((o) =>
        {
            if (o is not BrowserLink b) return;
            if (!MessageBoxHelper.Confirm(string.Format(IoC.Translate("settings_browser_unpair_one"), b.Name), IoC.Translate("settings_browser_unpair"), IoC.Translate("Cancel"), IoC.Translate("settings_browser_title"))) return;
            Lemora.Service.Browser.BrowserBridge.Forget(b.Id);
            RefreshBrowsers();
        });

        private RelayCommand? _cmdForgetBrowsers;
        public RelayCommand CmdForgetBrowsers => _cmdForgetBrowsers ??= new RelayCommand((o) =>
        {
            if (!MessageBoxHelper.Confirm(IoC.Translate("settings_browser_unpair_all_q"), IoC.Translate("settings_browser_unpair"), IoC.Translate("Cancel"), IoC.Translate("settings_browser_title"))) return;
            Lemora.Service.Browser.BrowserBridge.ForgetAll();
            RefreshBrowsers();
        });

        // install into the default browser: for Firefox the signed file ships inside Lemora (BrowserExtensionSetup), Chrome/Edge after the stores
        private static Lemora.Service.Browser.BrowserExtensionSetup.Kind DefaultKind => Lemora.Service.Browser.BrowserExtensionSetup.DefaultBrowser();
        private static bool HostOk => Lemora.Service.Browser.BrowserExtensionSetup.HostRegistered;
        public string BrowserExtHeader => string.Format(IoC.Translate("settings_browser_default"), Lemora.Service.Browser.BrowserExtensionSetup.Name(DefaultKind));
        public bool BrowserExtCanInstall => HostOk && DefaultKind == Lemora.Service.Browser.BrowserExtensionSetup.Kind.Firefox;
        public string BrowserExtButton => IoC.Translate(Lemora.Service.Browser.BrowserExtensionSetup.DefaultHasExtension() ? "settings_browser_reinstall" : "settings_browser_install");
        public string BrowserExtText =>
            !HostOk ? IoC.Translate("settings_browser_portable")
            : DefaultKind != Lemora.Service.Browser.BrowserExtensionSetup.Kind.Firefox ? Lemora.Service.Browser.BrowserExtensionSetup.InstallInDefault() ?? ""
            : Lemora.Service.Browser.BrowserExtensionSetup.FirefoxHasExtension() ? IoC.Translate("settings_browser_installed")
            : IoC.Translate("settings_browser_not_installed");

        private RelayCommand? _cmdInstallBrowserExt;
        public RelayCommand CmdInstallBrowserExt => _cmdInstallBrowserExt ??= new RelayCommand((o) =>
        {
            if (Lemora.Service.Browser.BrowserExtensionSetup.InstallInDefault() is { } err)
                MessageBoxHelper.Warning(err, IoC.Translate("settings_browser_title"));
        });

        /// <summary>the page reopens: refreshed list (a pairing may have arrived in the meantime)</summary>
        public void RefreshBrowsers()
        {
            RaisePropertyChanged(nameof(BrowserExtHeader));
            RaisePropertyChanged(nameof(BrowserExtCanInstall));
            RaisePropertyChanged(nameof(BrowserExtButton));
            RaisePropertyChanged(nameof(BrowserExtText));
            RaisePropertyChanged(nameof(Browsers));
            RaisePropertyChanged(nameof(BrowserPairedText));
            RaisePropertyChanged(nameof(BrowserHasPaired));
        }

        private RelayCommand? _cmdAdd;
        public RelayCommand CmdAdd
        {
            get
            {
                return _cmdAdd ??= new RelayCommand(async (o) =>
                {
                    var source = await DataSourceSelectorViewModel.SelectDataSourceAsync();
                    if (source == null) return;
                    var existedNames = Credentials.Where(x => x.DataSource == source).Select(x => x.Credential.Name).ToList();
                    var vm = new AlternativeCredentialEditViewModel(existedNames, showHost: false, title: IoC.Translate("Add") + " " + IoC.Translate("Credentials"))
                    {
                        RequireUserName = true,
                        RequirePassword = true,
                        RequirePrivateKey = true,
                    };
                    vm.OnSave += () =>
                    {
                        var ret = source.Database_InsertCredential(vm.New);
                        if (ret.IsSuccess)
                        {
                            if (ret.NeedReloadUI)
                            {
                                IoC.Get<GlobalData>().ReloadAll();
                            }
                            else
                            {
                                Credentials.Add(new CredentialItem(source, vm.New));
                            }
                            return true; // close the dialog
                        }
                        else
                        {
                            MessageBoxHelper.ErrorAlert(ret.ErrorInfo);
                            return false; // do not close the dialog
                        }
                    };
                    MaskLayerController.ShowWindowWithMask(vm);
                });
            }
        }


        private RelayCommand? _cmdEdit;
        public RelayCommand CmdEdit
        {
            get
            {
                return _cmdEdit ??= new RelayCommand((o) =>
                {
                    if (o is CredentialItem item)
                    {
                        var source = item.DataSource;
                        item.Credential.DecryptToConnectLevel();
                        var name = item.Credential.Name;
                        var existedNames = Credentials.Where(x => x != item).Select(x => x.Credential.Name).ToList();
                        var vm = new AlternativeCredentialEditViewModel(existedNames, org: item.Credential, showHost: false, title: IoC.Translate("Edit") + " " + IoC.Translate("Credentials"))
                        {
                            RequireUserName = true,
                            RequirePassword = true,
                            RequirePrivateKey = true,
                        };
                        vm.OnSave += () =>
                        {
                            var ret = source.Database_UpdateCredential(vm.New, name);
                            if (ret.IsSuccess)
                            {
                                if (ret.NeedReloadUI)
                                {
                                    IoC.Get<GlobalData>().ReloadAll();
                                }
                                return true;
                            }
                            else
                            {
                                MessageBoxHelper.ErrorAlert(ret.ErrorInfo);
                                return false; // do not close the dialog
                            }
                        };
                        MaskLayerController.ShowWindowWithMask(vm);
                    }
                });
            }
        }


        private RelayCommand? _cmdDelete;
        public RelayCommand CmdDelete
        {
            get
            {
                return _cmdDelete ??= new RelayCommand((o) =>
                {
                    if (o is not CredentialItem item
                        || true != MessageBoxHelper.Confirm(IoC.Translate("confirm_to_delete") + " -> " + item.Credential.Name))
                        return;

                    var ret = item.DataSource.Database_DeleteCredential(new[] { item.Credential.Name });
                    if (ret.IsSuccess)
                    {
                        if (ret.NeedReloadUI)
                        {
                            IoC.Get<GlobalData>().ReloadAll();
                        }
                        else
                        {
                            Credentials.Remove(item);
                        }
                    }
                    else
                    {
                        MessageBoxHelper.ErrorAlert(ret.ErrorInfo);
                    }
                });
            }
        }
    }
}
