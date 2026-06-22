using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Owns the per-window taskbar ordering.
    ///
    /// Each window has a string order identifier (see <see cref="OrderHelper"/>) determining its
    /// position on the taskbar. A window is identified by the title it had when it was first added
    /// to the taskbar; later title changes are ignored. New windows are appended to the right with
    /// an identifier following the maximum of all known (not necessarily currently shown) windows.
    /// Records are loaded from a CSV file on startup and saved, compacted, on exit.
    /// </summary>
    public class TasksOrderManager : IDisposable
    {
        // CSV columns: [0] = window title (key), [1] = order identifier. No header row.
        private const string OrderFileName = "TasksOrder.csv";

        private readonly ObservableCollection<ApplicationWindow> _windows;

        // Persistent map: title (at add time) -> order identifier. Survives window close.
        private readonly Dictionary<string, string> _orderByTitle = new();

        // Tracks the title key each live window was assigned under, so drag updates target the
        // right record even if the window's title changed after it was added.
        private readonly Dictionary<ApplicationWindow, string> _keyByWindow =
            new(ReferenceEqualityComparer.Instance);

        private bool _disposed;

        public TasksOrderManager(ObservableCollection<ApplicationWindow> windows)
        {
            _windows = windows;

            Load();

            foreach (ApplicationWindow window in _windows.ToList())
            {
                Attach(window);
            }

            _windows.CollectionChanged += Windows_CollectionChanged;
        }

        /// <summary>
        /// Comparer used to sort taskbar items by their order identifier.
        /// </summary>
        public static IComparer Comparer { get; } = new OrderComparer();

        /// <summary>
        /// Assigns a new order identifier to a window that was dragged between two neighbors and
        /// records it. Pass the order of the neighbor it now follows (or "" for the start) and the
        /// order of the neighbor it now precedes (or "" for the end).
        /// </summary>
        public void Reorder(ApplicationWindow window, string insertAfter_Order, string insertBefore_Order)
        {
            if (window == null)
                return;

            string newOrder;
            try
            {
                newOrder = OrderHelper.GetOrder(insertAfter_Order ?? "", insertBefore_Order ?? "");
            }
            catch (Exception ex)
            {
                ShellLogger.Error("TasksOrderManager: Unable to compute order for dragged window.", ex);
                return;
            }

            window.Order = newOrder;

            if (_keyByWindow.TryGetValue(window, out string key))
            {
                _orderByTitle[key] = newOrder;
            }
        }

        /// <summary>
        /// Compacts all known order identifiers into short, evenly spaced values (preserving their
        /// relative order) and writes them to persistent storage. Intended to run on app exit.
        /// </summary>
        public void SaveCompacted()
        {
            List<KeyValuePair<string, string>> ordered = _orderByTitle
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                .OrderBy(kv => kv.Value, StringComparer.Ordinal)
                .ToList();

            string[] compact = ComputeCompactOrders(ordered.Count);

            Dictionary<string, string> compacted = new();
            for (int i = 0; i < ordered.Count; i++)
            {
                compacted[ordered[i].Key] = compact[i];
            }

            Save(compacted);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _windows.CollectionChanged -= Windows_CollectionChanged;
            foreach (ApplicationWindow window in _keyByWindow.Keys.ToList())
            {
                window.PropertyChanged -= Window_PropertyChanged;
            }
            _keyByWindow.Clear();
        }

        private void Windows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ApplicationWindow window in e.NewItems.OfType<ApplicationWindow>())
                {
                    Attach(window);
                }
            }

            if (e.OldItems != null)
            {
                foreach (ApplicationWindow window in e.OldItems.OfType<ApplicationWindow>())
                {
                    Detach(window);
                }
            }
        }

        private void Attach(ApplicationWindow window)
        {
            window.PropertyChanged -= Window_PropertyChanged;
            window.PropertyChanged += Window_PropertyChanged;
            EnsureOrder(window);
        }

        private void Detach(ApplicationWindow window)
        {
            window.PropertyChanged -= Window_PropertyChanged;
            _keyByWindow.Remove(window);
            // The order record is intentionally kept in _orderByTitle so a window with the same
            // title reappears in its remembered position later.
        }

        private void Window_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ShowInTaskbar" || e.PropertyName == "Title")
            {
                EnsureOrder(sender as ApplicationWindow);
            }
        }

        // Assigns an order to the window the first time it is shown in the taskbar with a title.
        private void EnsureOrder(ApplicationWindow window)
        {
            if (window == null)
                return;

            // Already assigned this session; ignore later title changes per spec.
            if (!string.IsNullOrEmpty(window.Order))
                return;

            if (!window.ShowInTaskbar)
                return;

            string title = window.Title ?? "";
            if (string.IsNullOrEmpty(title))
                return; // wait until the title is available

            if (!_orderByTitle.TryGetValue(title, out string order) || string.IsNullOrEmpty(order))
            {
                // New window: place at the end, after the current maximum.
                order = OrderHelper.GetOrder(MaxOrder(), "");
                _orderByTitle[title] = order;
            }

            _keyByWindow[window] = title;
            window.Order = order;
        }

        private string MaxOrder()
        {
            string max = "";
            foreach (string value in _orderByTitle.Values)
            {
                if (!string.IsNullOrEmpty(value) && string.CompareOrdinal(value, max) > 0)
                    max = value;
            }
            return max;
        }

        #region Persistence

        private static string GetOrderFilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ZombieBar");
            return Path.Combine(dir, OrderFileName);
        }

        private void Load()
        {
            try
            {
                string path = GetOrderFilePath();
                if (!File.Exists(path))
                    return;

                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrEmpty(line))
                        continue;

                    List<string> fields = ParseCsvLine(line);
                    if (fields.Count < 2)
                        continue;

                    string title = fields[0];
                    string order = fields[1];
                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(order))
                        _orderByTitle[title] = order;
                }
            }
            catch (Exception ex)
            {
                ShellLogger.Error("TasksOrderManager: Unable to load order file.", ex);
            }
        }

        private void Save(Dictionary<string, string> map)
        {
            try
            {
                string path = GetOrderFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                StringBuilder sb = new();
                foreach (KeyValuePair<string, string> kv in map.OrderBy(kv => kv.Value, StringComparer.Ordinal))
                {
                    sb.Append(ToCsvField(kv.Key));
                    sb.Append(',');
                    sb.Append(ToCsvField(kv.Value));
                    sb.Append("\r\n");
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ShellLogger.Error("TasksOrderManager: Unable to save order file.", ex);
            }
        }

        #endregion

        #region Compaction

        // Produces n order identifiers, sorted ascending, evenly spaced across the value space and
        // padded to a fixed width so lexicographic order matches numeric order.
        private static string[] ComputeCompactOrders(int n)
        {
            string[] result = new string[n];
            if (n == 0)
                return result;

            int bytes = 1;
            while (Math.Pow(256, bytes) < n + 1)
                bytes++;

            double max = Math.Pow(256, bytes);
            double step = max / (n + 1);
            long previous = 0;

            for (int i = 0; i < n; i++)
            {
                long value = (long)Math.Round((i + 1) * step);
                if (value <= previous)
                    value = previous + 1;
                previous = value;
                result[i] = EncodeOrder(value, bytes);
            }

            return result;
        }

        private static string EncodeOrder(long value, int bytes)
        {
            StringBuilder sb = new(bytes * 2);
            for (int j = bytes - 1; j >= 0; j--)
            {
                sb.Append(((value >> (8 * j)) & 0xff).ToString("x2"));
            }
            return sb.ToString();
        }

        #endregion

        #region CSV helpers

        private static string ToCsvField(string value)
        {
            value ??= "";
            bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!mustQuote)
                return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static List<string> ParseCsvLine(string line)
        {
            List<string> fields = new();
            StringBuilder current = new();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == ',')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else if (c == '"' && current.Length == 0)
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            fields.Add(current.ToString());
            return fields;
        }

        #endregion

        private sealed class OrderComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                string a = (x as ApplicationWindow)?.Order ?? "";
                string b = (y as ApplicationWindow)?.Order ?? "";
                return string.CompareOrdinal(a, b);
            }
        }
    }
}
