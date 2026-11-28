using System.Windows;
using System.Windows.Documents;

namespace Shawn.Utils.Wpf.Controls
{
    public static class HyperlinkHelper
    {
        public static bool GetIsOpenExternal(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsOpenExternalProperty);
        }

        public static void SetIsOpenExternal(DependencyObject obj, bool value)
        {
            obj.SetValue(IsOpenExternalProperty, value);
        }

        public static readonly DependencyProperty IsOpenExternalProperty =
            DependencyProperty.RegisterAttached("IsOpenExternal", typeof(bool), typeof(HyperlinkHelper), new UIPropertyMetadata(false, OnIsOpenExternalChanged));

        private static void OnIsOpenExternalChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (sender is Hyperlink hyperlink)
                if ((bool)args.NewValue)
                {
                    hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                }
                else
                {
                    hyperlink.RequestNavigate -= Hyperlink_RequestNavigate;
                }
        }

        private static void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            OpenUriBySystem(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        /// <summary>
        /// opens only web and mail addresses: a link in the notes (even imported ones) with file:, \\server\ or a local path
        /// would start a program or send the Windows credentials to another PC        /// </summary>
        public static bool IsSafeToOpen(string? uri) =>
            System.Uri.TryCreate(uri, System.UriKind.Absolute, out var u)
            && (u.Scheme == System.Uri.UriSchemeHttps || u.Scheme == System.Uri.UriSchemeHttp || u.Scheme == System.Uri.UriSchemeMailto)
            && !u.IsUnc;

        public static void OpenUriBySystem(string uri)
        {
            if (!IsSafeToOpen(uri))
            {
                SimpleLogHelper.Warning("Link not opened (only http, https and mailto): " + uri);
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = uri
            };
            System.Diagnostics.Process.Start(psi);
        }
    }
}