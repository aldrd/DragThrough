#nullable enable
using ManagedShell.Common.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Checks a static JSON manifest for a newer release and, on request, installs it: downloads
    /// the self-contained .exe, verifies its SHA-256, swaps it in for the running executable and
    /// relaunches. The whole feature is compiled in only for the free GitHub build (AUTOUPDATE);
    /// the Microsoft Store build omits it (the Store handles updates) - see ZombieBar.csproj.
    /// </summary>
    public class Updater : IDisposable
    {
        // All external addresses live in AppLinks - see that class to configure your distribution.

        // Argument passed to the freshly installed instance so it waits for this (old) instance
        // to exit before registering its app bar.
        private const string UpdatedArg = "--updated";

        // Argument passed to an elevated copy of the new exe (read-only install folder case) so
        // it replaces the executable on behalf of the non-elevated app. Followed by the
        // destination exe path and the previous process id.
        private const string ApplyUpdateArg = "--apply-update";

        // Backup suffix for the replaced executable; cleaned up by the next launch.
        private const string BackupSuffix = ".old";

        // Name of the downloaded update, placed next to the exe (writable) or in %TEMP%.
        private const string UpdateFileName = "Ssz.TaskBar.update.exe";

        public bool IsUpdateAvailable { get; private set; }
        public Version? AvailableVersion { get; private set; }

        /// <summary>Raised (on the UI thread) when a newer version is found.</summary>
        public event EventHandler? UpdateAvailable;

        private string? _downloadUrl;
        private string? _expectedSha256;

        private readonly Version _currentVersion;
        private readonly HttpClient _httpClient;

        private readonly int _initialInterval = 10000;
        private readonly int _recheckInterval = 86400000;
        private System.Timers.Timer _updateCheck;

        public Updater()
        {
            _currentVersion = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version
                              ?? new Version(0, 0, 0, 0);

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Ssz.TaskBar-Updater");

            _updateCheck = new System.Timers.Timer(_initialInterval) { AutoReset = true };
            _updateCheck.Elapsed += UpdateCheck_Elapsed;
            _updateCheck.Start();
        }

        private async void UpdateCheck_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // First tick is shortly after startup; afterwards check at the slower interval.
            _updateCheck.Interval = _recheckInterval;

            if (!await CheckForUpdate())
            {
                return;
            }

            IsUpdateAvailable = true;
            _updateCheck.Stop();

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                UpdateAvailable?.Invoke(this, EventArgs.Empty)));
        }

        private async Task<bool> CheckForUpdate()
        {
            try
            {
                UpdateManifest? manifest = await _httpClient.GetFromJsonAsync<UpdateManifest>(AppLinks.PublishManifestUrl);

                if (manifest != null
                    && Version.TryParse(manifest.Version, out Version? newVersion)
                    && newVersion > _currentVersion
                    && !string.IsNullOrWhiteSpace(manifest.Url))
                {
                    _downloadUrl = manifest.Url;
                    _expectedSha256 = manifest.Sha256;
                    AvailableVersion = newVersion;
                    return true;
                }
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"Updater: Unable to check for updates: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Downloads the new executable, verifies its SHA-256, then replaces the running
        /// executable and starts the new one. When the install folder is writable this happens
        /// in-process; otherwise an elevated copy of the new exe is launched (UAC prompt) to do
        /// the swap. Returns true if an install was started and the caller should now shut down
        /// so the new instance can take over; false if nothing was changed.
        /// </summary>
        public async Task<bool> InstallUpdateAsync()
        {
            if (string.IsNullOrEmpty(_downloadUrl))
            {
                return false;
            }

            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
            {
                return false;
            }

            string appDir = Path.GetDirectoryName(currentExe)!;
            bool writable = IsDirectoryWritable(appDir);
            string downloadPath = Path.Combine(writable ? appDir : Path.GetTempPath(), UpdateFileName);

            try
            {
                using (HttpResponseMessage response =
                       await _httpClient.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using FileStream fs = File.Create(downloadPath);
                    await response.Content.CopyToAsync(fs);
                }

                if (!string.IsNullOrWhiteSpace(_expectedSha256)
                    && !VerifySha256(downloadPath, _expectedSha256!))
                {
                    ShellLogger.Info("Updater: SHA-256 of the downloaded update did not match the manifest; aborting.");
                    TryDelete(downloadPath);
                    return false;
                }

                if (writable)
                {
                    return SwapInProcess(currentExe, downloadPath);
                }

                // Read-only install folder (e.g. Program Files): hand off to an elevated copy of
                // the new exe that waits for us to exit, replaces the file and relaunches.
                return LaunchElevatedApplier(downloadPath, currentExe);
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"Updater: Failed to install the update: {ex.Message}");
                TryDelete(downloadPath);
                return false;
            }
        }

        // Writable install folder: rename the running exe aside and drop the new one in place
        // (a running exe can be moved but not overwritten), then relaunch.
        private static bool SwapInProcess(string currentExe, string newExe)
        {
            string backupPath = currentExe + BackupSuffix;
            try
            {
                TryDelete(backupPath);
                File.Move(currentExe, backupPath);
                File.Move(newExe, currentExe);

                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    UseShellExecute = true,
                    ArgumentList = { UpdatedArg, Environment.ProcessId.ToString() }
                });
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"Updater: in-process swap failed: {ex.Message}");

                // Roll back if we moved the running exe aside but didn't finish.
                try
                {
                    if (!File.Exists(currentExe) && File.Exists(backupPath))
                    {
                        File.Move(backupPath, currentExe);
                    }
                }
                catch { }

                TryDelete(newExe);
                return false;
            }
        }

        // Read-only install folder: launch the downloaded exe elevated (UAC) to apply the update.
        private static bool LaunchElevatedApplier(string newExe, string destExe)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = newExe,
                    UseShellExecute = true,
                    Verb = "runas", // triggers the UAC elevation prompt
                    ArgumentList = { ApplyUpdateArg, destExe, Environment.ProcessId.ToString() }
                });
                return true;
            }
            catch (Exception ex)
            {
                // Includes the user declining the UAC prompt (Win32Exception 1223).
                ShellLogger.Info($"Updater: elevation declined or failed: {ex.Message}");
                TryDelete(newExe);
                return false;
            }
        }

        /// <summary>
        /// Entry point for an elevated instance launched with <c>--apply-update</c>: waits for the
        /// previous (non-elevated) instance to exit, overwrites the destination executable and
        /// relaunches it de-elevated via Explorer. Returns true if it handled the apply request
        /// (the caller must then exit without starting the app); false for a normal launch.
        /// </summary>
        public static bool TryApplyElevatedUpdate(string[] args)
        {
            int idx = Array.IndexOf(args, ApplyUpdateArg);
            if (idx < 0 || idx + 2 >= args.Length)
            {
                return false;
            }

            string destExe = args[idx + 1];
            if (!int.TryParse(args[idx + 2], out int previousPid))
            {
                return false;
            }

            try { Process.GetProcessById(previousPid).WaitForExit(15000); }
            catch { /* already gone */ }

            try
            {
                string self = Environment.ProcessPath ?? "";

                // The previous process has exited so the destination is unlocked; retry briefly
                // in case the file handle lingers.
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(self, destExe, true); break; }
                    catch (IOException) when (attempt < 10) { System.Threading.Thread.Sleep(200); }
                }

                // Relaunch through Explorer so the new instance runs at normal (non-elevated)
                // integrity rather than inheriting this elevated token.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true,
                    ArgumentList = { destExe }
                });
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"Updater: elevated apply failed: {ex.Message}");
            }

            return true;
        }

        private static bool IsDirectoryWritable(string directory)
        {
            try
            {
                string probe = Path.Combine(directory, Path.GetRandomFileName());
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Called once at startup of a freshly installed instance: waits for the previous
        /// instance to exit (so app bars don't overlap) and removes the leftovers it left behind.
        /// </summary>
        public static void FinishPendingUpdate(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == UpdatedArg && int.TryParse(args[i + 1], out int previousPid))
                {
                    try { Process.GetProcessById(previousPid).WaitForExit(5000); }
                    catch { /* already gone */ }
                    break;
                }
            }

            TryDelete((Environment.ProcessPath ?? "") + BackupSuffix);
            TryDelete(Path.Combine(Path.GetTempPath(), UpdateFileName));
        }

        private static bool VerifySha256(string filePath, string expectedHex)
        {
            using FileStream fs = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(fs);
            string actualHex = Convert.ToHexString(hash);
            return string.Equals(actualHex, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        public void Dispose()
        {
            _updateCheck?.Stop();
            _updateCheck?.Dispose();
            _httpClient?.Dispose();
        }
    }

    public class UpdateManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }
}
