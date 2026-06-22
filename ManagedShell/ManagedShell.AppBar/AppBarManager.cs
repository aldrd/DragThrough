using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ManagedShell.Common.Helpers;
using static ManagedShell.Interop.NativeMethods;
using Ssz.Utils.Wpf.WpfScreenHelper;
using Ssz.Utils.Wpf;

namespace ManagedShell.AppBar
{
    public class AppBarManager : IDisposable
    {
        private static object appBarLock = new object();
        
        private readonly ExplorerHelper _explorerHelper;
        private int uCallBack;
        
        public List<AppBarWindow> AppBarWindowsCollection { get; } = new List<AppBarWindow>();
        public EventHandler<AppBarEventArgs> AppBarEvent;

        public AppBarManager(ExplorerHelper explorerHelper)
        {
            _explorerHelper = explorerHelper;
        }

        public void SignalGracefulShutdown()
        {
            foreach (AppBarWindow window in AppBarWindowsCollection)
            {
                window.AllowClose = true;
            }
        }

        public void NotifyAppBarEvent(AppBarWindow sender, AppBarEventReason reason)
        {
            AppBarEventArgs args = new AppBarEventArgs { Reason = reason };
            AppBarEvent?.Invoke(sender, args);
        }

        #region AppBar message helpers
        public int RegisterBar(AppBarWindow appBarWindow, double width, double height, AppBarEdge edge = AppBarEdge.Top)
        {
            lock (appBarLock)
            {
                APPBARDATA abd = new APPBARDATA();
                abd.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                abd.hWnd = appBarWindow.Handle;

                if (!AppBarWindowsCollection.Contains(appBarWindow))
                {
                    if (!EnvironmentHelper.IsAppRunningAsShell)
                    {
                        uCallBack = RegisterWindowMessage("AppBarMessage");
                        abd.uCallbackMessage = uCallBack;
                        
                        SHAppBarMessage((int) ABMsg.ABM_NEW, ref abd);
                    }
                    
                    AppBarWindowsCollection.Add(appBarWindow);
                    
                    ShellLogger.Debug($"AppBarManager: Created AppBar for handle {appBarWindow.Handle}");

                    if (!EnvironmentHelper.IsAppRunningAsShell)
                    {
                        AppBarWindowSetPosition(appBarWindow, width, height, edge, true);
                    }
                    else
                    {
                        SetWorkingArea(appBarWindow.ScreenInfo);
                    }
                }
                else
                {
                    if (!EnvironmentHelper.IsAppRunningAsShell)
                    {
                        SHAppBarMessage((int) ABMsg.ABM_REMOVE, ref abd);
                    }

                    AppBarWindowsCollection.Remove(appBarWindow);
                    ShellLogger.Debug($"AppBarManager: Removed AppBar for handle {appBarWindow.Handle}");

                    if (EnvironmentHelper.IsAppRunningAsShell)
                    {
                        SetWorkingArea(appBarWindow.ScreenInfo);
                    }

                    return 0;
                }
            }

            return uCallBack;
        }

        public void AppBarActivate(IntPtr hwnd)
        {
            APPBARDATA abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = hwnd,
                lParam = (IntPtr)Convert.ToInt32(true)
            };
            
            SHAppBarMessage((int)ABMsg.ABM_ACTIVATE, ref abd);

            // apparently the TaskBars like to pop up when AppBars change
            if (_explorerHelper.HideExplorerTaskbar)
            {
                _explorerHelper.SetSecondaryTaskbarVisibility((int)SetWindowPosFlags.SWP_HIDEWINDOW);
            }
        }

        public void AppBarWindowPosChanged(IntPtr hwnd)
        {
            APPBARDATA abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = hwnd
            };
            
            SHAppBarMessage((int)ABMsg.ABM_WINDOWPOSCHANGED, ref abd);

            // apparently the TaskBars like to pop up when AppBars change
            if (_explorerHelper.HideExplorerTaskbar)
            {
                _explorerHelper.SetSecondaryTaskbarVisibility((int)SetWindowPosFlags.SWP_HIDEWINDOW);
            }
        }

        public void AppBarWindowSetPosition(AppBarWindow appBarWindow, double width, double height, AppBarEdge edge, bool isCreate = false)
        {
            lock (appBarLock)
            {
                APPBARDATA abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                    hWnd = appBarWindow.Handle,
                    uEdge = (int)edge
                };

                int sWidth = (int)width;
                int sHeight = (int)height;
                
                int top;
                int left;
                int right;
                int bottom;

                int screenEdgeOffset = 0;

                if (appBarWindow.ScreenInfo != null)
                {
                    top = appBarWindow.ScreenInfo.Bounds.Y;
                    left = appBarWindow.ScreenInfo.Bounds.X;
                    right = appBarWindow.ScreenInfo.Bounds.Right;
                    bottom = appBarWindow.ScreenInfo.Bounds.Bottom;
                }
                else
                {
                    var virtualScreen = WindowsScreen.VirtualScreen;
                    top = (int)virtualScreen.Top;
                    left = (int)virtualScreen.Left;
                    right = (int)virtualScreen.Right;
                    bottom = (int)virtualScreen.Bottom;
                }

                screenEdgeOffset = Convert.ToInt32(GetScreenEdgeOffset((AppBarEdge)abd.uEdge, appBarWindow.ScreenInfo));

                if (abd.uEdge == (int)AppBarEdge.Left || abd.uEdge == (int)AppBarEdge.Right)
                {
                    abd.rc.Top = top;
                    abd.rc.Bottom = bottom;
                    if (abd.uEdge == (int)AppBarEdge.Left)
                    {
                        abd.rc.Left = left + screenEdgeOffset;
                        abd.rc.Right = abd.rc.Left + sWidth;
                    }
                    else
                    {
                        abd.rc.Right = right - screenEdgeOffset;
                        abd.rc.Left = abd.rc.Right - sWidth;
                    }
                }
                else
                {
                    abd.rc.Left = left;
                    abd.rc.Right = right;
                    if (abd.uEdge == (int)AppBarEdge.Top)
                    {
                        abd.rc.Top = top + screenEdgeOffset;
                        abd.rc.Bottom = abd.rc.Top + sHeight;
                    }
                    else
                    {
                        abd.rc.Bottom = bottom - screenEdgeOffset;
                        abd.rc.Top = abd.rc.Bottom - sHeight;
                    }
                }

                _explorerHelper.SuspendTrayService();
                SHAppBarMessage((int)ABMsg.ABM_QUERYPOS, ref abd);
                _explorerHelper.ResumeTrayService();

                // system doesn't adjust all edges for us, do some adjustments
                switch (abd.uEdge)
                {
                    case (int)AppBarEdge.Left:
                        abd.rc.Right = abd.rc.Left + sWidth;
                        break;
                    case (int)AppBarEdge.Right:
                        abd.rc.Left = abd.rc.Right - sWidth;
                        break;
                    case (int)AppBarEdge.Top:
                        abd.rc.Bottom = abd.rc.Top + sHeight;
                        break;
                    case (int)AppBarEdge.Bottom:
                        abd.rc.Top = abd.rc.Bottom - sHeight;
                        break;
                }

                _explorerHelper.SuspendTrayService();
                SHAppBarMessage((int)ABMsg.ABM_SETPOS, ref abd);
                _explorerHelper.ResumeTrayService();

                // check if new coords
                bool isChanged = true;
                if (!isCreate)
                {
                    bool topUnchanged = abd.rc.Top == (appBarWindow.Top * appBarWindow.DpiScale);
                    bool leftUnchanged = abd.rc.Left == (appBarWindow.Left * appBarWindow.DpiScale);
                    bool bottomUnchanged = abd.rc.Bottom == (appBarWindow.Top * appBarWindow.DpiScale) + sHeight;
                    bool rightUnchanged = abd.rc.Right == (appBarWindow.Left * appBarWindow.DpiScale) + sWidth;

                    isChanged = !(topUnchanged
                                   && leftUnchanged
                                   && bottomUnchanged
                                   && rightUnchanged);
                }

                if (isChanged)
                {
                    ShellLogger.Debug($"AppBarManager: {appBarWindow.Name} changing position (TxLxBxR) to {abd.rc.Top}x{abd.rc.Left}x{abd.rc.Bottom}x{abd.rc.Right} from {appBarWindow.Top * ScreenHelper.PrimaryScreenScaleY}x{appBarWindow.Left * ScreenHelper.PrimaryScreenScaleX}x{(appBarWindow.Top * ScreenHelper.PrimaryScreenScaleY) + sHeight}x{ (appBarWindow.Left * ScreenHelper.PrimaryScreenScaleX) + sWidth}");
                    var rect = abd.rc;
                    appBarWindow.Width = (rect.Right - rect.Left) / appBarWindow.DpiScale;
                    appBarWindow.Height = (rect.Bottom - rect.Top) / appBarWindow.DpiScale;
                    appBarWindow.Left = rect.Left / appBarWindow.DpiScale;
                    appBarWindow.Top = rect.Top / appBarWindow.DpiScale;
                }

                if (abd.rc.Bottom - abd.rc.Top < sHeight)
                {
                    AppBarWindowSetPosition(appBarWindow, width, height, edge);
                }
            }
        }
        #endregion

        #region Work area
        public double GetScreenEdgeOffset(AppBarEdge edge, ScreenInfo screenInfo)
        {
            double screenEdgeOffset = 0;
            double dpiScale = 1.0;
            Rect workingAreaRect = GetWorkingArea(ref dpiScale, screenInfo, false);

            switch (edge)
            {
                case AppBarEdge.Top:
                    screenEdgeOffset += workingAreaRect.Top / dpiScale;
                    break;
                case AppBarEdge.Bottom:
                    screenEdgeOffset += (screenInfo.Bounds.Bottom - workingAreaRect.Bottom) / dpiScale;
                    break;
                case AppBarEdge.Left:
                    screenEdgeOffset += workingAreaRect.Left / dpiScale;
                    break;
                case AppBarEdge.Right:
                    screenEdgeOffset += (screenInfo.Bounds.Right - workingAreaRect.Right) / dpiScale;
                    break;
            }

            return screenEdgeOffset;
        }

        public Rect GetWorkingArea(ref double dpiScale, ScreenInfo screenInfo, bool includeAppBarWindows)
        {
            double topEdgeWindowHeight = 0;
            double bottomEdgeWindowHeight = 0;
            double leftEdgeWindowWidth = 0;
            double rightEdgeWindowWidth = 0;
            Rect rc;

            // get appropriate windows for this display
            foreach (var appBarWindow in AppBarWindowsCollection)
            {
                if (appBarWindow.ScreenInfo.DeviceName == screenInfo.DeviceName)
                {
                    if (appBarWindow.EnableAppBar && includeAppBarWindows)
                    {
                        if (appBarWindow.AppBarEdge == AppBarEdge.Top)
                        {
                            topEdgeWindowHeight += appBarWindow.ActualHeight;
                        }
                        else if (appBarWindow.AppBarEdge == AppBarEdge.Bottom)
                        {
                            bottomEdgeWindowHeight += appBarWindow.ActualHeight;
                        }
                        else if (appBarWindow.AppBarEdge == AppBarEdge.Left)
                        {
                            leftEdgeWindowWidth += appBarWindow.ActualWidth;
                        }
                        else if (appBarWindow.AppBarEdge == AppBarEdge.Right)
                        {
                            rightEdgeWindowWidth += appBarWindow.ActualWidth;
                        }                        
                    }

                    dpiScale = appBarWindow.DpiScale;
                }
            }

            // Windows taskbar
            //var db = WindowsTaskbar.DisplayBounds;
            //var cb = WindowsTaskbar.CurrentBounds;
            APPBARDATA data = new APPBARDATA();
            data.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(data);
            _explorerHelper.SuspendTrayService();
            SHAppBarMessage((int)ABMsg.ABM_GETTASKBARPOS, ref data);
            _explorerHelper.ResumeTrayService();
            bottomEdgeWindowHeight += data.rc.Height;

            rc.Top = screenInfo.Bounds.Top + (int)(topEdgeWindowHeight * dpiScale);
            rc.Bottom = screenInfo.Bounds.Bottom - (int)(bottomEdgeWindowHeight * dpiScale);
            rc.Left = screenInfo.Bounds.Left + (int)(leftEdgeWindowWidth * dpiScale);
            rc.Right = screenInfo.Bounds.Right - (int)(rightEdgeWindowWidth * dpiScale);

            return rc;
        }

        public void SetWorkingArea(ScreenInfo screenInfo)
        {
            double dpiScale = 1.0;
            Rect rc = GetWorkingArea(ref dpiScale, screenInfo, true);

            SystemParametersInfo((int)SPI.SETWORKAREA, 1, ref rc, (uint)(SPIF.UPDATEINIFILE | SPIF.SENDWININICHANGE));
        }

        public static void ResetWorkingArea()
        {
            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                // TODO this is wrong for multi-display
                // set work area back to full screen size. we can't assume what pieces of the old work area may or may not be still used
                Rect oldWorkArea;
                oldWorkArea.Left = SystemInformation.VirtualScreen.Left;
                oldWorkArea.Top = SystemInformation.VirtualScreen.Top;
                oldWorkArea.Right = SystemInformation.VirtualScreen.Right;
                oldWorkArea.Bottom = SystemInformation.VirtualScreen.Bottom;

                SystemParametersInfo((int)SPI.SETWORKAREA, 1, ref oldWorkArea,
                    (uint)(SPIF.UPDATEINIFILE | SPIF.SENDWININICHANGE));
            }
        }
        #endregion

        public void Dispose()
        {
            ResetWorkingArea();
        }
    }
}