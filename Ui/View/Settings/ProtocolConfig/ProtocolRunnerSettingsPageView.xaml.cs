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

        private void FreeRdpSelectExe_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Lemora.Model.ProtocolRunner.FreeRdpRunner r) return;
            string? dir = null;
            try { dir = System.IO.Path.GetDirectoryName(r.ExePath); } catch { /* invalid path: start from the default folder */ }
            var path = Shawn.Utils.Wpf.FileSystem.SelectFileHelper.OpenFile(filter: "exe|*.exe", checkFileExists: true, initialDirectory: dir);
            if (path != null) r.ExePath = path;
        }
    }
}
