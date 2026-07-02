using System;
using System.Runtime.InteropServices;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Minimal COM declaration of the public Windows <c>IVirtualDesktopManager</c> shell API, used to
    /// tell which virtual desktop a window belongs to. Only the two query methods we need are declared.
    /// Follows the COM-interop pattern of ManagedShell (see IAppVisibility), but lives here so the
    /// vendored ManagedShell copy stays untouched.
    /// </summary>
    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualDesktopManager
    {
        // BOOL is marshalled as an int (0/1); HRESULT is returned directly via PreserveSig so callers
        // can fail open instead of throwing on an unexpected Windows build.
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop([In] IntPtr topLevelWindow, [Out] out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId([In] IntPtr topLevelWindow, [Out] out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop([In] IntPtr topLevelWindow, [In] ref Guid desktopId);
    }

    /// <summary>CoClass for <see cref="IVirtualDesktopManager"/>.</summary>
    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    public class VirtualDesktopManagerCoClass
    {
    }
}
