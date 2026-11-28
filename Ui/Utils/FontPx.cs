using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Lemora.Utils
{
    /// <summary>
    /// Text scale factor: size chosen in the settings (S=10, M=12, L=14, XL=16...) divided by 12.
    /// ThemeService updates it every time the theme or the font size changes.
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
    /// Text size "designed for M (12)": <c>FontSize="{fpx:FontPx 13}"</c> (xmlns:fpx="clr-namespace:Lemora.Utils") is 13 with size M and grows/shrinks with the one chosen in the
    /// settings (XL = 16/12 = 1.33 times). Before, the fixed numbers in the XAML did not follow the choice and stayed small.
    /// </summary>
    public sealed class FontPx : MarkupExtension
    {
        /// <summary>
        /// Texts without their own size (a TextBlock inside a button, etc.) inherit the size from the window: by default it is the system one
        /// and did not follow the choice in the settings. Every window that does not already have its own size gets "Body" (updated when the theme changes).
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

        public override object ProvideValue(IServiceProvider serviceProvider) => CreateBinding(Size).ProvideValue(serviceProvider);   // this way it works both on a control and inside a Style/Template

        public static Binding CreateBinding(double size) => new Binding(nameof(FontScale.Scale))
        {
            Source = FontScale.Instance,
            Mode = BindingMode.OneWay,
            Converter = new Multiply(size),
        };

        /// <summary>for controls created in code</summary>
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
