#nullable enable
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DragThrough.Utilities
{
    /// <summary>
    /// Restarts Windows Explorer (the shell) for the current user session: asks the shell to exit
    /// cleanly, terminates any Explorer processes that are still running, then makes sure a new shell
    /// comes up. Our own taskbar re-registers on Explorer's "TaskbarCreated" broadcast, so it survives.
    /// </summary>
    public static class ExplorerRestarter
    {
        // Undocumented but long-standing message the taskbar handles as "Exit Explorer" (the same one
        // behind Ctrl+Shift+right-click on the taskbar). Unlike a kill, it lets Explorer save its state.
        private const uint WM_EXIT_EXPLORER = 0x5B4;

        private const string ShellTrayClass = "Shell_TrayWnd";

        private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);
        // Winlogon restarts a shell that was terminated (AutoRestartShell); give it a moment before we
        // start one ourselves, so we don't end up with a stray File Explorer window instead.
        private static readonly TimeSpan AutoRestartGrace = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ShellStartTimeout = TimeSpan.FromSeconds(15);

        private static int _running;

        /// <summary>
        /// Runs the restart off the UI thread. Returns null on success, or a short error description.
        /// Concurrent calls are ignored (they report success without doing anything).
        /// </summary>
        public static Task<string?> RestartAsync()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return Task.FromResult<string?>(null);

            return Task.Run(() =>
            {
                try { return Restart(); }
                finally { Interlocked.Exchange(ref _running, 0); }
            });
        }

        private static string? Restart()
        {
            int session = Process.GetCurrentProcess().SessionId;
            List<Process> old = GetSessionExplorers(session);

            try
            {
                IntPtr tray = NativeMethods.FindWindow(ShellTrayClass, null!);
                if (tray != IntPtr.Zero)
                    NativeMethods.PostMessage(tray, WM_EXIT_EXPLORER, IntPtr.Zero, IntPtr.Zero);

                WaitForExit(old, GracefulExitTimeout);

                // Folder windows may run in their own explorer.exe, and a hung shell ignores the exit
                // request; terminate whatever of the original processes is left. Only the processes
                // captured up front are touched, never a shell that has already been restarted.
                foreach (Process p in old.Where(p => !HasExited(p)))
                {
                    try { p.Kill(); }
                    catch (Exception ex) { ShellLogger.Info($"ExplorerRestarter: could not end explorer.exe ({p.Id}): {ex.Message}"); }
                }

                if (!WaitForExit(old, KillTimeout))
                    ShellLogger.Info("ExplorerRestarter: some explorer.exe processes did not exit.");
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"ExplorerRestarter: stopping Explorer failed: {ex.Message}");
            }
            finally
            {
                foreach (Process p in old)
                    p.Dispose();
            }

            // Always make sure a shell comes back, even if stopping it went wrong part-way.
            return EnsureShellRunning();
        }

        private static string? EnsureShellRunning()
        {
            if (!WaitForShell(AutoRestartGrace))
            {
                try
                {
                    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false })?.Dispose();
                }
                catch (Exception ex)
                {
                    ShellLogger.Info($"ExplorerRestarter: could not start explorer.exe: {ex.Message}");
                    return ex.Message;
                }

                if (!WaitForShell(ShellStartTimeout))
                    return "Explorer did not start in time.";
            }

            return null;
        }

        private static List<Process> GetSessionExplorers(int session)
        {
            var result = new List<Process>();
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                bool mine;
                try { mine = p.SessionId == session; }
                catch { mine = false; }

                if (mine)
                    result.Add(p);
                else
                    p.Dispose();
            }
            return result;
        }

        private static bool WaitForExit(List<Process> processes, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            foreach (Process p in processes)
            {
                int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try
                {
                    if (!p.WaitForExit(remaining))
                        return false;
                }
                catch { /* already gone / not accessible */ }
            }
            return true;
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }

        private static bool WaitForShell(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            do
            {
                if (NativeMethods.FindWindow(ShellTrayClass, null!) != IntPtr.Zero)
                    return true;
                Thread.Sleep(250);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }
    }
}
