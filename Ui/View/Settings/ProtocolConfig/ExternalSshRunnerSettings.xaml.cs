using System;
using System.Windows;
using Lemora.Model.Protocol.FileTransmit;
using Lemora.Service;

namespace Lemora.View.Settings.ProtocolConfig
{
    public partial class ExternalSshRunnerSettings : ExternalRunnerSettingsBase
    {
        public ExternalSshRunnerSettings()
        {
            InitializeComponent();
            base.InitBindableAvalonEditor(TextEditor);
            base.InitBindableAvalonEditor(TextEditorForSshWithPrivateKey);
        }
    }
}
