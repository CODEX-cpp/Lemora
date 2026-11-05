using System.Windows.Media;
using Lemora.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View
{
    /// <summary>una scheda della barra del titolo (Home, Impostazioni, Nuova connessione...)</summary>
    public class MainTabItem : NotifyPropertyChangedBase
    {
        // pallino di stato della sessione: verde collegata, rosso scollegata (irraggiungibile, caduta...), giallo mentre si collega o si riconnette
        private static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        private static readonly Brush Green = Frozen("#6CCB5F"), Red = Frozen("#F28B82"), Amber = Frozen("#F2C66D");
        private static Brush StatusOf(HostBase? h) => h == null ? Green : h.Status switch
        {
            ProtocolHostStatus.Connected => Green,
            ProtocolHostStatus.Disconnected => Red,
            _ => Amber,
        };
        public Brush StatusBrush => StatusOf(Host);
        public Brush Seg1StatusBrush => StatusOf(Host);
        public Brush Seg2StatusBrush => StatusOf(Seg2Host);
        /// <summary>seconda sessione di una scheda divisa</summary>
        public HostBase? Seg2Host { get; set; }
        /// <summary>chiamato quando una sessione cambia stato: i pallini si ridisegnano</summary>
        public void RefreshStatus()
        {
            RaisePropertyChanged(nameof(StatusBrush)); RaisePropertyChanged(nameof(Seg1StatusBrush)); RaisePropertyChanged(nameof(Seg2StatusBrush));
        }

        public string Title { get; set; } = "";
        private string? _toolTipText;
        /// <summary>suggerimento della scheda (se vuoto, il titolo)</summary>
        public string ToolTipText { get => string.IsNullOrEmpty(_toolTipText) ? Title : _toolTipText!; set => _toolTipText = value; }
        public bool IsActive { get; set; }
        public bool CanClose { get; set; }
        /// <summary>scheda "+" in fondo: apre l'avvio rapido (larghezza fissa)</summary>
        public bool IsAdd { get; set; }
        /// <summary>scheda di una sessione aperta: mostra il pallino verde "connesso"</summary>
        public bool IsSession { get; set; }
        /// <summary>mostra il pulsante "schermo intero" (sessioni RDP)</summary>
        public bool CanFullScreen { get; set; }
        public RelayCommand? FullScreenCommand { get; set; }
        /// <summary>clic sulla scheda (null = e' gia' quella aperta)</summary>
        public RelayCommand? Command { get; set; }
        /// <summary>clic sulla ✕</summary>
        public RelayCommand? CloseCommand { get; set; }

        /// <summary>sessione di questa scheda (per trascinarla su un'altra scheda e affiancarle)</summary>
        public Lemora.View.Host.ProtocolHosts.HostBase? Host { get; set; }

        // ---- scheda divisa: due sessioni affiancate nella stessa scheda (vista divisa)
        public bool IsSplit { get; set; }
        public string Seg1Proto { get; set; } = "";
        public string Seg1Title { get; set; } = "";
        public string Seg2Proto { get; set; } = "";
        public string Seg2Title { get; set; } = "";
        public bool Seg1Active { get; set; }
        public bool Seg2Active { get; set; }
        public RelayCommand? Seg1Command { get; set; }
        public RelayCommand? Seg2Command { get; set; }
    }
}
