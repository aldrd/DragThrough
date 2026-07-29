#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ZombieBar.Utilities;
using Application = System.Windows.Application;

namespace ZombieBar
{
    /// <summary>
    /// Progress window shown while an update downloads and installs. <see cref="Updater"/> shows it
    /// for every update install (whether started from the About window or the periodic update
    /// notification), updates it as bytes arrive, and closes it when done.
    /// </summary>
    public partial class UpdateProgressWindow : Window
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _finished;

        /// <summary>Cancelled when the user presses Cancel; passed to the download.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        public UpdateProgressWindow()
        {
            InitializeComponent();
            AppUi.ApplyDialogTheme(this);

            AppIcon.Source = AppUi.LoadAppIcon();

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version}" : "";
        }

        private void Cancel_OnClick(object sender, RoutedEventArgs e)
        {
            // After an error the same button becomes "Close".
            if (_finished)
            {
                Close();
                return;
            }

            _cts.Cancel();
            CancelButton.IsEnabled = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _cts.Dispose();
        }

        /// <summary>Updates the bar and the "12.3 / 70.5 MB · 17%" detail line.</summary>
        public void SetProgress(long received, long total)
        {
            StatusText.Text = Loc("update_downloading", "Downloading update…");

            if (total > 0)
            {
                double percent = received * 100.0 / total;
                Bar.Value = percent;
                DetailText.Text = $"{Mb(received):0.0} / {Mb(total):0.0} MB · {percent:0}%";
            }
            else
            {
                // Server didn't report a size; show what we've downloaded so far.
                DetailText.Text = $"{Mb(received):0.0} MB";
            }
        }

        /// <summary>Switches to the brief "installing" phase after the download completes.</summary>
        public void SetInstalling()
        {
            StatusText.Text = Loc("update_installing", "Installing…");
            Bar.Value = 100;
            DetailText.Text = "";
        }

        /// <summary>
        /// Shows an error and keeps the window open (so the user sees why the update didn't install),
        /// turning the Cancel button into a Close button. <paramref name="detail"/> is the technical
        /// reason (HTTP status, exception message, ...), shown smaller for diagnostics.
        /// </summary>
        public void SetError(string detail)
        {
            _finished = true;
            StatusText.Text = Loc("update_failed", "Couldn't install the update. Please try again later.");
            if (TryFindResource("DangerBrush") is Brush danger)
            {
                StatusText.Foreground = danger;
            }
            Bar.Visibility = Visibility.Collapsed;
            DetailText.Text = detail;
            CancelButton.Content = Loc("about_close", "Close");
            CancelButton.IsEnabled = true;
        }

        private static double Mb(long bytes) => bytes / 1048576.0;

        private static string Loc(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
