using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using Lemora.Model.Protocol;
using Lemora.Model.ProtocolRunner;
using Lemora.Service;
using Lemora.Utils;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf;
using Shawn.Utils.Wpf.Controls;
using Shawn.Utils.Wpf.FileSystem;

namespace Lemora.View.Settings.ProtocolConfig;

public class ExternalRunnerSettingsViewModel : IDisposable
{
    private readonly ILanguageService _languageService;
    private readonly PropertyChangedEventHandler? _propertyChangedHandler;
    public ExternalRunner ExternalRunner { get; }

    public ExternalRunnerSettingsViewModel(ExternalRunner externalRunner, ILanguageService languageService)
    {
        ExternalRunner = externalRunner;
        _languageService = languageService;

        _propertyChangedHandler = (sender, args) =>
        {
            if (args.PropertyName == nameof(Model.ProtocolRunner.ExternalRunner.ExePath))
            {
                AutoArguments();
            }
            IoC.Get<ProtocolConfigurationService>().Save();
        };
        ExternalRunner.PropertyChanged += _propertyChangedHandler;
    }

    public void Dispose()
    {
        // Unsubscribe from event to prevent memory leak
        if (_propertyChangedHandler != null)
        {
            ExternalRunner.PropertyChanged -= _propertyChangedHandler;
        }
    }

    private void AutoArguments()
    {
        if (string.IsNullOrEmpty(ExternalRunner.Arguments))
        {
            var path = ExternalRunner.ExePath;
            var name = new FileInfo(path).Name.ToLower();
            if (name.IndexOf("winscp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (ExternalRunner.OwnerProtocolName == FTP.ProtocolName)
                {
                    ExternalRunner.Arguments = "ftp://%LM_USERNAME%:%LM_PASSWORD%@%LM_HOSTNAME%:%LM_PORT%";
                }
                else if (ExternalRunner.OwnerProtocolName == SFTP.ProtocolName)
                {
                    ExternalRunner.Arguments = "sftp://%LM_USERNAME%:%LM_PASSWORD%@%LM_HOSTNAME%:%LM_PORT%";
                    if (ExternalRunner is ExternalRunnerForSSH ers)
                        ers.ArgumentsForPrivateKey = @"sftp://%LM_USERNAME%@%LM_HOSTNAME%:%LM_PORT% /privatekey=%LM_PRIVATE_KEY_PATH%";
                }
                ExternalRunner.RunWithHosting = true;
            }


            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && name.IndexOf("filezilla", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (ExternalRunner.OwnerProtocolName == FTP.ProtocolName)
                {
                    ExternalRunner.Arguments = "ftp://%LM_USERNAME%:%LM_PASSWORD%@%LM_HOSTNAME%";
                }
                else if (ExternalRunner.OwnerProtocolName == SFTP.ProtocolName)
                {
                    ExternalRunner.Arguments = "sftp://%LM_USERNAME%:%LM_PASSWORD%@%LM_HOSTNAME%";
                }
                ExternalRunner.RunWithHosting = false;
            }



            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && ExternalRunner.OwnerProtocolName == SSH.ProtocolName
                && name.IndexOf("kitty", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExternalRunner.Arguments = @"-ssh %LM_HOSTNAME% -P %LM_PORT% -l %LM_USERNAME% -pw %LM_PASSWORD% -%SSH_VERSION% -cmd ""%STARTUP_AUTO_COMMAND%""";
                if (ExternalRunner is ExternalRunnerForSSH ers)
                {
                    // NOT SUPPORTED
                    ers.ArgumentsForPrivateKey = @"";
                }
                ExternalRunner.RunWithHosting = true;
            }

            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && ExternalRunner.OwnerProtocolName == SSH.ProtocolName
                && name.IndexOf("putty", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExternalRunner.Arguments = @"-ssh %LM_HOSTNAME% -P %LM_PORT% -l %LM_USERNAME% -pw %LM_PASSWORD% -%SSH_VERSION%";
                if (ExternalRunner is ExternalRunnerForSSH ers)
                {
                    // NOT SUPPORTED
                    ers.ArgumentsForPrivateKey = @"";
                }
                ExternalRunner.RunWithHosting = true;
            }


            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && (name.IndexOf("wt.exe", StringComparison.OrdinalIgnoreCase) >= 0
                    || name == "wt"
                ))
            {
                if (ExternalRunner.OwnerProtocolName == SSH.ProtocolName)
                {
                    ExternalRunner.Arguments = @"-w 1 new-tab --title ""%LM_HOSTNAME%"" --suppressApplicationTitle plink -ssh %LM_HOSTNAME% -P %LM_PORT% -%SSH_VERSION% -C -X -no-antispoof -l %LM_USERNAME% -pw %LM_PASSWORD%";
                    if (ExternalRunner is ExternalRunnerForSSH ers && string.IsNullOrEmpty(ers.ArgumentsForPrivateKey))
                    {
                        ers.ArgumentsForPrivateKey = @"-w 1 new-tab --title ""%LM_HOSTNAME%"" --suppressApplicationTitle plink -ssh %LM_HOSTNAME% -P %LM_PORT% -%SSH_VERSION% -C -X -no-antispoof -l %LM_USERNAME% -i %LM_PRIVATE_KEY_PATH%";
                    }
                }
                ExternalRunner.RunWithHosting = false;
            }




            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && ExternalRunner.OwnerProtocolName == VNC.ProtocolName
                && name.IndexOf("VpxClient", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExternalRunner.Arguments = @"-s %LM_HOSTNAME% -u %LM_USERNAME% -p %LM_PASSWORD%";
                ExternalRunner.RunWithHosting = true;
            }


            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && ExternalRunner.OwnerProtocolName == VNC.ProtocolName
                && name.IndexOf("tvnviewer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ExternalRunner.Arguments = @"%LM_HOSTNAME%::%LM_PORT% -password=%LM_PASSWORD% -scale=auto";
                ExternalRunner.RunWithHosting = true;
            }



            if (string.IsNullOrEmpty(ExternalRunner.Arguments)
                && ExternalRunner.OwnerProtocolName == VNC.ProtocolName
                && (name.IndexOf("vncviewer", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("uvnc", StringComparison.OrdinalIgnoreCase) >= 0
                ))
            {
                ExternalRunner.Arguments = @"%LM_HOSTNAME%:%LM_PORT% -password=%LM_PASSWORD%";
                ExternalRunner.RunWithHosting = false;
            }
        }
    }


    private RelayCommand? _cmdSelectDbPath;
    [JsonIgnore]
    public RelayCommand CmdSelectExePath
    {
        get
        {
            return _cmdSelectDbPath ??= new RelayCommand((o) =>
            {
                string? initPath = null;
                try
                {
                    initPath = new FileInfo(ExternalRunner.ExePath).DirectoryName;
                }
                catch
                {
                    // ignored
                }

                var path = SelectFileHelper.OpenFile(filter: "exe|*.exe", checkFileExists: true, initialDirectory: initPath);
                if (path == null) return;
                ExternalRunner.ExePath = path;
            });
        }
    }


    private RelayCommand? _cmdAddEnvironmentVariable;
    public RelayCommand CmdAddEnvironmentVariable
    {
        get
        {
            return _cmdAddEnvironmentVariable ??= new RelayCommand((o) =>
            {
                ExternalRunner.EnvironmentVariables.Add(new ExternalRunner.ObservableKvp<string, string>("", ""));
            });
        }
    }

    private RelayCommand? _cmdDelEnvironmentVariable;
    public RelayCommand CmdDelEnvironmentVariable
    {
        get
        {
            return _cmdDelEnvironmentVariable ??= new RelayCommand((o) =>
            {
                if (o is ExternalRunner.ObservableKvp<string, string> item
                    && ExternalRunner.EnvironmentVariables.Contains(item)
                    && ((item.Key == "" && item.Value == "")
                        || true == MessageBoxHelper.Confirm(IoC.Translate("confirm_to_delete"), ownerViewModel: IoC.Get<MainWindowViewModel>()))
                   )
                {
                    ExternalRunner.EnvironmentVariables.Remove(item);
                    IoC.Get<ProtocolConfigurationService>().Save();
                }
            });
        }
    }


    private RelayCommand? _cmdAddSpecialCharacter;
    public RelayCommand CmdAddSpecialCharacter
    {
        get
        {
            return _cmdAddSpecialCharacter ??= new RelayCommand((o) =>
            {
                ExternalRunner.SpecialCharacters.Add(new ExternalRunner.ObservableKvp<string, string>("", ""));
            });
        }
    }


    private RelayCommand? _cmdDelSpecialCharacter;
    public RelayCommand CmdDelSpecialCharacter
    {
        get
        {
            return _cmdDelSpecialCharacter ??= new RelayCommand((o) =>
            {
                if (o is ExternalRunner.ObservableKvp<string, string> item
                    && ExternalRunner.SpecialCharacters.Contains(item)
                    && ((item.Key == "" && item.Value == "")
                        || true == MessageBoxHelper.Confirm(IoC.Translate("confirm_to_delete"), ownerViewModel: IoC.Get<MainWindowViewModel>()))
                   )
                {
                    ExternalRunner.SpecialCharacters.Remove(item);
                    IoC.Get<ProtocolConfigurationService>().Save();
                }
            });
        }
    }


    private RelayCommand? _cmdCopyJsonAndShare;
    public RelayCommand CmdCopyJsonAndShare
    {
        get
        {
            return _cmdCopyJsonAndShare ??= new RelayCommand((o) =>
            {
                try
                {
                    Clipboard.SetDataObject(
                        $@"
Runner for {ExternalRunner.OwnerProtocolName}

```
{JsonConvert.SerializeObject(ExternalRunner, Formatting.Indented)}
```
"
                        );
                    // prima proponeva di condividerlo online nel progetto originale: ora si copia e basta
                }
                catch
                {
                    // ignored
                }
            });
        }
    }
}