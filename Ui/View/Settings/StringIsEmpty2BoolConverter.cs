using System;
using System.Windows.Data;

namespace Lemora.View.Settings;

public class StringIsEmpty2BoolConverter : IValueConverter
{
    #region IValueConverter members

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        string str = value?.ToString() ?? "";
        return string.IsNullOrEmpty(str);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    #endregion IValueConverter members
}