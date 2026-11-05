using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Model.ProtocolRunner;
using Lemora.Model.ProtocolRunner.Default;
using Lemora.Service;
using Lemora.Utils;
using Lemora.View;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.Model;

public static class ProtocolActionHelper
{
    public static List<ProtocolAction> GetActions(this ProtocolBase server, bool forTabHeader = false)
    {
        bool writable = server.DataSource?.IsWritable != false;
        #region Build Actions
        var actions = new List<ProtocolAction>();
        var connectCount = 0;
        {
            if (!forTabHeader)
            {
                if (IoC.Get<SessionControlService>().TabWindowCount > 0)
                {
                    actions.Add(new ProtocolAction(
                        actionName: IoC.Translate("Connect (New window)"),
                        action: () => { GlobalEventHelper.OnRequestServerConnect?.Invoke(server, fromView: $"{nameof(LauncherWindowView)} - Action - New window", assignTabToken: DateTime.Now.Ticks.ToString()); }
                    ));
                }

                if (server is ProtocolBaseWithAddressPortUserPwd { AlternateCredentials.Count: > 0 } protocol)
                {
                    foreach (var credential in protocol.AlternateCredentials)
                    {
                        actions.Add(new ProtocolAction(
                            actionName: IoC.Translate("Connect") + $" ({IoC.Translate("with alternative")} `{credential.Name}`)",
                            action: () => { GlobalEventHelper.OnRequestServerConnect?.Invoke(server, fromView: $"{nameof(LauncherWindowView)} - Action - AlternateCredentials", assignCredentialName: credential.Name); }
                        ));
                    }
                }

                // external runners
                var protocolConfigurationService = IoC.Get<ProtocolConfigurationService>();
                if (protocolConfigurationService.ProtocolConfigs.ContainsKey(server.Protocol)
                    && protocolConfigurationService.ProtocolConfigs[server.Protocol].Runners.Count > 1)
                {
                    //actions.Add(new ProtocolAction(IoC.Translate("Connect") + $" (Internal)", () => { GlobalEventHelper.OnRequestServerConnect?.Invoke(server.Id, assignRunnerName: protocolConfigurationService.ProtocolConfigs[server.Protocol].Runners.First().Name, fromView: nameof(LauncherWindowView)); }));
                    foreach (var runner in protocolConfigurationService.ProtocolConfigs[server.Protocol].Runners)
                    {
                        if (runner is InternalDefaultRunner) continue;
                        if (runner is ExternalRunner { IsExeExisted: false }) continue;
                        actions.Add(new ProtocolAction(IoC.Translate("Connect") + $" (via {runner.Name})", () =>
                        {
                            GlobalEventHelper.OnRequestServerConnect?.Invoke(server, fromView: $"{nameof(LauncherWindowView)} - Action - {runner.Name}", assignRunnerName: runner.Name);
                        }));
                    }
                }
            }

            connectCount = actions.Count;   // tutto quello aggiunto prima sono varianti di connessione
            if (writable)
            {
                actions.Add(new ProtocolAction(IoC.Translate("Edit"), () =>
                {
                    GlobalEventHelper.OnRequestGoToServerEditPage?.Invoke(server: server, showAnimation: false);
                }));
                actions.Add(new ProtocolAction(IoC.Translate("Duplicate"), () =>
                {
                    GlobalEventHelper.OnRequestGoToServerDuplicatePage?.Invoke(server: server, showAnimation: false);
                }));
                if (!forTabHeader)
                {
                    actions.Add(new ProtocolAction(IoC.Get<ILanguageService>().Translate("Delete"), () =>
                    {
                        if (true == MessageBoxHelper.Confirm(Lemora.Setup.TeamsSync.DeleteConfirmText(new[] { server }, "confirm_to_delete_selected"), ownerViewModel: IoC.Get<MainWindowViewModel>()))
                        {
                            IoC.Get<GlobalData>().DeleteServer(new[] { server });
                        }
                    }));
                }
            }
        };


        if (server is ProtocolBaseWithAddressPort protocolServerWithAddrPortBase)
        {
            actions.Add(new ProtocolAction(IoC.Translate("server_card_operate_copy_address"),
                () =>
                {
                    try
                    {
                        Clipboard.SetDataObject(
                            IoC.TryGet<ConfigurationService>()?.General.CopyPortWhenCopyAddress == false
                                ? $"{protocolServerWithAddrPortBase.Address}"
                                : $"{protocolServerWithAddrPortBase.Address}:{protocolServerWithAddrPortBase.GetPort()}");
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }));
        }


        if (writable)
        {
            if (server is ProtocolBaseWithAddressPortUserPwd tmp)
            {
                actions.Add(new ProtocolAction(IoC.Translate("server_card_operate_copy_username"),
                    () =>
                    {
                        try
                        {
                            Clipboard.SetDataObject(tmp.UserName);
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }));
            }

            if (server is ProtocolBaseWithAddressPortUserPwd protocolServerWithAddrPortUserPwdBase)
            {
                actions.Add(new ProtocolAction(IoC.Translate("server_card_operate_copy_password"),
                    action: async () =>
                    {
                        if (await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;

                        try
                        {
                            Clipboard.SetDataObject(DataVault.DecryptOrReturnOriginalString(protocolServerWithAddrPortUserPwdBase.Password));
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }));
            }
        }


        actions.Add(new ProtocolAction(IoC.Translate("Create desktop shortcut"), () =>
        {
            var iconPath = AppStartupHelper.MakeIcon(server.Id, server.IconImg);
            AppStartupHelper.InstallDesktopShortcutByUlid(server.DisplayName, new[] { server.Id }, iconPath);
        }));


        if (server is SSH ssh)
        {
            actions.Add(new ProtocolAction(IoC.Translate("Open SFTP"), () =>
            {
                // open SFTP when SSH is connected.
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
                GlobalEventHelper.OnRequestServerConnect?.Invoke(sftp, fromView: $"{nameof(LauncherWindowView)} - Action - Open SFTP", assignTabToken: DateTime.Now.Ticks.ToString());
            }));
        }


        if (server is RDP rdp)
        {
            actions.Add(new ProtocolAction(Lemora.Setup.SetupText.T("export_rdp"), () =>
            {
                var path = SelectFileHelper.SaveFile(filter: "rdp|*.rdp", selectedFileName: rdp.DisplayName + ".rdp");
                if (string.IsNullOrEmpty(path)) return;
                // origine in sola lettura (Teams senza permesso di scrittura): il file si esporta SENZA password (chi non puo' scrivere non puo' leggerla)
                var source = rdp;
                if (!writable) { source = (RDP)rdp.Clone(); source.Password = ""; }
                var text = source.ToRdpConfig().ToString();
                File.WriteAllText(path, text);
            }));
        }

        // collegamento .mora: apre la connessione con un doppio clic (senza password); si sceglie la cartella dove salvarlo
        if (server is ProtocolBaseWithAddressPort)
            actions.Add(new ProtocolAction(Lemora.Setup.SetupText.T("export_link"), () => MoraService.ExportAs(server)));


        // categorie per il pannello dettaglio
        var tEdit = IoC.Translate("Edit");
        var tDuplicate = IoC.Translate("Duplicate");
        var tDelete = IoC.Get<ILanguageService>().Translate("Delete");
        var tCopy = new[] { IoC.Translate("server_card_operate_copy_address"), IoC.Translate("server_card_operate_copy_username"), IoC.Translate("server_card_operate_copy_password") };
        var tSftp = IoC.Translate("Open SFTP");
        var tExport = new[] { Lemora.Setup.SetupText.T("export_rdp"), Lemora.Setup.SetupText.T("export_link") };
        for (var i = 0; i < actions.Count; i++)
        {
            var act = actions[i];
            act.Kind = i < connectCount ? ActionKind.Connect
                : act.ActionName == tEdit ? ActionKind.Edit
                : act.ActionName == tDuplicate ? ActionKind.Duplicate
                : act.ActionName == tDelete ? ActionKind.Delete
                : Array.IndexOf(tCopy, act.ActionName) >= 0 ? ActionKind.Copy
                : act.ActionName == tSftp ? ActionKind.Connect
                : Array.IndexOf(tExport, act.ActionName) >= 0 ? ActionKind.Export
                : ActionKind.Other;
        }

        #endregion Build Actions

        return actions;
    }

    public static List<ProtocolAction> GetActions(this ProtocolBaseViewModel vm)
    {
        var server = vm.Server;
        return server.GetActions();
    }
}