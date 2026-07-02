using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ManagedShell.Common.Logging;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Remembers whether the additional taskbar is shown on each virtual desktop, keyed by the
    /// desktop's GUID and persisted across restarts. Desktops without an explicit entry fall back to
    /// the global default (<see cref="Settings.ShowAdditionalTaskbar"/>), which the tray's
    /// "Show taskbar" toggle sets for every desktop at once.
    /// </summary>
    public class DesktopTaskbarVisibility
    {
        private const string FileName = "DesktopVisibility.csv";

        // desktop GUID -> visible. Only desktops the user explicitly toggled are stored.
        private readonly Dictionary<Guid, bool> _overrides = new();

        public DesktopTaskbarVisibility()
        {
            Load();
        }

        /// <summary>Whether the taskbar should be shown on the given desktop.</summary>
        public bool IsVisibleOn(Guid desktop)
        {
            if (_overrides.TryGetValue(desktop, out bool visible))
                return visible;

            return Settings.Instance.ShowAdditionalTaskbar;
        }

        /// <summary>Sets the visibility for a single desktop (the tray's "on this desktop" toggle).</summary>
        public void SetCurrent(Guid desktop, bool visible)
        {
            if (desktop == Guid.Empty)
            {
                // No virtual-desktop info: fall back to the global switch so the toggle still works.
                Settings.Instance.ShowAdditionalTaskbar = visible;
                return;
            }

            _overrides[desktop] = visible;
            Save();
        }

        /// <summary>
        /// Shows/hides on every desktop: sets the global default and clears all per-desktop overrides
        /// (the tray's "Show taskbar" toggle).
        /// </summary>
        public void SetAll(bool visible)
        {
            Settings.Instance.ShowAdditionalTaskbar = visible;
            _overrides.Clear();
            Save();
        }

        #region Persistence

        private static string GetFilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ZombieBar");
            return Path.Combine(dir, FileName);
        }

        private void Load()
        {
            try
            {
                string path = GetFilePath();
                if (!File.Exists(path))
                    return;

                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    // Format: <desktop guid>,<0|1>
                    int comma = line.LastIndexOf(',');
                    if (comma <= 0)
                        continue;

                    if (Guid.TryParse(line.Substring(0, comma), out Guid desktop))
                    {
                        string flag = line.Substring(comma + 1).Trim();
                        _overrides[desktop] = flag == "1";
                    }
                }
            }
            catch (Exception ex)
            {
                ShellLogger.Error("DesktopTaskbarVisibility: Unable to load visibility file.", ex);
            }
        }

        private void Save()
        {
            try
            {
                string path = GetFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                StringBuilder sb = new();
                foreach (KeyValuePair<Guid, bool> kv in _overrides)
                {
                    sb.Append(kv.Key.ToString());
                    sb.Append(',');
                    sb.Append(kv.Value ? '1' : '0');
                    sb.Append("\r\n");
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ShellLogger.Error("DesktopTaskbarVisibility: Unable to save visibility file.", ex);
            }
        }

        #endregion
    }
}
