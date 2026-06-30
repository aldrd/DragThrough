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

        /// <summary>Cancelled when the user presses Cancel; passed to the download.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        public UpdateProgressWindow()
        {
            InitializeComponent();
            ApplyTheme();

            AppIcon.Source = AppUi.LoadAppIcon();

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"v{version}" : "";
        }

        private void Cancel_OnClick(object sender, RoutedEventArgs e)
        {
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

        private static double Mb(long bytes) => bytes / 1048576.0;

        private void ApplyTheme()
        {
            bool dark = AppUi.IsSystemDark();

            SetBrush("BgBrush",     dark ? "#FF202124" : "#FFFFFFFF");
            SetBrush("FgBrush",     dark ? "#FFF2F2F2" : "#FF1A1A1A");
            SetBrush("SubFgBrush",  dark ? "#FFA8A8A8" : "#FF6B6B6B");
            SetBrush("BorderBrush", dark ? "#FF3A3A3D" : "#FFE5E5E5");
            SetBrush("AccentBrush", dark ? "#FF4CC2FF" : "#FF0067C0");
            SetBrush("TrackBrush",  dark ? "#FF3A3A3D" : "#FFE9E9EA");
            SetBrush("HoverBrush",  dark ? "#1AFFFFFF" : "#14000000");
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
