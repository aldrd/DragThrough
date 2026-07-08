using System;
using ManagedShell;
using ZombieBar.Utilities;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ZombieBar
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public DictionaryManager DictionaryManager { get; }

        public TasksOrderManager TasksOrderManager { get; }

        // Virtual-desktop awareness: the TaskList reads this to filter windows to the current desktop.
        public VirtualDesktopService VirtualDesktopService { get; private set; }

        // Per-desktop show/hide state for the additional taskbar (persisted, keyed by desktop GUID).
        private DesktopTaskbarVisibility _desktopVisibility;

        private ManagedShellLogger _logger;
        private Taskbar _taskbar;
        private readonly StartMenuMonitor _startMenuMonitor;
        private readonly ShellManager _shellManager;

        // Always-present tray icon and the "drag through" monitor (integrated from DragThrough).
        // The app keeps running with these even when the additional taskbar is hidden.
        private AppTray _appTray;
        private readonly DragMonitor _dragMonitor = new DragMonitor();

        // Null when the auto-updater is compiled out (Microsoft Store build, see ZombieBar.csproj).
        private readonly Updater _updater;

        public App()
        {
#if AUTOUPDATE
            // If we were launched elevated only to replace the executable (read-only install
            // folder), do that and exit without starting the shell.
            if (Updater.TryApplyElevatedUpdate(Environment.GetCommandLineArgs()))
            {
                Environment.Exit(0);
            }
#endif
            _shellManager = SetupManagedShell();

            _startMenuMonitor = new StartMenuMonitor(new AppVisibilityHelper(false));
            DictionaryManager = new DictionaryManager();
            TasksOrderManager = new TasksOrderManager(_shellManager.Tasks.Windows);

            _desktopVisibility = new DesktopTaskbarVisibility();
            VirtualDesktopService = new VirtualDesktopService();
            VirtualDesktopService.DesktopChanged += VirtualDesktopService_DesktopChanged;
#if AUTOUPDATE
            _updater = new Updater();
#endif
        }

        public void ExitGracefully()
        {
            _shellManager.AppBarManager.SignalGracefulShutdown();
            Current.Shutdown();
        }

        // Single-instance feedback form, opened from the tray menu. Reused (re-activated) if already
        // open so a second click doesn't stack windows.
        private FeedbackWindow _feedbackWindow;

        private void OpenFeedbackWindow()
        {
            if (_feedbackWindow != null)
            {
                _feedbackWindow.Activate();
                return;
            }

            _feedbackWindow = new FeedbackWindow();
            _feedbackWindow.Closed += (_, _) => _feedbackWindow = null;
            _feedbackWindow.Show();
            _feedbackWindow.Activate();
        }

        // Tray "update" item action: if a background check already found an update, offer to install it;
        // otherwise check now and, if one is found, offer to install it. Both paths end in the same
        // install-or-not prompt. No-op when the auto-updater is compiled out (_updater == null).
        private async Task CheckOrInstallUpdateAsync()
        {
            if (_updater == null)
                return;

            Updater.UpdateCheckResult result = _updater.IsUpdateAvailable
                ? Updater.UpdateCheckResult.UpdateAvailable
                : await _updater.CheckForUpdatesNowAsync();

            string title = Loc("about_title", "About");
            switch (result)
            {
                case Updater.UpdateCheckResult.UpToDate:
                    MessageBox.Show(Loc("about_up_to_date", "You have the latest version."),
                        title, MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case Updater.UpdateCheckResult.Failed:
                    MessageBox.Show(Loc("about_check_failed", "Couldn't check for updates. Please try again later."),
                        title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;

                case Updater.UpdateCheckResult.UpdateAvailable:
                    // Reflect the newly-known state in the tray item ("Update available, install").
                    _appTray?.SetUpdateAvailable();

                    string prompt = string.Format(Loc("about_update_prompt", "Version {0} is available. Install it now?"),
                        _updater.AvailableVersion);
                    if (MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        if (await _updater.InstallUpdateAsync() == Updater.InstallResult.Installing)
                            ExitGracefully();
                    }
                    break;
            }
        }

        private static string Loc(string key, string fallback) =>
            Current?.TryFindResource(key) as string ?? fallback;

        public void ReopenTaskbar()
        {
            closeTaskbar();
            // Recreate only if the current desktop should show it.
            ApplyCurrentDesktopVisibility();
        }

        /// <summary>
        /// Shows or hides the additional taskbar on ALL virtual desktops (the tray's "Show taskbar"
        /// toggle): sets the global default and clears any per-desktop overrides. Hiding it leaves the
        /// app running in the tray; only the tray's Exit quits the whole application.
        /// </summary>
        public void SetAdditionalTaskbarVisible(bool visible)
        {
            _desktopVisibility.SetAll(visible);
            ApplyCurrentDesktopVisibility();
        }

        /// <summary>Shows or hides the additional taskbar only on the current virtual desktop.</summary>
        public void SetCurrentDesktopTaskbarVisible(bool visible)
        {
            _desktopVisibility.SetCurrent(VirtualDesktopService.CurrentDesktop, visible);
            ApplyCurrentDesktopVisibility();
        }

        /// <summary>Whether the additional taskbar is shown on the current virtual desktop.</summary>
        public bool IsTaskbarVisibleOnCurrentDesktop =>
            _desktopVisibility.IsVisibleOn(VirtualDesktopService.CurrentDesktop);

        // Brings the taskbar window's presence in line with the current desktop's stored state. Called
        // on startup, on desktop switches, and whenever a visibility toggle changes. The window is
        // created once and then shown/hidden in place (not destroyed) so switching desktops doesn't
        // churn the app-bar registration.
        private void ApplyCurrentDesktopVisibility()
        {
            bool visible = _desktopVisibility.IsVisibleOn(VirtualDesktopService.CurrentDesktop);

            if (visible)
            {
                if (_taskbar == null)
                {
                    openTaskbar();
                }
                else
                {
                    _taskbar.SetShown(true);
                }
            }
            else
            {
                _taskbar?.SetShown(false);
            }

            _appTray?.UpdateShowTaskbarCheck();
        }

        private void VirtualDesktopService_DesktopChanged(object sender, EventArgs e)
        {
            // The TaskList re-filters itself (it also listens for DesktopChanged); here we just apply
            // this desktop's show/hide state to the taskbar window.
            ApplyCurrentDesktopVisibility();
        }

        private void openTaskbar()
        {
            _taskbar = new Taskbar(_shellManager, _startMenuMonitor, ScreenInfo.FromPrimaryScreen(), (AppBarEdge)Settings.Instance.Edge);
            _taskbar.Show();
        }

        private void closeTaskbar()
        {
            if (_taskbar != null)
            {
                _taskbar.AllowClose = true;
                _taskbar.Close();
                _taskbar = null;
            }
        }

        private void App_OnStartup(object sender, StartupEventArgs e)
        {
#if AUTOUPDATE
            // If this instance was just installed by the updater, wait for the old one to exit
            // and clean up the backup it left behind before we register the app bar.
            Updater.FinishPendingUpdate(e.Args);
#endif
            DictionaryManager.SetLanguageFromSettings();
            DictionaryManager.SetThemeFromSettings();

            // Tray icon and the "drag through" monitor run for the whole app lifetime.
            _dragMonitor.Start();
            _appTray = new AppTray(SetAdditionalTaskbarVisible, SetCurrentDesktopTaskbarVisible,
                () => IsTaskbarVisibleOnCurrentDesktop, OpenFeedbackWindow,
                _updater != null ? CheckOrInstallUpdateAsync : (Func<Task>)null, ExitGracefully);
            // Open the flyout's help video in the background now, so the first hover shows it instantly.
            _appTray.PrewarmFlyout();

            // The auto-updater runs for the whole app lifetime (not tied to the taskbar, which can
            // be hidden). When an update is found, mark it on the tray's "About" item; the user
            // installs it from the About window.
            if (_updater != null)
            {
                _updater.UpdateAvailable += (_, _) => _appTray?.SetUpdateAvailable();
                if (_updater.IsUpdateAvailable)
                {
                    _appTray.SetUpdateAvailable();
                }
            }

            // Create and show the taskbar only when it should be visible on the current desktop; it is
            // then kept alive and just shown/hidden on later desktop switches (cheaper than re-creating).
            // Creating it lazily — instead of showing it up front and hiding it a moment later — is what
            // keeps a disabled taskbar from briefly flashing a strip at the bottom of the screen on
            // startup. Sticky-hide (see Taskbar) stops any late app-bar arrange message from re-showing a
            // taskbar that should stay hidden, so the old show-then-hide dance is no longer needed.
            ApplyCurrentDesktopVisibility();
        }

        private void App_OnExit(object sender, ExitEventArgs e)
        {
            ExitApp();
        }

        private void App_OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            ExitApp();
        }

        private ShellManager SetupManagedShell()
        {
            EnvironmentHelper.IsAppRunningAsShell = NativeMethods.GetShellWindow() == IntPtr.Zero;

            _logger = new ManagedShellLogger();

            // ZombieBar shows the task list but not the system tray (the NotifyIconList is
            // disabled in Taskbar.xaml). Leaving the tray service on makes ManagedShell hook the
            // Explorer tray and relocate Shell_TrayWnd off-screen, which breaks the real taskbar's
            // always-on-top so windows render over it. Turn it off so the system taskbar is left
            // untouched and keeps windows underneath it, just like when ZombieBar isn't running.
            ShellConfig config = ShellManager.DefaultShellConfig;
            config.EnableTrayService = false;
            config.AutoStartTrayService = false;

            return new ShellManager(config);
        }

        private bool _exited;

        private void ExitApp()
        {
            // App_OnExit and App_OnSessionEnding can both fire; guard against double dispose.
            if (_exited)
            {
                return;
            }
            _exited = true;

            // Compact all order identifiers and persist them before tearing down.
            TasksOrderManager?.SaveCompacted();
            TasksOrderManager?.Dispose();

            if (VirtualDesktopService != null)
            {
                VirtualDesktopService.DesktopChanged -= VirtualDesktopService_DesktopChanged;
                VirtualDesktopService.Dispose();
            }

            _appTray?.Dispose();
            _dragMonitor?.Dispose();

            DictionaryManager?.Dispose();
            _shellManager?.Dispose();
            _startMenuMonitor?.Dispose();
            _updater?.Dispose();
            _logger?.Dispose();
        }
    }
}
