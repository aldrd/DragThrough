#nullable enable
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ManagedShell.Common.Logging;
using Microsoft.Win32;

namespace DragThrough.Utilities
{
    /// <summary>
    /// Carries an install of the app from its former name, ZombieBar (up to 1.0.30.0), over to DragThrough.
    /// <para>
    /// The installer handles a fresh run of DragThrough-Setup.exe itself, but the in-app updater of an old
    /// version simply drops the new exe over its own ZombieBar.exe. Started under that name, the app copies
    /// itself to DragThrough.exe and hands over to the copy, which removes ZombieBar.exe and repoints the
    /// run-at-sign-in entry and shortcuts. On any start, the data folders and settings are moved across
    /// before anything reads them. Every step is best-effort: a failure leaves the app running under the
    /// old name or with default settings, never prevents it from starting.
    /// </para>
    /// </summary>
    internal static class LegacyMigration
    {
        internal const string LegacyName = "ZombieBar";
        internal const string AppName = "DragThrough";
        internal const string LegacyExeName = LegacyName + ".exe";
        private const string ExeName = AppName + ".exe";

#if !MSSTORE
        // Passed to DragThrough.exe by the ZombieBar.exe that started it, followed by that process's id.
        private const string ReplacesArg = "--replaces";

        // Inno Setup's uninstall entry: the AppId from installer\DragThrough.iss plus "_is1".
        private const string UninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B6D3A7E2-3C4F-4A1E-9E2B-7F5A1C9D0E84}_is1";
#endif

        /// <summary>
        /// Runs the migration. Returns true when this process has handed over to DragThrough.exe; the
        /// caller must then exit straight away.
        /// </summary>
        public static bool Run(string[] commandLineArgs)
        {
            // Also catches a failure to load what a step needs, which surfaces here rather than in the step.
            try
            {
                return RunSteps(commandLineArgs);
            }
            catch (Exception e)
            {
                ShellLogger.Info($"LegacyMigration: {e.Message}");
                return false;
            }
        }

        private static bool RunSteps(string[] commandLineArgs)
        {
            string roamingRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

#if !MSSTORE
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                string dir = Path.GetDirectoryName(exe)!;
                string legacyExe = Path.Combine(dir, LegacyExeName);

                if (string.Equals(Path.GetFileName(exe), LegacyExeName, StringComparison.OrdinalIgnoreCase))
                {
                    // Settings are keyed on the exe path, so leave them to the DragThrough.exe instance.
                    if (TryHandOver(exe, Path.Combine(dir, ExeName), commandLineArgs))
                        return true;
                }
                else
                {
                    // The roaming folder (task order, per-desktop visibility) appears as soon as the app is
                    // used, so while it is still under the old name this is the first start after the rename.
                    bool replacing = TryGetReplacedProcess(commandLineArgs, out int replacedPid);
                    if (replacing || Directory.Exists(Path.Combine(roamingRoot, LegacyName)))
                    {
                        if (replacing)
                            RemoveLegacyExe(legacyExe, replacedPid);
                        FixShortcuts(legacyExe, exe);
                        FixUninstallIcon(legacyExe, exe);
                    }
                    FixRunEntry(legacyExe, exe);
                }
            }
#endif

            MoveDirectory(Path.Combine(roamingRoot, LegacyName), Path.Combine(roamingRoot, AppName));
            MoveDirectory(Path.Combine(localRoot, LegacyName, "Logs"), Path.Combine(localRoot, AppName, "Logs"));
            MigrateUserConfig(localRoot);
            return false;
        }

#if !MSSTORE
        // A single-file app keeps loading assemblies from its own exe as it goes, so the running exe must
        // stay where it is: copy it under the new name and let the copy remove this one once it's gone.
        private static bool TryHandOver(string legacyExe, string newExe, string[] commandLineArgs)
        {
            try
            {
                File.Copy(legacyExe, newExe, true);

                var startInfo = new ProcessStartInfo(newExe) { UseShellExecute = false };
                // Pass the arguments on, e.g. the updater's "--updated <pid>".
                foreach (string arg in commandLineArgs.Skip(1))
                    startInfo.ArgumentList.Add(arg);
                startInfo.ArgumentList.Add(ReplacesArg);
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(startInfo);
                return true;
            }
            catch (Exception e)
            {
                // E.g. a read-only install folder; keep running as ZombieBar.exe.
                ShellLogger.Info($"LegacyMigration: unable to hand over to {newExe}: {e.Message}");
                return false;
            }
        }

        private static bool TryGetReplacedProcess(string[] args, out int pid)
        {
            pid = 0;
            int idx = Array.IndexOf(args, ReplacesArg);
            return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out pid);
        }

        private static void RemoveLegacyExe(string legacyExe, int pid)
        {
            try { Process.GetProcessById(pid).WaitForExit(5000); }
            catch { /* already gone */ }

            for (int attempt = 0; File.Exists(legacyExe); attempt++)
            {
                try
                {
                    File.Delete(legacyExe);
                }
                catch (Exception e)
                {
                    if (attempt >= 10)
                    {
                        ShellLogger.Info($"LegacyMigration: unable to delete {legacyExe}: {e.Message}");
                        return;
                    }
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        private static bool PointsAt(string? value, string exe) =>
            value != null && value.Contains(exe, StringComparison.OrdinalIgnoreCase);

        // The app's own checkbox used to write a "ZombieBar" value while the installer wrote "DragThrough",
        // so both could be present and start two copies at sign-in. Fold them into the single value.
        private static void FixRunEntry(string legacyExe, string newExe)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AutoStart.RunKey, true);
                if (key == null)
                    return;

                bool enabled = false;
                if (key.GetValue(LegacyName) != null)
                {
                    key.DeleteValue(LegacyName);
                    enabled = true;
                }
                if (PointsAt(key.GetValue(AutoStart.RunValue) as string, legacyExe))
                    enabled = true;

                if (enabled)
                    key.SetValue(AutoStart.RunValue, $"\"{newExe}\"");
            }
            catch (Exception e)
            {
                ShellLogger.Info($"LegacyMigration: unable to update the run-at-sign-in entry: {e.Message}");
            }
        }

        // Start menu and desktop shortcuts made by the installer, and taskbar/Start pins made by the user.
        private static void FixShortcuts(string legacyExe, string newExe)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] folders =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned"),
            };

            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return;
                dynamic shell = Activator.CreateInstance(shellType)!;

                foreach (string folder in folders.Where(Directory.Exists))
                {
                    foreach (string lnk in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(lnk);
                            if (!string.Equals((string)shortcut.TargetPath, legacyExe, StringComparison.OrdinalIgnoreCase))
                                continue;

                            shortcut.TargetPath = newExe;
                            string icon = shortcut.IconLocation;
                            if (PointsAt(icon, legacyExe))
                                shortcut.IconLocation = icon.Replace(legacyExe, newExe, StringComparison.OrdinalIgnoreCase);
                            shortcut.Save();
                        }
                        catch (Exception e)
                        {
                            ShellLogger.Info($"LegacyMigration: unable to update shortcut {lnk}: {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ShellLogger.Info($"LegacyMigration: unable to update shortcuts: {e.Message}");
            }
        }

        // The icon shown in Settings > Apps for the installer build.
        private static void FixUninstallIcon(string legacyExe, string newExe)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey, true);
                if (key?.GetValue("DisplayIcon") is string icon && PointsAt(icon, legacyExe))
                    key.SetValue("DisplayIcon", icon.Replace(legacyExe, newExe, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                ShellLogger.Info($"LegacyMigration: unable to update the uninstall entry: {e.Message}");
            }
        }
#endif

        private static void MoveDirectory(string from, string to)
        {
            try
            {
                if (!Directory.Exists(from) || Directory.Exists(to))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
            }
            catch (Exception e)
            {
                ShellLogger.Info($"LegacyMigration: unable to move {from}: {e.Message}");
            }
        }

        /// <summary>
        /// The settings file lives at &lt;company&gt;\&lt;exe name&gt;_Path_&lt;hash of exe path&gt;\&lt;version&gt;\user.config and
        /// names its section after the settings class, so both the rename and the namespace change leave the
        /// app looking for a file that isn't there. Copy the newest old one across with its section renamed.
        /// </summary>
        private static void MigrateUserConfig(string localRoot)
        {
            const string legacySection = LegacyName + ".Properties.Settings";
            const string newSection = AppName + ".Properties.Settings";

            try
            {
                string config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
                DirectoryInfo? appDir = Directory.GetParent(config)?.Parent;
                DirectoryInfo? companyDir = appDir?.Parent;
                if (appDir == null || companyDir == null)
                    return;

                // Already has settings of its own (any version): Settings.Upgrade takes it from there.
                if (appDir.Exists && appDir.EnumerateFiles("user.config", SearchOption.AllDirectories)
                        .Any(f => File.ReadAllText(f.FullName).Contains(newSection)))
                    return;

                // Old builds used either the current company folder or one named after the app.
                string? legacy = new[] { companyDir.FullName, Path.Combine(localRoot, LegacyName) }
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateDirectories(d, LegacyName + "_*"))
                    .SelectMany(Directory.EnumerateDirectories)
                    .Select(d => (Path: Path.Combine(d, "user.config"),
                                  Version: Version.TryParse(Path.GetFileName(d), out Version? v) ? v : null))
                    .Where(c => c.Version != null && File.Exists(c.Path))
                    .OrderByDescending(c => c.Version)
                    .Select(c => c.Path)
                    .FirstOrDefault(p => File.ReadAllText(p).Contains(legacySection));
                if (legacy == null)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(config)!);
                File.WriteAllText(config, File.ReadAllText(legacy).Replace(legacySection, newSection));
            }
            catch (Exception e)
            {
                ShellLogger.Info($"LegacyMigration: unable to migrate settings: {e.Message}");
            }
        }
    }
}
