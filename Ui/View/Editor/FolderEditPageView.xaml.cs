using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lemora.View.Editor
{
    /// <summary>folder edit page (see FolderEditPageViewModel): only focuses the name when coming from "Rename"</summary>
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
