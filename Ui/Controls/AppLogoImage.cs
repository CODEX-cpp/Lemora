using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lemora.Service;
using Lemora.Utils;

namespace Lemora.Controls
{
    /// <summary>
    /// The app logo inside the UI: white tile with a black mark on a dark background, black tile with a white mark on a light background.
    /// Updates itself when the app theme changes.
    /// </summary>
    public class AppLogoImage : Image
    {
        public AppLogoImage()
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            Loaded += (_, _) =>
            {
                Refresh();
                ThemeService.ThemeApplied -= Refresh;   // Loaded can fire more than once
                ThemeService.ThemeApplied += Refresh;
            };
            Unloaded += (_, _) => ThemeService.ThemeApplied -= Refresh;
        }

        private void Refresh() => Dispatcher.BeginInvoke(new Action(() => Source = AppIcons.TitleTile()));
    }
}
