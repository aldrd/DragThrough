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
using System.Threading;
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

        /// <summary>Outcome of an update check.</summary>
        public enum UpdateCheckResult
        {
            UpToDate,
            UpdateAvailable,
            Failed
        }

        private async void UpdateCheck_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // First tick is shortly after startup; afterwards check at the slower interval.
            _updateCheck.Interval = _recheckInterval;
            await CheckForUpdatesNowAsync();
        }

        /// <summary>
        /// Checks the manifest right now (e.g. from the "Check for updates" button). When a newer
        /// version is found it records it and raises <see cref="UpdateAvailable"/> (on the UI
        /// thread) so the taskbar's update notification appears too.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdatesNowAsync()
        {
            UpdateCheckResult result = await CheckForUpdate();

            if (result == UpdateCheckResult.UpdateAvailable)
            {
                IsUpdateAvailable = true;
                _updateCheck.Stop();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    UpdateAvailable?.Invoke(this, EventArgs.Empty)));
            }

            return result;
        }

        private async Task<UpdateCheckResult> CheckForUpdate()
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
                    return UpdateCheckResult.UpdateAvailable;
                }

                return UpdateCheckResult.UpToDate;
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"Updater: Unable to check for updates: {ex.Message}");
                return UpdateCheckResult.Failed;
            }
        }

        /// <summary>Outcome of <see cref="InstallUpdateAsync"/>.</summary>
        public enum InstallResult
        {
            /// <summary>The new version is being installed; the caller should now shut down.</summary>
            Installing,
            /// <summary>The user cancelled the download.</summary>
            Cancelled,
            /// <summary>Nothing was installed (no update, network or verification error).</summary>
            Failed
        }

        /// <summary>
        /// Downloads the new executable (showing a progress window with a Cancel button), verifies
        /// its SHA-256, then replaces the running executable and starts the new one. When the
        /// install folder is writable this happens in-process; otherwise an elevated copy of the
        /// new exe is launched (UAC prompt) to do the swap.
        /// </summary>
        public async Task<InstallResult> InstallUpdateAsync()
        {
            if (string.IsNullOrEmpty(_downloadUrl))
            {
                return InstallResult.Failed;
            }

            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
            {
                return InstallResult.Failed;
            }

            string appDir = Path.GetDirectoryName(currentExe)!;
            bool writable = IsDirectoryWritable(appDir);
            string downloadPath = Path.Combine(writable ? appDir : Path.GetTempPath(), UpdateFileName);

            // The progress window is shown for every install - whether started from the About
            // window or the periodic update notification - so the (large) download is always visible.
            UpdateProgressWindow? progress = ShowProgressWindow();
            CancellationToken token = progress?.CancellationToken ?? CancellationToken.None;
            try
            {
                await DownloadWithProgressAsync(_downloadUrl, downloadPath, progress, token);

                if (!string.IsNullOrWhiteSpace(_expectedSha256)
                    && !VerifySha256(downloadPath, _expectedSha256!))
                {
                    ShellLogger.Info("Updater: SHA-256 of the downloaded update did not match the manifest; aborting.");
                    TryDelete(downloadPath);
                    ShowError(progress, "Checksum verification failed.");
                    return InstallResult.Failed;
                }

                InvokeOnWindow(progress, w => w.SetInstalling());

                bool swapped = writable
                    ? SwapInProcess(currentExe, downloadPath)
                    // Read-only install folder (e.g. Program Files): hand off to an elevated copy of
                    // the new exe that waits for us to exit, replaces the file and relaunches.
                    : LaunchElevatedApplier(downloadPath, currentExe);

                if (swapped)
                {
                    // The app is about to shut down and hand off to the new instance; the progress
                    // window goes away with it.
                    return InstallResult.Installing;
                }

                ShowError(progress, "Could not replace the program file.");
                return InstallResult.Failed;
            }
            catch (OperationCanceledException)
            {
                // User pressed Cancel - just close the window, no error.
                ShellLogger.Info("Updater: update download cancelled by the user.");
                TryDelete(downloadPath);
                CloseProgressWindow(progress);
                return InstallResult.Cancelled;
            }
            catch (Exception ex)
            {
                // Keep the window open showing why it failed (e.g. "404 Not Found"), so the failed
                // download isn't a silent flash.
                ShellLogger.Info($"Updater: Failed to install the update: {ex.Message}");
                TryDelete(downloadPath);
                ShowError(progress, ex.Message);
                return InstallResult.Failed;
            }
        }

        private static void ShowError(UpdateProgressWindow? window, string detail)
        {
            if (window == null)
            {
                return;
            }
            try { window.Dispatcher.Invoke(() => window.SetError(detail)); } catch { }
        }

        // Streams the download to disk in chunks, reporting progress to the window on each whole
        // percent. Using ResponseHeadersRead means HttpClient.Timeout doesn't bound the body, so a
        // large file on a slow connection is fine. Throws OperationCanceledException if cancelled.
        private async Task DownloadWithProgressAsync(string url, string destPath, UpdateProgressWindow? progress,
                                                     CancellationToken token)
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            using Stream source = await response.Content.ReadAsStreamAsync(token);
            using FileStream fs = File.Create(destPath);

            byte[] buffer = new byte[81920];
            long received = 0;
            int lastPercent = -1;
            int read;

            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;

                if (progress == null)
                {
                    continue;
                }

                // Marshal to the UI thread only when the whole-percent value changes.
                int percent = total > 0 ? (int)(received * 100 / total) : -1;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    long r = received, t = total;
                    progress.Dispatcher.Invoke(() => progress.SetProgress(r, t));
                }
            }
        }

        private static UpdateProgressWindow? ShowProgressWindow()
        {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return null;
            }

            return dispatcher.Invoke(() =>
            {
                var window = new UpdateProgressWindow();
                window.Show();
                window.Activate();
                return window;
            });
        }

        private static void InvokeOnWindow(UpdateProgressWindow? window, Action<UpdateProgressWindow> action)
        {
            if (window == null)
            {
                return;
            }
            try { window.Dispatcher.Invoke(() => action(window)); } catch { }
        }

        private static void CloseProgressWindow(UpdateProgressWindow? window)
        {
            if (window == null)
            {
                return;
            }
            try { window.Dispatcher.Invoke(window.Close); } catch { }
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
