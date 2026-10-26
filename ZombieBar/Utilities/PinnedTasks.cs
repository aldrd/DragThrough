using System;
using System.Collections.Generic;
using System.Linq;
using ManagedShell.WindowsTasks;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Windows pinned to the additional taskbar in the "only open windows" mode: a pinned window stays
    /// on the bar even while minimized.
    ///
    /// Pins are kept per application, not per window: window handles change on every launch and titles
    /// change constantly (a browser's title is its active tab), so the application is the only identity
    /// that survives restarts. An application is its executable path, or for UWP apps (which all run in
    /// ApplicationFrameHost.exe) its AppUserModelID. Pinning one window therefore pins every window of
    /// that application. Persisted in <see cref="Settings.PinnedTaskApps"/>.
    /// </summary>
    public static class PinnedTasks
    {
        // '|' can't appear in a file path or an AppUserModelID.
        private const char Separator = '|';

        private static HashSet<string> _pinned;

        // Application key per window handle. Resolving a UWP window's AppUserModelID goes through the
        // shell property store, so keys are resolved up front (see Register) and the collection-view
        // filter only does lookups.
        private static readonly Dictionary<IntPtr, string> _keyByHandle = new();

        /// <summary>Raised after a pin is added or removed.</summary>
        public static event EventHandler Changed;

        private static HashSet<string> Pinned
        {
            get
            {
                if (_pinned == null)
                {
                    string stored = Settings.Instance.PinnedTaskApps ?? "";
                    _pinned = new HashSet<string>(
                        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase);
                }

                return _pinned;
            }
        }

        /// <summary>Resolves and caches the window's application key. Call outside the view filter.</summary>
        public static void Register(ApplicationWindow window)
        {
            if (window == null || _keyByHandle.ContainsKey(window.Handle))
                return;

            string key = ResolveKey(window);
            if (!string.IsNullOrEmpty(key))
                _keyByHandle[window.Handle] = key;
        }

        /// <summary>Drops cached keys of windows that no longer exist.</summary>
        public static void Forget(IEnumerable<IntPtr> liveHandles)
        {
            var live = new HashSet<IntPtr>(liveHandles);
            foreach (IntPtr handle in _keyByHandle.Keys.Where(h => !live.Contains(h)).ToList())
                _keyByHandle.Remove(handle);
        }

        /// <summary>Safe to call from the view filter: only reads the cache.</summary>
        public static bool IsPinned(ApplicationWindow window) =>
            window != null
            && _keyByHandle.TryGetValue(window.Handle, out string key)
            && Pinned.Contains(key);

        public static void Toggle(ApplicationWindow window)
        {
            if (window == null)
                return;

            Register(window);
            if (!_keyByHandle.TryGetValue(window.Handle, out string key))
                return;

            if (!Pinned.Remove(key))
                Pinned.Add(key);

            Settings.Instance.PinnedTaskApps = string.Join(Separator, Pinned.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static string ResolveKey(ApplicationWindow window)
        {
            try
            {
                if (window.IsUWP && !string.IsNullOrEmpty(window.AppUserModelID))
                    return window.AppUserModelID;

                return window.WinFileName;
            }
            catch
            {
                return null;
            }
        }
    }
}
