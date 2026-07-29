using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZombieBar.Converters
{
    /// <summary>
    /// Visible when the bound width (a double) is at least the threshold passed as the converter
    /// parameter; Collapsed otherwise. Used to reveal a task button's close (X) button only when the
    /// button is wide enough to fit it without crowding out the title.
    /// </summary>
    public class MinWidthVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = value is double d ? d : 0;

            double threshold = 0;
            if (parameter is string s)
                double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out threshold);
            else if (parameter is double pd)
                threshold = pd;

            return width >= threshold ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
