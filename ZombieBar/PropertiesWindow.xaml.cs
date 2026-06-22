using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ManagedShell.Common.Helpers;
using ZombieBar.Utilities;
using System.Windows;
using ManagedShell.Common.Logging;
using Microsoft.Win32;
using ManagedShell.AppBar;
using System.Windows.Forms;
using Ssz.Utils.Wpf.WpfScreenHelper;
using Ssz.Utils.Wpf;

namespace ZombieBar
{
    /// <summary>
    /// Interaction logic for PropertiesWindow.xaml
    /// </summary>
    public partial class PropertiesWindow : Window
    {
        private static PropertiesWindow _instance;

        private readonly DictionaryManager _dictionaryManager;

        private PropertiesWindow(DictionaryManager dictionaryManager)
        {
            _dictionaryManager = dictionaryManager;

            InitializeComponent();

            LoadAutoStart();
            LoadLanguages();
            LoadThemes();
        }

        public static void Open(DictionaryManager dictionaryManager)
        {
            if (_instance == null)
            {
                _instance = new PropertiesWindow(dictionaryManager);
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }
        }

        private void LoadAutoStart()
        {
            try
            {
                using RegistryKey rKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                List<string> rKeyValueNames = rKey?.GetValueNames().ToList();

                if (rKeyValueNames != null)
                {
                    if (rKeyValueNames.Contains("ZombieBar"))
                    {
                        AutoStartCheckBox.IsChecked = true;
                    }
                    else
                    {
                        AutoStartCheckBox.IsChecked = false;
                    }
                }
            }
            catch (Exception e)
            {
                ShellLogger.Error($"PropertiesWindow: Unable to load autorun setting from registry: {e.Message}");
            }
        }

        private void LoadLanguages()
        {
            foreach (var language in _dictionaryManager.GetLanguages())
            {
                cboLanguageSelect.Items.Add(language);
            }
        }

        private void LoadThemes()
        {
            foreach (var theme in _dictionaryManager.GetThemes())
            {
                cboThemeSelect.Items.Add(theme);
            }
        }

        private void UpdateWindowPosition()
        {
            switch (Settings.Instance.Edge)
            {
                case (int)AppBarEdge.Left:
                case (int)AppBarEdge.Top:
                    Left = (SystemInformation.WorkingArea.Left / ScreenHelper.PrimaryScreenScaleX) + 10;
                    Top = (SystemInformation.WorkingArea.Top / ScreenHelper.PrimaryScreenScaleY) + 10;
                    break;
                case (int)AppBarEdge.Right:
                    Left = (SystemInformation.WorkingArea.Right / ScreenHelper.PrimaryScreenScaleX) - Width - 10;
                    Top = (SystemInformation.WorkingArea.Top / ScreenHelper.PrimaryScreenScaleY) + 10;
                    break;
                case (int)AppBarEdge.Bottom:
                    Left = (SystemInformation.WorkingArea.Left / ScreenHelper.PrimaryScreenScaleX) + 10;
                    Top = (SystemInformation.WorkingArea.Bottom / ScreenHelper.PrimaryScreenScaleY) - Height - 10;
                    break;
            }
        }

        private void OK_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetQuickLaunchLocation_OnClick(object sender, RoutedEventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.Description = (string)FindResource("quick_launch_folder");
            fbd.UseDescriptionForTitle = true;
            fbd.ShowNewFolderButton = false;
            fbd.SelectedPath = Settings.Instance.QuickLaunchPath;

            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Settings.Instance.QuickLaunchPath = fbd.SelectedPath;
            }
        }

        private void PropertiesWindow_OnClosing(object sender, CancelEventArgs e)
        {
            _instance = null;
        }

        private void PropertiesWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            var virtualScreen = WindowsScreen.VirtualScreen;

            Left = 10;
            Top = (virtualScreen.Height / ScreenHelper.PrimaryScreenScaleY) - Height - 40;
            UpdateWindowPosition();
        }

        private void PropertiesWindow_OnContentRendered(object sender, EventArgs e)
        {
            UpdateWindowPosition();
        }

        private void AutoStartCheckBox_OnChecked(object sender, RoutedEventArgs e)
        {
            try
            {
                using RegistryKey rKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                var chkBox = (System.Windows.Controls.CheckBox)sender;

                if (chkBox.IsChecked.Equals(false))
                {
                    rKey?.DeleteValue("ZombieBar");
                }
                else
                {
                    rKey?.SetValue("ZombieBar", ExePath.GetExecutablePath());
                }
            }
            catch (Exception exception)
            {
                ShellLogger.Error($"PropertiesWindow: Unable to update registry autorun setting: {exception.Message}");
            }
        }

        private void cboEdgeSelect_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cboEdgeSelect.SelectedItem == null)
            {
                cboEdgeSelect.SelectedValue = cboEdgeSelect.Items[Settings.Instance.Edge];
            }
        }
    }
}
