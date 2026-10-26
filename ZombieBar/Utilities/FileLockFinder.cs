#nullable enable
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Finds the processes that keep a file or folder in use. Every running process the app can open
    /// is checked, in stages (cheapest first):
    /// <list type="number">
    /// <item>its executable, loaded modules (DLLs, .node add-ons) and working folder;</item>
    /// <item>its open file and folder handles (the system handle list; names are read on a watchdog
    /// thread, because asking for the name of some pipe handles never returns);</item>
    /// <item>File Explorer windows showing the folder;</item>
    /// <item>the Restart Manager over every file inside (likely-locked types first), which also names
    /// services and applications the way Windows' own dialogs do.</item>
    /// </list>
    /// Processes that can't be opened (system services, programs running as administrator unless this
    /// process is elevated) are listed in <see cref="ScanResult.Skipped"/>. A cancelled scan returns
    /// what it found so far.
    /// </summary>
    public static class FileLockFinder
    {
        public enum LockReason { FileOpen, FilesInside, FolderOpen, WorkingFolder, Executable, ModuleLoaded, ExplorerWindow }

        public enum ScanStage { Processes, Handles, Explorer, ListingFiles, Files }

        public readonly record struct ScanProgress(ScanStage Stage, int Done, int Total, int Found);

        public sealed class LockingProcess
        {
            public int Pid { get; set; }
            public string Name { get; set; } = "";
            public string? ExePath { get; set; }
            public string? ServiceName { get; set; }
            public HashSet<LockReason> Reasons { get; set; } = new();
            /// <summary>A few of the files or folders it uses (full paths).</summary>
            public List<string> Items { get; set; } = new();
        }

        public sealed class SkippedProcess
        {
            public int Pid { get; set; }
            public string Name { get; set; } = "";
            public bool SameSession { get; set; }
        }

        public sealed class ScanResult
        {
            public string Path { get; set; } = "";
            public bool IsFolder { get; set; }
            public bool Elevated { get; set; }
            public bool Cancelled { get; set; }
            public bool HandleScanFailed { get; set; }
            public List<LockingProcess> Processes { get; set; } = new();
            public List<SkippedProcess> Skipped { get; set; } = new();
            /// <summary>Names of running antivirus / search-indexer processes (they open files briefly).</summary>
            public List<string> Scanners { get; set; } = new();
            public int FilesFound { get; set; }
            public int FilesChecked { get; set; }
            /// <summary>Set when the folder has more files than are listed; <see cref="FilesFound"/> is the cap.</summary>
            public bool FileListCapped { get; set; }
        }

        public const int MaxFolderFiles = 300_000;
        private const int RmBatchSize = 1000;
        private const int MaxItemsPerProcess = 3;

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>Scans <paramref name="path"/>. Throws only if the path doesn't exist.</summary>
        public static ScanResult Scan(string path, IProgress<ScanProgress>? progress = null, CancellationToken token = default)
        {
            path = System.IO.Path.GetFullPath(path);
            bool isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path))
                throw new FileNotFoundException(null, path);

            var scan = new Scanner(path, isFolder, progress, token);
            try
            {
                scan.Processes();
                scan.Handles();
                if (isFolder)
                    scan.ExplorerWindows();
                scan.Files();
            }
            catch (OperationCanceledException)
            {
                scan.Result.Cancelled = true;
            }
            return scan.Finish();
        }

        private sealed class Scanner
        {
            public readonly ScanResult Result;
            private readonly string _path;
            private readonly string[] _forms;
            private readonly bool _isFolder;
            private readonly IProgress<ScanProgress>? _progress;
            private readonly CancellationToken _token;
            private readonly int _self = Environment.ProcessId;
            private readonly int _session = Process.GetCurrentProcess().SessionId;
            private readonly Dictionary<int, LockingProcess> _found = new();
            private readonly Dictionary<int, SkippedProcess> _skipped = new();
            private readonly Dictionary<int, (string Name, int Session)> _processes = new();
            private readonly Stopwatch _sinceReport = Stopwatch.StartNew();

            public Scanner(string path, bool isFolder, IProgress<ScanProgress>? progress, CancellationToken token)
            {
                _path = path.TrimEnd('\\');
                _isFolder = isFolder;
                _progress = progress;
                _token = token;
                Result = new ScanResult { Path = path, IsFolder = isFolder, Elevated = IsElevated };

                // Handle and module names come back with long names and the real casing; the path
                // may have been given with 8.3 names, through a junction, etc. Match all forms.
                var forms = new List<string> { _path };
                if (GetFinalPath(path, isFolder) is { } final && !forms.Contains(final, StringComparer.OrdinalIgnoreCase))
                    forms.Add(final.TrimEnd('\\'));
                _forms = forms.ToArray();

                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            int session = -1;
                            try { session = p.SessionId; } catch { }
                            _processes[p.Id] = (p.ProcessName + ".exe", session);
                        }
                        catch { }
                    }
                }
                Result.Scanners = _processes.Values.Select(v => v.Name)
                    .Where(n => ScannerProcessNames.Contains(System.IO.Path.GetFileNameWithoutExtension(n)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            }

            // True if p is the target (file) or the target folder / something inside it.
            private bool Matches(string? p)
            {
                if (string.IsNullOrEmpty(p))
                    return false;
                p = p.TrimEnd('\\');
                foreach (string f in _forms)
                {
                    if (string.Equals(p, f, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (_isFolder && p.Length > f.Length && p[f.Length] == '\\' && p.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            private void Report(ScanStage stage, int done, int total, bool force = false)
            {
                _token.ThrowIfCancellationRequested();
                if (_progress == null || !force && _sinceReport.ElapsedMilliseconds < 100)
                    return;
                _sinceReport.Restart();
                _progress.Report(new ScanProgress(stage, done, total, _found.Count));
            }

            private void Add(int pid, LockReason reason, string? item, string? appName = null, string? service = null)
            {
                if (pid == _self || pid == 0)
                    return;
                if (!_found.TryGetValue(pid, out LockingProcess? p))
                {
                    string? exe = GetImagePath(pid);
                    string name = !string.IsNullOrWhiteSpace(appName) ? appName!
                                : exe != null ? System.IO.Path.GetFileName(exe)
                                : _processes.TryGetValue(pid, out var info) ? info.Name
                                : pid.ToString();
                    p = new LockingProcess { Pid = pid, Name = name, ExePath = exe };
                    _found[pid] = p;
                }
                if (!string.IsNullOrEmpty(service))
                    p.ServiceName ??= service;
                p.Reasons.Add(reason);
                if (item != null && p.Items.Count < MaxItemsPerProcess && !p.Items.Contains(item, StringComparer.OrdinalIgnoreCase))
                    p.Items.Add(item);
            }

            private void Skip(int pid)
            {
                if (pid == _self || pid == 0 || _skipped.ContainsKey(pid))
                    return;
                var info = _processes.TryGetValue(pid, out var i) ? i : (Name: pid.ToString(), Session: -1);
                _skipped[pid] = new SkippedProcess { Pid = pid, Name = info.Name, SameSession = info.Session == _session };
            }

            // --- 1. Executables, modules, working folders ---------------------------------------------
            public void Processes()
            {
                int[] pids = _processes.Keys.Where(p => p != _self && p != 0).ToArray();
                for (int i = 0; i < pids.Length; i++)
                {
                    Report(ScanStage.Processes, i, pids.Length);
                    int pid = pids[i];

                    string? exe = GetImagePath(pid);
                    if (Matches(exe))
                        Add(pid, LockReason.Executable, exe);

                    IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        Skip(pid);
                        continue;
                    }
                    try
                    {
                        foreach (string module in GetModules(h))
                            if (Matches(module) && !string.Equals(module, exe, StringComparison.OrdinalIgnoreCase))
                                Add(pid, LockReason.ModuleLoaded, module);

                        string? cwd = ReadProcessParametersString(h, 0x38, 0x24);
                        if (Matches(cwd))
                            Add(pid, LockReason.WorkingFolder, cwd!.TrimEnd('\\'));
                    }
                    finally { CloseHandle(h); }
                }
                Report(ScanStage.Processes, pids.Length, pids.Length, force: true);
            }

            // --- 2. Open file and folder handles ------------------------------------------------------
            public void Handles()
            {
                Report(ScanStage.Handles, 0, 0, force: true);
                Dictionary<int, List<(IntPtr Handle, uint Access)>>? byPid = GetFileHandles();
                if (byPid == null)
                {
                    Result.HandleScanFailed = true;
                    return;
                }

                int[] pids = byPid.Keys.Where(p => p != _self && p != 0).OrderBy(p => p).ToArray();
                var worker = new HandleNameWorker();
                int timeouts = 0;
                try
                {
                    for (int i = 0; i < pids.Length; i++)
                    {
                        Report(ScanStage.Handles, i, pids.Length);
                        int pid = pids[i];
                        IntPtr process = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                        if (process == IntPtr.Zero)
                        {
                            Skip(pid);
                            continue;
                        }
                        try
                        {
                            foreach ((IntPtr handle, uint access) in byPid[pid])
                            {
                                _token.ThrowIfCancellationRequested();
                                // After repeated hangs, stop asking about handles opened the way
                                // synchronous pipes usually are.
                                if (timeouts >= MaxNameTimeouts && PipeLikeAccess.Contains(access))
                                    continue;
                                if (!DuplicateHandle(process, handle, GetCurrentProcess(), out IntPtr dup, 0, false, DUPLICATE_SAME_ACCESS))
                                    continue;

                                string? name = worker.Query(dup, out bool timedOut);
                                if (timedOut)
                                {
                                    timeouts++;
                                    worker = new HandleNameWorker();   // the old one stays stuck; it's a background thread
                                    continue;
                                }
                                if (!Matches(name))
                                    continue;

                                bool folder = Directory.Exists(name);
                                Add(pid, !_isFolder ? LockReason.FileOpen : folder ? LockReason.FolderOpen : LockReason.FilesInside, name);
                            }
                        }
                        finally { CloseHandle(process); }
                    }
                }
                finally { worker.Dispose(); }
                Report(ScanStage.Handles, pids.Length, pids.Length, force: true);
            }

            // --- 3. File Explorer windows ---------------------------------------------------------------
            public void ExplorerWindows()
            {
                Report(ScanStage.Explorer, 0, 0, force: true);
                var hits = new List<(int Pid, string Path)>();
                var thread = new Thread(() =>
                {
                    try
                    {
                        NativeMethods.EnumWindows((hwnd, _) =>
                        {
                            if (ExplorerPathHelper.IsFileExplorerWindow(hwnd) && ExplorerPathHelper.GetPath(hwnd) is { } p
                                && Matches(p) && NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) != 0)
                                hits.Add(((int)pid, p));
                            return true;
                        }, 0);
                    }
                    catch { }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                if (!thread.Join(TimeSpan.FromSeconds(10)))
                    return;
                foreach ((int pid, string p) in hits)
                    Add(pid, LockReason.ExplorerWindow, p);
            }

            // --- 4. Restart Manager -----------------------------------------------------------------
            public void Files()
            {
                List<string> files;
                if (_isFolder)
                {
                    files = ListFolderFiles();
                    Result.FilesFound = files.Count;
                }
                else
                {
                    files = new List<string> { Result.Path };
                    Result.FilesFound = 1;
                }

                for (int i = 0; i < files.Count; i += RmBatchSize)
                {
                    Report(ScanStage.Files, i, files.Count);
                    string[] batch = files.Skip(i).Take(RmBatchSize).ToArray();
                    foreach (RmProcess rp in RmGetLockers(batch))
                        Add(rp.Pid, _isFolder ? LockReason.FilesInside : LockReason.FileOpen, null, rp.AppName, rp.Service);
                    Result.FilesChecked = Math.Min(files.Count, i + batch.Length);
                }
                Report(ScanStage.Files, files.Count, files.Count, force: true);
            }

            private List<string> ListFolderFiles()
            {
                var files = new List<string>();
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                try
                {
                    foreach (string f in Directory.EnumerateFiles(Result.Path, "*", options))
                    {
                        files.Add(f);
                        if (files.Count % 500 == 0)
                            Report(ScanStage.ListingFiles, files.Count, 0);
                        if (files.Count >= MaxFolderFiles)
                        {
                            Result.FileListCapped = true;
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ShellLogger.Info($"FileLockFinder: enumerating {Result.Path} stopped early: {ex.Message}");
                }

                // Files a program typically keeps open or mapped go first, so they are covered even if
                // the user stops a long check.
                return files.Where(f => LikelyLockedExtensions.Contains(System.IO.Path.GetExtension(f)))
                            .Concat(files.Where(f => !LikelyLockedExtensions.Contains(System.IO.Path.GetExtension(f))))
                            .ToList();
            }

            public ScanResult Finish()
            {
                Result.Processes = _found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                Result.Skipped = _skipped.Values.Where(s => !_found.ContainsKey(s.Pid))
                    .OrderByDescending(s => s.SameSession).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return Result;
            }
        }

        private static readonly HashSet<string> LikelyLockedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".node", ".asar", ".sys", ".ocx", ".pyd", ".so", ".jar", ".db", ".sqlite", ".lock", ".log", ".pak", ".dat"
        };

        // Antivirus and search-indexer processes: they open files for a moment and run as SYSTEM.
        private static readonly HashSet<string> ScannerProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MsMpEng", "NisSrv", "MpDefenderCoreService", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost",
            "avp", "ekrn", "bdservicehost", "vsserv", "AvastSvc", "AVGSvc", "mcshield", "mfemms", "ccSvcHst",
            "SavService", "SophosFileScanner", "WRSA", "CSFalconService", "SentinelAgent", "dwservice", "avguard",
        };

        // --- Modules ---------------------------------------------------------------------------------
        private static List<string> GetModules(IntPtr process)
        {
            var result = new List<string>();
            try
            {
                var modules = new IntPtr[512];
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int bytes = modules.Length * IntPtr.Size;
                    if (!EnumProcessModulesEx(process, modules, bytes, out int needed, LIST_MODULES_ALL))
                        return result;
                    if (needed > bytes)
                    {
                        modules = new IntPtr[needed / IntPtr.Size + 64];
                        continue;
                    }

                    var sb = new StringBuilder(1024);
                    for (int i = 0; i < needed / IntPtr.Size; i++)
                    {
                        sb.Clear();
                        if (GetModuleFileNameEx(process, modules[i], sb, sb.Capacity) > 0)
                            result.Add(sb.ToString());
                    }
                    break;
                }
            }
            catch { }
            return result;
        }

        // --- System handle list ----------------------------------------------------------------------
        // Groups the handles of all processes whose object type is "File" (files, folders, pipes,
        // devices). The type's index is found from a handle this process opens on purpose.
        private static Dictionary<int, List<(IntPtr Handle, uint Access)>>? GetFileHandles()
        {
            using FileStream probe = new(Environment.ProcessPath ?? typeof(FileLockFinder).Assembly.Location,
                                         FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            IntPtr probeHandle = probe.SafeFileHandle.DangerousGetHandle();
            int self = Environment.ProcessId;

            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 8 * 1024 * 1024;
                int status = 0;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                    if (status != STATUS_INFO_LENGTH_MISMATCH)
                        break;
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    size = Math.Max(size * 2, needed + 1024 * 1024);
                    if (size > 1024 * 1024 * 1024)
                        return null;
                }
                if (status != 0 || buffer == IntPtr.Zero)
                {
                    ShellLogger.Info($"FileLockFinder: handle list unavailable (0x{status:X8})");
                    return null;
                }

                int p = IntPtr.Size;
                int entrySize = 3 * p + 16;
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                IntPtr first = buffer + 2 * p;

                int fileType = -1;
                for (long i = 0; i < count && fileType < 0; i++)
                {
                    IntPtr e = first + (int)(i * entrySize);
                    if (Marshal.ReadIntPtr(e, p).ToInt64() == self && Marshal.ReadIntPtr(e, 2 * p) == probeHandle)
                        fileType = (ushort)Marshal.ReadInt16(e, 3 * p + 6);
                }
                if (fileType < 0)
                    return null;

                var byPid = new Dictionary<int, List<(IntPtr, uint)>>();
                for (long i = 0; i < count; i++)
                {
                    IntPtr e = first + (int)(i * entrySize);
                    if ((ushort)Marshal.ReadInt16(e, 3 * p + 6) != fileType)
                        continue;
                    int pid = (int)Marshal.ReadIntPtr(e, p).ToInt64();
                    if (!byPid.TryGetValue(pid, out var list))
                        byPid[pid] = list = new List<(IntPtr, uint)>();
                    list.Add((Marshal.ReadIntPtr(e, 2 * p), (uint)Marshal.ReadInt32(e, 3 * p)));
                }
                return byPid;
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"FileLockFinder: handle list failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        private const int MaxNameTimeouts = 10;
        private const int NameTimeoutMs = 300;

        // Access masks of synchronous named pipes and similar objects whose name query can block.
        private static readonly HashSet<uint> PipeLikeAccess = new() { 0x0012019F, 0x001A019F, 0x00120189, 0x00100000, 0x0012008D };

        /// <summary>
        /// Reads handle names on its own thread. GetFileType / GetFinalPathNameByHandle wait for any
        /// synchronous I/O pending on the object (a pipe being read), possibly forever; a query that
        /// takes too long is abandoned along with the thread.
        /// </summary>
        private sealed class HandleNameWorker : IDisposable
        {
            private readonly AutoResetEvent _request = new(false);
            private readonly AutoResetEvent _done = new(false);
            private IntPtr _handle;
            private string? _name;
            private volatile bool _stop;

            public HandleNameWorker()
            {
                var thread = new Thread(Run) { IsBackground = true, Name = "Handle names" };
                thread.Start();
            }

            /// <summary>Takes ownership of <paramref name="handle"/> (it is closed here or by the worker).</summary>
            public string? Query(IntPtr handle, out bool timedOut)
            {
                _handle = handle;
                _request.Set();
                timedOut = !_done.WaitOne(NameTimeoutMs);
                return timedOut ? null : _name;
            }

            private void Run()
            {
                while (true)
                {
                    _request.WaitOne();
                    if (_stop)
                        return;
                    IntPtr h = _handle;
                    string? name = null;
                    try
                    {
                        if (GetFileType(h) == FILE_TYPE_DISK)
                            name = GetFinalPath(h);
                    }
                    catch { }
                    finally { CloseHandle(h); }
                    _name = name;
                    _done.Set();
                }
            }

            public void Dispose()
            {
                _stop = true;
                _request.Set();
            }
        }

        private static string? GetFinalPath(IntPtr handle)
        {
            var sb = new StringBuilder(1024);
            int length = GetFinalPathNameByHandle(handle, sb, sb.Capacity, 0);
            if (length > sb.Capacity)
            {
                sb = new StringBuilder(length + 1);
                length = GetFinalPathNameByHandle(handle, sb, sb.Capacity, 0);
            }
            if (length <= 0 || length > sb.Capacity)
                return null;
            string s = sb.ToString();
            if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + s.Substring(8);
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
                return s.Substring(4);
            return s;
        }

        private static string? GetFinalPath(string path, bool isFolder)
        {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, isFolder ? FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
                return null;
            try { return GetFinalPath(h); }
            finally { CloseHandle(h); }
        }

        // --- Restart Manager -----------------------------------------------------------------------
        private readonly record struct RmProcess(int Pid, string AppName, string Service);

        private static List<RmProcess> RmGetLockers(IReadOnlyList<string> files)
        {
            var found = new List<RmProcess>();
            if (files.Count == 0)
                return found;

            var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
            int rc = RmStartSession(out uint session, 0, key);
            if (rc != 0)
            {
                ShellLogger.Info($"FileLockFinder: RmStartSession failed ({rc})");
                return found;
            }

            try
            {
                rc = RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null);
                if (rc != 0)
                {
                    ShellLogger.Info($"FileLockFinder: RmRegisterResources failed ({rc})");
                    return found;
                }

                uint needed = 0, count = 0, reasons = 0;
                RM_PROCESS_INFO[]? infos = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                    if (rc != ERROR_MORE_DATA)
                        break;
                    count = needed;
                    infos = new RM_PROCESS_INFO[needed];
                }
                if (rc != 0)
                {
                    ShellLogger.Info($"FileLockFinder: RmGetList failed ({rc})");
                    return found;
                }

                for (int i = 0; i < count && infos != null; i++)
                    found.Add(new RmProcess(infos[i].Process.dwProcessId, infos[i].strAppName, infos[i].strServiceShortName));
            }
            finally
            {
                RmEndSession(session);
            }

            return found;
        }

        // --- Process details -------------------------------------------------------------------------
        public static string? GetImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>The process's working folder, or null if it can't be read.</summary>
        public static string? GetCurrentDirectory(int pid) => ReadProcessParametersString(pid, 0x38, 0x24);

        /// <summary>The process's command line, or null if it can't be read.</summary>
        public static string? GetCommandLine(int pid) => ReadProcessParametersString(pid, 0x70, 0x40);

        /// <summary>The id of the process that started this one (it may have exited since), or 0.</summary>
        public static int GetParentPid(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return 0;
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                return NtQueryInformationProcess(h, ProcessBasicInformation, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                    ? (int)pbi.InheritedFromUniqueProcessId.ToInt64()
                    : 0;
            }
            catch { return 0; }
            finally { CloseHandle(h); }
        }

        private static string? ReadProcessParametersString(int pid, int offset64, int offset32)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero)
                return null;
            try { return ReadProcessParametersString(h, offset64, offset32); }
            finally { CloseHandle(h); }
        }

        // Reads a UNICODE_STRING field of the target's RTL_USER_PROCESS_PARAMETERS (via its PEB): the
        // working folder is CurrentDirectory.DosPath, the command line is CommandLine. Needs a handle
        // with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ; returns null for anything it can't read.
        private static string? ReadProcessParametersString(IntPtr h, int offset64, int offset32)
        {
            if (!Environment.Is64BitProcess)
                return null;
            try
            {
                if (NtQueryInformationProcess(h, ProcessWow64Information, out IntPtr peb32, IntPtr.Size, out _) == 0
                    && peb32 != IntPtr.Zero)
                {
                    // 32-bit process: PEB32.ProcessParameters at 0x10.
                    long parameters = ReadUInt32(h, peb32.ToInt64() + 0x10);
                    return parameters == 0 ? null : ReadUnicodeString32(h, parameters + offset32);
                }

                var pbi = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(h, ProcessBasicInformation, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0
                    || pbi.PebBaseAddress == IntPtr.Zero)
                    return null;

                // 64-bit process: PEB.ProcessParameters at 0x20.
                long parameters64 = ReadInt64(h, pbi.PebBaseAddress.ToInt64() + 0x20);
                return parameters64 == 0 ? null : ReadUnicodeString64(h, parameters64 + offset64);
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? Read(IntPtr h, long address, int size)
        {
            var buffer = new byte[size];
            return ReadProcessMemory(h, new IntPtr(address), buffer, size, out IntPtr read) && read.ToInt64() == size ? buffer : null;
        }

        private static long ReadInt64(IntPtr h, long address) => Read(h, address, 8) is { } b ? BitConverter.ToInt64(b, 0) : 0;
        private static long ReadUInt32(IntPtr h, long address) => Read(h, address, 4) is { } b ? BitConverter.ToUInt32(b, 0) : 0;

        private static string? ReadUnicodeString64(IntPtr h, long address)
        {
            byte[]? us = Read(h, address, 16);                 // USHORT Length, USHORT MaxLength, pad, PWSTR Buffer
            if (us == null) return null;
            int length = BitConverter.ToUInt16(us, 0);
            long buffer = BitConverter.ToInt64(us, 8);
            return ReadChars(h, buffer, length);
        }

        private static string? ReadUnicodeString32(IntPtr h, long address)
        {
            byte[]? us = Read(h, address, 8);                  // USHORT Length, USHORT MaxLength, ULONG Buffer
            if (us == null) return null;
            int length = BitConverter.ToUInt16(us, 0);
            long buffer = BitConverter.ToUInt32(us, 4);
            return ReadChars(h, buffer, length);
        }

        private static string? ReadChars(IntPtr h, long buffer, int byteLength)
        {
            if (buffer == 0 || byteLength <= 0 || byteLength > 32767 * 2)
                return null;
            byte[]? chars = Read(h, buffer, byteLength);
            return chars == null ? null : Encoding.Unicode.GetString(chars);
        }

        /// <summary>Enables SeDebugPrivilege (elevated scan only), so that more processes can be opened.</summary>
        public static void EnableDebugPrivilege()
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                    return;
                try
                {
                    if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out long luid))
                        return;
                    var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                finally { CloseHandle(token); }
            }
            catch { }
        }

        // --- Interop ---------------------------------------------------------------------------------
        private const int CCH_RM_SESSION_KEY = 32;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;
        private const int ERROR_MORE_DATA = 234;

        private const int PROCESS_DUP_HANDLE = 0x0040;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ProcessBasicInformation = 0;
        private const int ProcessWow64Information = 26;
        private const int SystemExtendedHandleInformation = 64;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        private const uint DUPLICATE_SAME_ACCESS = 0x2;
        private const int FILE_TYPE_DISK = 1;
        private const int LIST_MODULES_ALL = 3;
        private const uint FILE_SHARE_ALL = 0x7;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
        private const int TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const int TOKEN_QUERY = 0x8;
        private const int SE_PRIVILEGE_ENABLED = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll")]
        private static extern int GetFileType(IntPtr hFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern int GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, int cchFilePath, int dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, int cb, out int lpcbNeeded, int dwFilterFlag);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameExW")]
        private static extern int GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, int nSize);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            out IntPtr processInformation, int processInformationLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
            int bufferLength, IntPtr previousState, IntPtr returnLength);
    }
}
