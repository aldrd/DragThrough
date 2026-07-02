using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// "Drag through": while a configured modifier (Windows key and/or Shift) is held and a file
    /// is dragged out of a File Explorer window, the Explorer window is moved off-screen so the
    /// drop can land on whatever is behind it; afterwards Explorer is restored (and optionally
    /// minimized). Integrated from the standalone DragThrough app; its options are stored in the
    /// ZombieBar <see cref="Settings"/> and read live.
    /// </summary>
    public class DragMonitor : IDisposable
    {
        #region WinAPI
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
        [DllImport("user32.dll")] static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        #endregion

        #region Constants
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;
        const int VK_LSHIFT = 0xA0;
        const int VK_RSHIFT = 0xA1;
        const int VK_ESCAPE = 0x1B;
        const int VK_LBUTTON = 0x01;

        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint GA_ROOT = 2;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const int SW_MINIMIZE = 6;

        const int DRAG_THRESHOLD = 8;
        const int DRAG_CONFIRM_MS = 120;
        const int WIN_RELEASE_WAIT_MS = 3000;
        const int ESCAPE_DELAY_MS = 180;
        const int OFFSCREEN_X = -32000;
        const int OFFSCREEN_Y = -32000;
        #endregion

        enum State { Idle, MouseDown, Dragging }

        State _state = State.Idle;
        Thread _thread;
        volatile bool _running;

        POINT _downPt;
        DateTime _downTime;
        IntPtr _explorer = IntPtr.Zero;
        RECT _originalRect;
        bool _movedOffscreen = false;
        bool _windowsModifierUsedForDrag = false;
        DateTime _waitForWinReleaseUntil = DateTime.MinValue;
        DateTime _sendEscapeAt = DateTime.MinValue;

        // Live settings accessors.
        private static bool CfgWindowsKey => Settings.Instance.EnableWindowsKeyModifier;
        private static bool CfgShift => Settings.Instance.EnableShiftModifier;
        private static bool CfgMinimize => Settings.Instance.MinimizeExplorerAfterSuccessfulDrag;
        // Always on: the tray toggle for this was removed; Escape always dismisses Windows Search.
        private static bool CfgDismissSearch => true;

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ZombieBarDragThroughLoop"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(200);
            RestoreExplorerPosition();
        }

        public void Dispose()
        {
            Stop();
        }

        void Loop()
        {
            while (_running)
            {
                try
                {
                    Tick();
                    DismissSearchAfterWindowsKeyRelease();
                    SendPendingEscape();
                }
                catch
                {
                    RestoreExplorerPosition();
                    Reset();
                }

                Thread.Sleep(ShouldPollFast() ? 5 : 40);
            }
        }

        void Tick()
        {
            bool modifier = IsEnabledModifierDown();
            bool lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            GetCursorPos(out POINT pt);

            switch (_state)
            {
                case State.Idle:
                    if (!modifier) return;
                    if (!lmb) return;

                    IntPtr explorer = GetExplorerUnderCursor(pt);
                    if (explorer == IntPtr.Zero) return;

                    _explorer = explorer;
                    _downPt = pt;
                    _downTime = DateTime.UtcNow;
                    _windowsModifierUsedForDrag = IsWindowsKeyDown();
                    _state = State.MouseDown;
                    break;

                case State.MouseDown:
                    if (!modifier || !lmb)
                    {
                        Reset();
                        return;
                    }

                    int dx = Math.Abs(pt.X - _downPt.X);
                    int dy = Math.Abs(pt.Y - _downPt.Y);
                    double ms = (DateTime.UtcNow - _downTime).TotalMilliseconds;

                    // Wait until a REAL drag has already started.
                    if ((dx >= DRAG_THRESHOLD || dy >= DRAG_THRESHOLD) && ms >= DRAG_CONFIRM_MS)
                    {
                        MoveExplorerOffscreen();
                        _state = State.Dragging;
                    }
                    break;

                case State.Dragging:
                    // Drag finished
                    if (!lmb)
                    {
                        RestoreExplorerPosition();
                        if (CfgMinimize) MinimizeExplorer();
                        BeginWindowsSearchDismissal();
                        Reset();
                        return;
                    }

                    // Modifier released
                    if (!modifier)
                    {
                        RestoreExplorerPosition();
                        BeginWindowsSearchDismissal();
                        Reset();
                        return;
                    }
                    break;
            }
        }

        void MoveExplorerOffscreen()
        {
            if (_explorer == IntPtr.Zero) return;
            if (!IsWindow(_explorer)) return;
            if (!GetWindowRect(_explorer, out _originalRect)) return;

            SetWindowPos(_explorer, IntPtr.Zero, OFFSCREEN_X, OFFSCREEN_Y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            _movedOffscreen = true;
        }

        void RestoreExplorerPosition()
        {
            if (!_movedOffscreen) return;

            try
            {
                if (_explorer != IntPtr.Zero && IsWindow(_explorer))
                {
                    SetWindowPos(_explorer, IntPtr.Zero, _originalRect.Left, _originalRect.Top, 0, 0,
                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
            catch { }

            _movedOffscreen = false;
        }

        void MinimizeExplorer()
        {
            try
            {
                if (_explorer != IntPtr.Zero && IsWindow(_explorer))
                {
                    ShowWindow(_explorer, SW_MINIMIZE);
                }
            }
            catch { }
        }

        void BeginWindowsSearchDismissal()
        {
            if (!CfgDismissSearch) return;
            if (!_windowsModifierUsedForDrag) return;

            _waitForWinReleaseUntil = DateTime.UtcNow.AddMilliseconds(WIN_RELEASE_WAIT_MS);
        }

        void DismissSearchAfterWindowsKeyRelease()
        {
            if (!CfgDismissSearch)
            {
                _waitForWinReleaseUntil = DateTime.MinValue;
                _sendEscapeAt = DateTime.MinValue;
                return;
            }

            if (DateTime.UtcNow > _waitForWinReleaseUntil) return;
            if (IsWindowsKeyDown()) return;

            _waitForWinReleaseUntil = DateTime.MinValue;
            _sendEscapeAt = DateTime.UtcNow.AddMilliseconds(ESCAPE_DELAY_MS);
        }

        void SendPendingEscape()
        {
            if (!CfgDismissSearch)
            {
                _sendEscapeAt = DateTime.MinValue;
                return;
            }

            if (_sendEscapeAt == DateTime.MinValue) return;
            if (DateTime.UtcNow < _sendEscapeAt) return;

            _sendEscapeAt = DateTime.MinValue;
            SendEscape();
        }

        bool IsWindowsKeyDown()
        {
            return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        }

        bool IsShiftKeyDown()
        {
            return (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;
        }

        bool IsEnabledModifierDown()
        {
            return (CfgWindowsKey && IsWindowsKeyDown()) || (CfgShift && IsShiftKeyDown());
        }

        void SendEscape()
        {
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        bool ShouldPollFast()
        {
            DateTime now = DateTime.UtcNow;
            return _movedOffscreen || now <= _waitForWinReleaseUntil || now <= _sendEscapeAt;
        }

        void Reset()
        {
            _state = State.Idle;
            _explorer = IntPtr.Zero;
            _windowsModifierUsedForDrag = false;
        }

        IntPtr GetExplorerUnderCursor(POINT pt)
        {
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            return IsExplorerWindow(root) ? root : IntPtr.Zero;
        }

        bool IsExplorerWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, 256);
            string cn = sb.ToString();
            return cn == "CabinetWClass" || cn == "ExploreWClass";
        }
    }
}
