#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ZombieBar.Utilities;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ZombieBar
{
    /// <summary>
    /// "About" window opened from the tray menu. Shows the product name and version, a link to the
    /// developer's "Buy me a coffee" page, and a "Check for updates" button. The update button is
    /// hidden when the auto-updater is compiled out (Microsoft Store build), i.e. when no Updater
    /// is passed.
    /// </summary>
    public partial class AboutWindow : Window
    {
        private readonly Updater? _updater;

        public AboutWindow(Updater? updater)
        {
            _updater = updater;
            InitializeComponent();
            AppUi.ApplyDialogTheme(this);

            AppIcon.Source = AppUi.LoadAppIcon();

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"{Loc("about_version", "Version")} {version?.ToString() ?? ""}".Trim();

            if (_updater == null)
            {
                CheckUpdatesButton.Visibility = Visibility.Collapsed;
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        private void Coffee_OnClick(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(AppLinks.BuyMeACoffeeUrl) { UseShellExecute = true }); }
            catch { /* no browser available */ }
        }

        private async void CheckUpdates_OnClick(object sender, RoutedEventArgs e)
        {
            if (_updater == null)
            {
                return;
            }

            CheckUpdatesButton.IsEnabled = false;
            ShowStatus(Loc("about_checking", "Checking for updates…"));

            Updater.UpdateCheckResult result = await _updater.CheckForUpdatesNowAsync();

            switch (result)
            {
                case Updater.UpdateCheckResult.UpToDate:
                    ShowStatus(Loc("about_up_to_date", "You have the latest version."));
                    break;

                case Updater.UpdateCheckResult.Failed:
                    ShowStatus(Loc("about_check_failed", "Couldn't check for updates. Please try again later."));
                    break;

                case Updater.UpdateCheckResult.UpdateAvailable:
                    ShowStatus(string.Format(Loc("about_update_available", "Version {0} is available."), _updater.AvailableVersion));

                    string prompt = string.Format(Loc("about_update_prompt", "Version {0} is available. Install it now?"), _updater.AvailableVersion);
                    if (MessageBox.Show(this, prompt, Loc("about_title", "About"),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        if (await _updater.InstallUpdateAsync() == Updater.InstallResult.Installing)
                        {
                            ((App)Application.Current).ExitGracefully();
                            return;
                        }

                        // Cancelled or failed: keep the "version available" status shown above.
                    }
                    break;
            }

            CheckUpdatesButton.IsEnabled = true;
        }

        private void ShowStatus(string text)
        {
            UpdateStatus.Text = text;
            UpdateStatus.Visibility = Visibility.Visible;
        }

        private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
    }
}
