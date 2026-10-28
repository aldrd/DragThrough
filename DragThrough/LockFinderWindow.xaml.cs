#nullable enable
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DragThrough.Utilities;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DragThrough
{
    /// <summary>
    /// "Find the process locking a file": takes the file or folder from an on-screen "File In Use"
    /// dialog or another program's lock / access-denied error (or lets the user choose one), lists the
    /// programs using it and offers to switch to or close each of them. Single instance; opening it
    /// again re-runs the search. Stays on top and moves beside the dialog it read, so that dialog
    /// (often not movable) stays visible.
    /// </summary>
    public partial class LockFinderWindow : Window
    {
        private static LockFinderWindow? _instance;

        private string? _path;
        private string _heading = "";
        private LockDialogFinder.Result? _found;
        private CancellationTokenSource? _scanCts;
        private IntPtr _anchor;
        private bool _userMoved;

        private LockFinderWindow()
        {
            InitializeComponent();
            AppUi.ApplyDialogTheme(this);
            AppIcon.Source = AppUi.LoadAppIcon();
        }

        /// <summary>Shows the window (or brings the open one forward) and looks for a lock dialog.</summary>
        public static void Open()
        {
            if (_instance == null)
            {
                _instance = new LockFinderWindow();
                _instance.Closed += (_, _) => _instance = null;
                _instance.Show();
            }

            _instance.BringToFront();
            _ = _instance.FindFromDialogAsync();
        }

        private void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(hwnd);
        }

        private async Task FindFromDialogAsync()
        {
            CancelScan();
            ShowBusy(Loc("lock_searching", "Searching…"), canStop: false);
            LockDialogFinder.Result found = await Task.Run(LockDialogFinder.Find);
            if (!IsLoaded)
                return;

            _found = found;
            _anchor = found.DialogFound ? found.DialogHandle : IntPtr.Zero;
            _userMoved = false;
            PlaceBesideAnchor();
            BringToFront();

            if (found.Candidates.Count == 1)
            {
                await ScanAsync(found.Candidates[0], DialogHeading(found));
                return;
            }

            if (found.Candidates.Count > 1)
            {
                ShowCandidates(found);
                return;
            }

            _path = null;
            EndBusy();
            ClearResults();
            PathText.Visibility = Visibility.Collapsed;
            SetStatus(found.DialogFound
                ? string.Format(Loc("lock_unresolved", "The “{1}” window was found, but the location of “{0}” could not be determined. Choose the file or folder to check."),
                                found.UnresolvedName ?? "?", DialogName(found))
                : Loc("lock_no_dialog", "No window about a file in use or denied access was found. Choose a file or folder to check."));
            SetNote(null);
        }

        private static string DialogName(LockDialogFinder.Result found) =>
            string.IsNullOrWhiteSpace(found.DialogTitle) ? found.ProcessName ?? "?" : found.DialogTitle!;

        private static string DialogHeading(LockDialogFinder.Result found) =>
            string.Format(Loc("lock_from_dialog", "From the “{0}” window ({1}):"), DialogName(found), found.ProcessName ?? "?");

        // Several files or folders fit the dialog: let the user pick which one to check.
        private void ShowCandidates(LockDialogFinder.Result found)
        {
            CancelScan();
            _path = null;
            EndBusy();
            ClearResults();
            PathText.Visibility = Visibility.Collapsed;
            SetStatus(string.Format(Loc("lock_candidates", "The “{0}” window ({1}) may be about any of these. Choose what to check:"),
                                    DialogName(found), found.ProcessName ?? "?"));
            SetNote(null);

            foreach (string candidate in found.Candidates)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var name = new TextBlock { Text = Path.GetFileName(candidate.TrimEnd('\\')), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                info.Children.Add(name);
                info.Children.Add(Small(candidate));

                var check = new Button { Content = Loc("lock_check", "Check"), VerticalAlignment = VerticalAlignment.Center };
                check.SetResourceReference(StyleProperty, "DialogButton");
                check.Click += async (_, _) => await ScanAsync(candidate, DialogHeading(found));

                Grid.SetColumn(check, 1);
                grid.Children.Add(info);
                grid.Children.Add(check);
                ResultsPanel.Children.Add(RowBorder(grid));
            }
        }

        // --- Scanning ------------------------------------------------------------------------------
        private async Task ScanAsync(string path, string heading, bool elevated = false)
        {
            CancelScan();
            var cts = new CancellationTokenSource();
            _scanCts = cts;

            _path = path;
            _heading = heading;
            ShowBusy(heading, canStop: true);
            PathText.Text = path;
            PathText.Visibility = Visibility.Visible;
            ProgressText.Text = elevated
                ? Loc("lock_progress_elevated", "Checking with administrator rights… Confirm the Windows prompt.")
                : Loc("lock_progress_start", "Getting ready…");
            ProgressText.Visibility = Visibility.Visible;

            var progress = new Progress<FileLockFinder.ScanProgress>(p =>
            {
                if (cts == _scanCts)
                    ProgressText.Text = ProgressLine(p);
            });

            // Findings are shown as they come; the scan thread hands over copies.
            bool isFolder = Directory.Exists(path);
            void Found(FileLockFinder.LockingProcess p) => Dispatcher.BeginInvoke(() =>
            {
                if (cts == _scanCts && IsLoaded)
                    ShowFound(p, isFolder, path);
            });

            FileLockFinder.ScanResult? result = null;
            string? error = null;
            try
            {
                if (elevated)
                {
                    result = await ElevatedLockScan.RunAsync(path, cts.Token, Found, p => ((IProgress<FileLockFinder.ScanProgress>)progress).Report(p));
                    if (result == null)
                        error = Loc("lock_admin_declined", "The check with administrator rights was cancelled.");
                }
                else
                {
                    // The scan stops itself on cancellation and returns what it has found so far.
                    result = await Task.Run(() => FileLockFinder.Scan(path, progress, cts.Token, Found));
                }
            }
            catch (OperationCanceledException) { error = Loc("lock_cancelled", "The check was stopped."); }
            catch (FileNotFoundException) { error = Loc("lock_not_found", "The file or folder no longer exists."); }
            catch (Exception ex) { error = string.Format(Loc("lock_error", "Could not check: {0}"), ex.Message); }

            if (cts != _scanCts || !IsLoaded)
                return;
            _scanCts = null;
            EndBusy();
            ShowResult(path, heading, result, error);
        }

        // Rows found during the scan stay where they are; the summary goes below them.
        private void ShowResult(string path, string heading, FileLockFinder.ScanResult? result, string? error)
        {
            SetStatus(heading);

            var notes = new List<string>();
            if (error != null)
                notes.Add(error);
            if (result != null)
            {
                if (result.Elevated)
                    notes.Add(Loc("lock_elevated_done", "Checked with administrator rights."));
                if (result.Cancelled)
                    notes.Add(Loc("lock_partial", "The check was stopped: only what was found until then is shown."));
                if (result.FileListCapped)
                    notes.Add(string.Format(Loc("lock_truncated", "The folder has more than {0} files: only that many were checked by name (open files and folders of programs were checked in full)."),
                                            result.FilesFound.ToString("N0")));
                if (result.HandleScanFailed)
                    notes.Add(Loc("lock_handles_unavailable", "Windows did not provide the list of open files, so only loaded libraries, working folders and the files themselves were checked."));
            }
            SetNote(notes.Count > 0 ? string.Join("\n", notes) : null);

            if (result != null)
            {
                foreach (FileLockFinder.LockingProcess p in result.Processes)
                    ShowFound(p, result.IsFolder, result.Path);

                if (_rows.Count == 0)
                {
                    var none = new TextBlock
                    {
                        Text = Loc("lock_none", "None of the programs that could be checked is using it right now."),
                        TextWrapping = TextWrapping.Wrap
                    };
                    none.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                    ResultsPanel.Children.Add(none);

                    var hints = new List<string>();
                    if (result.Scanners.Count > 0)
                        hints.Add(string.Format(Loc("lock_hint_scanners", "An antivirus or the search indexer ({0}) may be opening the files for a moment: wait a little and check again."),
                                                string.Join(", ", result.Scanners)));
                    hints.Add(Loc("lock_hint_general", "If the folder is open in File Explorer (or selected with the preview pane on), close that window. If an installer or updater reports the error, quit the program completely, including its tray icon, and retry."));
                    foreach (string hint in hints)
                        ResultsPanel.Children.Add(Small("• " + hint));
                }

                AddSkippedInfo(path, heading, result);
            }

            if (_found is { Candidates.Count: > 1 } found && found.Candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                var back = new Button
                {
                    Content = Loc("lock_back_to_candidates", "Other matches…"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                back.SetResourceReference(StyleProperty, "DialogButton");
                back.Click += (_, _) => ShowCandidates(found);
                ResultsPanel.Children.Add(back);
            }
        }

        // Processes that couldn't be opened, and (when not elevated) the offer to check as administrator.
        private void AddSkippedInfo(string path, string heading, FileLockFinder.ScanResult result)
        {
            if (result.Skipped.Count == 0)
                return;

            const int shown = 8;
            List<string> names = result.Skipped.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string list = string.Join(", ", names.Take(shown)) + (names.Count > shown ? ", …" : "");
            string text = result.Elevated
                ? string.Format(Loc("lock_skipped_elevated", "{0} protected system processes could not be checked even with administrator rights: {1}"), result.Skipped.Count, list)
                : string.Format(Loc("lock_skipped", "{0} processes could not be checked (system services or programs running as administrator): {1}"), result.Skipped.Count, list);
            var info = Small(text);
            info.Margin = new Thickness(0, 8, 0, 0);
            ResultsPanel.Children.Add(info);

            if (result.Elevated)
                return;
            var admin = new Button
            {
                Content = Loc("lock_check_admin", "Check with administrator rights"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0)
            };
            admin.SetResourceReference(StyleProperty, "DialogButton");
            admin.Click += async (_, _) => await ScanAsync(path, heading, elevated: true);
            ResultsPanel.Children.Add(admin);
        }

        private static string ProgressLine(FileLockFinder.ScanProgress p)
        {
            string line = p.Stage switch
            {
                FileLockFinder.ScanStage.Processes => string.Format(Loc("lock_progress_processes", "Checking programs and their libraries: {0} of {1}"), p.Done, p.Total),
                FileLockFinder.ScanStage.Handles when p.Total == 0 => Loc("lock_progress_handle_list", "Reading the list of open files…"),
                FileLockFinder.ScanStage.Handles => string.Format(Loc("lock_progress_handles", "Checking open files and folders: program {0} of {1}"), p.Done, p.Total),
                FileLockFinder.ScanStage.Explorer => Loc("lock_progress_explorer", "Checking File Explorer windows…"),
                FileLockFinder.ScanStage.ListingFiles => string.Format(Loc("lock_progress_listing", "Listing files in the folder: {0}"), p.Done.ToString("N0")),
                FileLockFinder.ScanStage.Files => string.Format(Loc("lock_progress_files", "Checking files: {0} of {1}"), p.Done.ToString("N0"), p.Total.ToString("N0")),
                _ => ""
            };
            if (p.Found > 0)
                line += " · " + string.Format(Loc("lock_progress_found", "programs found: {0}"), p.Found);
            return line;
        }

        private void CancelScan()
        {
            _scanCts?.Cancel();
            _scanCts = null;
        }

        // --- Result rows ---------------------------------------------------------------------------
        private readonly Dictionary<int, LiveRow> _rows = new();

        // Adds a row for a newly found process, or refreshes the existing one (same place in the list).
        private void ShowFound(FileLockFinder.LockingProcess p, bool isFolder, string root)
        {
            if (_rows.TryGetValue(p.Pid, out LiveRow? row))
            {
                row.Update(p);
                return;
            }
            row = new LiveRow(this, p, isFolder, root);
            _rows[p.Pid] = row;
            ResultsPanel.Children.Add(row.Element);
        }

        private sealed class LiveRow
        {
            public Border Element { get; }
            private readonly TextBlock _details;
            private readonly bool _isFolder;
            private readonly string _root;

            public LiveRow(LockFinderWindow owner, FileLockFinder.LockingProcess p, bool isFolder, string root)
            {
                _isFolder = isFolder;
                _root = root;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
                title.Inlines.Add(new System.Windows.Documents.Run(p.Name) { FontWeight = FontWeights.SemiBold });
                title.Inlines.Add(new System.Windows.Documents.Run("   " + string.Format(Loc("lock_pid", "PID {0}"), p.Pid)));
                title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                info.Children.Add(title);
                _details = Small("");
                info.Children.Add(_details);

                bool isExplorer = p.ExePath != null
                    && string.Equals(Path.GetFileName(p.ExePath), "explorer.exe", StringComparison.OrdinalIgnoreCase);
                List<IntPtr> windows = GetTopLevelWindows(p.Pid);
                if (isExplorer)
                    info.Children.Add(Small(Loc("lock_explorer_hint", "This is Windows Explorer: close the folder window or the preview pane, or use “Restart Explorer”.")));
                else if (windows.Count == 0)
                    info.Children.Add(Small(Loc("lock_no_window", "This program has no window to switch to or close.")));

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var switchButton = new Button { Content = Loc("lock_switch", "Switch to"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = windows.Count > 0 };
                switchButton.SetResourceReference(StyleProperty, "DialogButton");
                switchButton.Click += (_, _) => owner.SwitchTo(p);
                var closeButton = new Button { Content = Loc("lock_close", "Close program"), IsEnabled = windows.Count > 0 && !isExplorer };
                closeButton.SetResourceReference(StyleProperty, "DialogButton");
                closeButton.Click += (_, _) => owner.CloseProgram(p);
                buttons.Children.Add(switchButton);
                buttons.Children.Add(closeButton);

                Grid.SetColumn(buttons, 1);
                grid.Children.Add(info);
                grid.Children.Add(buttons);
                Element = RowBorder(grid);
                Update(p);
            }

            public void Update(FileLockFinder.LockingProcess p)
            {
                var details = new List<string>();
                if (p.ExePath != null) details.Add(p.ExePath);
                if (p.ServiceName != null) details.Add(string.Format(Loc("lock_service", "Service: {0}"), p.ServiceName));
                details.Add(string.Join(", ", p.Reasons.OrderBy(r => r).Select(ReasonText)));
                List<string> items = p.Items.Select(i => ItemText(i, _isFolder, _root)).Where(i => i.Length > 0).ToList();
                if (items.Count > 0)
                    details.Add(string.Format(Loc("lock_items", "Uses: {0}"), string.Join("; ", items)));
                string text = string.Join("\n", details);
                if (_details.Text != text)
                    _details.Text = text;
            }
        }

        // Items inside the checked folder are shown relative to it ("app\node_modules.asar").
        private static string ItemText(string item, bool isFolder, string root)
        {
            if (!isFolder)
                return "";
            try
            {
                string relative = Path.GetRelativePath(root, item);
                return relative == "." ? Loc("lock_item_self", "the folder itself")
                     : relative.StartsWith("..", StringComparison.Ordinal) ? item
                     : relative;
            }
            catch { return item; }
        }

        private static Border RowBorder(FrameworkElement content)
        {
            var row = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            row.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            return row;
        }

        private static TextBlock Small(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubFgBrush");
            return t;
        }

        private static string ReasonText(FileLockFinder.LockReason r) => r switch
        {
            FileLockFinder.LockReason.FileOpen       => Loc("lock_reason_file", "has the file open"),
            FileLockFinder.LockReason.FilesInside    => Loc("lock_reason_files_inside", "has files inside the folder open"),
            FileLockFinder.LockReason.FolderOpen     => Loc("lock_reason_folder", "has the folder or a folder inside it open"),
            FileLockFinder.LockReason.WorkingFolder  => Loc("lock_reason_cwd", "its working folder is inside the folder"),
            FileLockFinder.LockReason.Executable     => Loc("lock_reason_exe", "runs from this folder"),
            FileLockFinder.LockReason.ModuleLoaded   => Loc("lock_reason_module", "has a library from here loaded"),
            FileLockFinder.LockReason.ExplorerWindow => Loc("lock_reason_explorer", "shows the folder in a File Explorer window"),
            _ => ""
        };

        // --- Switch to / close ---------------------------------------------------------------------
        private void SwitchTo(FileLockFinder.LockingProcess p)
        {
            IntPtr hwnd = GetTopLevelWindows(p.Pid).FirstOrDefault();
            if (hwnd == IntPtr.Zero)
            {
                SetNote(Loc("lock_no_window", "This program has no window to switch to or close."));
                return;
            }

            // Let the program come above this always-on-top window until the user returns here.
            Topmost = false;
            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.WindowShowStyle.Restore);
            NativeMethods.SetForegroundWindow(hwnd);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Topmost = true;
        }

        private async void CloseProgram(FileLockFinder.LockingProcess p)
        {
            if (MessageBox.Show(this,
                    string.Format(Loc("lock_close_confirm", "Close {0}? The program may ask you to save unsaved changes."), p.Name),
                    Loc("lock_title", "Who is using the file"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            List<IntPtr> windows = GetTopLevelWindows(p.Pid);
            // A polite WM_CLOSE (like the window's X button), so the program can save or ask first.
            bool posted = windows.Count > 0
                && windows.Select(w => NativeMethods.PostMessage(w, (uint)NativeMethods.WM.CLOSE, IntPtr.Zero, IntPtr.Zero)).ToList().Any(ok => ok);
            if (!posted)
            {
                MessageBox.Show(this,
                    string.Format(Loc("lock_close_failed", "Could not close {0}. It may be running as administrator."), p.Name),
                    Loc("lock_title", "Who is using the file"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Give the program a moment to close (or show its "save changes?" prompt), then re-check.
            Topmost = false;
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (IsLoaded && _path != null)
                await ScanAsync(_path, _heading);
        }

        // Visible, unowned top-level windows of the process - the ones that appear on the taskbar.
        private static List<IntPtr> GetTopLevelWindows(int pid)
        {
            var result = new List<IntPtr>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd)
                    && NativeMethods.GetWindow(hwnd, NativeMethods.GetWindow_Cmd.GW_OWNER) == IntPtr.Zero
                    && NativeMethods.GetWindowThreadProcessId(hwnd, out uint owner) != 0
                    && owner == pid)
                    result.Add(hwnd);
                return true;
            }, 0);
            return result;
        }

        // --- Placement beside the source dialog ----------------------------------------------------
        // Right of the dialog, else left, below, above - the first that fits in the work area without
        // covering it; if none does, the one covering least. In physical pixels (both windows' rects).
        private void PlaceBesideAnchor()
        {
            IntPtr self = new WindowInteropHelper(this).Handle;
            if (_anchor == IntPtr.Zero || _userMoved || self == IntPtr.Zero
                || !NativeMethods.IsWindowVisible(_anchor) || !GetWindowRect(_anchor, out RECT d) || !GetWindowRect(self, out RECT me))
                return;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(_anchor, MONITOR_DEFAULTTONEAREST), ref mi))
                return;
            RECT wa = mi.rcWork;
            int w = me.Right - me.Left, h = me.Bottom - me.Top;

            (int X, int Y)[] options =
            {
                (d.Right, d.Top), (d.Left - w, d.Top), (d.Left, d.Bottom), (d.Left, d.Top - h),
            };

            (int X, int Y) best = default;
            long bestOverlap = long.MaxValue;
            foreach ((int x0, int y0) in options)
            {
                int x = Math.Max(wa.Left, Math.Min(x0, wa.Right - w));
                int y = Math.Max(wa.Top, Math.Min(y0, wa.Bottom - h));
                long ox = Math.Max(0, Math.Min(x + w, d.Right) - Math.Max(x, d.Left));
                long oy = Math.Max(0, Math.Min(y + h, d.Bottom) - Math.Max(y, d.Top));
                long overlap = ox * oy;
                if (overlap < bestOverlap)
                {
                    bestOverlap = overlap;
                    best = (x, y);
                    if (overlap == 0)
                        break;
                }
            }

            SetWindowPos(self, HWND_TOPMOST, best.X, best.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceBesideAnchor();

        // --- Bottom buttons ------------------------------------------------------------------------
        private async void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Title = Loc("lock_choose_file", "Choose file…") };
            if (_path != null)
                dialog.InitialDirectory = Directory.Exists(_path) ? _path : Path.GetDirectoryName(_path);
            if (dialog.ShowDialog(this) == true)
                await ScanAsync(dialog.FileName, Loc("lock_selected", "Selected:"));
        }

        private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc("lock_choose_folder", "Choose folder…") };
            if (_path != null)
                dialog.InitialDirectory = Directory.Exists(_path) ? _path : Path.GetDirectoryName(_path);
            if (dialog.ShowDialog(this) == true)
                await ScanAsync(dialog.FolderName, Loc("lock_selected", "Selected:"));
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_path != null)
                await ScanAsync(_path, _heading);
            else
                await FindFromDialogAsync();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _scanCts?.Cancel();
            StopButton.IsEnabled = false;
            ProgressText.Text = Loc("lock_stopping", "Stopping…");
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            CancelScan();
            base.OnClosed(e);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                _userMoved = true;
                DragMove();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        // --- State helpers -------------------------------------------------------------------------
        private void ShowBusy(string status, bool canStop)
        {
            ClearResults();
            SetStatus(status);
            SetNote(null);
            Progress.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = canStop ? Visibility.Collapsed : Visibility.Visible;
            RefreshButton.IsEnabled = false;
            StopButton.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
            StopButton.IsEnabled = true;
        }

        private void EndBusy()
        {
            Progress.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Visible;
            RefreshButton.IsEnabled = true;
        }

        private void ClearResults()
        {
            ResultsPanel.Children.Clear();
            _rows.Clear();
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void SetNote(string? text)
        {
            NoteText.Text = text ?? "";
            NoteText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        // --- Interop -------------------------------------------------------------------------------
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    }
}
