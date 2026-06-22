#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;
using ManagedShell.Interop;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Resolves the filesystem path shown by an open File Explorer window. The path is read
    /// from the Windows shell (the open folder's <c>Folder.Self.Path</c>), not from the window
    /// title, so it is independent of the system display language.
    /// </summary>
    public static class ExplorerPathHelper
    {
        // File Explorer folder windows use this window class on Windows 10/11. The legacy
        // "ExploreWClass" (folder tree variant) is included for completeness.
        private static readonly string[] ExplorerClassNames = { "CabinetWClass", "ExploreWClass" };

        public static bool IsFileExplorerWindow(IntPtr hwnd)
        {
            string className = GetClassName(hwnd);
            return Array.IndexOf(ExplorerClassNames, className) >= 0;
        }

        /// <summary>
        /// Returns the folder path of the File Explorer window with the given handle, or null
        /// if it is not an Explorer window or shows a virtual location with no filesystem path
        /// (e.g. "This PC", Control Panel).
        /// </summary>
        public static string? GetPath(IntPtr hwnd)
        {
            if (!IsFileExplorerWindow(hwnd))
                return null;

            object? shell = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                    return null;

                shell = Activator.CreateInstance(shellType);
                dynamic windows = ((dynamic)shell!).Windows();
                int count = windows.Count;
                for (int i = 0; i < count; i++)
                {
                    object? w = null;
                    try
                    {
                        w = windows.Item(i);
                        if (w == null)
                            continue;

                        dynamic window = w;
                        if (new IntPtr(Convert.ToInt64(window.HWND)) != hwnd)
                            continue;

                        string? path = null;
                        try { path = window.Document?.Folder?.Self?.Path as string; }
                        catch { }

                        // Virtual locations ("This PC", Control Panel, ...) have no real path;
                        // their Path is empty or a "::{CLSID}" parsing name.
                        if (string.IsNullOrEmpty(path) || path!.StartsWith("::"))
                            return null;

                        return path;
                    }
                    finally
                    {
                        if (w != null)
                            Marshal.FinalReleaseComObject(w);
                    }
                }
            }
            catch
            {
                // Shell COM unavailable or the window went away mid-enumeration; fall through.
            }
            finally
            {
                if (shell != null)
                    Marshal.FinalReleaseComObject(shell);
            }

            return null;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
