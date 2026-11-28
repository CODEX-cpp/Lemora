using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lemora.Utils
{
    /// <summary>
    /// The two versions of the logo: white tile with black mark (for dark backgrounds) and black tile with white mark (for light backgrounds).
    /// In the UI the choice follows the theme background color; for the taskbar and notification area it follows the Windows theme.
    /// </summary>
    internal static class AppIcons
    {
        private static Uri Pack(string file) => new Uri("pack://application:,,,/Resources/Image/Logo/" + file, UriKind.Absolute);

        /// <summary>true if the Windows taskbar and notification area use the light theme</summary>
        public static bool WindowsTaskbarIsLight()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
            }
            catch { return false; }
        }

        /// <summary>true if the app theme background is dark</summary>
        public static bool AppBackgroundIsDark()
        {
            try
            {
                if (Application.Current?.TryFindResource("BackgroundBrush") is SolidColorBrush b)
                {
                    var c = b.Color;
                    return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 140;
                }
            }
            catch { /* theme not loaded yet */ }
            return true;
        }

        /// <summary>logo for the app's top-left tile: white on dark background, black on light background</summary>
        public static ImageSource TitleTile() =>
            new BitmapImage(Pack(AppBackgroundIsDark() ? "tile-dark.png" : "tile-light.png"));

        /// <summary>icon of the open window (taskbar button): follows the Windows theme</summary>
        public static ImageSource WindowIcon() =>
            BitmapFrame.Create(Pack(WindowsTaskbarIsLight() ? "app-light.ico" : "app-dark.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        /// <summary>icon for the notification area: follows the Windows theme</summary>
        public static System.Drawing.Icon TrayIcon()
        {
            var stream = Application.GetResourceStream(Pack(WindowsTaskbarIsLight() ? "app-light.ico" : "app-dark.ico")).Stream;
            return new System.Drawing.Icon(stream);
        }
    }
}
