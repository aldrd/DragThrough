using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace DragThrough
{
    // ============================================================
    // SETTINGS
    // ============================================================

    class AppSettings
    {
        public bool EnableWindowsKeyModifier { get; set; } = true;

        public bool EnableShiftModifier { get; set; } = false;

        public bool MinimizeExplorerAfterSuccessfulDrag { get; set; } = true;

        public bool DismissWindowsSearchWithEscape { get; set; } = true;

        public static string ConfigPath =>
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new AppSettings();

                string json =
                    File.ReadAllText(ConfigPath);

                return JsonSerializer.Deserialize<AppSettings>(json)
                       ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                string json =
                    JsonSerializer.Serialize(
                        this,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
            }
        }
    }

    // ============================================================
    // PROGRAM
    // ============================================================

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(
                HighDpiMode.PerMonitorV2);

            Application.EnableVisualStyles();

            Application.SetCompatibleTextRenderingDefault(false);

            using var mutex =
                new Mutex(
                    true,
                    "ExplorerDragHide",
                    out bool created);

            if (!created)
            {
                MessageBox.Show(
                    "Already running.",
                    "ExplorerDragHide",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            Application.Run(new TrayApp());
        }
    }

    // ============================================================
    // TRAY APP
    // ============================================================

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _tray;

        readonly DragMonitor _monitor;

        readonly AppSettings _settings;

        public TrayApp()
        {
            _settings = AppSettings.Load();

            _settings.Save();

            _monitor = new DragMonitor(_settings);

            _tray = new NotifyIcon
            {
                Icon = LoadIconFromPng("DragThrough.Assets.icon_20.png"),
                Visible = true,
                Text = "Drag Through"
            };

            _tray.ContextMenuStrip = BuildMenu();

            _tray.MouseUp += TrayMouseUp;

            _monitor.Start();
        }

        static Icon LoadIconFromPng(string resourceName)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var bmp = new Bitmap(stream);
            return Icon.FromHandle(bmp.GetHicon());
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip
            {
                AutoClose = true,
                Font = SystemFonts.MenuFont
            };

            var title =
                new ToolStripMenuItem(
                    "Temporarily hide Explorer while dragging files");

            title.Enabled = false;

            menu.Items.Add(title);

            menu.Items.Add(new ToolStripSeparator());

            var win =
                new ToolStripMenuItem(
                    "Use Windows key as drag modifier")
                {
                    Checked =
                        _settings.EnableWindowsKeyModifier
                };

            win.Click += (_, _) =>
            {
                _settings.EnableWindowsKeyModifier =
                    !_settings.EnableWindowsKeyModifier;

                win.Checked =
                    _settings.EnableWindowsKeyModifier;

                _settings.Save();
            };

            menu.Items.Add(win);

            var shift =
                new ToolStripMenuItem(
                    "Enable Shift modifier (may not work correctly, e.g. DaVinci Resolve)")
                {
                    Checked =
                        _settings.EnableShiftModifier
                };

            shift.Click += (_, _) =>
            {
                _settings.EnableShiftModifier =
                    !_settings.EnableShiftModifier;

                shift.Checked =
                    _settings.EnableShiftModifier;

                _settings.Save();
            };

            menu.Items.Add(shift);

            menu.Items.Add(new ToolStripSeparator());

            var minimizeExplorer =
                new ToolStripMenuItem(
                    "Minimize Explorer after successful drag")
                {
                    Checked =
                        _settings.MinimizeExplorerAfterSuccessfulDrag
                };

            minimizeExplorer.Click += (_, _) =>
            {
                _settings.MinimizeExplorerAfterSuccessfulDrag =
                    !_settings.MinimizeExplorerAfterSuccessfulDrag;

                minimizeExplorer.Checked =
                    _settings.MinimizeExplorerAfterSuccessfulDrag;

                _settings.Save();
            };

            menu.Items.Add(minimizeExplorer);

            var dismissSearch =
                new ToolStripMenuItem(
                    "Dismiss Windows Search with Escape")
                {
                    Checked =
                        _settings.DismissWindowsSearchWithEscape
                };

            dismissSearch.Click += (_, _) =>
            {
                _settings.DismissWindowsSearchWithEscape =
                    !_settings.DismissWindowsSearchWithEscape;

                dismissSearch.Checked =
                    _settings.DismissWindowsSearchWithEscape;

                _settings.Save();
            };

            menu.Items.Add(dismissSearch);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("Exit", null, (_, _) =>
            {
                _monitor.Stop();

                _tray.Visible = false;

                Application.Exit();
            });

            return menu;
        }

        void TrayMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            ContextMenuStrip? menu =
                _tray.ContextMenuStrip;

            if (menu == null)
                return;

            if (menu.Visible)
            {
                menu.Close(
                    ToolStripDropDownCloseReason.AppClicked);

                return;
            }

            ShowTrayMenu(menu);
        }

        void ShowTrayMenu(ContextMenuStrip menu)
        {
            Size size =
                menu.GetPreferredSize(Size.Empty);

            Point cursor =
                Cursor.Position;

            Rectangle screen =
                Screen.FromPoint(cursor).WorkingArea;

            int x =
                cursor.X - size.Width;

            int y =
                cursor.Y - size.Height - 8;

            x = Math.Max(
                screen.Left,
                Math.Min(x, screen.Right - size.Width));

            y = Math.Max(
                screen.Top,
                Math.Min(y, screen.Bottom - size.Height));

            menu.Show(
                new Point(x, y));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _monitor.Stop();

                _tray.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ============================================================
    // DRAG MONITOR
    // ============================================================

    class DragMonitor
    {
        // --------------------------------------------------------
        // WINAPI
        // --------------------------------------------------------

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(
            IntPtr hwnd,
            uint flags);

        [DllImport("user32.dll")]
        static extern int GetClassName(
            IntPtr hwnd,
            StringBuilder sb,
            int max);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(
            IntPtr hwnd,
            out RECT rect);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(
            IntPtr hwnd,
            int cmdShow);

        [DllImport("user32.dll")]
        static extern void keybd_event(
            byte virtualKey,
            byte scanCode,
            uint flags,
            UIntPtr extraInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // --------------------------------------------------------
        // CONSTANTS
        // --------------------------------------------------------

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

        // --------------------------------------------------------

        enum State
        {
            Idle,
            MouseDown,
            Dragging
        }

        State _state = State.Idle;

        readonly AppSettings _cfg;

        Thread _thread = null!;

        volatile bool _running;

        POINT _downPt;

        DateTime _downTime;

        IntPtr _explorer = IntPtr.Zero;

        RECT _originalRect;

        bool _movedOffscreen = false;

        bool _windowsModifierUsedForDrag = false;

        DateTime _waitForWinReleaseUntil = DateTime.MinValue;

        DateTime _sendEscapeAt = DateTime.MinValue;

        // --------------------------------------------------------

        public DragMonitor(AppSettings cfg)
        {
            _cfg = cfg;
        }

        // --------------------------------------------------------

        public void Start()
        {
            _running = true;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ExplorerDragHideLoop"
            };

            _thread.Start();
        }

        public void Stop()
        {
            _running = false;

            _thread?.Join(200);

            RestoreExplorerPosition();
        }

        // --------------------------------------------------------

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

                Thread.Sleep(
                    ShouldPollFast() ? 5 : 40);
            }
        }

        // --------------------------------------------------------

        void Tick()
        {
            bool modifier =
                IsEnabledModifierDown();

            bool lmb =
                (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            GetCursorPos(out POINT pt);

            switch (_state)
            {
                // ------------------------------------------------

                case State.Idle:

                    if (!modifier)
                        return;

                    if (!lmb)
                        return;

                    IntPtr explorer =
                        GetExplorerUnderCursor(pt);

                    if (explorer == IntPtr.Zero)
                        return;

                    _explorer = explorer;

                    _downPt = pt;

                    _downTime = DateTime.UtcNow;

                    _windowsModifierUsedForDrag =
                        IsWindowsKeyDown();

                    _state = State.MouseDown;

                    break;

                // ------------------------------------------------

                case State.MouseDown:

                    if (!modifier || !lmb)
                    {
                        Reset();

                        return;
                    }

                    int dx =
                        Math.Abs(pt.X - _downPt.X);

                    int dy =
                        Math.Abs(pt.Y - _downPt.Y);

                    double ms =
                        (DateTime.UtcNow - _downTime)
                        .TotalMilliseconds;

                    // IMPORTANT:
                    // Wait until REAL drag already started.

                    if ((dx >= DRAG_THRESHOLD ||
                         dy >= DRAG_THRESHOLD)
                        && ms >= DRAG_CONFIRM_MS)
                    {
                        MoveExplorerOffscreen();

                        _state = State.Dragging;
                    }

                    break;

                // ------------------------------------------------

                case State.Dragging:

                    // Drag finished

                    if (!lmb)
                    {
                        RestoreExplorerPosition();

                        if (_cfg.MinimizeExplorerAfterSuccessfulDrag)
                            MinimizeExplorer();

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

        // --------------------------------------------------------

        void MoveExplorerOffscreen()
        {
            if (_explorer == IntPtr.Zero)
                return;

            if (!IsWindow(_explorer))
                return;

            if (!GetWindowRect(
                _explorer,
                out _originalRect))
            {
                return;
            }

            SetWindowPos(
                _explorer,
                IntPtr.Zero,
                OFFSCREEN_X,
                OFFSCREEN_Y,
                0,
                0,
                SWP_NOSIZE |
                SWP_NOZORDER |
                SWP_NOACTIVATE);

            _movedOffscreen = true;
        }

        // --------------------------------------------------------

        void RestoreExplorerPosition()
        {
            if (!_movedOffscreen)
                return;

            try
            {
                if (_explorer != IntPtr.Zero &&
                    IsWindow(_explorer))
                {
                    SetWindowPos(
                        _explorer,
                        IntPtr.Zero,
                        _originalRect.Left,
                        _originalRect.Top,
                        0,
                        0,
                        SWP_NOSIZE |
                        SWP_NOZORDER |
                        SWP_NOACTIVATE);
                }
            }
            catch
            {
            }

            _movedOffscreen = false;
        }

        // --------------------------------------------------------

        void MinimizeExplorer()
        {
            try
            {
                if (_explorer != IntPtr.Zero &&
                    IsWindow(_explorer))
                {
                    ShowWindow(
                        _explorer,
                        SW_MINIMIZE);
                }
            }
            catch
            {
            }
        }

        // --------------------------------------------------------

        void BeginWindowsSearchDismissal()
        {
            if (!_cfg.DismissWindowsSearchWithEscape)
                return;

            if (!_windowsModifierUsedForDrag)
                return;

            _waitForWinReleaseUntil =
                DateTime.UtcNow.AddMilliseconds(
                    WIN_RELEASE_WAIT_MS);
        }

        // --------------------------------------------------------

        void DismissSearchAfterWindowsKeyRelease()
        {
            if (!_cfg.DismissWindowsSearchWithEscape)
            {
                _waitForWinReleaseUntil = DateTime.MinValue;
                _sendEscapeAt = DateTime.MinValue;

                return;
            }

            if (DateTime.UtcNow > _waitForWinReleaseUntil)
                return;

            if (IsWindowsKeyDown())
                return;

            _waitForWinReleaseUntil = DateTime.MinValue;

            DateTime firstEscapeAt =
                DateTime.UtcNow.AddMilliseconds(
                    ESCAPE_DELAY_MS);

            _sendEscapeAt = firstEscapeAt;
        }

        void SendPendingEscape()
        {
            if (!_cfg.DismissWindowsSearchWithEscape)
            {
                _sendEscapeAt = DateTime.MinValue;

                return;
            }

            DateTime now = DateTime.UtcNow;

            if (_sendEscapeAt == DateTime.MinValue)
                return;

            if (now < _sendEscapeAt)
                return;

            _sendEscapeAt = DateTime.MinValue;

            SendEscape();
        }

        bool IsWindowsKeyDown()
        {
            return
                (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        }

        bool IsShiftKeyDown()
        {
            return
                (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;
        }

        bool IsEnabledModifierDown()
        {
            return
                (_cfg.EnableWindowsKeyModifier &&
                 IsWindowsKeyDown()) ||
                (_cfg.EnableShiftModifier &&
                 IsShiftKeyDown());
        }

        void SendEscape()
        {
            keybd_event(
                VK_ESCAPE,
                0,
                0,
                UIntPtr.Zero);

            keybd_event(
                VK_ESCAPE,
                0,
                KEYEVENTF_KEYUP,
                UIntPtr.Zero);
        }

        bool ShouldPollFast()
        {
            DateTime now = DateTime.UtcNow;

            return
                _movedOffscreen ||
                now <= _waitForWinReleaseUntil ||
                now <= _sendEscapeAt;
        }

        void Reset()
        {
            _state = State.Idle;

            _explorer = IntPtr.Zero;

            _windowsModifierUsedForDrag = false;
        }

        // --------------------------------------------------------

        IntPtr GetExplorerUnderCursor(POINT pt)
        {
            IntPtr hwnd =
                WindowFromPoint(pt);

            if (hwnd == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr root =
                GetAncestor(hwnd, GA_ROOT);

            if (IsExplorerWindow(root))
                return root;

            return IntPtr.Zero;
        }

        bool IsExplorerWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var sb =
                new StringBuilder(256);

            GetClassName(hwnd, sb, 256);

            string cn = sb.ToString();

            return
                cn == "CabinetWClass" ||
                cn == "ExploreWClass";
        }
    }
}
