using System;
using ManagedShell;
using ZombieBar.Utilities;
using System.Windows;
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
            openTaskbar();
        }

        /// <summary>
        /// Shows or hides the additional taskbar and persists the choice. Hiding it (the taskbar's
        /// "Remove" item or the tray's "Show taskbar" toggle) leaves the app running in the tray;
        /// only the tray's Exit quits the whole application.
        /// </summary>
        public void SetAdditionalTaskbarVisible(bool visible)
        {
            Settings.Instance.ShowAdditionalTaskbar = visible;

            if (visible)
            {
                if (_taskbar == null)
                {
                    openTaskbar();
                }
            }
            else
            {
                closeTaskbar();
            }

            _appTray?.UpdateShowTaskbarCheck();
        }

        private void openTaskbar()
        {
            _taskbar = new Taskbar(_shellManager, _startMenuMonitor, _updater, ScreenInfo.FromPrimaryScreen(), (AppBarEdge)Settings.Instance.Edge);
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
            _appTray = new AppTray(SetAdditionalTaskbarVisible, OpenFeedbackWindow, OpenAboutWindow, ExitGracefully);

            // The additional taskbar is shown on first run (default) and whenever it was left
            // visible; "Remove" / the tray toggle persist the hidden state.
            if (Settings.Instance.ShowAdditionalTaskbar)
            {
                openTaskbar();
            }
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
