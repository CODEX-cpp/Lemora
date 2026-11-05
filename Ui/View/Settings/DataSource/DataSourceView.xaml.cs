using System.Windows;
using System.Windows.Controls;

namespace Lemora.View.Settings.DataSource
{
    public partial class DataSourceView : UserControl
    {
        public DataSourceView()
        {
            InitializeComponent();
        }

        // dopo la scelta nel menu "Aggiungi" il menu si chiude
        private void AddSourceItem_OnClick(object sender, RoutedEventArgs e)
        {
            CbAddSource.IsChecked = false;
        }
    }
}
