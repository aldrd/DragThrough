#nullable enable
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        private readonly Action _openAbout;
        private readonly Action _exit;
        private readonly Action<string, string> _showBalloon;

        private bool _syncing;

        public TrayFlyoutWindow(Action<bool> setTaskbarVisible, Action<bool> setTaskbarVisibleThisDesktop,
                                Func<bool> isTaskbarVisibleThisDesktop, Action openFeedback, Action openAbout,
                                Action exit, Action<string, string> showBalloon)
        {
            _setTaskbarVisible = setTaskbarVisible;
            _setTaskbarVisibleThisDesktop = setTaskbarVisibleThisDesktop;
            _isTaskbarVisibleThisDesktop = isTaskbarVisibleThisDesktop;
            _openFeedback = openFeedback;
            _openAbout = openAbout;
            _exit = exit;
            _showBalloon = showBalloon;

            InitializeComponent();
            ApplyTheme();

            AppIcon.Source = AppUi.LoadAppIcon();

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version}" : "";

            // Closing the Share popup whenever the flyout loses focus keeps the two in sync.
            Deactivated += (_, _) => SharePopup.IsOpen = false;
        }

        /// <summary>Positions the flyout just above the cursor (the tray icon) and shows it.</summary>
        public void ShowAtCursor()
        {
            ApplyTheme();
            SyncFromSettings();
            SharePopup.IsOpen = false;

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

        /// <summary>Shows or hides the "Update available" badge next to the About item.</summary>
        public void SetUpdateAvailable(bool available)
        {
            AboutUpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Reflects the current settings in the toggles (also call after an external change).</summary>
        public void SyncFromSettings()
        {
            _syncing = true;
            WinKeyToggle.IsChecked = Settings.Instance.EnableWindowsKeyModifier;
            ShiftToggle.IsChecked = Settings.Instance.EnableShiftModifier;
            MinimizeToggle.IsChecked = Settings.Instance.MinimizeExplorerAfterSuccessfulDrag;
            DismissSearchToggle.IsChecked = Settings.Instance.DismissWindowsSearchWithEscape;
            ShowTaskbarToggle.IsChecked = Settings.Instance.ShowAdditionalTaskbar;
            ShowTaskbarThisDesktopToggle.IsChecked = _isTaskbarVisibleThisDesktop();
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

        private void DismissSearchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            Settings.Instance.DismissWindowsSearchWithEscape = DismissSearchToggle.IsChecked == true;
        }

        private void ShowTaskbarToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _setTaskbarVisible(ShowTaskbarToggle.IsChecked == true);
        }

        private void ShowTaskbarThisDesktopToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _setTaskbarVisibleThisDesktop(ShowTaskbarThisDesktopToggle.IsChecked == true);
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

        private void About_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            _openAbout();
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
