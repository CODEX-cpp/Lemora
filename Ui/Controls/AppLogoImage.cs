using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lemora.Service;
using Lemora.Utils;

namespace Lemora.Controls
{
    /// <summary>
    /// Il logo dell'app dentro l'interfaccia: riquadro bianco con segno nero su fondo scuro, nero con segno bianco su fondo chiaro.
    /// Si aggiorna da solo quando cambia il tema dell'app.
    /// </summary>
    public class AppLogoImage : Image
    {
        public AppLogoImage()
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            Loaded += (_, _) =>
            {
                Refresh();
                ThemeService.ThemeApplied -= Refresh;   // Loaded puo' scattare piu' volte
                ThemeService.ThemeApplied += Refresh;
            };
            Unloaded += (_, _) => ThemeService.ThemeApplied -= Refresh;
        }

        private void Refresh() => Dispatcher.BeginInvoke(new Action(() => Source = AppIcons.TitleTile()));
    }
}
