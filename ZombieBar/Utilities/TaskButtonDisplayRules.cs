#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ManagedShell.WindowsTasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Per-program rules for how a task button presents a window: which text to show and which
    /// end of it to keep when it does not fit.
    ///
    /// File Explorer windows show the folder path (language-independent) and keep the end (the
    /// deepest folder). Other programs show their title and, by default, keep the start. To force
    /// a direction for a specific program, add an entry to <see cref="ProgramTrim"/> keyed by its
    /// executable file name (e.g. "devenv.exe").
    /// </summary>
    public static class TaskButtonDisplayRules
    {
        public enum Trim { Start, End }

        // Per-program overrides for the trim direction, keyed by executable file name
        // (case-insensitive). File Explorer is handled separately below because it also swaps
        // the text for the folder path. Add programs here as needed, for example:
        //     ["devenv.exe"] = Trim.End,
        //     ["notepad.exe"] = Trim.Start,
        private static readonly Dictionary<string, Trim> ProgramTrim =
            new Dictionary<string, Trim>(StringComparer.OrdinalIgnoreCase)
            {
            };

        /// <summary>The text to show on the button: the folder path for File Explorer, else the title.</summary>
        public static string GetText(string? title, ApplicationWindow? window)
        {
            if (window != null && ExplorerPathHelper.IsFileExplorerWindow(window.Handle))
            {
                string? path = ExplorerPathHelper.GetPath(window.Handle);
                if (!string.IsNullOrEmpty(path))
                    return path!;
            }

            return title ?? "";
        }

        /// <summary>Whether the button keeps the end of the text (true) or the start (false).</summary>
        public static bool TrimToEnd(ApplicationWindow? window)
        {
            if (window == null)
                return false;

            // File Explorer: keep the end of the path (the deepest folder).
            if (ExplorerPathHelper.IsFileExplorerWindow(window.Handle))
                return true;

            string exe = GetExeName(window);
            if (exe.Length > 0 && ProgramTrim.TryGetValue(exe, out Trim trim))
                return trim == Trim.End;

            // Default: preserve the existing heuristic (URI-like titles keep their end).
            return window.TaskBarDisplayType == TaskBarDisplayType.Right;
        }

        private static string GetExeName(ApplicationWindow window)
        {
            try
            {
                string file = window.WinFileName;
                return string.IsNullOrEmpty(file) ? "" : Path.GetFileName(file);
            }
            catch
            {
                return "";
            }
        }
    }
}
