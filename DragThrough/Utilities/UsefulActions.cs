#nullable enable
using System;
using System.Threading.Tasks;

namespace DragThrough.Utilities
{
    /// <summary>
    /// The "Useful buttons" panel of the tray flyout. Each entry becomes one button: its localized
    /// label and hover tooltip (resource key + fallback), a Segoe MDL2 Assets glyph, an optional
    /// confirmation prompt and the action itself. To add a button, add an entry here and its strings
    /// to every language file.
    /// </summary>
    public static class UsefulActions
    {
        /// <param name="Run">Performs the action; returns null on success or a short error description.</param>
        public sealed record Item(
            string Key,
            string Fallback,
            string TooltipKey,
            string TooltipFallback,
            string Glyph,
            string? ConfirmKey,
            string? ConfirmFallback,
            Func<Task<string?>> Run);

        public static readonly Item[] All =
        {
            new Item("useful_restart_explorer", "Restart Explorer",
                     "useful_restart_explorer_tip",
                     "Restarts Windows Explorer: the taskbar, desktop and folder windows. Helps when they freeze or stop updating. Open folder windows will be closed.",
                     "\uE72C",
                     null, null,
                     ExplorerRestarter.RestartAsync),

            new Item("useful_find_locker", "Find the process locking a file",
                     "useful_find_locker_tip",
                     "Shows which program keeps a file or folder open. Use it when Windows says “File In Use” or “Folder In Use”: the file is taken from that window, or you can choose it yourself.",
                     "\uE72E",
                     null, null,
                     () => { LockFinderWindow.Open(); return Task.FromResult<string?>(null); }),
        };
    }
}
