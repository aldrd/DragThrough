#nullable enable
using System;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Returns a <see cref="SolidColorBrush"/> matching the current Windows taskbar
    /// background color. It is intended for use from the "System" theme.
    ///
    /// The brush is intentionally translucent: the "System" theme enables an acrylic
    /// blur behind the taskbar window (see <c>Taskbar.SetBlur</c>), and this brush is the
    /// tint painted over that blur, mirroring the Windows 11 taskbar's translucent look.
    ///
    /// The value is computed at XAML parse time. The taskbar re-applies the active theme
    /// whenever it receives WM_SETTINGCHANGE / WM_SYSCOLORCHANGE / WM_DWMCOLORIZATIONCOLORCHANGED
    /// (see <c>Taskbar.WndProc</c>), so the brush tracks live changes to the system accent
    /// color and the light/dark mode on both Windows 10 and Windows 11.
    /// </summary>
    public class SystemTaskbarColorExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return new SolidColorBrush(SystemTaskbarColor.GetColor());
        }
    }

    /// <summary>
    /// The pointer-over (hover) fill <see cref="Color"/> for a "System" theme task button,
    /// mirroring the Windows 11 taskbar button's hover highlight. Returns a Color (not a brush)
    /// so it can feed a SolidColorBrush resource that templates reference via DynamicResource;
    /// that keeps the hover color live when the theme is re-applied on a system color change.
    /// </summary>
    public class SystemTaskbarButtonHoverColorExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return SystemTaskbarColor.GetButtonHoverColor();
        }
    }

    /// <summary>
    /// The pressed fill <see cref="Color"/> for a "System" theme task button, mirroring the
    /// Windows 11 taskbar button's pressed highlight. Returns a Color like its hover sibling.
    /// </summary>
    public class SystemTaskbarButtonPressedColorExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return SystemTaskbarColor.GetButtonPressedColor();
        }
    }

    /// <summary>
    /// The fill <see cref="Color"/> for the active (foreground) window's task button, mirroring
    /// the Windows 11 taskbar's focused button — a light frosted shade. Returns a Color like its
    /// siblings so it can feed a DynamicResource brush that stays live across theme re-applies.
    /// </summary>
    public class SystemTaskbarButtonActiveColorExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return SystemTaskbarColor.GetButtonActiveColor();
        }
    }

    /// <summary>
    /// A readable text <see cref="Color"/> for the taskbar: dark text on a light taskbar, light
    /// text on a dark one (including dark accent / custom colors). Re-evaluated on a system color
    /// change like its siblings, so the text contrast follows light/dark mode live.
    /// </summary>
    public class SystemTaskbarForegroundColorExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return SystemTaskbarColor.GetForegroundColor();
        }
    }

    public static class SystemTaskbarColor
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";

        // Default taskbar colors when the accent color is not used on the taskbar.
        private static readonly Color LightTaskbar = Color.FromRgb(0xF3, 0xF3, 0xF3);
        private static readonly Color DarkTaskbar = Color.FromRgb(0x1F, 0x1F, 0x1F);

        // Acrylic tint opacity. The OS draws the blur behind the window; this alpha controls
        // how much the system color tints that blur. ~0xB3 (70%) mirrors the Windows 11
        // taskbar's translucent look while keeping task labels readable. Tune to taste.
        private const byte AcrylicTintAlpha = 0xB3;

        /// <summary>
        /// The translucent tint painted over the acrylic blur. See <see cref="GetOpaqueColor"/>
        /// for how the underlying color is chosen.
        /// </summary>
        public static Color GetColor()
        {
            Color color = GetOpaqueColor();
            return Color.FromArgb(AcrylicTintAlpha, color.R, color.G, color.B);
        }

        // Windows 11 taskbar buttons are transparent at rest and frost *lighter* on interaction
        // (a translucent white overlay), matching the active fill direction below rather than the
        // generic WinUI "Subtle" darken. Hover is a weaker frost than the active state, and
        // pressed is stronger (presses in toward the active look). Dark mode needs far less white
        // than light mode to read as a lift. Tune the alphas to taste.
        private static readonly Color HoverLight = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        private static readonly Color HoverDark = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        private static readonly Color PressedLight = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
        private static readonly Color PressedDark = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);

        // The active (foreground) window's button is the strongest frosted fill. The light value
        // is measured from the real Windows 11 taskbar (its focused button samples as white at
        // ~50% over the bar); the dark value is a subtler lift so it reads as a lighter-than-bar
        // gray instead of near-white.
        private static readonly Color ActiveLight = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        private static readonly Color ActiveDark = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);

        /// <summary>
        /// Mirrors how Windows colors the taskbar: when "Show accent color on Start and the
        /// taskbar" is enabled the accent color is used, otherwise a light or dark shade is
        /// chosen based on the system (not app) light/dark mode setting.
        /// </summary>
        private static Color GetOpaqueColor()
        {
            bool accentOnTaskbar = ReadDword(PersonalizeKey, "ColorPrevalence", 0) != 0;
            if (accentOnTaskbar)
            {
                Color? accent = GetAccentColor();
                if (accent.HasValue)
                    return accent.Value;
            }

            return IsSystemLightTheme() ? LightTaskbar : DarkTaskbar;
        }

        /// <summary>
        /// A readable text color for the taskbar background: black on a light background, white on
        /// a dark one. Based on the perceived brightness of the (opaque) taskbar color, so it works
        /// for the light/dark defaults and for dark accent / custom colors.
        /// </summary>
        public static Color GetForegroundColor()
        {
            Color background = GetOpaqueColor();

            // Windows' own "is this color light?" test, used to pick text color on accent/taskbar
            // surfaces: a light color gets dark text, a dark color gets light text. Matching it
            // keeps our choice consistent with the real taskbar for accent and custom colors.
            bool isLight = (5 * background.G + 2 * background.R + background.B) > (8 * 128);

            return isLight ? Colors.Black : Colors.White;
        }

        /// <summary>The hover fill for a task button, following the system light/dark mode.</summary>
        public static Color GetButtonHoverColor()
        {
            return IsSystemLightTheme() ? HoverLight : HoverDark;
        }

        /// <summary>The pressed fill for a task button, following the system light/dark mode.</summary>
        public static Color GetButtonPressedColor()
        {
            return IsSystemLightTheme() ? PressedLight : PressedDark;
        }

        /// <summary>The active window's button fill, following the system light/dark mode.</summary>
        public static Color GetButtonActiveColor()
        {
            return IsSystemLightTheme() ? ActiveLight : ActiveDark;
        }

        // The taskbar follows the system (not the per-app) light/dark setting.
        private static bool IsSystemLightTheme()
        {
            return ReadDword(PersonalizeKey, "SystemUsesLightTheme", 1) != 0;
        }

        private static Color? GetAccentColor()
        {
            // HKCU\...\DWM\AccentColor is a DWORD stored as 0xAABBGGRR (ABGR).
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("AccentColor") is int abgr)
            {
                byte r = (byte)(abgr & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
            return null;
        }

        private static int ReadDword(string subKey, string name, int fallback)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey);
            return key?.GetValue(name) is int i ? i : fallback;
        }
    }
}
