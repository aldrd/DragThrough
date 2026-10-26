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

        // Whether the additional taskbar is currently meant to be shown. When false, we actively refuse
        // any shell attempt to re-show the window (see WndProc), so it can't flash a blank strip.
        private bool _shownState = true;

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

            // Decided before the window handle exists, so a floating bar never registers an app bar.
            EnableAppBar = !IsFloating;
            TaskList.NaturalItemsWidthChanged += (_, _) => ScheduleFloatingPosition();
            SizeChanged += (_, _) => ScheduleFloatingPosition();
        }

        // "Only open windows" mode on a horizontal edge: the bar floats at the screen edge, only as wide
        // as its tasks. An app bar always reserves the whole edge of the work area however narrow its
        // window is, so the app-bar registration is dropped as well - the bar overlays (a strip of) the
        // windows behind it instead of shrinking every maximized window.
        private bool IsFloating => Settings.Instance.ShowOnlyOpenTasks && Orientation == Orientation.Horizontal;

        private bool _floatingPositionScheduled;

        private void UpdateFloatingMode()
        {
            bool floating = IsFloating;
            if (floating == !EnableAppBar)
            {
                SetPosition();
                return;
            }

            if (floating)
            {
                UnregisterAppBar();
                EnableAppBar = false;
            }
            else
            {
                EnableAppBar = true;
                TaskList.MaxItemsWidth = double.NaN;
                if (_shownState)
                {
                    RegisterAppBar();
                }
            }

            SetPosition();
        }

        // Coalesced: the content width, the window size and the work area all feed the position, and
        // they tend to change together.
        private void ScheduleFloatingPosition()
        {
            if (_floatingPositionScheduled || !IsFloating || EnableAppBar)
            {
                return;
            }

            _floatingPositionScheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _floatingPositionScheduled = false;
                if (IsFloating && !EnableAppBar && !AllowClose)
                {
                    SetPosition();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public override void SetPositionSimple()
        {
            if (!IsFloating)
            {
                base.SetPositionSimple();
                return;
            }

            // Read fresh: ScreenInfo.WorkingArea is a snapshot, and dropping the app bar changes it.
            System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromRectangle(ScreenInfo.Bounds).WorkingArea;
            double areaLeft = area.Left / DpiScale;
            double areaWidth = area.Width / DpiScale;

            // Everything around the task buttons (borders, list margin) - constant, so measuring it from
            // the current layout converges in one pass.
            double chrome = ActualWidth > 0 && TaskList.ItemsWidth > 0 ? ActualWidth - TaskList.ItemsWidth : 0;
            TaskList.MaxItemsWidth = Math.Max(0, areaWidth - chrome);

            // +1 absorbs DPI rounding so a bar sized exactly to its buttons doesn't wrap the last one. An
            // empty bar keeps a small square so it can still be right-clicked.
            double width = Math.Min(areaWidth, Math.Max(DesiredHeight, TaskList.NaturalItemsWidth + chrome + 1));

            Width = width;
            Height = DesiredHeight;
            Left = Settings.Instance.CenterTasksInTaskbar ? areaLeft + (areaWidth - width) / 2 : areaLeft;
            Top = AppBarEdge == AppBarEdge.Top
                ? area.Top / DpiScale
                : area.Bottom / DpiScale - Height;
        }

        protected override void OnSourceInitialized(object sender, EventArgs e)
        {
            base.OnSourceInitialized(sender, e);

            _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            SetBlur(AllowsTransparency);
            ScheduleFloatingPosition();
        }

        /// <summary>
        /// Shows or hides this taskbar without destroying it, releasing/reserving its app-bar space
        /// with it. Used to switch per-desktop visibility cheaply on desktop changes (no window
        /// re-creation, so no leaked windows or app-bar re-registration churn).
        /// </summary>
        public void SetShown(bool shown)
        {
            _shownState = shown;

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

            // While the additional taskbar is intentionally hidden, shell events (a full-screen app
            // closing, z-order re-asserts on desktop switches, etc.) try to re-show this window via
            // SetWindowPos(SWP_SHOWWINDOW) — most reliably from the app bar's own full-screen handler
            // (ShowWindowTopMost). WPF still has Visibility=Hidden, so the re-shown window paints as a
            // blank white strip until something hides it again. Strip the show flag (and force hide) so a
            // hidden taskbar stays hidden no matter which shell event tries to reveal it — no flicker.
            if (!_shownState && msg == (int)NativeMethods.WM.WINDOWPOSCHANGING)
            {
                NativeMethods.WINDOWPOS wp = NativeMethods.WINDOWPOS.FromMessage(lParam);
                if ((wp.flags & NativeMethods.SetWindowPosFlags.SWP_SHOWWINDOW) != 0)
                {
                    wp.flags &= ~NativeMethods.SetWindowPosFlags.SWP_SHOWWINDOW;
                    wp.flags |= NativeMethods.SetWindowPosFlags.SWP_HIDEWINDOW;
                    wp.UpdateMessage(lParam);
                }
            }

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

            // The work area changed (e.g. Explorer's taskbar moved or auto-hides): re-anchor a floating bar.
            if (msg == (int)NativeMethods.WM.SETTINGCHANGE)
            {
                ScheduleFloatingPosition();
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
                UpdateFloatingMode();
            }
            else if (e.PropertyName == nameof(Settings.ShowOnlyOpenTasks))
            {
                UpdateFloatingMode();
            }
            else if (e.PropertyName == nameof(Settings.CenterTasksInTaskbar))
            {
                ScheduleFloatingPosition();
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
