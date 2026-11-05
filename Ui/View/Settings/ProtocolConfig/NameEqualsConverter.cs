using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lemora.View.Settings.ProtocolConfig
{
    /// <summary>true se i due testi (nome del programma e nome del programma scelto) sono uguali: serve all'etichetta «Predefinito»</summary>
    public class NameEqualsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values.Length >= 2 && values[0] is string a && values[1] is string b && a == b ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
