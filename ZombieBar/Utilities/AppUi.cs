#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ZombieBar.Utilities
{
    /// <summary>Small shared helpers for the app's WPF popups (system theme + the tray icon).</summary>
    public static class AppUi
    {
        /// <summary>
        /// Sets the shared dialog palette (BgBrush, FgBrush, AccentBrush, ...) on the window as
        /// dynamic resources, following the system light/dark theme. The dialog styles in
        /// Resources/DialogStyles.xaml reference these, so every dialog (update, About, Feedback)
        /// gets the same look. Call before showing (and on each show to track theme changes).
        /// </summary>
        public static void ApplyDialogTheme(Window window)
        {
            bool dark = IsSystemDark();

            void Set(string key, string hex)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                window.Resources[key] = brush;
            }

            Set("BgBrush",          dark ? "#FF202124" : "#FFFFFFFF");
            Set("FgBrush",          dark ? "#FFF2F2F2" : "#FF1A1A1A");
            Set("SubFgBrush",       dark ? "#FFA8A8A8" : "#FF6B6B6B");
            Set("BorderBrush",      dark ? "#FF3A3A3D" : "#FFE5E5E5");
            Set("SepBrush",         dark ? "#FF3A3A3D" : "#FFE6E6E6");
            Set("HoverBrush",       dark ? "#1AFFFFFF" : "#14000000");
            Set("AccentBrush",      dark ? "#FF4CC2FF" : "#FF0067C0");
            Set("AccentFgBrush",    dark ? "#FF101114" : "#FFFFFFFF");
            Set("AccentHoverBrush", dark ? "#FF6BCEFF" : "#FF1A78D2");
            Set("ControlBgBrush",   dark ? "#FF2A2B2E" : "#FFF8F8F8");
            Set("TrackBrush",       dark ? "#FF3A3A3D" : "#FFE9E9EA");
            Set("DangerBrush",      dark ? "#FFE57373" : "#FFD13438");
        }

        /// <summary>True when Windows is using the dark "apps" theme.</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v)
                {
                    return v == 0;
                }
            }
            catch { /* default to light */ }
            return false;
        }

        /// <summary>
        /// The app's tray icon (embedded tray_icon.ico) as a WPF image, so windows match the system
        /// tray. Picks the largest frame in the .ico for a crisp result.
        /// </summary>
        public static ImageSource? LoadAppIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string? name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("tray_icon.ico", StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    return null;
                }

                using Stream? stream = asm.GetManifestResourceStream(name);
                if (stream == null)
                {
                    return null;
                }

                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }
            catch
            {
                return null;
            }
        }
    }
}
