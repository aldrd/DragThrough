#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Resources;
using ZombieBar.Utilities;
using Application = System.Windows.Application;

namespace ZombieBar
{
    /// <summary>
    /// The app's tray menu, drawn as a modern WPF flyout (rounded, shadowed, theme-aware) instead
    /// of the system context menu. Created once by <see cref="AppTray"/> and reused: it is shown
    /// above the tray icon on click and hidden when it loses focus. The options map directly to
    /// <see cref="Settings"/>; the actions are delegated back to the app.
    /// </summary>
    public partial class TrayFlyoutWindow : Window
    {
        private readonly Action<bool> _setTaskbarVisible;
        private readonly Action<bool> _setTaskbarVisibleThisDesktop;
        private readonly Func<bool> _isTaskbarVisibleThisDesktop;
        private readonly Action _openFeedback;
        // Runs the "check for updates / install" flow. Null when the auto-updater is compiled out.
        private readonly Func<Task>? _checkOrInstallUpdate;
        private readonly Action _exit;
        private readonly Action<string, string> _showBalloon;

        private bool _syncing;

        // Embedded help videos extracted to temp files (MediaElement can't play a pack:// resource),
        // keyed by file name so each is unpacked only once per run.
        private static readonly Dictionary<string, string?> _extractedVideos = new();
        private string? _currentVideo;
        // The video file currently assigned to HelpVideo.Source. Kept loaded (the player is never torn
        // down) so hovering the same item again just calls Play() and appears instantly, instead of
        // re-opening and buffering the clip on every hover.
        private string? _loadedVideo;
        // True while the window is shown off-screen purely to let the MediaElement open the clip; the
        // window is hidden again as soon as the media opens.
        private bool _warming;
        private bool _warmed;

        public TrayFlyoutWindow(Action<bool> setTaskbarVisible, Action<bool> setTaskbarVisibleThisDesktop,
                                Func<bool> isTaskbarVisibleThisDesktop, Action openFeedback,
                                Func<Task>? checkOrInstallUpdate, Action exit, Action<string, string> showBalloon)
        {
            _setTaskbarVisible = setTaskbarVisible;
            _setTaskbarVisibleThisDesktop = setTaskbarVisibleThisDesktop;
            _isTaskbarVisibleThisDesktop = isTaskbarVisibleThisDesktop;
            _openFeedback = openFeedback;
            _checkOrInstallUpdate = checkOrInstallUpdate;
            _exit = exit;
            _showBalloon = showBalloon;

            InitializeComponent();
            ApplyTheme();

            ImageSource appIcon = AppUi.LoadAppIcon();
            AppIcon.Source = appIcon;
            HelpBrandIcon.Source = appIcon;

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version}" : "";

            // The update item only makes sense with the auto-updater compiled in; hide it otherwise
            // (e.g. the Microsoft Store build, where the store handles updates). Its label is set to the
            // "unknown" state up front and refreshed by SetUpdateAvailable when a check finds an update.
            if (_checkOrInstallUpdate == null)
                UpdateItem.Visibility = Visibility.Collapsed;
            else
                SetUpdateAvailable(false);

            // Any menu item whose Tag names a video shows it in the help pane on hover; every other
            // item resets the pane to the brand card. Wired here so future items need no extra code.
            foreach (ButtonBase item in FindLogicalDescendants<ButtonBase>(MenuPanel))
                item.MouseEnter += MenuItem_MouseEnter;

            // Open (extract + buffer) the help video up front so the first hover is instant. The flyout
            // is created once and reused, so this cost is paid a single time, well before any hover.
            PreloadHelpVideo();

            Deactivated += (_, _) =>
            {
                // Closing the Share popup whenever the flyout loses focus keeps the two in sync,
                // and the help video is stopped so it isn't left playing on a hidden window.
                SharePopup.IsOpen = false;
                ShowHelpPlaceholder();
            };
        }

        /// <summary>Positions the flyout just above the cursor (the tray icon) and shows it.</summary>
        public void ShowAtCursor()
        {
            // If the startup warm-up (the 1x1 pixel window) is still running, cancel it so the flyout
            // shows at its full content size rather than the warm-up's single pixel.
            if (_warming)
            {
                _warming = false;
                SizeToContent = SizeToContent.WidthAndHeight;
                ShowActivated = true;
            }

            ApplyTheme();
            SyncFromSettings();
            SharePopup.IsOpen = false;
            ShowHelpPlaceholder();

            Opacity = 0;
            if (!IsVisible)
            {
                Show();
            }
            UpdateLayout();
            PositionAboveCursor();
            Opacity = 1;

            Activate();
            ManagedShell.Interop.NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }

        /// <summary>
        /// Sets the update item's label: "Update available, install" once a check has found an update,
        /// otherwise "Check for updates". Re-read from resources each call so it follows the language.
        /// </summary>
        public void SetUpdateAvailable(bool available)
        {
            UpdateItemText.Text = available
                ? Loc("tray_update_available", "Update available, install")
                : Loc("about_check_updates", "Check for updates");

            // When an update is waiting, make the row stand out: a filled accent background (driven by the
            // Tag="highlight" template trigger) with white accent-foreground text + icon, semibold.
            // Otherwise it's a plain menu row. SetResourceReference keeps the brushes theme-aware; called
            // on every open, so it always matches the current theme.
            if (available)
            {
                UpdateItem.Tag = "highlight";
                UpdateItemText.SetResourceReference(TextBlock.ForegroundProperty, "AccentFgBrush");
                UpdateItemIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentFgBrush");
                UpdateItemText.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                UpdateItem.Tag = null;
                UpdateItemText.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
                UpdateItemIcon.SetResourceReference(TextBlock.ForegroundProperty, "SubFgBrush");
                UpdateItemText.FontWeight = FontWeights.Normal;
            }
        }

        // === Help-video pane =============================================================
        private void MenuItem_MouseEnter(object sender, MouseEventArgs e)
        {
            // Only a Tag naming a video file drives the help pane; other Tags (e.g. "highlight" on the
            // update row) are not videos and just reset the pane to the brand card.
            string? video = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(video) || !video.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                ShowHelpPlaceholder();
            else
                ShowHelpVideo(video);
        }

        private void ShowHelpVideo(string fileName)
        {
            if (fileName == _currentVideo)
                return;

            string? path = ResolveVideoPath(fileName);
            if (path == null)
            {
                ShowHelpPlaceholder();
                return;
            }

            _currentVideo = fileName;
            // Only (re)assign the source when it's a different clip; for the already-loaded one the player
            // is still open, so Play() resumes from the first frame with no open/buffer delay.
            if (fileName != _loadedVideo)
            {
                _loadedVideo = fileName;
                HelpVideo.Source = new Uri(path);
            }
            HelpVideo.Position = TimeSpan.Zero;
            HelpVideo.Play();
            HelpPlaceholder.Visibility = Visibility.Collapsed;
            HelpVideo.Visibility = Visibility.Visible;
        }

        private void ShowHelpPlaceholder()
        {
            if (_currentVideo == null)
                return;

            _currentVideo = null;
            // Pause rather than Stop/clear the source: keeping the player open means the next hover starts
            // instantly instead of paying the open+buffer cost again.
            HelpVideo.Pause();
            HelpVideo.Visibility = Visibility.Collapsed;
            HelpPlaceholder.Visibility = Visibility.Visible;
        }

        // Warm the player at startup: extract the first referenced help video and open it so its first
        // hover doesn't have to unpack + buffer 2 MB of clip on the UI thread.
        private void PreloadHelpVideo()
        {
            string? first = FindLogicalDescendants<ButtonBase>(MenuPanel)
                .Select(b => b.Tag as string)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (first == null)
                return;

            string? path = ResolveVideoPath(first);
            if (path == null)
                return;

            _loadedVideo = first;
            HelpVideo.Source = new Uri(path);
            // Kick off the open; MediaOpened parks it paused at the first frame while it stays hidden.
            HelpVideo.Play();
        }

        /// <summary>
        /// Opens the help video ahead of time by briefly showing the flyout as a single imperceptible
        /// pixel. A MediaElement only starts loading its source once it's shown in a rendered window, and
        /// the first open in a process also pays a one-time media-pipeline init cost (~2-4s). Doing this
        /// at startup moves both off the first hover, so the video appears instantly when the user hovers.
        /// </summary>
        public void WarmUp()
        {
            if (_warmed || _loadedVideo == null)
                return;
            _warmed = true;
            _warming = true;

            // The MediaElement only opens its source once the window is genuinely rendered on-screen:
            // a fully-transparent (Opacity 0) or entirely off-screen window is culled and never realizes
            // the media. So show a 1x1, opaque, non-activating window tucked into the top-left corner -
            // a single pixel is imperceptible but enough to make WPF render (and thus open) the clip.
            SizeToContent = SizeToContent.Manual;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 1;
            Height = 1;
            Left = 0;
            Top = 0;
            Opacity = 1;
            ShowActivated = false;
            Show();
        }

        private void EndWarmUp()
        {
            if (!_warming)
                return;
            _warming = false;
            Hide();
            // Restore normal show behaviour for the real, on-screen presentations.
            SizeToContent = SizeToContent.WidthAndHeight;
            Opacity = 1;
            ShowActivated = true;
        }

        private void HelpVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Don't leave the warm-up window stuck open if the clip can't be decoded.
            EndWarmUp();
        }

        private void HelpVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            // If the clip finished opening while nothing is being shown (the preload/warm-up, or a hover
            // that ended before it opened), hold it paused at the first frame so it's ready to resume.
            if (_currentVideo == null)
            {
                HelpVideo.Pause();
                HelpVideo.Position = TimeSpan.Zero;
            }
            EndWarmUp();
        }

        private void HelpVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Loop the clip for as long as the item stays hovered.
            HelpVideo.Position = TimeSpan.Zero;
            HelpVideo.Play();
        }

        // Extracts an embedded help video (Assets\<fileName>) to a temp file once and returns its path
        // (null if it doesn't exist), because MediaElement can't play from a pack:// application resource.
        private static string? ResolveVideoPath(string fileName)
        {
            if (_extractedVideos.TryGetValue(fileName, out string? cached))
                return cached != null && File.Exists(cached) ? cached : null;

            string? result = null;
            try
            {
                StreamResourceInfo? sri = Application.GetResourceStream(
                    new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute));
                if (sri != null)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "DragThrough", "media");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, fileName);

                    try
                    {
                        using Stream src = sri.Stream;
                        using FileStream dst = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                        src.CopyTo(dst);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        // A previous run already extracted it and the file is in use; reuse it.
                    }

                    result = path;
                }
            }
            catch
            {
                result = null;
            }

            _extractedVideos[fileName] = result;
            return result;
        }

        private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject dep)
                    continue;

                if (dep is T match)
                    yield return match;

                foreach (T descendant in FindLogicalDescendants<T>(dep))
                    yield return descendant;
            }
        }

        /// <summary>Reflects the current settings in the toggles (also call after an external change).</summary>
        public void SyncFromSettings()
        {
            _syncing = true;
            ShiftToggle.IsChecked = Settings.Instance.EnableShiftModifier;
            WinKeyToggle.IsChecked = Settings.Instance.EnableWindowsKeyModifier;
            MinimizeToggle.IsChecked = Settings.Instance.MinimizeExplorerAfterSuccessfulDrag;
            ShowTaskbarThisDesktopToggle.IsChecked = _isTaskbarVisibleThisDesktop();
            CenterTasksToggle.IsChecked = Settings.Instance.CenterTasksInTaskbar;
            CompactTasksToggle.IsChecked = Settings.Instance.CompactSingleInstanceTasks;
            _syncing = false;
        }

        private void PositionAboveCursor()
        {
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;             // physical px
            System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);

            double widthPx = ActualWidth * dpi.DpiScaleX;
            double heightPx = ActualHeight * dpi.DpiScaleY;

            double xPx = cursor.X - widthPx;
            double yPx = cursor.Y - heightPx - 6;

            xPx = Math.Max(screen.Left, Math.Min(xPx, screen.Right - widthPx));
            yPx = Math.Max(screen.Top, Math.Min(yPx, screen.Bottom - heightPx));

            Left = xPx / dpi.DpiScaleX;
            Top = yPx / dpi.DpiScaleY;
        }

        // === Toggles ======================================================================
        private void WinKeyToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            Settings.Instance.EnableWindowsKeyModifier = WinKeyToggle.IsChecked == true;
        }

        private void ShiftToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            Settings.Instance.EnableShiftModifier = ShiftToggle.IsChecked == true;
        }

        private void MinimizeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            Settings.Instance.MinimizeExplorerAfterSuccessfulDrag = MinimizeToggle.IsChecked == true;
        }


        private void ShowTaskbarThisDesktopToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _setTaskbarVisibleThisDesktop(ShowTaskbarThisDesktopToggle.IsChecked == true);
        }

        private void CenterTasksToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            Settings.Instance.CenterTasksInTaskbar = CenterTasksToggle.IsChecked == true;
        }

        private void CompactTasksToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            Settings.Instance.CompactSingleInstanceTasks = CompactTasksToggle.IsChecked == true;
        }

        // === Actions ======================================================================
        private void Share_Click(object sender, RoutedEventArgs e)
        {
            if (SharePopup.IsOpen)
            {
                SharePopup.IsOpen = false;
                return;
            }

            BuildSharePanel();
            SharePopup.IsOpen = true;
        }

        private void Coffee_Click(object sender, RoutedEventArgs e)
        {
            AppShare.OpenUrl(AppLinks.BuyMeACoffeeUrl);
            Hide();
        }

        private void Feedback_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            _openFeedback();
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            if (_checkOrInstallUpdate != null)
                await _checkOrInstallUpdate();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            _exit();
        }

        private void BuildSharePanel()
        {
            SharePanel.Children.Clear();
            foreach (AppShare.Target target in AppShare.Targets)
            {
                var button = new Button
                {
                    Style = (Style)Resources["MenuButton"],
                    Tag = target.Kind,
                    Content = new TextBlock { Text = Loc(target.Key, target.Fallback), TextWrapping = TextWrapping.Wrap }
                };
                button.Click += ShareTarget_Click;
                SharePanel.Children.Add(button);

                if (target.Kind == "copy")
                {
                    SharePanel.Children.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(10, 4, 10, 4),
                        Background = (Brush)Resources["SepBrush"]
                    });
                }
            }
        }

        private void ShareTarget_Click(object sender, RoutedEventArgs e)
        {
            string kind = (string)((Button)sender).Tag;
            SharePopup.IsOpen = false;

            bool copied = AppShare.Share(kind);
            if (copied)
            {
                _showBalloon(Loc("share_app", "Share the app"), Loc("share_link_copied", "Link copied to clipboard"));
            }

            Hide();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (SharePopup.IsOpen)
                {
                    SharePopup.IsOpen = false;
                }
                else
                {
                    Hide();
                }
                e.Handled = true;
            }
        }

        // === Theme ========================================================================
        // Light/dark palette picked from the OS "apps" theme, set as dynamic resources so the XAML
        // (which references them via DynamicResource) recolors on each show.
        private void ApplyTheme()
        {
            bool dark = AppUi.IsSystemDark();

            SetBrush("BgBrush",          dark ? "#FF202124" : "#FFFFFFFF");
            SetBrush("FgBrush",          dark ? "#FFF2F2F2" : "#FF1A1A1A");
            SetBrush("SubFgBrush",       dark ? "#FFA8A8A8" : "#FF6B6B6B");
            SetBrush("BorderBrush",      dark ? "#FF3A3A3D" : "#FFE5E5E5");
            SetBrush("SepBrush",         dark ? "#FF3A3A3D" : "#FFE6E6E6");
            SetBrush("HoverBrush",       dark ? "#1AFFFFFF" : "#14000000");
            SetBrush("AccentBrush",      dark ? "#FF4CC2FF" : "#FF0067C0");
            SetBrush("AccentFgBrush",    dark ? "#FF101114" : "#FFFFFFFF");
            SetBrush("TrackOffBrush",    dark ? "#FF3A3A3D" : "#FFE9E9EA");
            SetBrush("SwitchBorderBrush",dark ? "#FF9A9A9A" : "#FF8A8A8A");
            SetBrush("ThumbBrush",       dark ? "#FFC8C8C8" : "#FF5B5B5B");
            SetBrush("DangerBrush",      dark ? "#FFE57373" : "#FFD13438");
            SetBrush("HelpBgBrush",      dark ? "#FF17181A" : "#FFF4F5F7");
        }

        private void SetBrush(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Resources[key] = brush;
        }

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
