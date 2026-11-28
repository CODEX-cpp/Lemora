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

        // after a choice in the "Add" menu, the menu closes
        private void AddSourceItem_OnClick(object sender, RoutedEventArgs e)
        {
            CbAddSource.IsChecked = false;
        }
    }
}
