using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lemora.View.Editor
{
    /// <summary>pagina di modifica cartella (vedi FolderEditPageViewModel): solo il focus sul nome quando si arriva da "Rinomina"</summary>
    public partial class FolderEditPageView : UserControl
    {
        public FolderEditPageView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (DataContext is FolderEditPageViewModel { FocusName: true })
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { NameBox.Focus(); NameBox.SelectAll(); });
            };
        }
    }
}
