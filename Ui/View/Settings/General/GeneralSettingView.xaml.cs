using System;
using System.Threading.Tasks;
using Lemora.Service;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Lemora.Utils;
using Stylet;
using Shawn.Utils;

namespace Lemora.View.Settings.General
{
    /// <summary>
    /// GeneralSettingView.xaml 的交互逻辑
    /// </summary>
    public partial class GeneralSettingView : UserControl
    {
        public GeneralSettingView()
        {
            InitializeComponent();
            Task.Factory.StartNew(async () =>
            {
                var b = await SecondaryVerificationHelper.GetEnabled();
                Execute.OnUIThread(() =>
                {
                    CbRequireWindowsPasswordBeforeSensitiveOperation.IsChecked = b;
                });
            });
        }

        private async void CbRequireWindowsPasswordBeforeSensitiveOperation_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (DataContext is GeneralSettingViewModel vm && sender is CheckBox cb)
            {
                var ret = await SecondaryVerificationHelper.VerifyAsyncUi();
                if (ret == true)
                {
                    cb.IsChecked = cb.IsChecked != true;
                    SecondaryVerificationHelper.SetEnabled(cb.IsChecked == true);
                }
            }
        }
    }
}
