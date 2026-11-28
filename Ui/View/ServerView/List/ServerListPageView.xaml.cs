using Lemora.Controls.NoteDisplay;
using Lemora.Model;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.Model;
using Lemora.Service.Locality;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View.ServerView;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Lemora.View.ServerView
{
    public partial class ServerListPageView : ServerViewBase
    {
        public ServerListPageView()
        {
            Lemora.Utils.StartDiag.Mark("ServerListPageView: construction starts");
            InitializeComponent();
            Lemora.Utils.StartDiag.Mark("ServerListPageView: XAML loaded");
            // hide GridBottom when hover.
            MouseMove += (sender, args) =>
            {
                var p = args.GetPosition(GridBottom);
                GridBottom.Visibility = p.Y > 0 ? Visibility.Collapsed : Visibility.Visible;
            };

            // Home refreshes on its own (favorites, history, quick connections)
            DataContextChanged += (sender, args) =>
            {
                if (args.NewValue is ServerListPageViewModel vm)
                {
                    vm.EnsureHomeHooked();
                    vm.RefreshHome();
                    vm.HomeSelectionChanged += item =>
                    {
                        // the highlighted entry must stay visible while scrolling
                        foreach (var ic in new[] { HomeFavList, HomeHistoryList, HomeOtherList })
                            if (ic.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement fe) { fe.BringIntoView(); break; }
                    };
                }
            };

            Loaded += (sender, args) =>
            {
                _checkBoxSelectedAll = CheckBoxSelectedAll;
                _lvServerCards = LvServerCards;
                _layoutWindow = Window.GetWindow(this);
                Lemora.Utils.StartDiag.Mark($"ServerListPageView: Loaded (window {_layoutWindow?.GetType().Name ?? "none"}, width {ActualWidth:0})");
                if (_layoutWindow != null) _layoutWindow.PreviewKeyDown += LayoutWindow_OnPreviewKeyDown;
                UpdateLayoutMode();
                Lemora.Utils.StartupSplash.HomeReady();   // at startup the Home replaces the loading skeleton from here
            };
            Unloaded += (sender, args) =>
            {
                Lemora.Utils.StartDiag.Mark($"ServerListPageView: Unloaded (visibile {IsVisible})");
                if (_layoutWindow != null) _layoutWindow.PreviewKeyDown -= LayoutWindow_OnPreviewKeyDown;
                // the other pages (tree) have a fixed bar: the search goes back to its usual position and width
                MainView?.PlaceSearch(0, HomeLayout.SearchWidth(HomeLayoutMode.Wide));
                _layoutWindow = null;
            };

            // responsive Home: layout according to the window width (see HomeLayout)
            SizeChanged += (sender, args) => UpdateLayoutMode();
            AddBox.SizeChanged += (sender, args) => PlaceSearch();
            DataContextChanged += (sender, args) =>
            {
                if (args.OldValue is ServerListPageViewModel o) o.PropertyChanged -= LayoutVm_OnPropertyChanged;
                if (args.NewValue is ServerListPageViewModel n) n.PropertyChanged += LayoutVm_OnPropertyChanged;
                UpdateLayoutMode();
            };
        }

        #region Home responsive

        private Window? _layoutWindow;
        private HomeLayoutMode? _appliedMode;
        // the main window hosting the search (with the skeleton startup the page joins the window later, which is why it is not cached)
        private static MainWindowView? MainView => IoC.Get<MainWindowViewModel>()?.View as MainWindowView;
        // folder and detail widths with a wide window (the user can drag them): restored when going back to wide
        private double _wideNavWidth = 220, _wideDetailWidth = 320;
        private static readonly System.Windows.Media.Effects.DropShadowEffect OverlayShadow = CreateShadow();

        private static System.Windows.Media.Effects.DropShadowEffect CreateShadow()
        {
            var s = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = System.Windows.Media.Colors.Black };
            s.Freeze();
            return s;
        }

        private void LayoutVm_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ServerListPageViewModel.DetailOpen) or nameof(ServerListPageViewModel.NavDrawerOpen)
                or nameof(ServerListPageViewModel.DetailServerViewModel) or nameof(ServerListPageViewModel.DetailQuick)
                or nameof(ServerListPageViewModel.FolderSummary))
                ApplyLayout();
        }

        /// <summary>medium or small window: choosing an entry opens the overlaid detail; with the square one it opens only with the &gt; arrow</summary>
        private static void OpenDetailOnSelect(ServerListPageViewModel vm)
        {
            if (vm.LayoutMode is HomeLayoutMode.Medium or HomeLayoutMode.Small) vm.DetailOpen = true;
        }

        private void UpdateLayoutMode()
        {
            if (DataContext is not ServerListPageViewModel vm || ActualWidth <= 0) return;
            var mode = HomeLayout.For(ActualWidth);
            if (vm.LayoutMode != mode)
            {
                if (vm.LayoutMode == HomeLayoutMode.Wide)
                {
                    if (ColNav.ActualWidth > 0) _wideNavWidth = ColNav.ActualWidth;
                    if (ColDetail.ActualWidth > 0) _wideDetailWidth = ColDetail.ActualWidth;
                }
                vm.LayoutMode = mode;
                vm.NavDrawerOpen = false;
                vm.DetailOpen = false;
            }
            ApplyLayout();
        }

        /// <summary>position and width of folders, detail and bar according to the mode (the parts inside the rows follow LayoutMode in the XAML)</summary>
        private void ApplyLayout()
        {
            if (DataContext is not ServerListPageViewModel vm) return;
            var mode = vm.LayoutMode;
            var wide = mode == HomeLayoutMode.Wide;
            var compact = mode is HomeLayoutMode.Small or HomeLayoutMode.Square;

            // column widths are only touched when the mode changes: with a wide window they stay as dragged by the user
            var modeChanged = _appliedMode != mode;
            _appliedMode = mode;

            // folders: full (wide), icon bar (medium), drawer menu over the list (small and square)
            if (modeChanged)
            {
                if (wide) { ColNav.MinWidth = 170; ColNav.Width = new GridLength(_wideNavWidth); }
                else { ColNav.MinWidth = 0; ColNav.Width = new GridLength(mode == HomeLayoutMode.Medium ? 56 : 0); }
            }
            ColSplitNav.Width = new GridLength(compact ? 0 : 6);
            SplitNav.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(NavIsland, compact ? 5 : 1);
            NavIsland.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            NavIsland.Width = compact ? Math.Min(260, Math.Max(200, ActualWidth - 60)) : double.NaN;
            Panel.SetZIndex(NavIsland, compact ? 20 : 0);
            NavIsland.Effect = compact ? OverlayShadow : null;
            NavIsland.Visibility = !compact || vm.NavDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
            NavDim.Visibility = compact && vm.NavDrawerOpen ? Visibility.Visible : Visibility.Collapsed;

            // detail: fixed column (wide), overlaid on the right (medium and small), in place of the list (square)
            ColSplitDetail.Width = new GridLength(wide ? 6 : 0);
            SplitDetail.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            if (wide)
            {
                if (modeChanged) { ColDetail.MinWidth = 260; ColDetail.Width = new GridLength(_wideDetailWidth); }
                Grid.SetColumn(DetailIsland, 4);
                DetailIsland.HorizontalAlignment = HorizontalAlignment.Stretch;
                DetailIsland.Width = double.NaN;
                Panel.SetZIndex(DetailIsland, 0);
                DetailIsland.Effect = null;
                DetailIsland.Visibility = Visibility.Visible;
            }
            else
            {
                ColDetail.MinWidth = 0;
                ColDetail.Width = new GridLength(0);
                Grid.SetColumn(DetailIsland, 2);
                var full = mode == HomeLayoutMode.Square;
                DetailIsland.HorizontalAlignment = full ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
                DetailIsland.Width = full ? double.NaN : 330;
                Panel.SetZIndex(DetailIsland, 10);
                DetailIsland.Effect = full ? null : OverlayShadow;
                var hasDetail = vm.DetailServerViewModel != null || vm.DetailQuick != null || vm.FolderSummary != null;
                DetailIsland.Visibility = vm.DetailOpen && hasDetail ? Visibility.Visible : Visibility.Collapsed;
            }
            var square = mode == HomeLayoutMode.Square;
            DetailBack.Visibility = square ? Visibility.Visible : Visibility.Collapsed;
            foreach (var child in DetailGrid.Children.OfType<FrameworkElement>())
                if (!ReferenceEquals(child, DetailBack)) child.Margin = new Thickness(0, square ? 40 : 0, 0, 0);

            // bar: folders button and view selector
            BtnNavMenu.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            ViewSwitch.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            ProtocolSwitch.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            PlaceSearch();
        }

        /// <summary>
        /// The search lives in the main window but is shown inside this bar: fixed width (wide and medium) or all the free space
        /// (small and square, right of the folders button); "Add" follows right after it.
        /// </summary>
        private void PlaceSearch()
        {
            if (DataContext is not ServerListPageViewModel vm || ActualWidth <= 0) return;
            var compact = vm.LayoutMode is HomeLayoutMode.Small or HomeLayoutMode.Square;
            var left = compact ? 44.0 : 0;
            double width;
            if (!compact) width = HomeLayout.SearchWidth(vm.LayoutMode);
            else
            {
                // page margins (6+6), border (1+1) and the bar's inner padding (10+10)
                var toolbar = ActualWidth - 12 - 2 - 20;
                width = Math.Max(120, toolbar - left - 13 - AddBox.ActualWidth - 2);
            }
            AddBox.Margin = new Thickness(left + width + 13, 0, 0, 0);
            MainView?.PlaceSearch(left, width);
        }

        private void NavMenu_OnClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is ServerListPageViewModel vm) vm.NavDrawerOpen = !vm.NavDrawerOpen;
        }

        private void NavDim_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ServerListPageViewModel vm) vm.NavDrawerOpen = false;
            e.Handled = true;
        }

        private void NavPick_OnClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is ServerListPageViewModel vm) vm.NavDrawerOpen = false;
        }

        private void DetailBack_OnClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is ServerListPageViewModel vm) vm.DetailOpen = false;
        }

        // overlaid detail: a click outside closes it (if the click is on another row, that row reopens it with the new entry)
        private void BodyGrid_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ServerListPageViewModel { DetailOpen: true, LayoutMode: HomeLayoutMode.Medium or HomeLayoutMode.Small } vm) return;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, DetailIsland)) return;
            vm.DetailOpen = false;
        }

        // Esc first closes the folders menu, then the overlaid or full-screen detail
        private void LayoutWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || DataContext is not ServerListPageViewModel vm || !IsVisible) return;
            if (vm.NavDrawerOpen) { vm.NavDrawerOpen = false; e.Handled = true; }
            else if (vm.DetailOpen && vm.LayoutMode != HomeLayoutMode.Wide) { vm.DetailOpen = false; e.Handled = true; }
        }

        #endregion

        private void ServerListItemSource_OnFilter(object sender, FilterEventArgs e)
        {
            // MainFilterString changed -> refresh view source -> calc visible in `ServerListItemSource_OnFilter`
            if (e.Item is ProtocolBaseViewModel server
                && DataContext is ServerPageViewModelBase vm)
            {
                if (vm.IsServerVisible.TryGetValue(server, out var flag) == false
                    || flag)
                {
                    e.Accepted = true;
                }
                else
                {
                    e.Accepted = false;
                    server.IsSelected = false;
                }
                server.SetIsVisible(e.Accepted);


                if (IoC.Get<DataSourceService>().AdditionalSources.Any())
                {
                    RefreshHeaderCheckBox();
                }
            }
        }

        private void ItemsCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            ItemsCheckBox_OnClick_Static(sender, e);
        }

        private static CheckBox? _checkBoxSelectedAll;
        private static ListBox? _lvServerCards;
        public static void ItemsCheckBox_OnClick_Static(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not CheckBox checkBox) return;
            if (_checkBoxSelectedAll == null) return;
            if (_lvServerCards == null) return;

            if (checkBox == _checkBoxSelectedAll)
            {
                var expanderList = MyVisualTreeHelper.FindVisualChilds<Expander>(_lvServerCards);
                foreach (var expander in expanderList)
                {
                    if (expander.FindName("HeaderCheckBox") is CheckBox headerCheckBox)
                    {
                        headerCheckBox.IsChecked = checkBox.IsChecked == true;
                    }
                }
            }
            if (checkBox.Name == "HeaderCheckBox")
            {
                var group = (CollectionViewGroup)checkBox.DataContext;
                foreach (var obj in group.Items)
                {
                    if (obj is ProtocolBaseViewModel item)
                        item.IsSelected = checkBox.IsChecked == true;
                }
            }
            else
            {
                var expander = MyVisualTreeHelper.VisualUpwardSearch<Expander>(checkBox);
                RefreshCheckExpanderHeaderCheckBoxState(expander);
            }
        }

        private static void RefreshCheckExpanderHeaderCheckBoxState(Expander? expander)
        {
            if (expander?.FindName("HeaderCheckBox") is CheckBox headerCheckBox)
            {
                var group = (CollectionViewGroup)expander.DataContext;
                if (group.Items.OfType<ProtocolBaseViewModel>().Any(x => x.IsSelected))
                {
                    if (group.Items.OfType<ProtocolBaseViewModel>().All(x => x.IsSelected))
                        headerCheckBox.IsChecked = true;
                    else
                        headerCheckBox.IsChecked = null;
                }
                else
                {
                    headerCheckBox.IsChecked = false;
                }
            }
        }

        private readonly DebounceDispatcher _debounceDispatcher = new();
        public void RefreshHeaderCheckBox()
        {
            if (_lvServerCards == null) return;
            Execute.OnUIThreadSync(() =>
            {
                _debounceDispatcher.Debounce(200, (obj) =>
                {
                    if (_lvServerCards != null)
                    {
                        var expanderList = MyVisualTreeHelper.FindVisualChilds<Expander>(_lvServerCards);
                        foreach (var expander in expanderList)
                        {
                            RefreshCheckExpanderHeaderCheckBoxState(expander);
                        }
                    }
                });
            });
        }

        // home (favorites and history): a click on the row selects it and shows the details, a double click connects;
        // a click on the star only toggles the favorite.
        // Before, a single click connected right away: selecting a row to then press Edit started a connection.
        private void HomeItem_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HomeItem item } || DataContext is not ServerListPageViewModel page) return;
            // a click on the star only toggles the favorite
            for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, sender); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is CheckBox or Button) return;   // star and user menu: they do not connect
            // right of the separator: opens the details in the side panel, without connecting
            for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, sender); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement { Name: "DetailsZone" })
                {
                    page.ShowHomeDetails(item);
                    page.DetailOpen = true;
                    return;
                }
            if (e.ClickCount == 1) { page.SelectHomeItem(item); OpenDetailOnSelect(page); }
            else if (e.ClickCount == 2) page.ConnectHomeItem(item);   // only the second click of the double click connects (just once)
        }

        // detail panel of a quick connection (not saved)
        private void QuickDetailConnect_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: HomeItem item } && DataContext is ServerListPageViewModel page) page.ConnectHomeItem(item);
        }

        private void QuickDetailSave_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HomeItem { Quick: { } q } }) return;
            var host = q.Host.Trim();
            var address = host.Contains(':') && host.Count(c => c == ':') == 1 ? host.Substring(0, host.IndexOf(':')) : host;
            var folder = Lemora.Service.QuickSessionFollowUp.FindFolderForAddress(address);
            var (user, pwd, key) = q.GetUserPassword();
            GlobalEventHelper.OnGoToServerAddPage?.Invoke(null, null, new Lemora.View.Editor.ServerEditorPageViewModel.ParamsServerAddPreset
            {
                Protocol = q.Protocol,
                Host = host,   // the name stays empty: whoever saves it writes it
                TreeNodes = folder == null ? new List<string>() : Lemora.Service.FolderService.ToNodes(folder),
                UserName = user,
                Password = pwd,
                PrivateKey = key,
            });
        }

        private void QuickDetailRemove_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HomeItem { Quick: { } q } } || DataContext is not ServerListPageViewModel page) return;
            Lemora.Service.Locality.LocalityConnectRecorder.QuickConnectionHistoryRemove(q);
            page.DetailQuick = null;
            page.RefreshHome();
        }

        /// <summary>user menu of the Home row (see UserMenu)</summary>
        private void HomeUser_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HomeItem item } anchor
                || item.Server?.Server is not Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd server) return;
            UserMenu.Show(anchor, server, item.UserChoices, item.RefreshUser);
        }

        private void DetailAction_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: Lemora.Model.ProtocolAction action })
            {
                // closes the drop-down menu containing the button (if any)
                DependencyObject? d = sender as DependencyObject;
                while (d != null && d is not System.Windows.Controls.Primitives.Popup)
                    d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
                if (d is System.Windows.Controls.Primitives.Popup { PlacementTarget: CheckBox cb }) cb.IsChecked = false;
                action.Run();
            }
        }

        /// <summary>right click on a row or card: entry menu (Connect and allowed actions); the event stops here</summary>
        private void ServerRow_OnRightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ProtocolBaseViewModel vm } fe) return;
            // menus of their own inside the row (e.g. tags) keep handling it
            for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, sender); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement { ContextMenu: not null }) return;
            ServerContextMenu.Show(fe, vm);
            e.Handled = true;
        }

        private ProtocolBaseViewModel? _shiftSelectStartItem = null;
        private void ServerList_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1 && sender is DependencyObject obj)
            {
                // shift or ctrl + mouse button down to select item
                if (MyVisualTreeHelper.VisualUpwardSearch<ListBoxItem>(obj) is { DataContext: ProtocolBaseViewModel vm } listBoxItem)
                {
                    if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                    {
                        vm.IsSelected = !vm.IsSelected;
                        // stop the move button in the GroupItem expander header from triggering the header's expand/collapse click
                    }
                    else if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                    {
                        if (_shiftSelectStartItem != null)
                        {
                            // shift select from _shiftSelectStartItem to listBoxItem
                            var items = LvServerCards.Items.Cast<ProtocolBaseViewModel>().ToList();
                            int startIdx = items.IndexOf(_shiftSelectStartItem);
                            int endIdx = items.IndexOf(vm);
                            if (startIdx < 0 || endIdx < 0)
                            {
                                _shiftSelectStartItem = null;
                            }
                            if (startIdx > endIdx)
                                (startIdx, endIdx) = (endIdx, startIdx);
                            for (int i = 0; i < items.Count; i++)
                            {
                                if (i >= startIdx && i <= endIdx)
                                {
                                    items[i].IsSelected = true;
                                }
                                else
                                {
                                    items[i].IsSelected = false;
                                }
                            }
                            e.Handled = true;
                        }
                    }
                    else
                    {
                        _shiftSelectStartItem = vm;
                        if (DataContext is ServerListPageViewModel pageVm) { pageVm.DetailServerViewModel = vm; OpenDetailOnSelect(pageVm); }
                    }
                }
            }
        }
        private void ServerListExpanderIcon_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DependencyObject obj)
            {
                // stop the move button in the GroupItem expander header from triggering the header's expand/collapse click
                if (MyVisualTreeHelper.VisualUpwardSearch<GroupItem>(obj) != null)
                {
                    e.Handled = true;
                }
            }
        }


        private void ServerList_PreviewMouseMoveEvent(object sender, MouseEventArgs e)
        {
            //SimpleLogHelper.Debug($"{e.LeftButton} + {sender is Grid}");
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    // drag ListBoxItem
                    if (sender is ListBoxItem { DataContext: ProtocolBaseViewModel protocol } listBoxItem
                        && LocalityListViewService.Settings.ServerOrderBy == EnumServerOrderBy.Custom
                        && protocol.HoverNoteDisplayControl?.PopupNoteContent.Content == null)
                    {
                        var dataObj = new DataObject();
                        dataObj.SetData("DragSource", listBoxItem);
                        DragDrop.DoDragDrop(listBoxItem, dataObj, DragDropEffects.Move);
                        listBoxItem.IsSelected = true;
                    }
                    // drag GroupItem
                    else if (sender is DependencyObject obj)
                    {
                        if (e.OriginalSource is DependencyObject os)
                        {
                            if (null != MyVisualTreeHelper.VisualUpwardSearch<NoteDisplayAndEditor>(os))
                            {
                                return;
                            }
                        }
                        GroupItem? groupItem = null;
                        if (sender is GroupItem gi) // drag the GroupItem itself
                        {
                            groupItem = gi;
                        }
                        else // drag an element inside the GroupItem header
                        {
                            groupItem = MyVisualTreeHelper.VisualUpwardSearch<GroupItem>(obj);
                        }
                        if (groupItem != null)
                        {
                            var dataObj = new DataObject();
                            dataObj.SetData("DragSource", groupItem);
                            DragDrop.DoDragDrop(groupItem, dataObj, DragDropEffects.Move);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var ps = new Dictionary<string, string>
                    {
                        { "Sender", sender.GetType().Name },
                        { "e.Source", e.Source.GetType().Name },
                        { "e.OriginalSource", e.OriginalSource.GetType().Name }
                    };
                    UnifyTracing.Error(ex, properties: ps);
                }
            }
        }
        private void ServerList_OnDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.OriginalSource is DependencyObject os)
                {
                    if (null != MyVisualTreeHelper.VisualUpwardSearch<NoteDisplayAndEditor>(os))
                    {
                        return;
                    }
                }

                // item move
                if (LocalityListViewService.Settings.ServerOrderBy == EnumServerOrderBy.Custom
                    && e.Data.GetData("DragSource") is ListBoxItem { DataContext: ProtocolBaseViewModel toBeMovedProtocol } listBoxItem
                    && sender is ListBoxItem { DataContext: ProtocolBaseViewModel target } targetListBoxItem
                    && toBeMovedProtocol != target)
                {
                    var items = LvServerCards.Items.Cast<ProtocolBaseViewModel>().ToList();
                    int removedIdx = items.IndexOf(toBeMovedProtocol);
                    int targetIdx = items.IndexOf(target);
#if DEBUG
                    SimpleLogHelper.Debug($"Before Drop:" + string.Join(", ", items.Select(x => x.Server.DisplayName)));
                    SimpleLogHelper.Debug($"Drop: {toBeMovedProtocol.Server.DisplayName}({removedIdx}) -> {target.Server.DisplayName}({targetIdx})");
#endif
                    bool isNextDoor = Math.Abs(removedIdx - targetIdx) == 1; // whether they are adjacent

                    int append = 0; // 0: before, 1: after
                    if (isNextDoor && removedIdx < targetIdx) // if the moved item is right before the target (adjacent), insert after the target, i.e. targetIdx += 1;
                    {
                        append = 1;
                    }
                    else if (isNextDoor == false && e.GetPosition(targetListBoxItem).Y > targetListBoxItem.ActualHeight / 2) // if they are not adjacent, the drop position decides whether to insert before or after the target
                    {
                        append = 1;
                    }

                    if (removedIdx >= 0
                        && targetIdx >= 0
                        && removedIdx != targetIdx)
                    {
                        items.RemoveAt(removedIdx);
                        targetIdx = items.IndexOf(target) + append;  // re-calc targetIdx since collection changed
                        if (targetIdx > items.Count)
                        {
                            items.Add(toBeMovedProtocol);
                        }
                        else
                        {
                            items.Insert(targetIdx, toBeMovedProtocol);
                        }
                        LocalityListViewService.ServerCustomOrderSave(items);
                        IoC.Get<ServerListPageViewModel>().CalcServerVisibleAndRefresh();
#if DEBUG
                        SimpleLogHelper.Debug($"After Drop:" + string.Join(", ", items.Select(x => x.Server.DisplayName)));
#endif
                    }
                }
                // group move
                else if (LvServerCards.IsGrouping == true
                    && e.Data.GetData("DragSource") is GroupItem { DataContext: CollectionViewGroup { Name: DataSourceBase toBeMovedDataSource } toBeMovedGroupItem }
                    && IoC.Get<DataSourceService>().AdditionalSources.Any()
                    && LvServerCards?.Items?.Groups?.Count > 0)
                {
                    DataSourceBase? targetGroup = null;
                    // GroupItem drop to ListBoxItem
                    if (sender is ListBoxItem { DataContext: ProtocolBaseViewModel { GroupDataSource: { } } protocol })
                    {
                        targetGroup = protocol.GroupDataSource;
                    }
                    // GroupItem drop to something in GroupItem
                    else if (sender is DependencyObject obj)
                    {
                        var groupItem = (sender is GroupItem gi) ? gi : MyVisualTreeHelper.VisualUpwardSearch<GroupItem>(obj);
                        if (groupItem is { DataContext: CollectionViewGroup { Name: DataSourceBase ds } })
                        {
                            targetGroup = ds;
                        }
                    }

                    if (targetGroup != null && targetGroup != toBeMovedDataSource)
                    {
                        var groups = LvServerCards.Items.Groups.Cast<CollectionViewGroup>().ToList();
                        var targetGroupItem = groups.FirstOrDefault(x => x.Name == targetGroup);
                        if (targetGroupItem != null)
                        {
                            int removedIdx = groups.IndexOf(toBeMovedGroupItem);
                            int targetIdx = groups.IndexOf(targetGroupItem);
#if DEBUG
                            SimpleLogHelper.Debug($"groups Before Drop:" + string.Join(", ", groups.Select(x => x.Name.ToString())));
                            SimpleLogHelper.Debug($"groups Drop: {toBeMovedGroupItem.Name}({removedIdx}) -> {targetGroupItem.Name}({targetIdx})");
#endif
                            // by default insert before the target
                            int append = 0; // 0: before, 1: after
                            if (Math.Abs(removedIdx - targetIdx) == 1 && removedIdx < targetIdx) // if the moved item is right before the target (adjacent), insert after the target, i.e. targetIdx += 1;
                            {
                                append = 1;
                            }
                            if (removedIdx >= 0
                                && targetIdx >= 0
                                && removedIdx != targetIdx)
                            {
                                groups.RemoveAt(removedIdx);
                                targetIdx = groups.IndexOf(targetGroupItem) + append;  // re-calc targetIdx since collection changed
                                if (targetIdx > groups.Count)
                                {
                                    groups.Add(toBeMovedGroupItem);
                                }
                                else
                                {
                                    groups.Insert(targetIdx, toBeMovedGroupItem);
                                }
                                LocalityListViewService.GroupedOrderSave(groups.Select(x => x.Name.ToString() ?? "").Where(x => string.IsNullOrEmpty(x) == false).ToArray());
                                IoC.Get<ServerListPageViewModel>().CalcServerVisibleAndRefresh();
#if DEBUG
                                SimpleLogHelper.Debug($"groups After Drop:" + string.Join(", ", groups.Select(x => x.Name.ToString())));
#endif
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var ps = new Dictionary<string, string>
                {
                    { "Sender", sender.GetType().Name },
                    { "e.Source", e.Source.GetType().Name },
                    { "e.OriginalSource", e.OriginalSource.GetType().Name }
                };
                UnifyTracing.Error(ex, properties: ps);
            }
        }

        private void TagList_PreviewMouseMoveEvent(object sender, MouseEventArgs e)
        {
            ServerPageView_TagListHelper.TagList_PreviewMouseMoveEvent(sender, e);
        }
        private void TagList_OnDrop(object sender, DragEventArgs e)
        {
            ServerPageView_TagListHelper.TagList_OnDrop(DataContext, sender, e);
        }

        // click on a navigation folder
        private void FolderNav_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FolderNavItem item && DataContext is ServerListPageViewModel vm)
            {
                if (e.ClickCount == 2) { vm.CmdFolderSettings.Execute(item); e.Handled = true; return; }
                vm.CmdSelectFolder.Execute(item);
                vm.NavDrawerOpen = false;   // drawer menu: choice made, it closes
                e.Handled = true;
            }
        }

        // subfolder in the central list: a click opens it
        private void FolderChild_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FolderNavItem item && DataContext is ServerListPageViewModel vm)
            {
                vm.CmdSelectFolder.Execute(item);
                e.Handled = true;
            }
        }

        // ⋯ button: opens the same menu as the right click
        private void FolderMenu_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var row = fe;
                while (row != null && row.ContextMenu == null) row = System.Windows.Media.VisualTreeHelper.GetParent(row) as FrameworkElement;
                if (row?.ContextMenu != null)
                {
                    row.ContextMenu.PlacementTarget = row;
                    row.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    row.ContextMenu.IsOpen = true;
                    e.Handled = true;
                }
            }
        }

        private void HeaderTag_OnClick(object sender, RoutedEventArgs e)
        {
            ServerPageView_TagListHelper.HeaderTag_OnClick(DataContext, sender, e);
        }

        private void ServerName_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Grid g && DataContext is ServerListPageViewModel vm)
            {
                vm.NameWidth = e.NewSize.Width;
            }
        }

        private void ServerNote_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Grid g && DataContext is ServerListPageViewModel vm)
            {
                vm.NoteWidth = e.NewSize.Width;
            }
        }
    }

    public class NameMaxWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double windowWidth = IoC.Get<MainWindowView>().Width;
            double free = windowWidth;
            free -= 200.0; // subtract the size of fixed columns
            free -= (double)value; // subtract the width of the note column
            free -= 20.0; // leave minimum width for the address column
            return free;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NoteMaxWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double windowWidth = IoC.Get<MainWindowView>().Width;
            double free = windowWidth;
            free -= 200.0; // subtract the size of fixed columns
            free -= (double)value; // subtract the width of the name column
            free -= 20.0; // leave minimum width for the address column
            return free;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ConverterTagNameCount : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 3
                && values[0] is string tagName
                && values[1] is int count
                && values[2] is bool isPinned)
            {
                return isPinned ? $"📌 {tagName} ({count})" : $"{tagName} ({count})";
            }
            return values[0];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }




    //public class ConverterGroupIsSelected : IMultiValueConverter
    //{
    //    /*****
    //        <DataTrigger.Binding>
    //            <MultiBinding Converter="{StaticResource ConverterIsEqual}" >
    //                <Binding RelativeSource="{RelativeSource FindAncestor, AncestorType=view:ServerListPageView}" Path="DataContext.SelectedTabName" Mode="OneWay"></Binding>
    //                <Binding Path="Name" Mode="OneWay"></Binding>
    //            </MultiBinding>
    //        </DataTrigger.Binding>
    //     */
    //    public object? Convert(object[] value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    //    {
    //        if (value.Length == 2
    //            && value[0] is IEnumerable<ProtocolBaseViewModel> protocolBaseViewModels
    //            && value[1] is DataSourceBase dataSource)
    //        {
    //            if (protocolBaseViewModels.Where(x => x.Server.DataSource == dataSource).Any(x => x.IsSelected))
    //            {
    //                if (protocolBaseViewModels.Where(x => x.Server.DataSource == dataSource).All(x => x.IsSelected))
    //                    return true;
    //                return null;
    //            }
    //        }
    //        return false;
    //    }
    //    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    //    {
    //        throw new NotSupportedException();
    //    }
    //}
}