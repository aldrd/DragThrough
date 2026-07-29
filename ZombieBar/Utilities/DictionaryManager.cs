using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace ZombieBar.Utilities
{
    public class DictionaryManager : IDisposable
    {
        private const string DICT_DEFAULT = "System";
        private const string DICT_EXT = "xaml";

        private const string LANG_DEFAULT = DICT_DEFAULT;
        private const string LANG_FALLBACK = "English";
        private const string LANG_FOLDER = "Languages";
        private const string LANG_EXT = DICT_EXT;

        // Hidden foundation theme, always loaded first and never shown in the picker. Every
        // selectable theme is merged on top of it. It carries the classic Windows look so the
        // retro themes can reuse its 3D button template.
        private const string THEME_BASE = "Base";

        // Default selected theme.
        public const string THEME_DEFAULT = "System";

        // Classic 3D variant of the System theme; like System it derives its colors from
        // the system, so the taskbar must re-apply it when the system colors change.
        public const string THEME_SYSTEM_CLASSIC = "System 2";

        private const string THEME_FOLDER = "Themes";
        private const string THEME_EXT = DICT_EXT;

        // Built-in dictionaries are compiled into the exe (no loose files ship next to it). These
        // lists give their display names (with the original casing the UI and settings expect);
        // the matching .xaml are loaded via application pack URIs. Users can still add custom
        // dictionaries as loose .xaml files in a Themes/Languages folder next to the exe.
        private static readonly string[] BuiltInThemes =
        {
            THEME_BASE, "System", "System 2", "Watercolor",
            "Windows 2000", "Windows 95-98", "Windows Me",
            "Windows XP Blue", "Windows XP Classic"
        };

        private static readonly string[] BuiltInLanguages =
        {
            LANG_FALLBACK, "español", "français", "português", "русский", "中文(简体)"
        };

        private static string[] BuiltInNames(string dictFolder) =>
            dictFolder == THEME_FOLDER ? BuiltInThemes : BuiltInLanguages;

        public DictionaryManager()
        {
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
        }

        public void SetThemeFromSettings()
        {
            // Always load the hidden base first, then merge the selected theme on top.
            SetTheme(THEME_BASE);
            if (Settings.Instance.Theme != THEME_BASE)
            {
                SetTheme(Settings.Instance.Theme);
            }
        }

        private void SetTheme(string theme)
        {
            SetDictionary(theme, THEME_FOLDER, THEME_BASE, THEME_EXT, 0);
        }

        private static Collection<ResourceDictionary> GetMergedDictionaries()
        {
            return Application.Current.Resources.MergedDictionaries;
        }

        private static bool IsThemeDictionary(ResourceDictionary rd)
        {
            // Normalize the separator: dictionaries loaded via Path.Combine carry a backslash
            // on Windows ("Themes\Base.xaml"), while the App.xaml and loose-file ones use a
            // forward slash. Matching only "Themes/" would miss the backslash variants and
            // leave stale theme dictionaries merged on every re-apply.
            return rd.Source != null
                && rd.Source.ToString().Replace('\\', '/').Contains($"{THEME_FOLDER}/");
        }

        private void ClearPreviousThemes()
        {
            foreach (ResourceDictionary rd in GetMergedDictionaries().Where(IsThemeDictionary).ToList())
            {
                _ = GetMergedDictionaries().Remove(rd);
            }
        }

        public void SetLanguageFromSettings()
        {
            SetLanguage(LANG_FALLBACK);
            if (Settings.Instance.Language == LANG_DEFAULT)
            {
                var currentUICulture = System.Globalization.CultureInfo.CurrentUICulture;
                string systemLanguageParent = currentUICulture.Parent.NativeName;
                string systemLanguage = currentUICulture.NativeName;
                ManagedShell.Common.Logging.ShellLogger.Info
                    ($"Loading system language (if available): {systemLanguageParent}, {systemLanguage}");
                SetLanguage(systemLanguageParent);
                SetLanguage(systemLanguage);
            }
            else
            {
                SetLanguage(Settings.Instance.Language);
            }
        }

        private void SetLanguage(string language)
        {
            SetDictionary(language, LANG_FOLDER, LANG_FALLBACK, LANG_EXT, 1);
        }

        private void SetDictionary(string dictionary, string dictFolder, string dictDefault, string dictExtension, int dictType)
        {
            if (dictType == 0 && dictionary == dictDefault)
            {
                ClearPreviousThemes();
            }

            Uri source = ResolveDictionary(dictionary, dictFolder, dictExtension);
            if (source == null)
            {
                return;
            }

            GetMergedDictionaries().Add(new ResourceDictionary() { Source = source });
        }

        // Resolves a dictionary to its source URI. A loose .xaml of the same name next to the exe
        // wins (lets users override a built-in or add a custom one); otherwise a built-in is loaded
        // from the assembly via an application pack URI. Returns null if neither exists.
        private static Uri ResolveDictionary(string dictionary, string dictFolder, string dictExtension)
        {
            string fileName = dictionary + "." + dictExtension;
            string exeDir = Path.GetDirectoryName(ExePath.GetExecutablePath()) ?? AppDomain.CurrentDomain.BaseDirectory;
            string loosePath = Path.Combine(exeDir, dictFolder, fileName);

            if (File.Exists(loosePath))
            {
                return new Uri(loosePath, UriKind.Absolute);
            }

            if (BuiltInNames(dictFolder).Contains(dictionary))
            {
                // Application-relative pack URI -> the dictionary compiled into this exe.
                return new Uri($"{dictFolder}/{fileName}", UriKind.Relative);
            }

            return null;
        }

        public List<string> GetThemes()
        {
            // The hidden base theme is never user-selectable.
            return GetDictionaries(THEME_FOLDER, THEME_EXT)
                .Where(t => t != THEME_BASE)
                .ToList();
        }

        public List<string> GetLanguages()
        {
            // "System" follows the OS language; the rest are the built-in (and any custom) dictionaries.
            List<string> languages = new List<string> { LANG_DEFAULT };
            languages.AddRange(GetDictionaries(LANG_FOLDER, LANG_EXT));
            return languages;
        }

        // The built-in dictionaries compiled into the exe, plus any custom .xaml the user dropped
        // in a Themes/Languages folder next to the exe (single-file apps extract to a temp dir, so
        // we look beside the real executable, not in BaseDirectory).
        private List<string> GetDictionaries(string dictFolder, string dictExtension)
        {
            List<string> dictionaries = new List<string>(BuiltInNames(dictFolder));

            string exeDir = Path.GetDirectoryName(ExePath.GetExecutablePath()) ?? AppDomain.CurrentDomain.BaseDirectory;
            string customDictDir = Path.Combine(exeDir, dictFolder);

            if (Directory.Exists(customDictDir))
            {
                foreach (string subStr in Directory.GetFiles(customDictDir)
                    .Where(s => Path.GetExtension(s).Contains(dictExtension) && !dictionaries.Contains(Path.GetFileNameWithoutExtension(s))))
                {
                    dictionaries.Add(Path.GetFileNameWithoutExtension(subStr));
                }
            }

            return dictionaries;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Language")
            {
                SetLanguageFromSettings();
            }
            if (e.PropertyName == "Theme")
            {
                SetThemeFromSettings();
            }
        }

        public void Dispose()
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
        }
    }
}