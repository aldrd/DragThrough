using System;
using ManagedShell;
using ZombieBar.Utilities;
using System.Windows;
using System.Windows.Threading;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using Application = System.Windows.Application;

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

        // Single-instance "About" window, opened from the tray menu.
        private AboutWindow _aboutWindow;

        private void OpenAboutWindow()
        {
            if (_aboutWindow != null)
            {
                _aboutWindow.Activate();
                return;
            }

            _aboutWindow = new AboutWindow(_updater);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
            _aboutWindow.Activate();
        }

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
                () => IsTaskbarVisibleOnCurrentDesktop, OpenFeedbackWindow, OpenAboutWindow, ExitGracefully);
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

            // Create the taskbar window once up front, then show/hide it for the current desktop.
            // Keeping a single window alive (rather than creating it on a later desktop switch) avoids
            // fragile app-bar re-registration mid-switch, and Show/Hide is far cheaper than re-creating.
            openTaskbar();
            // Apply the initial per-desktop visibility once the app-bar has finished registering, so
            // hiding it on a hidden-by-default desktop isn't undone by a late app-bar arrange message.
            Dispatcher.BeginInvoke(new Action(ApplyCurrentDesktopVisibility), DispatcherPriority.ApplicationIdle);
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
