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
        private Updater _updater;

        public Taskbar(ShellManager shellManager, StartMenuMonitor startMenuMonitor, Updater updater, ScreenInfo screen, AppBarEdge edge)
            : base(shellManager.AppBarManager, shellManager.ExplorerHelper, shellManager.FullScreenHelper, screen, edge, 0)
        {
            _shellManager = shellManager;
            _updater = updater;

            InitializeComponent();

            // Reveal the update notification as soon as one is found (null when the auto-updater
            // is compiled out of the Microsoft Store build).
            if (_updater != null)
            {
                _updater.UpdateAvailable += Updater_UpdateAvailable;
                if (_updater.IsUpdateAvailable)
                {
                    UpdateAvailableMenuItem.Visibility = Visibility.Visible;
                }
            }
            
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

            SetBlur(AllowsTransparency);
        }
        
        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            base.WndProc(hwnd, msg, wParam, lParam, ref handled);

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

        private void Updater_UpdateAvailable(object sender, EventArgs e)
        {
            UpdateAvailableMenuItem.Visibility = Visibility.Visible;
        }

        private async void UpdateAvailableMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (_updater == null)
            {
                return;
            }

            // Download, verify and swap in the new version, then hand off to it.
            UpdateAvailableMenuItem.IsEnabled = false;

            if (await _updater.InstallUpdateAsync())
            {
                ((App)Application.Current).ExitGracefully();
                return;
            }

            UpdateAvailableMenuItem.IsEnabled = true;

            // Couldn't install automatically (e.g. read-only install folder); open the releases page.
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLinks.ReleasesPageUrl,
                UseShellExecute = true
            });
        }

        private void PropertiesMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            PropertiesWindow.Open(((App)Application.Current).DictionaryManager);
        }

        private void RemoveMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            // "Remove" only hides the additional taskbar; the app keeps running in the tray.
            ((App)Application.Current).SetAdditionalTaskbarVisible(false);
        }

        protected override void CustomClosing()
        {
            if (AllowClose)
            {
                if (!_isReopening) _explorerHelper.HideExplorerTaskbar = false;
                QuickLaunchToolbar.Visibility = Visibility.Collapsed;

                Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

                if (_updater != null)
                {
                    _updater.UpdateAvailable -= Updater_UpdateAvailable;
                }
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (_updater != null && _updater.IsUpdateAvailable)
            {
                UpdateAvailableMenuItem.Visibility = Visibility.Visible;
            }
        }
    }
}
