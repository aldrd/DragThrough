#nullable enable
using System;
using System.Globalization;
using System.Windows.Data;
using ManagedShell.WindowsTasks;
using ZombieBar.Utilities;

namespace ZombieBar.Converters
{
    /// <summary>
    /// Resolves the text shown on a task button per <see cref="TaskButtonDisplayRules"/> — the
    /// folder path for File Explorer (language-independent), the window title otherwise.
    ///
    /// values[0] = Title (string) — also the change trigger, so navigating in Explorer (which
    /// changes the title) re-evaluates the path; values[1] = the <see cref="ApplicationWindow"/>.
    /// </summary>
    public class TaskButtonTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string title = values.Length > 0 ? values[0] as string ?? "" : "";
            ApplicationWindow? window = values.Length > 1 ? values[1] as ApplicationWindow : null;
            return TaskButtonDisplayRules.GetText(title, window);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
