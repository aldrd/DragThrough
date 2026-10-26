#nullable enable
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ZombieBar.Utilities;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ZombieBar
{
    /// <summary>
    /// "Find the process locking a file": takes the file or folder from an on-screen "File In Use" /
    /// "Folder In Use" dialog (or lets the user choose one), lists the programs using it and offers to
    /// switch to or close each of them. Single instance; opening it again re-runs the search.
    /// </summary>
    public partial class LockFinderWindow : Window
    {
        private static LockFinderWindow? _instance;

        private string? _path;
        private string _heading = "";
        private CancellationTokenSource? _scanCts;

        private LockFinderWindow()
        {
            InitializeComponent();
            AppUi.ApplyDialogTheme(this);
            AppIcon.Source = AppUi.LoadAppIcon();
        }

        /// <summary>Shows the window (or brings the open one forward) and looks for an "In Use" dialog.</summary>
        public static void Open()
        {
            if (_instance == null)
            {
                _instance = new LockFinderWindow();
                _instance.Closed += (_, _) => _instance = null;
                _instance.Show();
            }

            _instance.Activate();
            _ = _instance.FindFromDialogAsync();
        }

        private async Task FindFromDialogAsync()
        {
            ShowBusy();
            FileInUseDialogFinder.Result found = await Task.Run(FileInUseDialogFinder.Find);
            if (!IsLoaded)
                return;

            if (found.Path != null)
            {
                await ScanAsync(found.Path, Loc("lock_from_dialog", "From the “In Use” window:"));
                return;
            }

            _path = null;
            ResultsPanel.Children.Clear();
            PathText.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            SetStatus(found.DialogFound
                ? string.Format(Loc("lock_unresolved", "The “In Use” window was found, but the location of “{0}” could not be determined. Choose the file or folder to check."),
                                found.UnresolvedName ?? "?")
                : Loc("lock_no_dialog", "No “File In Use” or “Folder In Use” window was found. Choose a file or folder to check."));
            SetNote(null);
        }

        private async Task ScanAsync(string path, string heading)
        {
            _scanCts?.Cancel();
            var cts = new CancellationTokenSource();
            _scanCts = cts;

            _path = path;
            _heading = heading;
            ShowBusy();
            PathText.Text = path;
            PathText.Visibility = Visibility.Visible;

            FileLockFinder.ScanResult? result = null;
            string? error = null;
            try { result = await Task.Run(() => FileLockFinder.Scan(path, cts.Token), cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (FileNotFoundException) { error = Loc("lock_not_found", "The file or folder no longer exists."); }
            catch (Exception ex) { error = string.Format(Loc("lock_error", "Could not check: {0}"), ex.Message); }

            if (cts != _scanCts || !IsLoaded)
                return;

            RefreshButton.IsEnabled = true;
            ResultsPanel.Children.Clear();

            if (result == null)
            {
                SetStatus(heading);
                SetNote(error);
                return;
            }

            if (result.Processes.Count == 0)
            {
                SetStatus(heading);
                var none = new TextBlock
                {
                    Text = Loc("lock_none", "No program is using it right now. Programs running as administrator, and open handles to a folder itself, can't be detected."),
                    TextWrapping = TextWrapping.Wrap
                };
                none.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                ResultsPanel.Children.Add(none);
            }
            else
            {
                SetStatus(heading);
                SetNote(result.TruncatedAt is int n
                    ? string.Format(Loc("lock_truncated", "The folder is large: only the first {0} files inside were checked."), n)
                    : null);
                foreach (FileLockFinder.LockingProcess p in result.Processes)
                    ResultsPanel.Children.Add(BuildRow(p, result.IsFolder));
            }
        }

        // --- Result rows ---------------------------------------------------------------------------
        private FrameworkElement BuildRow(FileLockFinder.LockingProcess p, bool isFolder)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
            title.Inlines.Add(new System.Windows.Documents.Run(p.Name) { FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new System.Windows.Documents.Run("   " + string.Format(Loc("lock_pid", "PID {0}"), p.Pid)));
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            info.Children.Add(title);

            var details = new List<string>();
            if (p.ExePath != null) details.Add(p.ExePath);
            if (p.ServiceName != null) details.Add(string.Format(Loc("lock_service", "Service: {0}"), p.ServiceName));
            details.Add(string.Join(", ", p.Reasons.Select(ReasonText)));
            info.Children.Add(Small(string.Join("\n", details)));

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
            switchButton.Click += (_, _) => SwitchTo(p);
            var closeButton = new Button { Content = Loc("lock_close", "Close program"), IsEnabled = windows.Count > 0 && !isExplorer };
            closeButton.SetResourceReference(StyleProperty, "DialogButton");
            closeButton.Click += (_, _) => CloseProgram(p);
            buttons.Children.Add(switchButton);
            buttons.Children.Add(closeButton);

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(info);
            grid.Children.Add(buttons);

            var row = new Border
            {
                Child = grid,
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
            FileLockFinder.LockReason.FileOpen      => Loc("lock_reason_file", "has the file open"),
            FileLockFinder.LockReason.FilesInside   => Loc("lock_reason_files_inside", "has files inside the folder open"),
            FileLockFinder.LockReason.WorkingFolder => Loc("lock_reason_cwd", "its working folder is inside the folder"),
            FileLockFinder.LockReason.Executable    => Loc("lock_reason_exe", "runs from this folder"),
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

            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.WindowShowStyle.Restore);
            NativeMethods.SetForegroundWindow(hwnd);
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

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _scanCts?.Cancel();
            base.OnClosed(e);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
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
        private void ShowBusy()
        {
            RefreshButton.IsEnabled = false;
            ResultsPanel.Children.Clear();
            SetStatus(Loc("lock_searching", "Searching…"));
            SetNote(null);
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void SetNote(string? text)
        {
            NoteText.Text = text ?? "";
            NoteText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
