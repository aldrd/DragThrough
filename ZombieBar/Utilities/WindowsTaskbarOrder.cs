#nullable enable
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Reads the order of the buttons on the main Windows taskbar through UI Automation and maps them to
    /// windows, so the additional taskbar can show its windows in the same sequence.
    ///
    /// The taskbar exposes no window handles, so buttons are matched to windows by what they do expose:
    ///   1. a separate (uncombined) button's name contains its window's title;
    ///   2. a combined button stands for an application: matched by its AppUserModelID (Windows 11
    ///      reports it as the AutomationId "Appid: ..."), by the executable path inside such an id, or by
    ///      the button's name starting with the executable's description;
    ///   3. a window still unmatched joins another matched window of the same executable, since Windows
    ///      keeps an application's buttons together.
    /// Windows that can't be placed get no rank (the caller orders them after the ranked ones).
    ///
    /// Taskbars: the primary one (Shell_TrayWnd) first, then the secondary ones (Shell_SecondaryTrayWnd)
    /// from left to right. Windows 10 hosts the buttons in the MSTaskListWClass window; Windows 11's XAML
    /// taskbar exposes them as Taskbar.TaskListButtonAutomationPeer elements.
    ///
    /// UI Automation is cross-process COM and can block on a busy Explorer: call <see cref="Read"/> off
    /// the UI thread only.
    /// </summary>
    public static class WindowsTaskbarOrder
    {
        public sealed record WindowInfo(IntPtr Handle, string Title, string ExePath);

        private sealed record Button(string Name, string AppId);

        private const string Win11ButtonClass = "Taskbar.TaskListButtonAutomationPeer";
        private const string AppIdPrefix = "Appid: ";

        private static readonly ConcurrentDictionary<string, string> DescriptionByExe = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Rank (position on the Windows taskbar) per window handle, for the windows that could be placed;
        /// windows of one combined button share a rank. Null when the taskbar couldn't be read at all.
        /// </summary>
        public static Dictionary<IntPtr, int>? Read(IReadOnlyList<WindowInfo> windows)
        {
            try
            {
                List<Button> buttons = ReadButtons();
                if (buttons.Count == 0)
                    return null;

                return Match(buttons, windows);
            }
            catch (Exception ex)
            {
                ShellLogger.Debug($"WindowsTaskbarOrder: unable to read the taskbar: {ex.Message}");
                return null;
            }
        }

        private static List<Button> ReadButtons()
        {
            var buttons = new List<Button>();

            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);

            using (cache.Activate())
            {
                foreach (IntPtr bar in GetTaskbars())
                {
                    AutomationElement barElement = AutomationElement.FromHandle(bar);
                    Rect barRect = barElement.Cached.BoundingRectangle;
                    bool vertical = barRect.Height > barRect.Width;

                    List<AutomationElement> elements = GetButtonElements(bar, barElement);
                    IEnumerable<AutomationElement> ordered = vertical
                        ? elements.OrderBy(e => e.Cached.BoundingRectangle.Top).ThenBy(e => e.Cached.BoundingRectangle.Left)
                        : elements.OrderBy(e => e.Cached.BoundingRectangle.Left).ThenBy(e => e.Cached.BoundingRectangle.Top);

                    foreach (AutomationElement element in ordered)
                    {
                        string name = element.Cached.Name ?? "";
                        string automationId = element.Cached.AutomationId ?? "";
                        string appId = automationId.StartsWith(AppIdPrefix, StringComparison.Ordinal)
                            ? automationId.Substring(AppIdPrefix.Length).Trim()
                            : "";
                        if (name.Length > 0 || appId.Length > 0)
                            buttons.Add(new Button(name, appId));
                    }
                }
            }

            return buttons;
        }

        private static List<AutomationElement> GetButtonElements(IntPtr bar, AutomationElement barElement)
        {
            // Windows 10: the buttons are the children of the task list window.
            IntPtr taskList = FindDescendantWindow(bar, "MSTaskListWClass");
            if (taskList != IntPtr.Zero)
            {
                List<AutomationElement> children = AutomationElement.FromHandle(taskList)
                    .FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Where(e => e.Cached.ControlType == ControlType.Button
                                || e.Cached.ControlType == ControlType.MenuItem
                                || e.Cached.ControlType == ControlType.ListItem)
                    .Where(e => !e.Cached.BoundingRectangle.IsEmpty)
                    .ToList();
                if (children.Count > 0)
                    return children;
            }

            // Windows 11: XAML taskbar.
            return barElement
                .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, Win11ButtonClass))
                .Cast<AutomationElement>()
                .Where(e => !e.Cached.BoundingRectangle.IsEmpty)
                .ToList();
        }

        private static List<IntPtr> GetTaskbars()
        {
            var bars = new List<IntPtr>();
            IntPtr primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero)
                bars.Add(primary);

            var secondary = new List<(IntPtr Handle, int Left, int Top)>();
            IntPtr hwnd = IntPtr.Zero;
            while ((hwnd = NativeMethods.FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", IntPtr.Zero)) != IntPtr.Zero)
            {
                NativeMethods.GetWindowRect(hwnd, out NativeMethods.Rect rect);
                secondary.Add((hwnd, rect.Left, rect.Top));
            }

            bars.AddRange(secondary.OrderBy(s => s.Left).ThenBy(s => s.Top).Select(s => s.Handle));
            return bars;
        }

        // Depth-first search for a child window of the given class (Windows 10 nests the task list a few
        // levels deep, and the nesting differs between the primary and secondary taskbars).
        private static IntPtr FindDescendantWindow(IntPtr parent, string className)
        {
            IntPtr child = IntPtr.Zero;
            while ((child = NativeMethods.FindWindowEx(parent, child, IntPtr.Zero, null)) != IntPtr.Zero)
            {
                if (GetClassName(child) == className)
                    return child;

                IntPtr found = FindDescendantWindow(child, className);
                if (found != IntPtr.Zero)
                    return found;
            }

            return IntPtr.Zero;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static Dictionary<IntPtr, int> Match(List<Button> buttons, IReadOnlyList<WindowInfo> windows)
        {
            var rank = new Dictionary<IntPtr, int>();

            // 1. Separate buttons carry their window's title. Longest title first, so "Report - Word" wins
            //    over a window titled just "Word".
            for (int i = 0; i < buttons.Count; i++)
            {
                string name = buttons[i].Name;
                WindowInfo? byTitle = windows
                    .Where(w => !rank.ContainsKey(w.Handle) && w.Title.Length > 0
                                && name.Contains(w.Title, StringComparison.Ordinal))
                    .OrderByDescending(w => w.Title.Length)
                    .FirstOrDefault();
                if (byTitle != null)
                    rank[byTitle.Handle] = i;
            }

            // 2. Combined buttons stand for an application.
            foreach (WindowInfo w in windows.Where(w => !rank.ContainsKey(w.Handle)))
            {
                string aumid = SafeAumid(w.Handle);
                for (int i = 0; i < buttons.Count; i++)
                {
                    if (AppMatches(buttons[i], w, aumid))
                    {
                        rank[w.Handle] = i;
                        break;
                    }
                }
            }

            // 3. Windows keeps an application's buttons next to each other.
            foreach (WindowInfo w in windows.Where(w => !rank.ContainsKey(w.Handle) && w.ExePath.Length > 0))
            {
                WindowInfo? sibling = windows.FirstOrDefault(o => rank.ContainsKey(o.Handle)
                    && string.Equals(o.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase));
                if (sibling != null)
                    rank[w.Handle] = rank[sibling.Handle];
            }

            return rank;
        }

        private static bool AppMatches(Button button, WindowInfo window, string aumid)
        {
            if (button.AppId.Length > 0)
            {
                if (aumid.Length > 0 && string.Equals(button.AppId, aumid, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (window.ExePath.Length > 0 && AppIdIsExe(button.AppId, window.ExePath))
                    return true;
            }

            string description = GetDescription(window.ExePath);
            return description.Length >= 3 && button.Name.StartsWith(description, StringComparison.OrdinalIgnoreCase);
        }

        // An application without an explicit AppUserModelID is identified by its executable path, with a
        // known-folder GUID standing in for the folder: "{6D809377-...}\Vendor\App\app.exe".
        private static bool AppIdIsExe(string appId, string exePath)
        {
            int close = appId.IndexOf("}\\", StringComparison.Ordinal);
            if (appId.StartsWith("{", StringComparison.Ordinal) && close > 0)
            {
                string relative = appId.Substring(close + 2);
                return relative.Length > 0 && exePath.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase);
            }

            return appId.Contains('\\') && string.Equals(appId, exePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeAumid(IntPtr hwnd)
        {
            try { return ShellHelper.GetAppUserModelIdPropertyForHandle(hwnd) ?? ""; }
            catch { return ""; }
        }

        private static string GetDescription(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
                return "";

            return DescriptionByExe.GetOrAdd(exePath, path =>
            {
                try
                {
                    return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "" : "";
                }
                catch
                {
                    return "";
                }
            });
        }
    }
}
