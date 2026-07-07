#nullable enable
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell;
using ManagedShell.WindowsTray;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZombieBar.Utilities;
using Application = System.Windows.Application;
using ZombieBar.Controls;
using System.Diagnostics;
using Ssz.Utils.Wpf;

namespace ZombieBar
{
    /// <summary>
    /// Interaction logic for Taskbar.xaml
    /// </summary>
    public partial class Taskbar : AppBarWindow
    {
        private bool _isReopening;
        private ShellManager _shellManager;

        // Registered "TaskbarCreated" broadcast id; Explorer sends it to every top-level window when
        // it (re)starts. -1 until the window handle exists.
        private int _taskbarCreatedMsg = -1;

        public Taskbar(ShellManager shellManager, StartMenuMonitor startMenuMonitor, ScreenInfo screen, AppBarEdge edge)
            : base(shellManager.AppBarManager, shellManager.ExplorerHelper, shellManager.FullScreenHelper, screen, edge, 0)
        {
            _shellManager = shellManager;

            InitializeComponent();

            // The auto-updater is owned by the App (it runs even when this taskbar is hidden); an
            // available update is surfaced on the tray's "About" item, not here.

            DataContext = _shellManager;
            
            //StartButton.StartMenuMonitor = startMenuMonitor;

            DesiredHeight = Application.Current.FindResource("TaskbarHeight") as double? ?? 0;
            DesiredWidth = Application.Current.FindResource("TaskbarWidth") as double? ?? 0;

            AllowsTransparency = Application.Current.FindResource("AllowsTransparency") as bool? ?? false;
            SetFontSmoothing();

            //_explorerHelper.HideExplorerTaskbar = true;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            // Layout rounding causes incorrect sizing on non-integer scales
            if(ScreenHelper.PrimaryScreenScaleX % 1 != 0) UseLayoutRounding = false;

            if (Settings.Instance.ShowQuickLaunch)
            {
                QuickLaunchToolbar.Visibility = Visibility.Visible;
            }
        }

        protected override void OnSourceInitialized(object sender, EventArgs e)
        {
            base.OnSourceInitialized(sender, e);

            _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            SetBlur(AllowsTransparency);
        }

        /// <summary>
        /// Shows or hides this taskbar without destroying it, releasing/reserving its app-bar space
        /// with it. Used to switch per-desktop visibility cheaply on desktop changes (no window
        /// re-creation, so no leaked windows or app-bar re-registration churn).
        /// </summary>
        public void SetShown(bool shown)
        {
            if (shown)
            {
                if (!IsVisible)
                {
                    Show();
                }
                RegisterAppBar();
                SetPosition();
            }
            else
            {
                UnregisterAppBar();
                Hide();

                // A virtual-desktop switch re-shows this app-bar window at the OS level (it is a
                // top-most tool window the shell carries across desktops), but WPF keeps its
                // Visibility=Hidden, so nothing is painted and a bare, see-through strip is left where
                // the taskbar was. WPF's Hide() is a no-op once it already believes the window is
                // hidden, so force the native SW_HIDE to re-assert it on every desktop change.
                if (Handle != IntPtr.Zero)
                    NativeMethods.ShowWindow(Handle, NativeMethods.WindowShowStyle.Hide);
            }
        }
        
        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            base.WndProc(hwnd, msg, wParam, lParam, ref handled);

            // Explorer restart re-broadcasts "TaskbarCreated" and re-composites windows, which (like a
            // virtual-desktop switch) can leave this app bar as a bare see-through strip and also drops
            // its app-bar space reservation. Rebuild it from scratch so it recovers exactly as on
            // startup: a hidden taskbar closes cleanly (no strip), a shown one re-registers the app bar.
            // Dispatched async so we don't close this window from inside its own message handler.
            if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != -1)
            {
                Dispatcher.BeginInvoke(new Action(() => ((App)Application.Current).ReopenTaskbar()),
                    System.Windows.Threading.DispatcherPriority.Background);
                return IntPtr.Zero;
            }

            bool colorMessage = msg == (int)NativeMethods.WM.SYSCOLORCHANGE ||
                                msg == (int)NativeMethods.WM.SETTINGCHANGE ||
                                msg == (int)NativeMethods.WM.DWMCOLORIZATIONCOLORCHANGED;

            // Themes that derive their colors from the system: the default "System" theme uses
            // the live Windows taskbar color, and its classic "System 2" variant uses SystemColors.*.
            bool themeTracksSystem = Settings.Instance.Theme == DictionaryManager.THEME_DEFAULT ||
                                     Settings.Instance.Theme == DictionaryManager.THEME_SYSTEM_CLASSIC;

            if (colorMessage && themeTracksSystem)
            {
                handled = true;

                // If the color scheme changes, re-apply the current theme to get updated colors.
                ((App)Application.Current).DictionaryManager.SetThemeFromSettings();
            }

            return IntPtr.Zero;
        }

        //public override void SetPosition()
        //{
        //    base.SetPosition();

        //    _shellManager.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
        //    {
        //        edge = (NativeMethods.ABEdge)AppBarEdge,
        //        rc = new NativeMethods.Rect
        //        {
        //            Top = (int)(Top * DpiScale),
        //            Left = (int)(Left * DpiScale),
        //            Bottom = (int)((Top + Height) * DpiScale),
        //            Right = (int)((Left + Width) * DpiScale)
        //        }
        //    });
        //}

        private void SetFontSmoothing()
        {
            VisualTextRenderingMode = Settings.Instance.AllowFontSmoothing ? TextRenderingMode.Auto : TextRenderingMode.Aliased;
        }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Theme")
            {
                bool newTransparency = Application.Current.FindResource("AllowsTransparency") as bool? ?? false;
                double newHeight = Application.Current.FindResource("TaskbarHeight") as double? ?? 0;
                double newWidth = Application.Current.FindResource("TaskbarWidth") as double? ?? 0;
                bool heightChanged = newHeight != DesiredHeight;
                bool widthChanged = newWidth != DesiredWidth;

                if (AllowsTransparency != newTransparency)
                {
                    // Transparency cannot be changed on an open window.
                    _isReopening = true;
                    ((App)Application.Current).ReopenTaskbar();
                    return;
                }

                DesiredHeight = newHeight;
                DesiredWidth = newWidth;

                if (Orientation == Orientation.Horizontal && heightChanged)
                {
                    Height = DesiredHeight;
                    SetPosition();
                }
                else if (Orientation == Orientation.Vertical && widthChanged)
                {
                    Width = DesiredWidth;
                    SetPosition();
                }
            }
            else if (e.PropertyName == "AllowFontSmoothing")
            {
                SetFontSmoothing();
            }
            else if (e.PropertyName == "ShowQuickLaunch")
            {
                if (Settings.Instance.ShowQuickLaunch)
                {
                    QuickLaunchToolbar.Visibility = Visibility.Visible;
                }
                else
                {
                    QuickLaunchToolbar.Visibility = Visibility.Collapsed;
                }
            }
            else if (e.PropertyName == "Edge")
            {
                AppBarEdge = (AppBarEdge)Settings.Instance.Edge;
                SetPosition();
            }
        }

        private void TaskManagerMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ShellHelper.StartTaskManager();
        }

        private void PropertiesMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            PropertiesWindow.Open(((App)Application.Current).DictionaryManager);
        }

        private void RemoveMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            // "Remove" hides the additional taskbar on this virtual desktop only; the app keeps
            // running in the tray, and other desktops are unaffected.
            ((App)Application.Current).SetCurrentDesktopTaskbarVisible(false);
        }

        protected override void CustomClosing()
        {
            if (AllowClose)
            {
                if (!_isReopening) _explorerHelper.HideExplorerTaskbar = false;
                QuickLaunchToolbar.Visibility = Visibility.Collapsed;

                Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            }
        }
    }
}
