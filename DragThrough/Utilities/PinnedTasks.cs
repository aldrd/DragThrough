using System;
using System.Collections.Generic;
using ManagedShell.WindowsTasks;

namespace DragThrough.Utilities
{
    /// <summary>
    /// Windows pinned to the additional taskbar in the "only open windows" mode: a pinned window stays
    /// on the bar even while minimized. A pin belongs to the single window it was set on (by handle),
    /// lives in memory only, and is dropped when that window closes or the app restarts.
    /// </summary>
    public static class PinnedTasks
    {
        private static readonly HashSet<IntPtr> Pinned = new();

        /// <summary>Raised after a pin is added or removed.</summary>
        public static event EventHandler Changed;

        /// <summary>Safe to call from the view filter: a set lookup only.</summary>
        public static bool IsPinned(ApplicationWindow window) =>
            window != null && Pinned.Contains(window.Handle);

        public static void Toggle(ApplicationWindow window)
        {
            if (window == null)
                return;

            if (!Pinned.Remove(window.Handle))
                Pinned.Add(window.Handle);

            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Drops pins of windows that no longer exist (a reused handle must not inherit a pin).</summary>
        public static void Forget(IEnumerable<IntPtr> liveHandles)
        {
            var live = new HashSet<IntPtr>(liveHandles);
            Pinned.RemoveWhere(h => !live.Contains(h));
        }
    }
}
