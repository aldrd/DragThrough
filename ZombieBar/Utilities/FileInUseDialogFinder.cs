#nullable enable
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Looks for Explorer's "File In Use" / "Folder In Use" dialog and works out which file or folder
    /// it is about. The dialog only shows the item's name (not its folder, and without the extension
    /// when extensions are hidden), so the name is matched against the folders open in File Explorer
    /// windows - the dialog's own Explorer window first - and the desktop. Window titles are not
    /// used, so this does not depend on the system language.
    /// </summary>
    public static class FileInUseDialogFinder
    {
        public sealed class Result
        {
            public bool DialogFound { get; init; }
            /// <summary>The resolved file or folder, or null if it couldn't be determined.</summary>
            public string? Path { get; init; }
            /// <summary>A name read from the dialog that could not be located (for the user's information).</summary>
            public string? UnresolvedName { get; init; }
        }

        private const string DialogClass = "#32770";

        /// <summary>Runs the search on its own STA thread (UI Automation and Shell COM both want one).</summary>
        public static Result Find()
        {
            Result result = new() { DialogFound = false };
            var thread = new Thread(() =>
            {
                try { result = FindCore(); }
                catch { result = new Result { DialogFound = false }; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(20));
            return result;
        }

        private static Result FindCore()
        {
            HashSet<uint> explorers = GetSessionExplorerPids();
            List<IntPtr> dialogs = new();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd)
                    && GetClassName(hwnd) == DialogClass
                    && NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) != 0
                    && explorers.Contains(pid))
                    dialogs.Add(hwnd);
                return true;
            }, 0);

            if (dialogs.Count == 0)
                return new Result { DialogFound = false };

            // Dialogs titled like the "In Use" one go first; EnumWindows already yields top-most first.
            dialogs = dialogs.OrderByDescending(d => HasInUseTitle(d)).ToList();

            List<string> folders = GetCandidateFolders();
            bool inUseDialogSeen = false;
            string? unresolved = null;

            foreach (IntPtr dialog in dialogs)
            {
                bool inUseTitle = HasInUseTitle(dialog);
                List<string> names = GetTexts(dialog).Select(Clean).Where(LooksLikeName).Distinct()
                                         .OrderByDescending(n => Path.HasExtension(n)).ToList();

                List<string> dialogFolders = GetOwnerExplorerFolders(dialog).Concat(folders)
                                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string name in names)
                {
                    string? path = Resolve(name, dialogFolders);
                    if (path != null)
                        return new Result { DialogFound = true, Path = path };
                }

                // Without a resolvable name, only a dialog whose title is a known "In Use" title counts
                // as found (so other Explorer dialogs - properties, confirmations - are not reported).
                if (inUseTitle && !inUseDialogSeen)
                {
                    inUseDialogSeen = true;
                    unresolved = names.FirstOrDefault(n => Path.HasExtension(n) || !n.Contains(' '));
                }
            }

            return new Result { DialogFound = inUseDialogSeen, UnresolvedName = unresolved };
        }

        // Titles of the dialog in the app's languages; used only to recognise the dialog when its
        // item can't be resolved, never to find the item itself.
        private static readonly string[] InUseTitleWords =
        {
            "In Use", "уже используется", "en uso", "en cours d", "em uso", "正在使用",
        };

        private static bool HasInUseTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            return InUseTitleWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        private static string? Resolve(string name, List<string> folders)
        {
            if (Path.IsPathRooted(name))
                return File.Exists(name) || Directory.Exists(name) ? name : null;

            foreach (string folder in folders)
            {
                try
                {
                    string direct = Path.Combine(folder, name);
                    if (File.Exists(direct) || Directory.Exists(direct))
                        return direct;

                    // Extensions hidden in Explorer: "Report" for "Report.docx". Accept only a single,
                    // unambiguous match.
                    string[] matches = Directory.GetFiles(folder, name + ".*")
                        .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (matches.Length == 1)
                        return matches[0];
                }
                catch { /* inaccessible folder, invalid name */ }
            }
            return null;
        }

        private static List<string> GetTexts(IntPtr dialog)
        {
            var texts = new List<string>();
            try
            {
                AutomationElement root = AutomationElement.FromHandle(dialog);
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    try
                    {
                        // Button captions ("Cancel", "Try Again") and the like are never the item name.
                        if (SkippedControlTypes.Contains(e.Current.ControlType))
                            continue;
                        string name = e.Current.Name;
                        if (!string.IsNullOrWhiteSpace(name))
                            texts.Add(name);
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch { /* dialog closed or UIA unavailable */ }

            // Classic (non-DirectUI) dialogs: plain child-window text.
            NativeMethods.EnumChildWindows(dialog, (child, _) =>
            {
                var sb = new StringBuilder(512);
                if (GetClassName(child) != "Button" && NativeMethods.GetWindowText(child, sb, sb.Capacity) > 0)
                    texts.Add(sb.ToString());
                return true;
            }, 0);

            return texts;
        }

        private static readonly ControlType[] SkippedControlTypes =
        {
            ControlType.Button, ControlType.CheckBox, ControlType.Hyperlink, ControlType.TitleBar,
            ControlType.MenuItem, ControlType.MenuBar, ControlType.ScrollBar, ControlType.SplitButton,
            ControlType.RadioButton, ControlType.Window,
        };

        // Explorer wraps names in invisible bidi/formatting marks in some languages.
        private static string Clean(string s) =>
            new string(s.Where(c => c != '\u200E' && c != '\u200F' && c != '\u202A' && c != '\u202C' && c != '\u202B').ToArray()).Trim();

        private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Where(c => c != '\\' && c != ':').ToArray();

        private static bool LooksLikeName(string s) =>
            s.Length > 0 && s.Length <= 260 && s.IndexOfAny(Invalid) < 0 && !s.EndsWith(".", StringComparison.Ordinal)
            || Path.IsPathRooted(s) && s.Length <= 32767;

        private static IEnumerable<string> GetOwnerExplorerFolders(IntPtr dialog)
        {
            var result = new List<string>();
            IntPtr owner = dialog;
            for (int i = 0; i < 4; i++)
            {
                owner = NativeMethods.GetWindow(owner, NativeMethods.GetWindow_Cmd.GW_OWNER);
                if (owner == IntPtr.Zero)
                    break;
                if (ExplorerPathHelper.GetPath(owner) is { } path)
                    result.Add(path);
            }
            return result;
        }

        private static List<string> GetCandidateFolders()
        {
            var folders = new List<string>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (ExplorerPathHelper.IsFileExplorerWindow(hwnd) && ExplorerPathHelper.GetPath(hwnd) is { } path)
                    folders.Add(path);
                return true;
            }, 0);

            foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
            {
                string p = Environment.GetFolderPath(sf);
                if (!string.IsNullOrEmpty(p))
                    folders.Add(p);
            }
            return folders;
        }

        private static HashSet<uint> GetSessionExplorerPids()
        {
            int session = Process.GetCurrentProcess().SessionId;
            var pids = new HashSet<uint>();
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    try { if (p.SessionId == session) pids.Add((uint)p.Id); }
                    catch { }
                }
            }
            return pids;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
