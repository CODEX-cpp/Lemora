using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lemora.Service.FileTransfer;

namespace Lemora.View.FileTransfer
{
    public partial class FileTransferPopupView : UserControl
    {
        public FileTransferPopupView()
        {
            InitializeComponent();
            DragEnter += (_, e) => Over(e);
            DragOver += (_, e) => Over(e);
            DragLeave += (_, _) => DropZone.Tag = null;
            Drop += async (_, e) =>
            {
                DropZone.Tag = null;
                var paths = FileTransferShell.GetLocalPaths(e.Data);
                if (paths.Count > 0 && DataContext is FileTransferViewModel vm) { e.Handled = true; await vm.DropAsync(paths); }
            };
        }

        private void Over(DragEventArgs e)
        {
            var ok = FileTransferShell.HasFiles(e.Data) && DataContext is FileTransferViewModel { IsUnavailable: false };
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DropZone.Tag = ok ? "Over" : null;
            e.Handled = true;
        }
    }
}
