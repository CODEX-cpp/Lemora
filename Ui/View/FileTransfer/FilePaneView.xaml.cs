using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lemora.Service.FileTransfer;

namespace Lemora.View.FileTransfer
{
    public partial class FilePaneView : UserControl
    {
        private const string RemoteFormat = "LM.FT.Remote";
        private Point _dragStart;
        private bool _dragCandidate;
        private ListViewItem? _dropItem;

        public FilePaneView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => HookUnavailable();
        }

        private FilePaneViewModel? Pane => DataContext as FilePaneViewModel;

        /// <summary>colonna remota: se il trasferimento file non e' disponibile, un riquadro con il motivo copre l'elenco</summary>
        private void HookUnavailable()
        {
            if (Pane is not { IsRemote: true } p) { UnavailableOverlay.Visibility = Visibility.Collapsed; return; }
            var vm = p.Navigator.Vm;
            void Sync()
            {
                UnavailableOverlay.Visibility = vm.IsUnavailable ? Visibility.Visible : Visibility.Collapsed;
                UnavTitle.Text = vm.UnavailableTitle; UnavText.Text = vm.UnavailableText;
                UnavButton.Content = string.IsNullOrEmpty(vm.UnavailableAction) ? FtText.T("ft_retry") : vm.UnavailableAction;
                UnavButton.Command = vm.CmdUnavailableAction;
            }
            vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FileTransferViewModel.IsUnavailable) or nameof(FileTransferViewModel.UnavailableTitle) or nameof(FileTransferViewModel.UnavailableText) or nameof(FileTransferViewModel.UnavailableAction)) Sync(); };
            Sync();
        }

        private void List_OnSelectionChanged(object sender, SelectionChangedEventArgs e) { if (Pane is { } p) p.Selection = List.SelectedItems.Cast<PaneItem>().ToList(); }
        private void List_OnGotFocus(object sender, KeyboardFocusChangedEventArgs e) { if (Pane is { } p) p.Navigator.ActivePane = p; }
        private void Header_OnClick(object sender, RoutedEventArgs e) { if (sender is GridViewColumnHeader { Tag: string key } && Pane is { } p) p.CmdSort.Execute(key); }

        private static string? PathOf(PaneItem i, bool remote) => remote ? i.Remote?.FullPath : i.LocalPath;

        private void Open(PaneItem? item)
        {
            if (item is not { IsDirectory: true } || Pane is not { } p) return;
            if (PathOf(item, p.IsRemote) is { } path) _ = p.GoAsync(path);
        }

        private void List_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindItem(e.OriginalSource as DependencyObject) is { } it) Open(it.DataContext as PaneItem);
        }

        private void List_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Pane is not { } p) return;
            var nav = p.Navigator; nav.ActivePane = p;
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            switch (e.Key)
            {
                case Key.Enter: Open(p.Selection.FirstOrDefault()); e.Handled = true; break;
                case Key.Back: p.CmdUp.Execute(null); e.Handled = true; break;
                case Key.Left when alt: p.CmdBack.Execute(null); e.Handled = true; break;
                case Key.Right when alt: p.CmdForward.Execute(null); e.Handled = true; break;
                case Key.F5: p.CmdRefresh.Execute(null); e.Handled = true; break;
                case Key.F2: _ = nav.RenameAsync(); e.Handled = true; break;
                case Key.F7: p.CmdNewFolder.Execute(null); e.Handled = true; break;
                case Key.Delete when p.IsRemote: _ = nav.DeleteRemoteAsync(); e.Handled = true; break;
                case Key.C when ctrl && p.IsRemote: nav.CmdCopyRemote.Execute(null); e.Handled = true; break;
                case Key.V when ctrl && p.IsRemote: nav.CmdPasteRemote.Execute(null); e.Handled = true; break;
            }
        }

        #region trascinamento

        private static ListViewItem? FindItem(DependencyObject? d)
        {
            while (d != null && d is not ListViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as ListViewItem;
        }

        private void List_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragCandidate = FindItem(e.OriginalSource as DependencyObject)?.IsSelected == true;   // si trascina solo partendo da una riga gia' selezionata
        }

        private void List_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !_dragCandidate || Pane is not { } p || p.Selection.Count == 0) return;
            var d = e.GetPosition(null) - _dragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragCandidate = false;
            DataObject? data = null;
            if (p.IsRemote)
            {
                var items = p.Selection.Where(i => i.Remote is { IsDrive: false }).Select(i => i.Remote!).ToList();
                if (items.Count > 0) { data = new DataObject(); data.SetData(RemoteFormat, items); }
            }
            else
            {
                var paths = p.Selection.Where(i => i.LocalPath != null).Select(i => i.LocalPath!).ToList();
                if (paths.Count > 0) data = FileTransferShell.CreateDragData(paths);   // anche Esplora risorse la accetta
            }
            if (data != null) DragDrop.DoDragDrop(List, data, DragDropEffects.Copy);
        }

        private void SetDropItem(ListViewItem? it)
        {
            if (ReferenceEquals(_dropItem, it)) return;
            if (_dropItem != null) _dropItem.Tag = null;
            _dropItem = it;
            if (_dropItem != null) _dropItem.Tag = "DropTarget";
        }

        private bool Accepts(IDataObject data) => Pane is { } p && (p.IsRemote ? FileTransferShell.HasFiles(data) && !p.Navigator.Vm.IsUnavailable : data.GetDataPresent(RemoteFormat));

        private void List_OnDragOver(object sender, DragEventArgs e)
        {
            var ok = Accepts(e.Data);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            // una cartella sotto il cursore diventa la destinazione (bordo accento tratteggiato), altrimenti la cartella aperta
            var it = ok ? FindItem(e.OriginalSource as DependencyObject) : null;
            SetDropItem(it?.DataContext is PaneItem { IsDirectory: true } ? it : null);
            e.Handled = true;
        }

        private void List_OnDragLeave(object sender, DragEventArgs e) => SetDropItem(null);

        private async void List_OnDrop(object sender, DragEventArgs e)
        {
            var folder = _dropItem?.DataContext as PaneItem;
            SetDropItem(null);
            if (Pane is not { } p || !Accepts(e.Data)) return;
            e.Handled = true;
            if (p.IsRemote) await p.Navigator.DropOnRemoteAsync(FileTransferShell.GetLocalPaths(e.Data), folder?.Remote?.FullPath);
            else if (e.Data.GetData(RemoteFormat) is List<RemoteEntry> items) p.Navigator.DropOnLocal(items, folder?.LocalPath);
        }

        #endregion
    }
}
