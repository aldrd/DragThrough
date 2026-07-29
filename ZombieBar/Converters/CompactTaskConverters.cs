using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZombieBar.Converters
{
    /// <summary>
    /// Picks a task button's width: the compact width when the button is in compact mode (icon + close
    /// only), otherwise the normal computed button width. Values: [0] IsCompact (bool), [1] normal width
    /// (double), [2] compact width (double).
    /// </summary>
    public class CompactWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool compact = values.Length > 0 && values[0] is bool b && b;
            if (compact)
                return values.Length > 2 && values[2] is double cw ? cw : double.NaN;
            return values.Length > 1 && values[1] is double w ? w : double.NaN;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Shows the task button's close (X) button when the button is compact (icon + close only), or when
    /// it is wide enough to fit the close button without crowding the title. Values: [0] the button's
    /// ActualWidth (double), [1] IsCompact (bool); the width threshold is the converter parameter.
    /// </summary>
    public class CompactCloseVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool compact = values.Length > 1 && values[1] is bool b && b;
            if (compact)
                return Visibility.Visible;

            double width = values.Length > 0 && values[0] is double d ? d : 0;
            double threshold = 0;
            if (parameter is string s)
                double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out threshold);

            return width >= threshold ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
