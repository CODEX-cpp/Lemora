using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lemora.View.Editor;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.Host
{
    public partial class SessionRdpSettingsDialogView : WindowChromeBase
    {
        public SessionRdpSettingsDialogView()
        {
            InitializeComponent();
            // il pannello non supera lo schermo: cornice trasparente di 40 px per lato inclusa
            Width = System.Math.Min(Width, SystemParameters.WorkArea.Width - 20);
            Loaded += (s, e) => { ShowOnlyWantedSection(); AdaptInputWidths(); };
        }

        private readonly System.Collections.Generic.Dictionary<Grid, double> _inputWidths = new();

        private void Scroll_OnSizeChanged(object sender, SizeChangedEventArgs e) => AdaptInputWidths();

        /// <summary>
        /// I campi del modulo hanno larghezza fissa (520): nel pannello seguono lo spazio disponibile (da 220 a 760 px),
        /// cosi' i testi lunghi non vengono tagliati e, con poco spazio, i campi si restringono invece di uscire dal pannello.
        /// </summary>
        private void AdaptInputWidths()
        {
            if (RdpForm == null || Scroll == null || Scroll.ActualWidth < 100) return;
            const double titleColumn = 130 + 16;   // titolo + margine della riga
            var width = System.Math.Clamp(Scroll.ActualWidth - 48 - titleColumn - 12, 220, 760);
            foreach (var g in Descendants(RdpForm).OfType<Grid>())
            {
                if (!_inputWidths.ContainsKey(g) && (double.IsNaN(g.Width) || System.Math.Abs(g.Width - 520) > 0.5)) continue;
                if (!_inputWidths.ContainsKey(g)) _inputWidths[g] = g.Width;
                g.Width = width;
            }
        }

        private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        /// <summary>
        /// Il modulo RDP della creazione ha tutte le sezioni: qui se ne tiene una (schermo oppure avanzate), aperta,
        /// e si nasconde tutto il resto risalendo l'albero fino alla radice del modulo.
        /// </summary>
        private void ShowOnlyWantedSection()
        {
            if (DataContext is not SessionRdpSettingsDialogViewModel vm) return;
            var marker = vm.Kind == SessionRdpSettingsKind.Display ? "RdpWindowResizeMode" : "AudioRedirectionMode";
            var wanted = Descendants(RdpForm).OfType<Expander>().FirstOrDefault(x => AccordionHelper.GetProperties(x).Contains(marker));
            if (wanted == null) return;
            wanted.Style = (Style)FindResource("FlatSection");
            wanted.IsExpanded = true;
            DependencyObject node = wanted;
            while (node != null && !ReferenceEquals(node, RdpForm))
            {
                var parent = VisualTreeHelper.GetParent(node);
                if (parent is Panel panel)
                    foreach (UIElement sibling in panel.Children)
                        if (!ReferenceEquals(sibling, node)) sibling.Visibility = Visibility.Collapsed;
                node = parent;
            }
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }
    }
}
