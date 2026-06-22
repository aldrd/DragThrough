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
            string dictFilePath;

            if (dictionary == dictDefault)
            {
                if (dictType == 0)
                {
                    ClearPreviousThemes();
                }
                dictFilePath = Path.ChangeExtension(Path.Combine(dictFolder, dictDefault), dictExtension);
            }
            else
            {
                dictFilePath = Path.ChangeExtension(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dictFolder, dictionary),
                                                    dictExtension);

                if (!File.Exists(dictFilePath))
                {
                    dictFilePath = // Custom dictionary in app directory
                        Path.ChangeExtension(Path.Combine(Path.GetDirectoryName(ExePath.GetExecutablePath()), dictFolder, dictionary),
                                             dictExtension);

                    if (!File.Exists(dictFilePath))
                    {
                        return;
                    }
                }
            }

            GetMergedDictionaries().Add(new ResourceDictionary()
            {
                Source = new Uri(dictFilePath, UriKind.RelativeOrAbsolute)
            });
        }

        public List<string> GetThemes()
        {
            // The hidden base theme is never user-selectable.
            return GetDictionaries(THEME_DEFAULT, THEME_FOLDER, THEME_EXT)
                .Where(t => t != THEME_BASE)
                .ToList();
        }

        public List<string> GetLanguages()
        {
            List<string> languages = new List<string> { LANG_DEFAULT };
            languages.AddRange(GetDictionaries(LANG_FALLBACK, LANG_FOLDER, LANG_EXT));
            return languages;
        }

        private List<string> GetDictionaries(string dictDefault, string dictFolder, string dictExtension)
        {
            List<string> dictionaries = new List<string> { dictDefault };

            foreach (string subStr in Directory.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dictFolder))
                                               .Where(s => Path.GetExtension(s).Contains(dictExtension)))
            {
                string name = Path.GetFileNameWithoutExtension(subStr);
                if (!dictionaries.Contains(name))
                {
                    dictionaries.Add(name);
                }
            }

            // Because ZombieBar is published as a single-file app, it gets extracted to a temp directory, so custom dictionaries won't be there.
            // Get the executable path to find the custom dictionaries directory when not a debug build.
            string customDictDir = Path.Combine(Path.GetDirectoryName(ExePath.GetExecutablePath()), dictFolder);

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