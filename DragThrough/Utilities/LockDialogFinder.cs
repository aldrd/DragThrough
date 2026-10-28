#nullable enable
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace DragThrough.Utilities
{
    /// <summary>
    /// Looks for an on-screen dialog complaining that a file or folder is in use or can't be accessed
    /// and works out which files or folders it is about. Two kinds are recognised:
    /// <list type="bullet">
    /// <item>Explorer's "File In Use" / "Folder In Use" dialog: it shows only the item's name (without
    /// the extension when extensions are hidden), which is matched against the folders open in File
    /// Explorer windows - the dialog's own window first - and the desktop.</item>
    /// <item>Message boxes and other dialogs of any program whose text looks like a lock or access error
    /// ("access is denied", "os error 32", "used by another process", ...). Quoted names, absolute paths
    /// and file names are taken from the text; relative ones are looked up next to the program that
    /// shows the dialog (its folder and the folders above it, its working folder, the paths on its
    /// command line, the same for the process that started it), then in Explorer windows and on the
    /// desktop.</item>
    /// </list>
    /// Dialogs are checked top-most first (the foreground window before all others).
    /// </summary>
    public static class LockDialogFinder
    {
        public sealed class Result
        {
            public bool DialogFound { get; init; }
            public string? DialogTitle { get; init; }
            public string? ProcessName { get; init; }
            /// <summary>The dialog's window, so the results can be shown beside it.</summary>
            public IntPtr DialogHandle { get; init; }
            /// <summary>Existing files or folders the dialog is about, most likely first.</summary>
            public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();
            /// <summary>A name read from the dialog that could not be located (for the user's information).</summary>
            public string? UnresolvedName { get; init; }
        }

        private const string DialogClass = "#32770";
        private const int MaxDialogs = 25;
        private const int MaxCandidates = 12;

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

        private enum Kind { None, Generic, Lock }

        private sealed record Examined(IntPtr Hwnd, string Title, string ProcessName, Kind Kind, List<string> Candidates, string? Unresolved);

        private static Result FindCore()
        {
            List<string>? sharedFolders = null;
            List<string> SharedFolders() => sharedFolders ??= GetCandidateFolders();

            HashSet<uint> explorers = GetSessionExplorerPids();
            var examined = new List<Examined>();
            foreach (IntPtr dialog in GetDialogs())
            {
                if (Examine(dialog, explorers, SharedFolders) is { } e && e.Kind != Kind.None)
                    examined.Add(e);
            }

            // Lock/access errors with something to check beat generic errors; a recognised lock dialog
            // whose item can't be located is still reported, so the user knows it was seen.
            Examined? best = examined.FirstOrDefault(e => e.Kind == Kind.Lock && e.Candidates.Count > 0)
                          ?? examined.FirstOrDefault(e => e.Kind == Kind.Generic && e.Candidates.Count > 0)
                          ?? examined.FirstOrDefault(e => e.Kind == Kind.Lock);
            if (best == null)
                return new Result { DialogFound = false };

            return new Result
            {
                DialogFound = true,
                DialogTitle = best.Title,
                DialogHandle = best.Hwnd,
                ProcessName = best.ProcessName,
                Candidates = best.Candidates,
                UnresolvedName = best.Candidates.Count == 0 ? best.Unresolved : null,
            };
        }

        // Visible dialogs of other processes in z-order (EnumWindows yields top-most first), with the
        // foreground window moved to the front.
        private static List<IntPtr> GetDialogs()
        {
            uint self = (uint)Environment.ProcessId;
            var dialogs = new List<IntPtr>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd)
                    && !NativeMethods.IsIconic(hwnd)
                    && NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) != 0
                    && pid != self
                    && IsDialogLike(hwnd))
                    dialogs.Add(hwnd);
                return dialogs.Count < MaxDialogs;
            }, 0);

            IntPtr foreground = GetForegroundWindow();
            if (dialogs.Remove(foreground))
                dialogs.Insert(0, foreground);
            return dialogs;
        }

        private static bool IsDialogLike(IntPtr hwnd)
        {
            string cls = GetClassName(hwnd);
            if (cls == DialogClass)
                return true;
            // Custom-drawn dialogs (Qt, Electron, WinUI, ...): an owned window with a title bar.
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            return (style & WS_CAPTION) == WS_CAPTION
                && NativeMethods.GetWindow(hwnd, NativeMethods.GetWindow_Cmd.GW_OWNER) != IntPtr.Zero
                && !SkippedClasses.Contains(cls);
        }

        private static readonly HashSet<string> SkippedClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "tooltips_class32", "#32768", "SysShadow", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        };

        private static Examined? Examine(IntPtr dialog, HashSet<uint> explorers, Func<List<string>> sharedFolders)
        {
            if (NativeMethods.GetWindowThreadProcessId(dialog, out uint upid) == 0)
                return null;
            int pid = (int)upid;
            string title = GetWindowText(dialog);
            List<string> texts = GetTexts(dialog).Select(Clean).Where(t => t.Length > 0).Distinct().ToList();
            string allText = title + "\n" + string.Join("\n", texts);
            string processName = SafeProcessName(pid);

            if (explorers.Contains(upid))
            {
                // Explorer's own dialog: any text line may be the bare item name.
                bool inUse = InUseTitleWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)) || HasAny(allText, LockPhrases);
                List<string> names = texts.Where(LooksLikeName).OrderByDescending(n => Path.HasExtension(n)).ToList();
                List<string> folders = GetOwnerExplorerFolders(dialog).Concat(sharedFolders()).ToList();
                List<string> found = ResolveAll(names, folders);
                // Without a known "In Use" title only a resolvable name counts (so that properties
                // windows and confirmations aren't reported as "found but unresolved").
                Kind kind = inUse ? Kind.Lock : found.Count > 0 ? Kind.Generic : Kind.None;
                return new Examined(dialog, title, processName, kind, found,
                                    names.FirstOrDefault(n => Path.HasExtension(n) || !n.Contains(' ')));
            }

            Kind k = HasAny(allText, LockPhrases) ? Kind.Lock : HasAny(allText, ErrorWords) ? Kind.Generic : Kind.None;
            if (k == Kind.None)
                return new Examined(dialog, title, processName, k, new List<string>(), null);

            // The title is the program's name ("Cursor"), not the item: only the body text is used.
            List<string> extracted = ExtractNames(texts);
            List<string> candidates = extracted.Count == 0
                ? new List<string>()
                : ResolveAll(extracted, GetProcessContextFolders(pid).Concat(GetOwnerExplorerFolders(dialog)).Concat(sharedFolders()).ToList());
            return new Examined(dialog, title, processName, k, candidates, extracted.FirstOrDefault());
        }

        // --- Recognising the dialog ------------------------------------------------------------------
        // Titles of Explorer's dialog in the app's languages.
        private static readonly string[] InUseTitleWords =
        {
            "In Use", "уже используется", "en uso", "en cours d", "em uso", "正在使用",
        };

        // Lock / access errors as worded by Windows, common runtimes (Rust/.NET/Win32 "os error N",
        // "error 32") and installers, in the app's languages.
        private static readonly string[] LockPhrases =
        {
            "os error 5)", "os error 5 ", "os error 32", "os error 33", "error 5:", "error 32:", "error code 5", "error code 32",
            "access is denied", "access denied", "permission denied", "sharing violation", "lock violation",
            "used by another process", "in use by another", "being used by", "is open in", "is in use", "file is locked", "is locked",
            "cannot access the file", "can't access the file",
            "отказано в доступе", "доступ запрещен", "доступ запрещён", "используется другим", "занят другим", "открыт в", "заблокирован",
            "acceso denegado", "en uso por otro", "está siendo utilizado", "está en uso", "otro proceso",
            "accès refusé", "accès est refusé", "utilisé par un autre", "est ouvert dans", "en cours d'utilisation",
            "acesso negado", "em uso por outro", "sendo usado por outro", "está aberto em", "outro processo",
            "拒绝访问", "另一个程序正在使用", "另一进程使用", "另一个进程", "正在使用", "被占用",
        };

        // Weaker signal: an error that mentions an existing file or folder may still be a lock.
        private static readonly string[] ErrorWords =
        {
            "error", "cannot", "can't", "could not", "couldn't", "unable to", "failed",
            "ошибка", "не удается", "не удаётся", "не удалось", "невозможно",
            "no se puede", "no se pudo", "erreur", "impossible", "échec", "erro", "não foi possível", "não é possível",
            "错误", "无法", "失败",
        };

        private static bool HasAny(string text, string[] phrases) =>
            phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        // --- Taking names out of the text ------------------------------------------------------------
        private static readonly Regex QuotedRegex = new(
            "\"([^\"\\r\\n]{1,260})\"|«([^»\\r\\n]{1,260})»|“([^”\\r\\n]{1,260})”|„([^“”\\r\\n]{1,260})[“”]|‘([^’\\r\\n]{1,260})’|「([^」\\r\\n]{1,260})」|『([^』\\r\\n]{1,260})』|(?<![\\w])'([^'\\r\\n]{1,260})'(?![\\w])",
            RegexOptions.Compiled);

        private static readonly Regex AbsolutePathRegex = new(
            @"(?:\\\\\?\\)?(?:[A-Za-z]:\\|\\\\[^\\\s""]+\\)[^\r\n""<>|*?“”«»]*",
            RegexOptions.Compiled);

        private static readonly Regex FileNameRegex = new(
            @"(?<![\w\\/.])[\w\-~$@()\[\]]+(?:\.[\w\-~$@()\[\]]+)*\.[A-Za-z][A-Za-z0-9]{0,7}(?![\w\\/])",
            RegexOptions.Compiled);

        private static List<string> ExtractNames(List<string> texts)
        {
            var names = new List<string>();
            foreach (string text in texts)
            {
                foreach (Match m in AbsolutePathRegex.Matches(text))
                    if (TrimToExisting(m.Value) is { } p)
                        names.Add(p);

                foreach (Match m in QuotedRegex.Matches(text))
                {
                    string q = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.Trim();
                    if (LooksLikeName(q))
                        names.Add(q);
                }

                foreach (Match m in FileNameRegex.Matches(text))
                    names.Add(m.Value);
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // "C:\App\resources: Access denied" - the path ends somewhere in the sentence; cut it back to the
        // longest prefix that exists.
        private static string? TrimToExisting(string s)
        {
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
                s = s.Substring(4);
            for (int i = 0; i < 40 && s.Length > 3; i++)
            {
                string trimmed = s.TrimEnd(' ', '.', ',', ';', ':', ')', ']', '}', '\'', '!', '?', '\t');
                if (trimmed.Length >= 3 && Exists(trimmed))
                    return trimmed.TrimEnd('\\').Length >= 3 ? trimmed.TrimEnd('\\') : trimmed;
                int cut = trimmed.LastIndexOf(' ');
                if (cut < 3)
                    break;
                s = trimmed.Substring(0, cut);
            }
            return null;
        }

        // --- Locating names --------------------------------------------------------------------------
        private static List<string> ResolveAll(List<string> names, List<string> folders)
        {
            folders = folders.Where(f => !string.IsNullOrEmpty(f) && !IsRoot(f))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = new List<string>();
            foreach (string name in names)
            {
                foreach (string path in Resolve(name, folders))
                {
                    if (!IsTooBroad(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
                        found.Add(path);
                    if (found.Count >= MaxCandidates)
                        return found;
                }
            }
            return found;
        }

        private static IEnumerable<string> Resolve(string name, List<string> folders)
        {
            if (Path.IsPathRooted(name))
            {
                if (Exists(name))
                    yield return Path.GetFullPath(name);
                yield break;
            }

            foreach (string folder in folders)
            {
                string? direct = null;
                string[] matches = Array.Empty<string>();
                try
                {
                    direct = Path.GetFullPath(Path.Combine(folder, name));
                    if (!Exists(direct))
                    {
                        direct = null;
                        // Extensions hidden in Explorer: "Report" for "Report.docx". Accept only a single,
                        // unambiguous match.
                        if (!Path.HasExtension(name) && name.IndexOfAny(new[] { '\\', '/' }) < 0)
                            matches = Directory.GetFiles(folder, name + ".*")
                                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                    }
                }
                catch { /* inaccessible folder, invalid name */ }

                if (direct != null)
                    yield return direct;
                else if (matches.Length == 1)
                    yield return matches[0];
            }
        }

        private static bool Exists(string path)
        {
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
        }

        private static bool IsRoot(string folder)
        {
            try { return Path.GetPathRoot(folder) is { } root && string.Equals(root.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return true; }
        }

        // Folders far too big (and too general) to be what a dialog is about.
        private static bool IsTooBroad(string path)
        {
            if (IsRoot(path))
                return true;
            string p = path.TrimEnd('\\');
            return BroadFolders.Value.Contains(p);
        }

        private static readonly Lazy<HashSet<string>> BroadFolders = new(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Environment.SpecialFolder sf in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
            })
            {
                string f = Environment.GetFolderPath(sf);
                if (!string.IsNullOrEmpty(f))
                    set.Add(f.TrimEnd('\\'));
            }
            string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
            if (users.Length > 0)
                set.Add(users.TrimEnd('\\'));
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            set.Add(programs);
            set.Add(Path.GetTempPath().TrimEnd('\\'));
            return set;
        });

        // Where a relative name in a program's message most likely lives: next to the program (an
        // updater in "<app>\tools" talks about "<app>\resources"), in its working folder, next to the
        // files on its command line - and the same for the process that started it.
        private static List<string> GetProcessContextFolders(int pid)
        {
            var folders = new List<string>();
            AddProcessFolders(folders, pid, parentLevels: 3);
            int parent = FileLockFinder.GetParentPid(pid);
            if (parent > 4 && parent != pid && parent != Environment.ProcessId && !IsExplorer(parent))
                AddProcessFolders(folders, parent, parentLevels: 1);
            return folders;
        }

        private static void AddProcessFolders(List<string> folders, int pid, int parentLevels)
        {
            if (FileLockFinder.GetImagePath(pid) is { } exe)
            {
                string? dir = Path.GetDirectoryName(exe);
                for (int i = 0; i <= parentLevels && !string.IsNullOrEmpty(dir); i++)
                {
                    folders.Add(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }

            if (FileLockFinder.GetCurrentDirectory(pid) is { } cwd)
                folders.Add(cwd.TrimEnd('\\'));

            if (FileLockFinder.GetCommandLine(pid) is { } commandLine)
            {
                foreach (Match m in AbsolutePathRegex.Matches(commandLine.Replace("\"", "\n")))
                {
                    if (TrimToExisting(m.Value) is not { } p)
                        continue;
                    if (Directory.Exists(p))
                        folders.Add(p);
                    else if (Path.GetDirectoryName(p) is { } d)
                        folders.Add(d);
                }
            }
        }

        // --- Reading the dialog ----------------------------------------------------------------------
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
                        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out object vp)
                            && ((ValuePattern)vp).Current.Value is { Length: > 0 } value)
                            texts.Add(value);
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch { /* dialog closed or UIA unavailable */ }

            // Classic dialogs: child-window text. WM_GETTEXT (not GetWindowText) so that edit controls
            // of other processes return their contents too.
            NativeMethods.EnumChildWindows(dialog, (child, _) =>
            {
                if (GetClassName(child) != "Button" && GetTextByMessage(child) is { Length: > 0 } t)
                    texts.Add(t);
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

        private static bool IsExplorer(int pid) =>
            FileLockFinder.GetImagePath(pid) is { } exe
            && string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase);

        private static string SafeProcessName(int pid)
        {
            if (FileLockFinder.GetImagePath(pid) is { } exe)
                return Path.GetFileName(exe);
            try { using Process p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
            catch { return "?"; }
        }

        private static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowText(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string? GetTextByMessage(IntPtr hwnd)
        {
            if (SendMessageTimeout(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out IntPtr len) == IntPtr.Zero)
                return null;
            int length = (int)Math.Min(len.ToInt64(), 32767);
            if (length <= 0)
                return null;
            var sb = new StringBuilder(length + 1);
            return SendMessageTimeout(hwnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, 500, out _) == IntPtr.Zero
                ? null
                : sb.ToString();
        }

        // --- Interop ---------------------------------------------------------------------------------
        private const int GWL_STYLE = -16;
        private const long WS_CAPTION = 0x00C00000;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    }
}
