using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using ManagedShell.Common.Logging;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Virtual-desktop awareness for the additional taskbar. Tracks the current desktop's GUID (read
    /// from the registry) and raises <see cref="DesktopChanged"/> when the user switches desktops, and
    /// answers "which of these windows are on the current desktop?" via the public
    /// <see cref="IVirtualDesktopManager"/> COM API.
    ///
    /// The COM query is deliberately done on a background thread (see
    /// <see cref="GetWindowsOnCurrentDesktop"/>): COM STA calls pump the message loop, and if that
    /// happened on the UI thread mid-refresh it would re-enter WPF's <c>ListCollectionView</c> live
    /// shaping and crash. Everything fails open: if the API/registry is unavailable the caller is told
    /// to show every window, so the taskbar behaves exactly as before.
    /// </summary>
    public class VirtualDesktopService : IDisposable
    {
        // HKCU value holding the current desktop's GUID as a 16-byte REG_BINARY.
        private const string VirtualDesktopsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
        private const string CurrentDesktopValue = "CurrentVirtualDesktop";

        private const int PollIntervalMs = 300;

        private readonly DispatcherTimer _poll;
        private readonly bool _available;
        private Guid _current;
        private bool _disposed;

        /// <summary>Raised on the UI thread after the current virtual desktop changes.</summary>
        public event EventHandler DesktopChanged;

        public VirtualDesktopService()
        {
            _available = ProbeAvailable();
            _current = ReadCurrentDesktop();

            _poll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _poll.Tick += Poll_Tick;
            _poll.Start();
        }

        /// <summary>The GUID of the desktop currently in the foreground (Guid.Empty if unknown).</summary>
        public Guid CurrentDesktop => _current;

        /// <summary>
        /// Returns the subset of <paramref name="handles"/> that are on the currently active virtual
        /// desktop, or <c>null</c> if the API is unavailable (meaning: treat every window as visible).
        /// Safe to call from a background thread — it creates its own COM object so it never marshals
        /// back to, or pumps, the UI thread. Windows that error out individually are kept (fail open).
        /// </summary>
        public HashSet<IntPtr> GetWindowsOnCurrentDesktop(IReadOnlyCollection<IntPtr> handles)
        {
            if (!_available || handles == null)
                return null;

            IVirtualDesktopManager manager;
            try
            {
                manager = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass();
            }
            catch
            {
                return null;
            }

            try
            {
                var onCurrent = new HashSet<IntPtr>();
                foreach (IntPtr hwnd in handles)
                {
                    if (hwnd == IntPtr.Zero)
                        continue;

                    try
                    {
                        if (manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int on) == 0)
                        {
                            if (on != 0)
                                onCurrent.Add(hwnd);
                        }
                        else
                        {
                            onCurrent.Add(hwnd); // query failed for this window: don't hide it
                        }
                    }
                    catch
                    {
                        onCurrent.Add(hwnd);
                    }
                }

                return onCurrent;
            }
            finally
            {
                try { Marshal.ReleaseComObject(manager); } catch { }
            }
        }

        private void Poll_Tick(object sender, EventArgs e)
        {
            Guid latest = ReadCurrentDesktop();
            if (latest == _current)
                return;

            _current = latest;
            DesktopChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool ProbeAvailable()
        {
            try
            {
                var manager = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass();
                Marshal.ReleaseComObject(manager);
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Info($"VirtualDesktopService: IVirtualDesktopManager unavailable, virtual-desktop filtering disabled. {ex.Message}");
                return false;
            }
        }

        private static Guid ReadCurrentDesktop()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(VirtualDesktopsKey);
                if (key?.GetValue(CurrentDesktopValue) is byte[] bytes && bytes.Length == 16)
                    return new Guid(bytes);
            }
            catch
            {
                // Registry layout differs on some builds; treat as a single (empty) desktop.
            }

            return Guid.Empty;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _poll.Stop();
            _poll.Tick -= Poll_Tick;
        }
    }
}
