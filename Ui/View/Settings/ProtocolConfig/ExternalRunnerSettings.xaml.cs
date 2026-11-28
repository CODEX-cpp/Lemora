using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.CodeCompletion;
using Lemora.Model.Protocol.FileTransmit;
using Lemora.Service;

namespace Lemora.View.Settings.ProtocolConfig
{
    public partial class ExternalRunnerSettings : ExternalRunnerSettingsBase
    {
        public ExternalRunnerSettings()
        {
            InitializeComponent();
            base.InitBindableAvalonEditor(TextEditor);
        }
    }
}
