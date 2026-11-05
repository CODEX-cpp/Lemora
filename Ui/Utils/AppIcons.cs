using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lemora.Utils
{
    /// <summary>
    /// Le due versioni del logo: riquadro bianco con segno nero (per fondi scuri) e riquadro nero con segno bianco (per fondi chiari).
    /// Nell'interfaccia si sceglie dal colore di sfondo del tema; per barra e area di notifica dal tema di Windows.
    /// </summary>
    internal static class AppIcons
    {
        private static Uri Pack(string file) => new Uri("pack://application:,,,/Resources/Image/Logo/" + file, UriKind.Absolute);

        /// <summary>true se la barra delle applicazioni e l'area di notifica di Windows usano il tema chiaro</summary>
        public static bool WindowsTaskbarIsLight()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
            }
            catch { return false; }
        }

        /// <summary>true se lo sfondo del tema dell'app e' scuro</summary>
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
            catch { /* tema non ancora caricato */ }
            return true;
        }

        /// <summary>logo per il riquadro in alto a sinistra dell'app: bianco su fondo scuro, nero su fondo chiaro</summary>
        public static ImageSource TitleTile() =>
            new BitmapImage(Pack(AppBackgroundIsDark() ? "tile-dark.png" : "tile-light.png"));

        /// <summary>icona della finestra aperta (pulsante nella barra delle applicazioni): segue il tema di Windows</summary>
        public static ImageSource WindowIcon() =>
            BitmapFrame.Create(Pack(WindowsTaskbarIsLight() ? "app-light.ico" : "app-dark.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        /// <summary>icona per l'area di notifica: segue il tema di Windows</summary>
        public static System.Drawing.Icon TrayIcon()
        {
            var stream = Application.GetResourceStream(Pack(WindowsTaskbarIsLight() ? "app-light.ico" : "app-dark.ico")).Stream;
            return new System.Drawing.Icon(stream);
        }
    }
}
