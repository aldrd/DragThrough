#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// System tray icon for the app. It owns the NotifyIcon and tooltip; clicking the icon (either
    /// button) opens <see cref="TrayFlyoutWindow"/> - a modern WPF flyout that hosts the options,
    /// the "Show taskbar" toggle and Exit (the only place that quits the whole application).
    /// </summary>
    public class AppTray : IDisposable
    {
        private readonly NotifyIcon _tray;
        private readonly Action<bool> _setTaskbarVisible;
        private readonly Action _openFeedback;
        private readonly Action _openAbout;
        private readonly Action _exit;

        private TrayFlyoutWindow? _flyout;
        private bool _updateAvailable;

        // When the icon is clicked while the flyout is open, that same click first deactivates (and
        // hides) the flyout. Within this window we treat the click as "dismiss", not "reopen".
        private long _flyoutHiddenAt;
        private const long ReopenGuardMs = 250;

        /// <param name="setTaskbarVisible">Shows (true) or hides (false) the additional taskbar.</param>
        /// <param name="openFeedback">Opens the feedback form ("Report a problem or suggestion").</param>
        /// <param name="openAbout">Opens the "About" window.</param>
        /// <param name="exit">Quits the whole application.</param>
        public AppTray(Action<bool> setTaskbarVisible, Action openFeedback, Action openAbout, Action exit)
        {
            _setTaskbarVisible = setTaskbarVisible;
            _openFeedback = openFeedback;
            _openAbout = openAbout;
            _exit = exit;

            _tray = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = BuildTooltip()
            };

            _tray.MouseUp += TrayMouseUp;
        }

        /// <summary>Re-syncs the flyout's toggles after an external settings change (e.g. taskbar).</summary>
        public void UpdateShowTaskbarCheck()
        {
            if (_flyout != null && _flyout.IsVisible)
            {
                _flyout.SyncFromSettings();
            }
        }

        /// <summary>Marks that an update is available; the flyout's "About" item then says so.</summary>
        public void SetUpdateAvailable()
        {
            _updateAvailable = true;
            _flyout?.SetUpdateAvailable(true);
        }

        private void TrayMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
            {
                return;
            }

            // The click that dismissed an open flyout shouldn't immediately reopen it.
            if (Environment.TickCount64 - _flyoutHiddenAt < ReopenGuardMs)
            {
                return;
            }

            ShowFlyout();
        }

        private void ShowFlyout()
        {
            if (_flyout == null)
            {
                _flyout = new TrayFlyoutWindow(_setTaskbarVisible, _openFeedback, _openAbout, _exit, ShowBalloon);
                _flyout.Deactivated += (_, _) =>
                {
                    _flyoutHiddenAt = Environment.TickCount64;
                    _flyout?.Hide();
                };
            }

            _flyout.SetUpdateAvailable(_updateAvailable);
            _flyout.ShowAtCursor();
        }

        private void ShowBalloon(string title, string text)
        {
            try { _tray.ShowBalloonTip(2000, title, text, ToolTipIcon.Info); } catch { }
        }

        // Tray tooltip: the (localized) product name followed by the product version.
        private static string BuildTooltip()
        {
            string name = Loc("tray_tooltip", "DragThrough");
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{name} v{version}" : name;
        }

        private static string Loc(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private static Icon LoadTrayIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string? name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("tray_icon.ico", StringComparison.OrdinalIgnoreCase));

                if (name != null)
                {
                    using Stream? stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        return new Icon(stream, SystemInformation.SmallIconSize);
                    }
                }
            }
            catch { }

            return SystemIcons.Application;
        }

        public void Dispose()
        {
            _tray.Visible = false;
            _tray.Dispose();

            if (_flyout != null)
            {
                _flyout.Close();
                _flyout = null;
            }
        }
    }
}
