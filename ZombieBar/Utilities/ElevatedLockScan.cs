#nullable enable
using ManagedShell.Common.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Runs <see cref="FileLockFinder.Scan"/> in an elevated copy of this executable (UAC prompt), so
    /// that services and programs running as administrator are checked too. The elevated copy only
    /// scans and writes the result to a temporary file; it never starts the taskbar or tray.
    /// </summary>
    public static class ElevatedLockScan
    {
        private const string ScanArg = "--lock-scan";
        private const string ResultPrefix = "ZombieBar-lockscan-";

        /// <summary>
        /// Starts the elevated scan and waits for it. Returns null if the user declined the UAC
        /// prompt; throws if the scan could not be run. Cancelling asks the elevated copy to stop.
        /// </summary>
        public static async Task<FileLockFinder.ScanResult?> RunAsync(string path, CancellationToken token)
        {
            string output = Path.Combine(Path.GetTempPath(), ResultPrefix + Guid.NewGuid().ToString("N") + ".json");
            string cancelFile = output + ".cancel";
            Process? process;
            try
            {
                process = Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No executable path"))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    ArgumentList = { ScanArg, path, output, Environment.ProcessId.ToString() }
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
            {
                return null;
            }
            if (process == null)
                throw new InvalidOperationException("The elevated process did not start.");

            using (process)
            {
                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    TryWrite(cancelFile);
                    throw;
                }
            }

            try
            {
                if (!File.Exists(output))
                    throw new InvalidOperationException("The elevated check produced no result.");
                return JsonSerializer.Deserialize<FileLockFinder.ScanResult>(await File.ReadAllTextAsync(output, CancellationToken.None))
                       ?? throw new InvalidOperationException("The elevated check produced no result.");
            }
            finally
            {
                TryDelete(output);
                TryDelete(cancelFile);
            }
        }

        /// <summary>
        /// Entry point for the elevated copy (<c>--lock-scan &lt;path&gt; &lt;result file&gt; &lt;caller pid&gt;</c>).
        /// Returns true if it handled the request (the caller must then exit without starting the app).
        /// </summary>
        public static bool TryRun(string[] args)
        {
            int idx = Array.IndexOf(args, ScanArg);
            if (idx < 0 || idx + 3 >= args.Length)
                return false;

            string path = args[idx + 1];
            string output = args[idx + 2];
            // Only ever write a result file of our own naming into the temporary folder.
            if (!Path.GetFileName(output).StartsWith(ResultPrefix, StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(args[idx + 3], out int callerPid))
                return true;

            using var cts = new CancellationTokenSource();
            string cancelFile = output + ".cancel";
            using var watch = new Timer(_ =>
            {
                bool callerGone;
                try { using Process p = Process.GetProcessById(callerPid); callerGone = p.HasExited; }
                catch { callerGone = true; }
                if (callerGone || File.Exists(cancelFile))
                    cts.Cancel();
            }, null, 500, 500);

            try
            {
                FileLockFinder.EnableDebugPrivilege();
                FileLockFinder.ScanResult result = FileLockFinder.Scan(path, null, cts.Token);
                string temp = output + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(result));
                File.Move(temp, output, true);
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"ElevatedLockScan: {ex.Message}");
            }
            return true;
        }

        private const int ERROR_CANCELLED = 1223;

        private static void TryWrite(string file)
        {
            try { File.WriteAllText(file, ""); } catch { }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
