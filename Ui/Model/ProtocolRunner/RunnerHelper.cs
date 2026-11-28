using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Model.ProtocolRunner.Default;
using Lemora.Service;
using Lemora.Utils;
using Lemora.Utils.PuTTY;
using Lemora.View.Host;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.Model.ProtocolRunner
{
    public static class RunnerHelper
    {
        /// <summary>
        /// get a selected runner, or default runner.
        /// </summary>
        public static Runner GetRunner(ProtocolConfigurationService protocolConfigurationService, ProtocolBase server, string protocolName, string? assignRunnerName = null)
        {
            if (protocolConfigurationService.ProtocolConfigs.TryGetValue(protocolName, out var p) == false)
            {
                //SimpleLogHelper.Debug($"we can not customize runner for protocol: {protocolName}");
                return new InternalDefaultRunner(protocolName);
            }

            if (p.Runners.Count == 0)
            {
                //SimpleLogHelper.Debug($"we don't have any runner for protocol: {protocolName}");
                return new InternalDefaultRunner(protocolName);
            }

            var r = p.Runners.FirstOrDefault(x => x.Name == assignRunnerName);
            r ??= p.Runners.FirstOrDefault(x => x.Name == server.SelectedRunnerName);
            r ??= p.Runners.FirstOrDefault(x => x.Name == p.SelectedRunnerName);
            r ??= p.Runners.FirstOrDefault();
            // KiTTY (old configurations only) knows only -pw: the SSH password would stay on the command line, readable from process logs.
            // With a password PuTTY is used, which reads it from a file (-pwfile). Security hardening.
            if (r is KittyRunner && server is SSH && p.Runners.OfType<PuttyRunner>().FirstOrDefault() is { } putty)
            {
                Shawn.Utils.SimpleLogHelper.Info("SSH: KiTTY chosen, PuTTY is used (the password does not go on the command line)");
                return putty;
            }
            // FreeRDP on Windows has no cameras or COM ports (rdpecam and serial channels missing in the Windows build): whoever has them
            // enabled opens the session with the Microsoft component, so nothing is lost. Not when FreeRDP is requested explicitly (Claude, menu)
            if (r is FreeRdpRunner && string.IsNullOrEmpty(assignRunnerName) && server is RDP { } rdp && NeedsMicrosoftClient(rdp)
                && p.Runners.OfType<InternalDefaultRunner>().FirstOrDefault() is { } ms)
            {
                Shawn.Utils.SimpleLogHelper.Info("RDP: cameras or COM ports enabled, the Microsoft component is used (FreeRDP does not have them on Windows)");
                return ms;
            }
            return r ?? new InternalDefaultRunner(protocolName);
        }

        /// <summary>settings FreeRDP on Windows cannot handle: with these the Microsoft component is used</summary>
        public static bool NeedsMicrosoftClient(RDP rdp) => rdp.EnableRedirectCameras == true || rdp.EnablePorts == true;

        public static bool IsRunWithoutHosting(this Runner runner)
        {
            return runner is ExternalRunner { RunWithHosting: false };
        }

        public static void RunWithoutHosting(this Runner runner, ProtocolBase protocol)
        {
            if (runner is not ExternalRunner er) return;
            var (isOk, exePath, exeArguments, environmentVariables) = er.GetStartInfo(protocol);
            if (!isOk) return;

            var startInfo = new ProcessStartInfo();
            if (environmentVariables?.Count > 0)
                foreach (var kv in environmentVariables)
                    startInfo.EnvironmentVariables[kv.Key] = NoNul(kv.Value);

            startInfo.UseShellExecute = false;
            startInfo.FileName = exePath;
            startInfo.Arguments = exeArguments;
            var process = new Process() { StartInfo = startInfo };
            SessionControlService.AddUnHostingWatch(process, protocol);
            process.EnableRaisingEvents = true;
            process.Start();
        }


        /// <summary>
        /// return (noError?, exePath, exeArguments, environmentVariables)
        /// </summary>
        /// <summary>a NUL inside a value ends the variable in the environment block and the rest becomes another variable (security hardening)</summary>
        internal static string NoNul(string? v) => (v ?? "").Replace("\0", "");

        private static Tuple<bool, string, string, Dictionary<string, string>> GetStartInfo(this Runner runner, ProtocolBase protocol)
        {
            string exePath = "";
            string exeArguments = "";
            var environmentVariables = new Dictionary<string, string>();
            if (runner is ExternalRunner er)
            {
                exePath = er.ExePath;
                // prepare args
                exeArguments = er.Arguments;
                if (runner is ExternalRunnerForSSH runnerForSsh)
                {
                    switch (protocol)
                    {
                        case SSH ssh when string.IsNullOrEmpty(ssh.PrivateKey) == false:
                        case SFTP sftp when string.IsNullOrEmpty(sftp.PrivateKey) == false:
                            var pw = protocol as ProtocolBaseWithAddressPortUserPwd;
                            // if private key is not all ascii, copy it to temp file
                            if (pw?.IsPrivateKeyAllAscii() == false && File.Exists(pw.PrivateKey))
                            {
                                var pk = Path.Combine(Path.GetTempPath(), new FileInfo(pw.PrivateKey).Name);
                                File.Copy(pw.PrivateKey, pk, true);
                                var autoDelTask = new Task(() =>
                                {
                                    Thread.Sleep(30 * 1000);
                                    try
                                    {
                                        if (File.Exists(pk))
                                            File.Delete(pk);
                                    }
                                    catch
                                    {
                                        // ignored
                                    }
                                });
                                autoDelTask.Start();
                                pw.PrivateKey = pk;
                            }
                            exeArguments = runnerForSsh.ArgumentsForPrivateKey;
                            break;
                    }
                }

                // make environment variables
                foreach (var kv in er.EnvironmentVariables)
                {
                    environmentVariables.Add(kv.Key, OtherNameAttributeExtensions.Replace(protocol, ProtocolBaseWithAddressPort.NormalizeMacros(kv.Value)));
                }


                // FreeRDP: parameters built from the server's RDP settings, passed in an environment variable of that process only
                // (the password does not end up on the command line, visible to Task Manager and the Windows logs)
                if (runner is FreeRdpRunner fr && protocol is RDP freeRdp)
                {
                    environmentVariables[FreeRdpRunner.ArgsEnvName] = fr.BuildEnvValue(freeRdp);
                    fr.TryResolveExe(out _);
                    return new Tuple<bool, string, string, Dictionary<string, string>>(CheckExe(runner, fr.ExePath, out var full), full,$"/args-from:env:{FreeRdpRunner.ArgsEnvName}", environmentVariables);
                }

                // Percent-encoding, some password may contain special characters, SFTP\XFTP need to encode them.
                // see: issue 673 of the original project
                // ref: https://winscp.net/eng/docs/session_url#special
                er.ApplySpecialCharacters(protocol);
            }
            else if (runner is InternalExeRunner ir)
            {
                exePath = ir.GetExeInstallPath(); // TODO: WHAT IF A CUSTOM EXE PATH?
                // always, not only when missing: Install compares the file with the one inside Lemora and rewrites it if it differs.
                // The data folder may be writable by others and PuTTY/KiTTY receive the password (security hardening)
                ir.Install();
                exeArguments = ir.GetExeArguments(protocol);
            }
            else
            {
                SimpleLogHelper.Error($"GetStartInfo: Runner '{runner.Name}' is not supported!");
                return new Tuple<bool, string, string, Dictionary<string, string>>(false, "", "",
                    new Dictionary<string, string>());
            }

            // check exe path exists
            var tmp = WinCmdRunner.CheckFileExistsAndFullName(exePath);
            if (tmp.Item1 == false)
            {
                MessageBoxHelper.ErrorAlert($"Exe file '{exePath}' of runner '{runner.Name}' does not existed!");
                return new Tuple<bool, string, string, Dictionary<string, string>>(false, "", "",
                    new Dictionary<string, string>());
            }
            exePath = tmp.Item2;
            if (runner is ExternalRunner)
            {
                // user, address, password... from a .mora or an import must not add options or commands (security hardening)
                var template = ProtocolBaseWithAddressPort.NormalizeMacros(exeArguments);
                var bad = CmdArg.UnsafeMacro(protocol, template, exePath);
                if (bad != null)
                {
                    MessageBoxHelper.ErrorAlert(IoC.Translate("runner_unsafe_value", bad, runner.Name));
                    return new Tuple<bool, string, string, Dictionary<string, string>>(false, "", "", new Dictionary<string, string>());
                }
            }
            exeArguments = OtherNameAttributeExtensions.Replace(protocol, ProtocolBaseWithAddressPort.NormalizeMacros(exeArguments));
            return new Tuple<bool, string, string, Dictionary<string, string>>(true, exePath, exeArguments, environmentVariables);
        }

        private static bool CheckExe(Runner runner, string exePath, out string fullName)
        {
            var tmp = WinCmdRunner.CheckFileExistsAndFullName(exePath);
            fullName = tmp.Item2;
            if (tmp.Item1) return true;
            MessageBoxHelper.ErrorAlert(IoC.Translate("runner_exe_not_found", exePath, runner.Name));
            return false;
        }

        public static HostBase GetHost(this Runner runner, ProtocolBase protocol, TabWindowView? tab = null, System.Windows.Size? area = null)
        {
            Debug.Assert(runner.IsRunWithoutHosting() == false);

            if (runner is TerminalRunner && protocol is SSH sshTerminal)
                return TerminalHost.Create(sshTerminal);

            if (runner is ExternalRunner er)
            {
                // custom runner
                var (isOk, exePath, exeArguments, environmentVariables) = er.GetStartInfo(protocol);
                if (isOk)
                {
                    var integrateHost = IntegrateHost.Create(protocol, runner, exePath, exeArguments, environmentVariables);
                    return integrateHost;
                }
            }
            if (runner is InternalExeRunner ir)
            {
                // default runner
                var (isOk, exePath, exeArguments, environmentVariables) = ir.GetStartInfo(protocol);
                if (isOk)
                {
                    var integrateHost = IntegrateHost.Create(protocol, runner, exePath, exeArguments, environmentVariables);
                    return integrateHost;
                }
            }

            // build-in runner
            switch (protocol)
            {
                case RDP rdp:
                    {
                        var size = area ?? tab?.GetTabContentSize(ColorAndBrushHelper.ColorIsTransparent(protocol.ColorHex) == true);
                        return AxMsRdpClient09Host.Create(rdp, (int)(size?.Width ?? 0), (int)(size?.Height ?? 0));
                    }
                case VNC vnc:
                    {
                        return VncHost.Create(vnc);
                    }
                case SFTP sftp:
                    {
                        return FileTransmitHost.Create(sftp);
                    }
                case FTP ftp:
                    {
                        return FileTransmitHost.Create(ftp);
                    }
                case LocalApp app:
                    {
                        return IntegrateHost.Create(app, runner, app.GetExePath(), app.GetArguments(false));
                    }
                default:
                    break;
            }
            SimpleLogHelper.Fatal($"Host of {protocol.GetType()} is not implemented, or the runner ${runner.Name} is not supported");
            throw new NotImplementedException($"Host of {protocol.GetType()} is not implemented, or the ${runner.Name} is not supported");
        }
    }
}