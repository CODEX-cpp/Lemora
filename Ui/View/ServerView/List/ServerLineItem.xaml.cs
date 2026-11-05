using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lemora.Model;
using Lemora.View;
using Lemora.View.ServerView;
using Shawn.Utils;

namespace Lemora.Controls
{
    /// <summary>
    /// Interaction logic for ServerLineItem.xaml
    /// </summary>
    public partial class ServerLineItem : UserControl
    {
        public static readonly DependencyProperty ProtocolServerViewModelProperty =
            DependencyProperty.Register("ProtocolBaseViewModel", typeof(ProtocolBaseViewModel), typeof(ServerLineItem),
                new PropertyMetadata(null, new PropertyChangedCallback(OnDataChanged)));

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var value = (ProtocolBaseViewModel)e.NewValue;
            ((ServerLineItem)d).DataContext = value;
        }

        public ProtocolBaseViewModel? ProtocolBaseViewModel
        {
            get => GetValue(ProtocolServerViewModelProperty) as ProtocolBaseViewModel;
            set => SetValue(ProtocolServerViewModelProperty, value);
        }


        public ServerLineItem()
        {
            InitializeComponent();
        }

        private void ItemsCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            ServerListPageView.ItemsCheckBox_OnClick_Static(sender, e);
        }

        /// <summary>menu dell'utente della riga (vedi UserMenu)</summary>
        private void UserDropdown_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ProtocolBaseViewModel vm } anchor
                && vm.Server is Lemora.Model.Protocol.Base.ProtocolBaseWithAddressPortUserPwd server)
                UserMenu.Show(anchor, server, vm.UserChoices, vm.RefreshUserChoices);
        }

        private void UIElement_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // stop right click edit 
            if (e.ChangedButton == MouseButton.Right)
            {
                e.Handled = true;
            }
        }
    }
}
