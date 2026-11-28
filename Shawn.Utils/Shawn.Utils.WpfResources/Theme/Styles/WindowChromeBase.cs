using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Shawn.Utils.WpfResources.Theme.Styles
{
    public abstract class WindowChromeBase : WindowBase
    {
        // https://developercommunity.visualstudio.com/t/overflow-exception-in-windowchrome/167357%EF%BC%8C%E6%99%9A%E4%BA%9B%E6%88%91%E6%9C%89%E7%A9%BA%E5%86%8D%E4%BB%94%E7%BB%86%E7%9C%8B%E7%9C%8B%E8%BF%99%E4%B8%AA%E8%A7%A3%E5%86%B3%E6%96%B9%E6%A1%88%E6%98%AF%E5%95%A5%E5%9B%9E%E4%BA%8B
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(HookProc);
            // Windows 11: the system draws a light 1 px line around the window (DWM frame) that shows at the sides of the
            // remote screen. It is removed (DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = 0xFFFFFFFE); on older systems the call does nothing.
            try
            {
                var none = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(source.Handle, 34, ref none, sizeof(int));
            }
            catch { /* dwmapi not available */ }
        }

        private IntPtr HookProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0084 /*WM_NCHITTEST*/ )
            {
                // This prevents a crash in WindowChromeWorker._HandleNCHitTest
                try
                {
                    lParam.ToInt32();
                }
                catch (OverflowException)
                {
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }
    }
}