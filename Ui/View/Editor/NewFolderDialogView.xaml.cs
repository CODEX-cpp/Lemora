using System.Windows;
using System.Windows.Input;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.Editor
{
    public partial class NewFolderDialogView : WindowChromeBase
    {
        public NewFolderDialogView()
        {
            InitializeComponent();
            Loaded += (s, e) => { TbName.Focus(); TbName.SelectAll(); };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) (DataContext as NewFolderDialogViewModel)?.CmdQuit.Execute(null);
                else if (e.Key == Key.Enter && DataContext is NewFolderDialogViewModel vm) { vm.CmdCreate.Execute(null); e.Handled = true; }
            };
        }
    }
}
