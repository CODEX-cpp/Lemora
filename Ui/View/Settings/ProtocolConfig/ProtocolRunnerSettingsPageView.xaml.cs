using System.Windows;
using System.Windows.Controls;
using Lemora.Service;
using Lemora.Service.DataSource;
using Shawn.Utils.Interface;
using Shawn.Utils.Wpf.Controls;

namespace Lemora.View.Settings.ProtocolConfig
{
    public partial class ProtocolRunnerSettingsPageView : UserControl
    {
        public ProtocolRunnerSettingsPageView()
        {
            InitializeComponent();
        }

        private void TerminalSettings_Click(object sender, RoutedEventArgs e)
        {
            Lemora.View.Host.ProtocolHosts.TerminalSettingsWindow.ShowSingle(Window.GetWindow(this));
        }
    }
}
