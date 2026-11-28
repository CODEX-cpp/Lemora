using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Lemora.Model;
using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Model.ProtocolRunner;
using Lemora.Model.ProtocolRunner.Default;
using Shawn.Utils;
using Lemora.Utils;
using Lemora.Utils.Tracing;

namespace Lemora.Service
{
    public class ProtocolConfigurationService
    {
        public Dictionary<string, ProtocolSettings> ProtocolConfigs { get; } = new Dictionary<string, ProtocolSettings>();


        private static List<string> _customProtocolBlackList = new List<string>();
        /// <summary>
        /// Protocol name in this list can not be custom
        /// </summary>
        public static List<string> CustomProtocolBlackList
        {
            get
            {
                if (_customProtocolBlackList.Count == 0)
                {
                    var protocolList = ProtocolBase.GetAllSubInstance();
                    var names = protocolList.Select(x => x.Protocol);
                    _customProtocolBlackList = names.ToList();
                }
                return _customProtocolBlackList;
            }
        }


        public ProtocolConfigurationService()
        {
            // the runner settings live in the encrypted database; the folder of the old .json files is only used to import them
            Load(AppPathHelper.Instance.ProtocolRunnerDirPath);
        }


        private void Load(string protocolFolderName)
        {
            // build-in protocol
            //ProtocolConfigs.Add(RDP.ProtocolName, InitProtocol(protocolFolderName, new RDP(), new InternalDefaultRunner(RDP.ProtocolName), $"Built-in AxMsRdpClient"));
            //ProtocolConfigs[RDP.ProtocolName].Runners.Clear();
            //ProtocolConfigs[RDP.ProtocolName].Runners.Add(new Runner("Built-in AxMsRdpClient", RDP.ProtocolName));
            //ProtocolConfigs[RDP.ProtocolName].Runners.Add(new Runner("Mstsc.exe", RDP.ProtocolName));
            // RDP: the Microsoft component stays the default (same path as always); FreeRDP is an extra, to be chosen
            ProtocolConfigs.Add(RDP.ProtocolName, InitProtocol(protocolFolderName, new RDP(), new InternalDefaultRunner(RDP.ProtocolName), RdpBuiltInName));
            ProtocolConfigs.Add(VNC.ProtocolName, InitProtocol(protocolFolderName, new VNC(), new InternalDefaultRunner(VNC.ProtocolName), $"Built-in VNC"));
            ProtocolConfigs.Add(SSH.ProtocolName, InitProtocol(protocolFolderName, new SSH(), new PuttyRunner(SSH.ProtocolName), PuttyRunner.Name));
            ProtocolConfigs.Add(Telnet.ProtocolName, InitProtocol(protocolFolderName, new Telnet(), new PuttyRunner(Telnet.ProtocolName), PuttyRunner.Name));
            ProtocolConfigs.Add(Serial.ProtocolName, InitProtocol(protocolFolderName, new Serial(), new PuttyRunner(Serial.ProtocolName), PuttyRunner.Name));
            ProtocolConfigs.Add(SFTP.ProtocolName, InitProtocol(protocolFolderName, new SFTP(), new InternalDefaultRunner(SFTP.ProtocolName), $"Built-in SFTP"));
            ProtocolConfigs.Add(FTP.ProtocolName, InitProtocol(protocolFolderName, new FTP(), new InternalDefaultRunner(FTP.ProtocolName), $"Built-in FTP"));


            // add macros to ExternalRunner
            foreach (var config in ProtocolConfigs)
            {
                var protocolName = config.Key;
                foreach (var runner in config.Value.Runners)
                {
                    if (runner is ExternalRunner er)
                    {
                        er.MarcoNames = config.Value.MarcoNames;
                        runner.OwnerProtocolName = protocolName;
                    }
                }
            }

            // commands saved with the old variables (other prefix or none): converted to %LM_...% and saved again
            bool saveFlag = false;
            string Fix(string v) { var n = ProtocolBaseWithAddressPort.NormalizeMacros(v); if (n != (v ?? "")) saveFlag = true; return n; }
            foreach (var config in ProtocolConfigs)
            {
                foreach (var runner in config.Value.Runners)
                {
                    if (runner is ExternalRunner er)
                    {
                        er.Arguments = Fix(er.Arguments);
                        if (er is ExternalRunnerForSSH ssh) ssh.ArgumentsForPrivateKey = Fix(ssh.ArgumentsForPrivateKey);
#pragma warning disable CS0612 // Params is the old container: it must still be updated for already saved configurations
                        foreach (var p in er.Params.Keys.ToList()) er.Params[p] = Fix(er.Params[p]);
#pragma warning restore CS0612
                        foreach (var t in er.EnvironmentVariables) t.Value = Fix(t.Value);
                    }
                }
            }
            // built-in terminal as default runner for SSH: only once (then the user's choice stays)
            try
            {
                if (Lemora.Service.Locality.AppDbStore.TryGet("terminal.default", out var done) && string.IsNullOrEmpty(done))
                {
                    ProtocolConfigs[SSH.ProtocolName].SelectedRunnerName = TerminalRunner.RunnerName;
                    saveFlag = true;
                    Lemora.Service.Locality.AppDbStore.Set("terminal.default", "1");
                }
                // FreeRDP as default RDP runner: only once, and only if sdl-freerdp.exe exists
                // (without it every connection would fail; retried at the next launch). Then the user's choice stays
                if (Lemora.Service.Locality.AppDbStore.TryGet("freerdp.default", out var frDone) && string.IsNullOrEmpty(frDone)
                    && ProtocolConfigs.TryGetValue(RDP.ProtocolName, out var rdpCfg)
                    && rdpCfg.Runners.FirstOrDefault(x => x is FreeRdpRunner) is FreeRdpRunner fr && fr.TryResolveExe(out _))
                {
                    rdpCfg.SelectedRunnerName = fr.Name;
                    saveFlag = true;
                    Lemora.Service.Locality.AppDbStore.Set("freerdp.default", "1");
                }
                // the database is shared with older versions of the program, which do not know this runner type:
                // if a previous version of this one wrote it into the list, the list is rewritten without it
                if (Lemora.Service.Locality.AppDbStore.TryGet(DbKey(SSH.ProtocolName), out var sshJson) && sshJson?.Contains("\"TerminalRunner\"") == true)
                    saveFlag = true;
            }
            catch (Exception e) { SimpleLogHelper.Warning(e.Message); }

            if (saveFlag)
            {
                Save();
            }
        }


        public const string RdpBuiltInName = "Built-in RDP (Microsoft)";

        private static string DbKey(string protocolNameUpper) => "runner." + protocolNameUpper.ToUpper();

        public static ProtocolSettings? LoadConfig(string protocolFolderName, string protocolName)
        {
            protocolName = protocolName.ToUpper();
            var file = Path.Combine(protocolFolderName, $"{protocolName}.json");
            // from the database (importing the old file the first time); if the database is not ready yet the file is read without touching it
            var jsonStr = Lemora.Service.Locality.AppDbStore.ReadOrMigrate(DbKey(protocolName), file, out var dbReady, out var fromFile);
            if (!dbReady && File.Exists(file))
            {
                jsonStr = File.ReadAllText(file, Encoding.UTF8);
                fromFile = true;
            }
            if (jsonStr != null)
            {
                try
                {
                    var c = JsonConvert.DeserializeObject<ProtocolSettings>(jsonStr);
                    if (c != null)
                    {
                        foreach (var runner in c.Runners)
                        {
                            runner.OwnerProtocolName = protocolName;
                        }
                        // the file in the data folder is neither encrypted nor signed: whoever writes it without the key must not be able to pick their own
                        // external program, which would receive the passwords at every connection. Fall back to the default runner (security hardening)
                        if (fromFile && c.Runners.FirstOrDefault(r => r.Name == c.SelectedRunnerName) is ExternalRunner er)
                        {
                            SimpleLogHelper.Warning($"Protocol {protocolName}: the external program '{er.Name}' comes from the file {Path.GetFileName(file)} and is not selected automatically; the default one is used");
                            c.SelectedRunnerName = "";
                        }
                        return c;
                    }
                }
                catch (Exception)
                {
                    // ignored
                }

                // fallback to manual parse
                try
                {
                    var c = new ProtocolSettings();
                    var jObject = JObject.Parse(jsonStr);
                    if (jObject["SelectedRunnerName"] == null || jObject["Runners"] == null)
                    {
                        return null;
                    }
                    c.SelectedRunnerName = jObject["SelectedRunnerName"]!.ToString();

                    // read the Runners array
                    var runners = (JArray)jObject["Runners"]!;
                    foreach (var runnerJObj in runners)
                    {
                        var runnerJson = runnerJObj.ToString();
                        try
                        {
                            var runner = JsonConvert.DeserializeObject<Runner>(runnerJson);
                            if (runner != null)
                            {
                                runner.OwnerProtocolName = protocolName;
                                c.Runners.Add(runner);
                            }
                        }
                        catch (Exception e)
                        {
                            UnifyTracing.Error(e);
                            SimpleLogHelper.Error(e);
                        }
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            return null;
        }


        private static ProtocolSettings InitProtocol<T, T2>(string protocolFolderName, T protocolBase, T2 defaultRunner, string defaultRunnerName) where T : ProtocolBase where T2 : Runner
        {
            var protocolName = protocolBase.Protocol;
            var macros = OtherNameAttributeExtensions.GetOtherNames(protocolBase.GetType()); // get property name for auto complete
            var loaded = LoadConfig(protocolFolderName, protocolName);
            var c = loaded ?? new ProtocolSettings();
            var isNewConfig = loaded == null;
            c.Init(macros.Select(x => x.Value).ToList(), macros.Select(x => x.Key).ToList());
            if (c.Runners.Count == 0 || c.Runners.All(x => x is InternalDefaultRunner))
            {
                c.Runners ??= new List<Runner>();
                if (VNC.ProtocolName == protocolName)
                {
                    if (c.Runners.All(x => x.Name != "UltraVNC"))
                        c.Runners.Add(new ExternalRunner("UltraVNC", protocolName)
                        {
                            ExePath = @"C:\Program Files (x86)\uvnc\vncviewer.exe",
                            Arguments = @"%LM_HOSTNAME%:%LM_PORT% -password %LM_PASSWORD%",
                            RunWithHosting = false,
                        });
                    if (c.Runners.All(x => x.Name != "TightVNC"))
                        c.Runners.Add(new ExternalRunner("TightVNC", protocolName)
                        {
                            ExePath = @"C:\Program Files\TightVNC\tvnviewer.exe",
                            Arguments = @"%LM_HOSTNAME%::%LM_PORT% -password=%LM_PASSWORD% -scale=auto",
                            RunWithHosting = true,
                            EnvironmentVariables = new ObservableCollection<ExternalRunner.ObservableKvp<string, string>>(new[] { new ExternalRunner.ObservableKvp<string, string>("VNC_PASSWORD", "%LM_PASSWORD%") }),
                        });
                }
                if (SFTP.ProtocolName == protocolName)
                {
                    if (c.Runners.All(x => x.Name != "WinSCP"))
                        c.Runners.Add(new ExternalRunnerForSSH("WinSCP", protocolName)
                        {
                            ExePath = @"C:\Program Files (x86)\WinSCP\WinSCP.exe",
                            Arguments = @"sftp://%LM_USERNAME%:%LM_PASSWORD%@%LM_HOSTNAME%:%LM_PORT%",
                            ArgumentsForPrivateKey = @"sftp://%LM_USERNAME%@%LM_HOSTNAME%:%LM_PORT% /privatekey=%LM_PRIVATE_KEY_PATH%",
                        });
                }
                if (SSH.ProtocolName == protocolName)
                {
                    if (c.Runners.All(x => x.Name != "Putty"))
                        c.Runners.Add(new ExternalRunnerForSSH("Putty", protocolName)
                        {
                            ExePath = @"D:\PuTTY.exe",
                            Arguments = @"-ssh %LM_HOSTNAME% -P %LM_PORT% -l %LM_USERNAME% -pw %LM_PASSWORD% -%SSH_VERSION% -cmd ""%STARTUP_AUTO_COMMAND%""",
                            ArgumentsForPrivateKey = @"-w 1 new-tab --title ""%LM_HOSTNAME%"" --suppressApplicationTitle plink -ssh %LM_HOSTNAME% -P %LM_PORT% -%SSH_VERSION% -C -X -no-antispoof -l %LM_USERNAME% -i %LM_PRIVATE_KEY_PATH%",
                        });
                    if (c.Runners.All(x => x.Name != "Windows Terminal"))
                        c.Runners.Add(new ExternalRunnerForSSH("Windows Terminal", protocolName)
                        {
                            ExePath = @"wt.exe",
                            Arguments = @"-w 1 new-tab --title ""%LM_HOSTNAME%"" --suppressApplicationTitle plink -ssh %LM_HOSTNAME% -P %LM_PORT% -%SSH_VERSION% -C -X -no-antispoof -l %LM_USERNAME% -pw %LM_PASSWORD%",
                            ArgumentsForPrivateKey = @"-w 1 new-tab --title ""%LM_HOSTNAME%"" --suppressApplicationTitle plink -ssh %LM_HOSTNAME% -P %LM_PORT% -%SSH_VERSION% -C -X -no-antispoof -l %LM_USERNAME% -i %LM_PRIVATE_KEY_PATH%",
                        });
                }
            }

            if (RDP.ProtocolName == protocolName && c.Runners.All(x => x is not FreeRdpRunner))
                c.Runners.Add(new FreeRdpRunner(protocolName));

            // RealVNC must be added to an already saved config too (outside the "empty list" check)
            if (VNC.ProtocolName == protocolName && c.Runners.All(x => x.Name != "RealVNC"))
                c.Runners.Add(new ExternalRunner("RealVNC", protocolName)
                {
                    ExePath = @"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe",
                    // RealVNC Viewer 7 has no parameter for a plaintext password: it asks for the credentials itself
                    Arguments = @"%LM_HOSTNAME%:%LM_PORT%",
                    RunWithHosting = false,
                });

            // built-in SSH terminal: available among the runners, but not the default until it is tested
            if (SSH.ProtocolName == protocolName && c.Runners.All(x => x is not TerminalRunner))
                c.Runners.Add(new TerminalRunner(protocolName) { Name = TerminalRunner.RunnerName });

            if (c.Runners.FirstOrDefault() is InternalDefaultRunner == false
                || c.Runners.FirstOrDefault()?.Name != defaultRunnerName)
            {
                c.Runners.RemoveAll(x => x is InternalDefaultRunner);
                c.Runners.Insert(0, defaultRunner);
            }

            // assigned last, so the InternalDefaultRunner name is correct whether it was loaded from the config or initialized above
            c.Runners.First(x => x is InternalDefaultRunner).Name = defaultRunnerName;

            // default runner of a new profile: VNC with the built-in one, SSH with Windows Terminal (whoever already chose keeps their choice)
            if (isNewConfig)
            {
                if (VNC.ProtocolName == protocolName || RDP.ProtocolName == protocolName) c.SelectedRunnerName = defaultRunnerName;
                else if (SSH.ProtocolName == protocolName) c.SelectedRunnerName = TerminalRunner.RunnerName;
            }
            return c;
        }

        private System.Windows.Threading.DispatcherTimer? _delayedSaveTimer;

        /// <summary>
        /// automatic save of the runner settings: starts shortly after the last change,
        /// so typing in a field does not rewrite the file at every letter
        /// </summary>
        public void SaveDelayed(int milliseconds = 700)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { Save(); return; }
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_delayedSaveTimer == null)
                {
                    _delayedSaveTimer = new System.Windows.Threading.DispatcherTimer();
                    _delayedSaveTimer.Tick += (_, _) =>
                    {
                        _delayedSaveTimer.Stop();
                        Save();
                    };
                }
                _delayedSaveTimer.Stop();
                _delayedSaveTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
                _delayedSaveTimer.Start();
            }));
        }

        public void Save()
        {
            foreach (var kv in ProtocolConfigs)
            {
                var protocolName = kv.Key;
                var config = kv.Value;
                var file = Path.Combine(AppPathHelper.Instance.ProtocolRunnerDirPath, $"{protocolName.ToUpper()}.json");
                RetryHelper.Try(() =>
                {
                    // the built-in terminal is not written into the list: it is always present and older versions of the program
                    // (same database) do not know its type and would lose the whole SSH runner configuration
                    var jo = JObject.FromObject(config);
                    if (jo["Runners"] is JArray ra)
                        foreach (var r in ra.Where(x => (string?)x["$type"] == nameof(TerminalRunner)).ToList()) r.Remove();
                    var json = jo.ToString(Formatting.Indented);
                    if (Lemora.Service.Locality.AppDbStore.Set(DbKey(protocolName), json))
                    {
                        // saved in the database: any old file is no longer needed
                        if (File.Exists(file)) File.Delete(file);
                    }
                    else
                    {
                        // database not ready yet: temporary file, it will be imported at the next launch
                        Directory.CreateDirectory(AppPathHelper.Instance.ProtocolRunnerDirPath);
                        File.WriteAllText(file, json, Encoding.UTF8);
                    }
                }, actionOnError: exception => UnifyTracing.Error(exception));
            }
        }
    }
}
