#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ManagedShell.WindowsTasks;
using ZombieBar.Utilities;

namespace ZombieBar.Converters
{
    /// <summary>
    /// Drives the visibility of the two task-button labels (start-trimming vs end-trimming) from
    /// <see cref="TaskButtonDisplayRules"/>. ConverterParameter "End" marks the end-trimming
    /// label; any other value marks the start-trimming one. Exactly one label is visible.
    ///
    /// values[0] = Title (change trigger), values[1] = the <see cref="ApplicationWindow"/>.
    /// </summary>
    public class TaskButtonTrimVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            ApplicationWindow? window = values.Length > 1 ? values[1] as ApplicationWindow : null;
            bool trimToEnd = TaskButtonDisplayRules.TrimToEnd(window);
            bool isEndLabel = string.Equals(parameter as string, "End", StringComparison.OrdinalIgnoreCase);
            return trimToEnd == isEndLabel ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
