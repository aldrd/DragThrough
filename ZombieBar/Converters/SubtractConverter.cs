using System;
using System.Globalization;
using System.Windows.Data;

namespace ZombieBar.Converters
{
    /// <summary>
    /// The bound double minus the amount passed as the converter parameter, clamped at zero. Used to
    /// cap the help video's height at the menu column's height less the pane's insets, so a tall clip
    /// scales down to fit instead of growing the (size-to-content) flyout.
    /// </summary>
    public class SubtractConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double input = value is double d ? d : 0;

            double amount = 0;
            if (parameter is string s)
                double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
            else if (parameter is double pd)
                amount = pd;

            return Math.Max(0, input - amount);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
