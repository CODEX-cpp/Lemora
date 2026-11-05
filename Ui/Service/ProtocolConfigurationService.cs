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
            // le impostazioni dei programmi stanno nel database cifrato; la cartella dei vecchi file .json serve solo per importarli
            Load(AppPathHelper.Instance.ProtocolRunnerDirPath);
        }


        private void Load(string protocolFolderName)
        {
            // build-in protocol
            //ProtocolConfigs.Add(RDP.ProtocolName, InitProtocol(protocolFolderName, new RDP(), new InternalDefaultRunner(RDP.ProtocolName), $"Built-in AxMsRdpClient"));
            //ProtocolConfigs[RDP.ProtocolName].Runners.Clear();
            //ProtocolConfigs[RDP.ProtocolName].Runners.Add(new Runner("Built-in AxMsRdpClient", RDP.ProtocolName));
            //ProtocolConfigs[RDP.ProtocolName].Runners.Add(new Runner("Mstsc.exe", RDP.ProtocolName));
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

            // comandi salvati con le variabili di prima (altro prefisso o nessuno): si portano a %LM_...% e si risalva
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
#pragma warning disable CS0612 // Params e' il vecchio contenitore: va ancora aggiornato per le configurazioni gia' salvate
                        foreach (var p in er.Params.Keys.ToList()) er.Params[p] = Fix(er.Params[p]);
#pragma warning restore CS0612
                        foreach (var t in er.EnvironmentVariables) t.Value = Fix(t.Value);
                    }
                }
            }
            // terminale integrato come programma predefinito per SSH: una volta sola (poi la scelta dell'utente resta)
            try
            {
                if (Lemora.Service.Locality.AppDbStore.TryGet("terminal.default", out var done) && string.IsNullOrEmpty(done))
                {
                    ProtocolConfigs[SSH.ProtocolName].SelectedRunnerName = TerminalRunner.RunnerName;
                    saveFlag = true;
                    Lemora.Service.Locality.AppDbStore.Set("terminal.default", "1");
                }
                // il database e' condiviso con le versioni piu' vecchie del programma, che non conoscono questo tipo di programma:
                // se una versione precedente di questa lo ha scritto nell'elenco, lo si riscrive senza
                if (Lemora.Service.Locality.AppDbStore.TryGet(DbKey(SSH.ProtocolName), out var sshJson) && sshJson?.Contains("\"TerminalRunner\"") == true)
                    saveFlag = true;
            }
            catch (Exception e) { SimpleLogHelper.Warning(e.Message); }

            if (saveFlag)
            {
                Save();
            }
        }


        private static string DbKey(string protocolNameUpper) => "runner." + protocolNameUpper.ToUpper();

        public static ProtocolSettings? LoadConfig(string protocolFolderName, string protocolName)
        {
            protocolName = protocolName.ToUpper();
            var file = Path.Combine(protocolFolderName, $"{protocolName}.json");
            // dal database (importando il vecchio file la prima volta); se il database non e' ancora pronto si legge il file senza toccarlo
            var jsonStr = Lemora.Service.Locality.AppDbStore.ReadOrMigrate(DbKey(protocolName), file, out var dbReady);
            if (!dbReady && File.Exists(file))
                jsonStr = File.ReadAllText(file, Encoding.UTF8);
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

                    // 读取 Runners 数组
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

            // RealVNC va aggiunto anche a una config già salvata (fuori dal controllo "lista vuota")
            if (VNC.ProtocolName == protocolName && c.Runners.All(x => x.Name != "RealVNC"))
                c.Runners.Add(new ExternalRunner("RealVNC", protocolName)
                {
                    ExePath = @"C:\Program Files\RealVNC\VNC Viewer\vncviewer.exe",
                    // RealVNC Viewer 7 non ha un parametro per la password in chiaro: le credenziali le chiede lui
                    Arguments = @"%LM_HOSTNAME%:%LM_PORT%",
                    RunWithHosting = false,
                });

            // terminale SSH integrato: disponibile tra i programmi, ma non predefinito finche' non e' provato
            if (SSH.ProtocolName == protocolName && c.Runners.All(x => x is not TerminalRunner))
                c.Runners.Add(new TerminalRunner(protocolName) { Name = TerminalRunner.RunnerName });

            if (c.Runners.FirstOrDefault() is InternalDefaultRunner == false
                || c.Runners.FirstOrDefault()?.Name != defaultRunnerName)
            {
                c.Runners.RemoveAll(x => x is InternalDefaultRunner);
                c.Runners.Insert(0, defaultRunner);
            }

            // 最后赋值，确保无论是从配置加载的还是上面初始化的 InternalDefaultRunner 名称正确
            c.Runners.First(x => x is InternalDefaultRunner).Name = defaultRunnerName;

            // programma predefinito di un profilo nuovo: VNC con quello integrato, SSH con Windows Terminal (chi ha gia' scelto resta com'e')
            if (isNewConfig)
            {
                if (VNC.ProtocolName == protocolName) c.SelectedRunnerName = defaultRunnerName;
                else if (SSH.ProtocolName == protocolName) c.SelectedRunnerName = TerminalRunner.RunnerName;
            }
            return c;
        }

        private System.Windows.Threading.DispatcherTimer? _delayedSaveTimer;

        /// <summary>
        /// salvataggio automatico delle impostazioni dei programmi: parte poco dopo l'ultima modifica,
        /// cosi' scrivendo in un campo non si riscrive il file a ogni lettera
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
                    // il terminale integrato non si scrive nell'elenco: e' sempre presente e le versioni piu' vecchie del programma
                    // (stesso database) non conoscono il suo tipo e perderebbero l'intera configurazione dei programmi SSH
                    var jo = JObject.FromObject(config);
                    if (jo["Runners"] is JArray ra)
                        foreach (var r in ra.Where(x => (string?)x["$type"] == nameof(TerminalRunner)).ToList()) r.Remove();
                    var json = jo.ToString(Formatting.Indented);
                    if (Lemora.Service.Locality.AppDbStore.Set(DbKey(protocolName), json))
                    {
                        // salvato nel database: l'eventuale vecchio file non serve piu'
                        if (File.Exists(file)) File.Delete(file);
                    }
                    else
                    {
                        // database non ancora pronto: file temporaneo, verra' importato al prossimo avvio
                        Directory.CreateDirectory(AppPathHelper.Instance.ProtocolRunnerDirPath);
                        File.WriteAllText(file, json, Encoding.UTF8);
                    }
                }, actionOnError: exception => UnifyTracing.Error(exception));
            }
        }
    }
}
