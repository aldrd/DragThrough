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

        // Set while the checkbox is being filled in from the current state, so that doing so does not
        // look like a user click and write the setting straight back.
        private bool _syncingAutoStart;

        private async void LoadAutoStart()
        {
            bool? enabled = await AutoStart.IsEnabledAsync();
            if (enabled == null)
                return;

            _syncingAutoStart = true;
            AutoStartCheckBox.IsChecked = enabled.Value;
            _syncingAutoStart = false;
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

        private async void AutoStartCheckBox_OnChecked(object sender, RoutedEventArgs e)
        {
            if (_syncingAutoStart)
                return;

            var chkBox = (System.Windows.Controls.CheckBox)sender;
            bool wanted = chkBox.IsChecked == true;

            AutoStart.Result result = await AutoStart.SetEnabledAsync(wanted);
            if (result == AutoStart.Result.Ok)
                return;

            // The change did not take, so put the checkbox back rather than leave it claiming otherwise.
            _syncingAutoStart = true;
            chkBox.IsChecked = !wanted;
            _syncingAutoStart = false;

            // Only the user can undo their own Task Manager opt-out, so say where to do it. A policy
            // block or an outright failure is nothing they can act on here; those are just logged.
            if (result == AutoStart.Result.BlockedByUser)
            {
                System.Windows.MessageBox.Show(this,
                    Loc("autostart_blocked", "Startup for this app was turned off in Task Manager. Turn it back on there, on the Startup tab."),
                    Loc("about_title", "DragThrough"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string Loc(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private void cboEdgeSelect_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cboEdgeSelect.SelectedItem == null)
            {
                cboEdgeSelect.SelectedValue = cboEdgeSelect.Items[Settings.Instance.Edge];
            }
        }
    }
}
