using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ZombieBar.Converters
{
    /// <summary>
    /// Produces the gradient <see cref="System.Windows.UIElement.OpacityMask"/> that softly fades the
    /// truncated edge of a task-button label — but only when the text is actually wider than the
    /// space it is shown in. When the title fits, returns <c>null</c> so no fade is applied and the
    /// text stays fully crisp.
    ///
    /// The brush is built in code (rather than referenced as a resource) so it never depends on
    /// resource resolution inside the merged theme dictionary.
    ///
    /// values[0] = content (text) width, values[1] = available (viewport) width.
    /// parameter  = "Right" to fade the trailing edge (start-trimmed titles) or "Left" to fade the
    ///              leading edge (end-trimmed titles / paths).
    /// </summary>
    public class OverflowFadeConverter : IMultiValueConverter
    {
        // Fraction of the label width the fade spans. Small = subtle.
        private const double FadeFraction = 0.15;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double content || values[1] is not double viewport)
                return null;

            // Fits (within a sub-pixel tolerance): no fade.
            if (content <= viewport + 0.5)
                return null;

            bool fadeRight = string.Equals(parameter as string, "Right", StringComparison.OrdinalIgnoreCase);

            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            if (fadeRight)
            {
                brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
                brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - FadeFraction));
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                brush.GradientStops.Add(new GradientStop(Colors.Black, FadeFraction));
                brush.GradientStops.Add(new GradientStop(Colors.Black, 1));
            }
            brush.Freeze();
            return brush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
