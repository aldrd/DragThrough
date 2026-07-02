using System;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Virtual-desktop awareness for the additional taskbar. Tracks the current desktop's GUID (read
    /// from the registry) and raises <see cref="DesktopChanged"/> when the user switches desktops.
    ///
    /// Per-window desktop membership is <b>not</b> resolved here: the task filter reads each window's
    /// live DWM cloak state directly (see <c>TaskList.IsOnCurrentDesktop</c>), which is reliable for
    /// minimized windows and needs no COM. This service only answers "which desktop are we on, and did
    /// it just change?" for the per-desktop show/hide feature and to trigger a re-filter.
    /// </summary>
    public class VirtualDesktopService : IDisposable
    {
        // HKCU value holding the current desktop's GUID as a 16-byte REG_BINARY.
        private const string VirtualDesktopsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
        private const string CurrentDesktopValue = "CurrentVirtualDesktop";

        private const int PollIntervalMs = 300;

        private readonly DispatcherTimer _poll;
        private Guid _current;
        private bool _disposed;

        /// <summary>Raised on the UI thread after the current virtual desktop changes.</summary>
        public event EventHandler DesktopChanged;

        public VirtualDesktopService()
        {
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

        private void Poll_Tick(object sender, EventArgs e)
        {
            Guid latest = ReadCurrentDesktop();
            if (latest == _current)
                return;

            _current = latest;
            DesktopChanged?.Invoke(this, EventArgs.Empty);
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
