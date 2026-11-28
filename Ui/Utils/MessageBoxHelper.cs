using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Lemora.View;
using Lemora.View.Utils;
using Shawn.Utils.Interface;
using Stylet;
using Screen = Stylet.Screen;

namespace Lemora.Utils
{
    public static class MessageBoxHelper
    {
        [DllImport("user32.dll")]
        private static extern int EnableWindow(IntPtr handle, bool enable);
        /// <summary>
        /// show a confirm box on owner, the default value owner is MainWindowViewModel
        /// </summary>
        public static bool Confirm(string content, string title = "", bool useNativeBox = false, object? ownerViewModel = null)
        {
            return Confirm(content,
                IoC.TryGet<ILanguageService>()?.Translate("Yes") ?? "Yes",
                IoC.TryGet<ILanguageService>()?.Translate("No") ?? "No",
                title, useNativeBox, ownerViewModel);
        }

        /// <summary>
        /// show a confirmation box on owner, the default value owner is MainWindowViewModel
        /// </summary>
        /// <param name="defaultNo">default button "No": an accidental Enter does not confirm (extension pairing)</param>
        public static bool Confirm(string content, string yesButtonText, string noButtonText, string title = "", bool useNativeBox = false, object? ownerViewModel = null, bool defaultNo = false)
        {
            // like Alert: everything on the UI thread (a call from a Task read the window's View and failed)
            bool ret = false;
            Execute.OnUIThreadSync(() => ret = ConfirmOnUi(content, yesButtonText, noButtonText, title, useNativeBox, ownerViewModel, defaultNo));
            return ret;
        }

        private static bool ConfirmOnUi(string content, string yesButtonText, string noButtonText, string title, bool useNativeBox, object? ownerViewModel, bool defaultNo = false)
        {
            if (string.IsNullOrEmpty(title))
                title = IoC.Translate("Warning");

            var ownerViewAware = (ownerViewModel as IViewAware);
            var mainWindowViewModel = IoC.TryGet<MainWindowViewModel>();
            if (useNativeBox
                || ownerViewModel != null && ownerViewAware == null     // owner is set and owner is not IViewAware
                || ownerViewModel == null && mainWindowViewModel?.View is Window { ShowInTaskbar: false }) // owner not set and MainWindow is hidden
            {
                var ret = MessageBoxResult.Yes == MessageBox.Show(content, title, MessageBoxButton.YesNo, MessageBoxImage.Question, defaultNo ? MessageBoxResult.No : MessageBoxResult.Yes);
                return ret;
            }
            else
            {
                IMaskLayerContainer? layerContainer = null;
                if (ownerViewModel is IMaskLayerContainer mlc)
                {
                    layerContainer = mlc;
                }
                else if (ownerViewModel == null)
                {
                    layerContainer = mainWindowViewModel;
                }
                long layerId = 0;
                if (layerContainer != null)
                    layerId = MaskLayerController.ShowProcessingRing(assignLayerContainer: layerContainer);
                var vm = IoC.Get<IMessageBoxViewModel>();
                vm.Setup(messageBoxText: content,
                    caption: title,
                    icon: MessageBoxImage.Question,
                    buttons: MessageBoxButton.YesNo,
                    defaultResult: defaultNo ? MessageBoxResult.No : MessageBoxResult.None,
                    buttonLabels: new Dictionary<MessageBoxResult, string>()
                    {
                        { MessageBoxResult.Yes, yesButtonText},
                        { MessageBoxResult.No, noButtonText},
                    });
                if (vm is Screen screen)
                {
                    screen.Activated += MessageBoxOnActivated;
                }
                Execute.OnUIThreadSync(() =>
                {
                    IoC.Get<IWindowManager>().ShowDialog(vm, ownerViewAware ?? mainWindowViewModel);
                });
                var ret = MessageBoxResult.Yes == vm.ClickedButton;
                if (layerContainer != null)
                    MaskLayerController.HideMask(layerId, layerContainer);
                return ret;
            }
        }

        /// <summary>
        /// question with three answers (Yes / No / Cancel with the given labels); Cancel is the default button, so an accidental Enter does not accept.
        /// Any thread
        /// </summary>
        public static MessageBoxResult Ask3(string content, string yesText, string noText, string cancelText, string title)
        {
            var ret = MessageBoxResult.Cancel;
            Execute.OnUIThreadSync(() =>
            {
                var main = IoC.TryGet<MainWindowViewModel>();
                if (main?.View is not Window { ShowInTaskbar: true })
                {
                    // main window hidden: Windows box, whose buttons cannot be renamed, so their meaning is written in the text
                    ret = MessageBox.Show(string.Format(IoC.Translate("util_msgbox_three_buttons"), content, yesText, noText, cancelText), title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                    return;
                }
                var vm = IoC.Get<IMessageBoxViewModel>();
                // Cancel is the default button; the "cancel" role is left unset (it falls on the last button, Cancel too): giving the same
                // button both roles made Setup throw and the connection close
                vm.Setup(messageBoxText: content, caption: title, icon: MessageBoxImage.Warning, buttons: MessageBoxButton.YesNoCancel,
                    defaultResult: MessageBoxResult.Cancel,
                    buttonLabels: new Dictionary<MessageBoxResult, string> { { MessageBoxResult.Yes, yesText }, { MessageBoxResult.No, noText }, { MessageBoxResult.Cancel, cancelText } });
                if (vm is Screen screen) screen.Activated += MessageBoxOnActivated;
                // the dimming with the spinner only now, and always removed: after the error above it stayed on (second spinner)
                var layerId = MaskLayerController.ShowProcessingRing(assignLayerContainer: main);
                try
                {
                    IoC.Get<IWindowManager>().ShowDialog(vm, main);
                    ret = vm.ClickedButton is MessageBoxResult.Yes or MessageBoxResult.No ? vm.ClickedButton : MessageBoxResult.Cancel;
                }
                finally { MaskLayerController.HideMask(layerId, main); }
            });
            return ret;
        }

        private static void MessageBoxOnActivated(object? sender, ActivationEventArgs e)
        {
            if (sender is Screen { View: Window { Owner: { }, IsLoaded: false } dlgWindow })
            {
                dlgWindow.Loaded -= MessageBoxOnLoaded;
                dlgWindow.Loaded += MessageBoxOnLoaded;
            }
        }

        private static void MessageBoxOnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window dlgWindow)
            {
                var windows = Application.Current.Windows;
                // enable the window != owner to let message box freeze the owner only.
                foreach (Window w in windows)
                {
                    if (w == dlgWindow.Owner) continue;
                    if (w is { IsLoaded: true })
                    {
                        if (HwndSource.FromVisual(w) is HwndSource hwndSource)
                            EnableWindow(hwndSource.Handle, true);
                    }
                }
            }
        }

        public static void Info(string content, string title = "", bool useNativeBox = false, IViewAware? ownerViewModel = null)
        {
            if (string.IsNullOrEmpty(title))
                title = IoC.TryGet<ILanguageService>()?.Translate("Info") ?? "Info";
            Alert(title, content, MessageBoxImage.Information, useNativeBox, ownerViewModel);
        }

        public static void Warning(string content, string title = "", bool useNativeBox = false, IViewAware? ownerViewModel = null)
        {
            if (string.IsNullOrEmpty(title))
                title = IoC.TryGet<ILanguageService>()?.Translate("Warning") ?? "Info";
            Alert(title, content, MessageBoxImage.Warning, useNativeBox, ownerViewModel);
        }


        public static void ErrorAlert(string content, string title = "", bool useNativeBox = false, IViewAware? ownerViewModel = null)
        {
            if (string.IsNullOrEmpty(title))
                title = IoC.TryGet<ILanguageService>()?.Translate("Error") ?? "Info";
            Alert(title, content, MessageBoxImage.Error, useNativeBox, ownerViewModel);
        }

        private static void Alert(string title, string content, MessageBoxImage icon, bool useNativeBox, object? ownerViewModel = null)
        {
            Execute.OnUIThreadSync(() =>
            {
                var ownerViewAware = (ownerViewModel as IViewAware);
                var mainWindowViewModel = IoC.TryGet<MainWindowViewModel>();
                if (useNativeBox
                    || ownerViewModel != null && ownerViewAware == null     // owner is set and owner is not IViewAware
                    || ownerViewModel == null && mainWindowViewModel?.View is Window { ShowInTaskbar: false } // owner not set and MainWindow is hidden
                    || ownerViewModel == null && mainWindowViewModel == null)
                {
                    MessageBox.Show(content, title, MessageBoxButton.OK, icon);
                }
                else
                {
                    IMaskLayerContainer? layerContainer = null;
                    if (ownerViewModel is IMaskLayerContainer mlc)
                    {
                        layerContainer = mlc;
                    }
                    else if (ownerViewModel == null)
                    {
                        layerContainer = mainWindowViewModel;
                    }
                    long layerId = 0;
                    if (layerContainer != null)
                        layerId = MaskLayerController.ShowProcessingRing(assignLayerContainer: layerContainer);
                    var vm = IoC.Get<IMessageBoxViewModel>();
                    vm.Setup(messageBoxText: content,
                        caption: title,
                        icon: icon,
                        buttons: MessageBoxButton.OK,
                        buttonLabels: new Dictionary<MessageBoxResult, string>()
                        {
                            {MessageBoxResult.None, IoC.TryGet<ILanguageService>()?.Translate("OK") ?? "OK"},
                            {MessageBoxResult.Yes, IoC.TryGet<ILanguageService>()?.Translate("OK")?? "OK"},
                            {MessageBoxResult.OK, IoC.TryGet<ILanguageService>()?.Translate("OK")?? "OK"},
                        });
                    if (vm is Screen screen)
                    {
                        screen.Activated += (sender, args) =>
                        {
                            if (screen.View is Window dlgWindow)
                            {
                                // dlg don't have a Owner
                                if (dlgWindow?.Owner == null)
                                    return;
                                var windows = Application.Current.Windows;
                                // enable the window != owner to let message box freeze the owner only.
                                foreach (Window w in windows)
                                {
                                    if (w == dlgWindow.Owner) continue;
                                    if (w is { IsLoaded: true })
                                    {
                                        if (HwndSource.FromVisual(w) is HwndSource hwndSource)
                                            EnableWindow(hwndSource.Handle, true);
                                    }
                                }
                            }
                        };
                    }
                    IoC.Get<IWindowManager>().ShowDialog(vm, ownerViewAware ?? mainWindowViewModel);
                    if (layerContainer != null)
                        MaskLayerController.HideMask(layerId, layerContainer: layerContainer);
                }
            });
        }
    }
}
