#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ZombieBar.Utilities
{
    /// <summary>Small shared helpers for the app's WPF popups (system theme + the tray icon).</summary>
    public static class AppUi
    {
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
