using System.Windows.Input;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.Editor
{
    public partial class UserChooserDialogView : WindowChromeBase
    {
        public UserChooserDialogView()
        {
            InitializeComponent();
            PreviewKeyUp += (sender, e) =>
            {
                if (e.Key == Key.Escape) (DataContext as UserChooserDialogViewModel)?.CmdQuit?.Execute(null);
            };
        }
    }
}
