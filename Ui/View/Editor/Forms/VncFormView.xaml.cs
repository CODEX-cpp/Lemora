using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using Lemora.Model.Protocol;

namespace Lemora.View.Editor.Forms
{
    public partial class VncFormView : UserControl
    {
        public VncFormView()
        {
            InitializeComponent();
        }
    }


    public class ConverterEVncQuality : IValueConverter
    {
        public object Convert(object? value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null)
                return Enum.GetValues(typeof(VNC.EVncQuality)).Cast<int>().Max() + 1;
            return ((int)((VNC.EVncQuality)value)).ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (VNC.EVncQuality)(int.Parse(value.ToString() ?? "0"));
        }
    }

    public class ConverterEVncWindowResizeMode : IValueConverter
    {
        #region IValueConverter 成员  
        public object Convert(object? value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null)
                return Enum.GetValues(typeof(VNC.EVncWindowResizeMode)).Cast<int>().Max() + 1;
            return ((int)((VNC.EVncWindowResizeMode)value)).ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (VNC.EVncWindowResizeMode)(int.Parse(value.ToString() ?? "0"));
        }
        #endregion
    }

}
