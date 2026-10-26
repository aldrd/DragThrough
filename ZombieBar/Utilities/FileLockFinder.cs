#nullable enable
using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Finds the processes that keep a file or folder in use, without administrator rights.
    /// Files: the Restart Manager (the API behind Windows' own "file is open in ..." messages).
    /// Folders: the Restart Manager over the files inside (capped, likely-locked file types first), plus processes whose working
    /// folder or executable is inside the folder. Open handles to the folder itself are not visible
    /// this way (that needs a system-wide handle scan, which is unreliable without elevation).
    /// </summary>
    public static class FileLockFinder
    {
        public enum LockReason { FileOpen, FilesInside, WorkingFolder, Executable }

        public sealed class LockingProcess
        {
            public int Pid { get; init; }
            public string Name { get; init; } = "";
            public string? ExePath { get; init; }
            public string? ServiceName { get; init; }
            public HashSet<LockReason> Reasons { get; } = new();
        }

        public sealed class ScanResult
        {
            public string Path { get; init; } = "";
            public bool IsFolder { get; init; }
            public List<LockingProcess> Processes { get; } = new();
            /// <summary>Set when a large folder was only partly checked: the number of files checked.</summary>
            public int? TruncatedAt { get; set; }
        }

        public const int MaxFolderFiles = 5000;
        private const int RmBatchSize = 500;

        /// <summary>Scans <paramref name="path"/>. Throws on a missing path or a Restart Manager failure.</summary>
        public static ScanResult Scan(string path, CancellationToken token = default)
        {
            path = System.IO.Path.GetFullPath(path);
            bool isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path))
                throw new FileNotFoundException(null, path);

            var result = new ScanResult { Path = path, IsFolder = isFolder };
            var byPid = new Dictionary<int, LockingProcess>();
            int self = Environment.ProcessId;

            LockingProcess Get(int pid, string? appName, string? service)
            {
                if (!byPid.TryGetValue(pid, out LockingProcess? p))
                {
                    string? exe = GetImagePath(pid);
                    string name = !string.IsNullOrWhiteSpace(appName) ? appName!
                                : exe != null ? System.IO.Path.GetFileName(exe)
                                : SafeProcessName(pid) ?? pid.ToString();
                    p = new LockingProcess { Pid = pid, Name = name, ExePath = exe, ServiceName = string.IsNullOrEmpty(service) ? null : service };
                    byPid[pid] = p;
                }
                return p;
            }

            if (!isFolder)
            {
                foreach (RmProcess rp in RmGetLockers(new[] { path }))
                    if (rp.Pid != self) Get(rp.Pid, rp.AppName, rp.Service).Reasons.Add(LockReason.FileOpen);
            }
            else
            {
                List<string> files = SelectFolderFiles(path, token, out bool truncated);
                if (truncated)
                    result.TruncatedAt = MaxFolderFiles;

                foreach (RmProcess rp in RmGetLockers(files))
                    if (rp.Pid != self) Get(rp.Pid, rp.AppName, rp.Service).Reasons.Add(LockReason.FilesInside);

                token.ThrowIfCancellationRequested();
                ScanProcessesInFolder(path, self, (pid, reason) => Get(pid, null, null).Reasons.Add(reason), token);
            }

            result.Processes.AddRange(byPid.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
            return result;
        }

        // --- Folders: files inside, working folders, executables --------------------------------------
        // Files a running program typically keeps open or mapped (and so blocks moving/deleting the
        // folder): executables, libraries, native modules and packed app archives.
        private static readonly HashSet<string> LikelyLockedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".node", ".asar", ".sys", ".ocx", ".pyd", ".so", ".jar", ".db", ".sqlite", ".lock", ".log", ".pak", ".dat"
        };

        private const int MaxFolderEntriesListed = 100_000;

        // Picks up to MaxFolderFiles files to hand to the Restart Manager. For a large folder the files
        // most likely to be locked go first, so e.g. an application's "resources" folder with tens of
        // thousands of scripts still gets its native modules and archives checked.
        private static List<string> SelectFolderFiles(string folder, CancellationToken token, out bool truncated)
        {
            List<string> all = EnumerateFilesCapped(folder, MaxFolderEntriesListed, token);
            truncated = all.Count > MaxFolderFiles;
            if (!truncated)
                return all;

            return all.Where(f => LikelyLockedExtensions.Contains(System.IO.Path.GetExtension(f)))
                      .Concat(all.Where(f => !LikelyLockedExtensions.Contains(System.IO.Path.GetExtension(f))))
                      .Take(MaxFolderFiles)
                      .ToList();
        }

        private static List<string> EnumerateFilesCapped(string folder, int cap, CancellationToken token)
        {
            var files = new List<string>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try
            {
                foreach (string f in Directory.EnumerateFiles(folder, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    files.Add(f);
                    if (files.Count >= cap)
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ShellLogger.Info($"FileLockFinder: enumerating {folder} stopped early: {ex.Message}");
            }
            return files;
        }

        private static void ScanProcessesInFolder(string folder, int self, Action<int, LockReason> report, CancellationToken token)
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            bool Inside(string? p) => p != null
                && (p.TrimEnd('\\') + "\\").StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            int session = Process.GetCurrentProcess().SessionId;
            foreach (Process proc in Process.GetProcesses())
            {
                using (proc)
                {
                    token.ThrowIfCancellationRequested();
                    int pid;
                    try
                    {
                        pid = proc.Id;
                        if (pid == self || pid <= 4 || proc.SessionId != session)
                            continue;
                    }
                    catch { continue; }

                    if (Inside(GetImagePath(pid)))
                        report(pid, LockReason.Executable);
                    if (Inside(GetCurrentDirectory(pid)))
                        report(pid, LockReason.WorkingFolder);
                }
            }
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
                throw new Win32Exception(rc);

            try
            {
                for (int i = 0; i < files.Count; i += RmBatchSize)
                {
                    string[] batch = files.Skip(i).Take(RmBatchSize).ToArray();
                    rc = RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null);
                    if (rc != 0)
                        throw new Win32Exception(rc);
                }

                uint needed = 0, count = 0, reasons = 0;
                RM_PROCESS_INFO[]? infos = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                    if (rc == 0)
                        break;
                    if (rc != ERROR_MORE_DATA)
                        throw new Win32Exception(rc);
                    count = needed;
                    infos = new RM_PROCESS_INFO[needed];
                }
                if (rc != 0)
                    throw new Win32Exception(rc);

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

        private static string? SafeProcessName(int pid)
        {
            try { using Process p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
            catch { return null; }
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

        // Reads a UNICODE_STRING field of the target's RTL_USER_PROCESS_PARAMETERS (via its PEB): the
        // working folder is CurrentDirectory.DosPath, the command line is CommandLine. Works for
        // same-user, non-elevated processes; returns null for anything it can't read.
        private static string? ReadProcessParametersString(int pid, int offset64, int offset32)
        {
            if (!Environment.Is64BitProcess)
                return null;

            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero)
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
            finally { CloseHandle(h); }
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

        // --- Interop ---------------------------------------------------------------------------------
        private const int CCH_RM_SESSION_KEY = 32;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;
        private const int ERROR_MORE_DATA = 234;

        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ProcessBasicInformation = 0;
        private const int ProcessWow64Information = 26;

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
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            out IntPtr processInformation, int processInformationLength, out int returnLength);
    }
}
