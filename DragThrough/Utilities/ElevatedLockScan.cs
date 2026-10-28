#nullable enable
using ManagedShell.Common.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DragThrough.Utilities
{
    /// <summary>
    /// Runs <see cref="FileLockFinder.Scan"/> in an elevated copy of this executable (UAC prompt), so
    /// that services and programs running as administrator are checked too. The elevated copy only
    /// scans and writes findings, progress and the result as JSON lines to a temporary file; it never
    /// starts the taskbar or tray.
    /// </summary>
    public static class ElevatedLockScan
    {
        private const string ScanArg = "--lock-scan";
        private const string ResultPrefix = "DragThrough-lockscan-";

        // One JSON object per line, so the caller can show findings while the elevated scan runs.
        public sealed class Line
        {
            public FileLockFinder.LockingProcess? Found { get; set; }
            public FileLockFinder.ScanProgress? Progress { get; set; }
            public FileLockFinder.ScanResult? Result { get; set; }
        }

        /// <summary>
        /// Starts the elevated scan and waits for it, passing on findings and progress as they come.
        /// Returns null if the user declined the UAC prompt; throws if the scan could not be run.
        /// Cancelling asks the elevated copy to stop; what it found until then is returned.
        /// </summary>
        public static async Task<FileLockFinder.ScanResult?> RunAsync(string path, CancellationToken token,
            Action<FileLockFinder.LockingProcess>? found = null, Action<FileLockFinder.ScanProgress>? progress = null)
        {
            string output = Path.Combine(Path.GetTempPath(), ResultPrefix + Guid.NewGuid().ToString("N") + ".jsonl");
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

            FileLockFinder.ScanResult? result = null;
            long offset = 0;
            var pending = new StringBuilder();

            void ReadNew()
            {
                if (!File.Exists(output))
                    return;
                try
                {
                    using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length <= offset)
                        return;
                    stream.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[stream.Length - offset];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    offset += read;
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
                catch (IOException) { return; }

                string text = pending.ToString();
                int last = text.LastIndexOf('\n');
                if (last < 0)
                    return;
                pending.Clear().Append(text, last + 1, text.Length - last - 1);
                foreach (string json in text.Substring(0, last).Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(json))
                        continue;
                    Line? line;
                    try { line = JsonSerializer.Deserialize<Line>(json); }
                    catch (JsonException) { continue; }
                    if (line?.Found != null) found?.Invoke(line.Found);
                    if (line?.Progress is { } pr) progress?.Invoke(pr);
                    if (line?.Result != null) result = line.Result;
                }
            }

            using (process)
            {
                try
                {
                    bool cancelSent = false;
                    var stopDeadline = DateTime.MaxValue;
                    while (!process.HasExited)
                    {
                        if (token.IsCancellationRequested && !cancelSent)
                        {
                            TryWrite(cancelFile);
                            cancelSent = true;
                            stopDeadline = DateTime.UtcNow.AddSeconds(10);
                        }
                        if (DateTime.UtcNow > stopDeadline)
                            throw new OperationCanceledException(token);
                        ReadNew();
                        await Task.Delay(250, CancellationToken.None);
                    }
                    ReadNew();
                }
                finally
                {
                    TryDelete(output);
                    TryDelete(cancelFile);
                }
            }

            if (result == null && token.IsCancellationRequested)
                throw new OperationCanceledException(token);
            return result ?? throw new InvalidOperationException("The elevated check produced no result.");
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
                || !string.Equals(Path.GetExtension(output), ".jsonl", StringComparison.OrdinalIgnoreCase)
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
                using var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete),
                                                    new UTF8Encoding(false)) { AutoFlush = true };
                var gate = new object();
                void Write(Line line)
                {
                    lock (gate)
                        writer.Write(JsonSerializer.Serialize(line) + "\n");
                }

                FileLockFinder.ScanResult result = FileLockFinder.Scan(path, new SyncProgress(p => Write(new Line { Progress = p })), cts.Token,
                                                                       p => Write(new Line { Found = p }));
                Write(new Line { Result = result });
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"ElevatedLockScan: {ex.Message}");
            }
            return true;
        }

        private const int ERROR_CANCELLED = 1223;

        // Reports on the calling thread (Progress<T> would post to the thread pool, out of order).
        private sealed class SyncProgress : IProgress<FileLockFinder.ScanProgress>
        {
            private readonly Action<FileLockFinder.ScanProgress> _report;
            public SyncProgress(Action<FileLockFinder.ScanProgress> report) => _report = report;
            public void Report(FileLockFinder.ScanProgress value) => _report(value);
        }

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
