using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Lemora.Utils
{
    /// <summary>
    /// Fattore di ingrandimento dei testi: dimensione scelta nelle impostazioni (S=10, M=12, L=14, XL=16...) diviso 12.
    /// Lo aggiorna ThemeService ogni volta che cambia il tema o la dimensione del carattere.
    /// </summary>
    public sealed class FontScale : INotifyPropertyChanged
    {
        public static FontScale Instance { get; } = new FontScale();
        private double _scale = 1;
        public double Scale
        {
            get => _scale;
            set
            {
                if (Math.Abs(_scale - value) < 0.0001) return;
                _scale = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Dimensione del testo "disegnata per M (12)": <c>FontSize="{fpx:FontPx 13}"</c> (xmlns:fpx="clr-namespace:Lemora.Utils") vale 13 con la dimensione M e cresce/cala con quella scelta nelle
    /// impostazioni (XL = 16/12 = 1,33 volte). Prima i numeri fissi nei XAML non seguivano la scelta e restavano piccoli.
    /// </summary>
    public sealed class FontPx : MarkupExtension
    {
        /// <summary>
        /// Testi senza dimensione propria (un TextBlock dentro un pulsante, ecc.) ereditano la dimensione dalla finestra: di base e' quella di sistema
        /// e non seguiva la scelta nelle impostazioni. Ogni finestra che non ha gia' una dimensione sua prende "Body" (aggiornata al cambio di tema).
        /// </summary>
        public static void InstallWindowFontSize()
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
            {
                if (s is Window w && DependencyPropertyHelper.GetValueSource(w, System.Windows.Controls.Control.FontSizeProperty).BaseValueSource == BaseValueSource.Default)
                    w.SetResourceReference(System.Windows.Controls.Control.FontSizeProperty, "GlobalFontSizeBody");
            }));
        }

        public double Size { get; set; }
        public FontPx() { }
        public FontPx(double size) { Size = size; }

        public override object ProvideValue(IServiceProvider serviceProvider) => CreateBinding(Size).ProvideValue(serviceProvider);   // cosi' vale sia su un controllo sia dentro Style/Template

        public static Binding CreateBinding(double size) => new Binding(nameof(FontScale.Scale))
        {
            Source = FontScale.Instance,
            Mode = BindingMode.OneWay,
            Converter = new Multiply(size),
        };

        /// <summary>per i controlli creati nel codice</summary>
        public static void Apply(DependencyObject target, DependencyProperty fontSizeProperty, double size)
            => BindingOperations.SetBinding(target, fontSizeProperty, CreateBinding(size));

        private sealed class Multiply : IValueConverter
        {
            private readonly double _size;
            public Multiply(double size) { _size = size; }
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
                => _size * (value is double d ? d : 1.0);
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }
}
