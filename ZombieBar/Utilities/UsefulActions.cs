#nullable enable
using System;
using System.Threading.Tasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// The "Useful buttons" panel of the tray flyout. Each entry becomes one button: its localized
    /// label (resource key + fallback), a Segoe MDL2 Assets glyph, an optional confirmation prompt and
    /// the action itself. To add a button, add an entry here and its strings to every language file.
    /// </summary>
    public static class UsefulActions
    {
        /// <param name="Run">Performs the action; returns null on success or a short error description.</param>
        public sealed record Item(
            string Key,
            string Fallback,
            string Glyph,
            string? ConfirmKey,
            string? ConfirmFallback,
            Func<Task<string?>> Run);

        public static readonly Item[] All =
        {
            new Item("useful_restart_explorer", "Restart Explorer", "\uE72C",
                       null, null,
                       ExplorerRestarter.RestartAsync),
        };
    }
}
