#nullable enable
using System;
using System.Threading.Tasks;
using ManagedShell.Common.Logging;
using Microsoft.Win32;
#if MSSTORE
using Windows.ApplicationModel;
#endif

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Run-at-sign-in, which works completely differently in the two builds.
    /// <para>
    /// The installer build writes an HKCU\...\Run entry pointing at the exe. The Store build cannot: a
    /// packaged app lives under a version-stamped WindowsApps folder, so that path would go stale on
    /// every update. It instead uses the startupTask declared in Package.appxmanifest, which Windows
    /// owns and keeps pointing at the current version - and which the user can also switch off in
    /// Settings > Apps > Startup or Task Manager, independently of this app's checkbox.
    /// </para>
    /// </summary>
    public static class AutoStart
    {
#if MSSTORE
        // Must match uap5:StartupTask/@TaskId in Package.appxmanifest.
        private const string TaskId = "DragThroughStartup";
#else
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "ZombieBar";
#endif

        /// <summary>Outcome of <see cref="SetEnabledAsync"/>, so the UI can react to the awkward cases.</summary>
        public enum Result
        {
            Ok,
            /// <summary>The user turned it off in Task Manager or Settings; only they can turn it back on.</summary>
            BlockedByUser,
            /// <summary>Group policy, or the mechanism is unavailable on this device.</summary>
            BlockedByPolicy,
            Failed,
        }

        /// <summary>Whether the app is currently set to start at sign-in. Null if it can't be determined.</summary>
        public static async Task<bool?> IsEnabledAsync()
        {
            try
            {
#if MSSTORE
                StartupTask task = await StartupTask.GetAsync(TaskId);
                return task.State == StartupTaskState.Enabled ||
                       task.State == StartupTaskState.EnabledByPolicy;
#else
                await Task.CompletedTask;
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(RunValue) != null;
#endif
            }
            catch (Exception e)
            {
                ShellLogger.Error($"AutoStart: unable to read the run-at-sign-in state: {e.Message}");
                return null;
            }
        }

        /// <summary>Turns run-at-sign-in on or off.</summary>
        public static async Task<Result> SetEnabledAsync(bool enabled)
        {
            try
            {
#if MSSTORE
                StartupTask task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    task.Disable();
                    return Result.Ok;
                }

                // No consent dialog is shown for a packaged desktop app, but a user who switched the task
                // off in Task Manager cannot be overridden from here - the request just comes back with
                // the state unchanged, which the caller surfaces instead of silently doing nothing.
                StartupTaskState state = await task.RequestEnableAsync();
                return state switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => Result.Ok,
                    StartupTaskState.DisabledByUser => Result.BlockedByUser,
                    StartupTaskState.DisabledByPolicy => Result.BlockedByPolicy,
                    _ => Result.Failed,
                };
#else
                await Task.CompletedTask;
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null)
                    return Result.Failed;

                if (enabled)
                    key.SetValue(RunValue, ExePath.GetExecutablePath());
                else if (key.GetValue(RunValue) != null)
                    key.DeleteValue(RunValue);

                return Result.Ok;
#endif
            }
            catch (Exception e)
            {
                ShellLogger.Error($"AutoStart: unable to change the run-at-sign-in state: {e.Message}");
                return Result.Failed;
            }
        }
    }
}
