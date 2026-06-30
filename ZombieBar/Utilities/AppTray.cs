#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// System tray icon for ZombieBar. It hosts the DragThrough options, a "Show taskbar" toggle
    /// for the additional taskbar, and Exit (the only place that quits the whole application).
    /// Menu strings come from the active language resource dictionary.
    /// </summary>
    public class AppTray : IDisposable
    {
        private readonly NotifyIcon _tray;
        private readonly Action<bool> _setTaskbarVisible;
        private readonly Action _openFeedback;
        private readonly Action _openAbout;
        private readonly Action _exit;

        private ToolStripMenuItem _titleItem = null!;
        private ToolStripMenuItem _winKeyItem = null!;
        private ToolStripMenuItem _shiftItem = null!;
        private ToolStripMenuItem _minimizeItem = null!;
        private ToolStripMenuItem _dismissSearchItem = null!;
        private ToolStripMenuItem _showTaskbarItem = null!;
        private ToolStripMenuItem _shareItem = null!;
        private ToolStripMenuItem _coffeeItem = null!;
        private ToolStripMenuItem _feedbackItem = null!;
        private ToolStripMenuItem _aboutItem = null!;
        private ToolStripMenuItem _exitItem = null!;

        // Localizable items of the "Share the app" submenu, refreshed with the menu (key + fallback).
        private readonly List<(ToolStripMenuItem item, string key, string fallback)> _shareLocalized = new();

        /// <param name="setTaskbarVisible">Shows (true) or hides (false) the additional taskbar.</param>
        /// <param name="openFeedback">Opens the feedback form ("Report a problem or suggestion").</param>
        /// <param name="openAbout">Opens the "About" window.</param>
        /// <param name="exit">Quits the whole application.</param>
        public AppTray(Action<bool> setTaskbarVisible, Action openFeedback, Action openAbout, Action exit)
        {
            _setTaskbarVisible = setTaskbarVisible;
            _openFeedback = openFeedback;
            _openAbout = openAbout;
            _exit = exit;

            _tray = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = BuildTooltip(),
                ContextMenuStrip = BuildMenu()
            };

            _tray.MouseUp += TrayMouseUp;
        }

        /// <summary>Reflects the current ShowAdditionalTaskbar setting in the menu check.</summary>
        public void UpdateShowTaskbarCheck()
        {
            _showTaskbarItem.Checked = Settings.Instance.ShowAdditionalTaskbar;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { AutoClose = true, Font = SystemFonts.MenuFont };
            menu.ImageScalingSize = new Size(IconSize, IconSize);
            menu.Opening += (_, _) => RefreshMenu();

            _titleItem = new ToolStripMenuItem { Enabled = false };
            menu.Items.Add(_titleItem);
            menu.Items.Add(new ToolStripSeparator());

            _winKeyItem = AddCheck(menu,
                () => Settings.Instance.EnableWindowsKeyModifier,
                v => Settings.Instance.EnableWindowsKeyModifier = v);

            _shiftItem = AddCheck(menu,
                () => Settings.Instance.EnableShiftModifier,
                v => Settings.Instance.EnableShiftModifier = v);

            menu.Items.Add(new ToolStripSeparator());

            _minimizeItem = AddCheck(menu,
                () => Settings.Instance.MinimizeExplorerAfterSuccessfulDrag,
                v => Settings.Instance.MinimizeExplorerAfterSuccessfulDrag = v);

            _dismissSearchItem = AddCheck(menu,
                () => Settings.Instance.DismissWindowsSearchWithEscape,
                v => Settings.Instance.DismissWindowsSearchWithEscape = v);

            menu.Items.Add(new ToolStripSeparator());

            _showTaskbarItem = new ToolStripMenuItem();
            _showTaskbarItem.Click += (_, _) => _setTaskbarVisible(!Settings.Instance.ShowAdditionalTaskbar);
            menu.Items.Add(_showTaskbarItem);

            menu.Items.Add(new ToolStripSeparator());

            _shareItem = BuildShareMenu();
            menu.Items.Add(_shareItem);

            // Coffee cup on the Buy Me a Coffee brand yellow.
            _coffeeItem = new ToolStripMenuItem { Image = DrawTile("☕", FromHex("#FFDD00"), FromHex("#3A2A00"), "Segoe UI Symbol", 0.6f) };
            _coffeeItem.Click += (_, _) => OpenUrl(AppLinks.BuyMeACoffeeUrl);
            menu.Items.Add(_coffeeItem);

            _feedbackItem = new ToolStripMenuItem();
            _feedbackItem.Click += (_, _) => _openFeedback();
            menu.Items.Add(_feedbackItem);

            // "About" - the second-to-last item, just above Exit.
            _aboutItem = new ToolStripMenuItem();
            _aboutItem.Click += (_, _) => _openAbout();
            menu.Items.Add(_aboutItem);

            menu.Items.Add(new ToolStripSeparator());

            _exitItem = new ToolStripMenuItem();
            _exitItem.Click += (_, _) => _exit();
            menu.Items.Add(_exitItem);

            RefreshMenu();
            return menu;
        }

        private static ToolStripMenuItem AddCheck(ContextMenuStrip menu, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem { Checked = get() };
            item.Click += (_, _) =>
            {
                set(!get());
                item.Checked = get();
            };
            menu.Items.Add(item);
            return item;
        }

        // Apply current language strings and check states (re-run each time the menu opens so it
        // tracks language and settings changes without a restart).
        private void RefreshMenu()
        {
            _titleItem.Text = Loc("tray_drag_title", "Temporarily hide Explorer while dragging files");
            _winKeyItem.Text = Loc("tray_use_windows_key", "Use Windows key as drag modifier");
            _shiftItem.Text = Loc("tray_enable_shift", "Enable Shift modifier (may not work in some apps, e.g. DaVinci Resolve)");
            _minimizeItem.Text = Loc("tray_minimize_explorer", "Minimize Explorer after successful drag");
            _dismissSearchItem.Text = Loc("tray_dismiss_search", "Dismiss Windows Search with Escape");
            _showTaskbarItem.Text = Loc("tray_show_taskbar", "Show taskbar");
            _shareItem.Text = Loc("share_app", "Share the app");
            foreach ((ToolStripMenuItem item, string key, string fallback) in _shareLocalized)
            {
                item.Text = Loc(key, fallback);
            }
            _coffeeItem.Text = Loc("tray_buy_coffee", "Buy me a coffee");
            _feedbackItem.Text = Loc("tray_feedback", "Report a problem or suggestion...");
            _aboutItem.Text = Loc("tray_about", "About");
            _exitItem.Text = Loc("tray_exit", "Exit");

            _winKeyItem.Checked = Settings.Instance.EnableWindowsKeyModifier;
            _shiftItem.Checked = Settings.Instance.EnableShiftModifier;
            _minimizeItem.Checked = Settings.Instance.MinimizeExplorerAfterSuccessfulDrag;
            _dismissSearchItem.Checked = Settings.Instance.DismissWindowsSearchWithEscape;
            _showTaskbarItem.Checked = Settings.Instance.ShowAdditionalTaskbar;
        }

        // "Share the app" submenu: copy link plus the share networks most used worldwide. Each item
        // opens the network's web "share" intent pre-filled with the project URL and a short blurb;
        // "Copy link" puts the URL on the clipboard instead. Icons are drawn in code (brand-colored
        // rounded tiles) so no image assets are needed.
        private ToolStripMenuItem BuildShareMenu()
        {
            var share = new ToolStripMenuItem { Image = Mdl2("", FromHex("#444444")) };  // share glyph
            share.DropDown.ImageScalingSize = new Size(IconSize, IconSize);

            AddShare(share, "copy", "share_copy_link", "Copy link", Mdl2("", FromHex("#5B5B5B")));
            share.DropDownItems.Add(new ToolStripSeparator());

            AddShare(share, "x", "share_tweet", "Tweet (X)", Tile("X", Color.Black));        // X
            AddShare(share, "telegram", "share_telegram", "Post on Telegram", Symbol("✈", FromHex("#229ED9")));
            AddShare(share, "whatsapp", "share_whatsapp", "WhatsApp", Symbol("☎", FromHex("#25D366")));
            AddShare(share, "facebook", "share_facebook", "Facebook", Tile("f", FromHex("#1877F2")));
            AddShare(share, "linkedin", "share_linkedin", "LinkedIn", Tile("in", FromHex("#0A66C2"), 0.42f));
            AddShare(share, "reddit", "share_reddit", "Reddit", Tile("R", FromHex("#FF4500")));
            AddShare(share, "email", "share_email", "Email", Mdl2("", FromHex("#6B6B6B")));

            return share;
        }

        private void AddShare(ToolStripMenuItem parent, string kind, string key, string fallback, Image icon)
        {
            var item = new ToolStripMenuItem(Loc(key, fallback), icon);
            item.Click += (_, _) => Share(kind);
            parent.DropDownItems.Add(item);
            _shareLocalized.Add((item, key, fallback));
        }

        // Either copies the project link or opens the chosen network's web share intent.
        private void Share(string kind)
        {
            string url = AppLinks.ProjectUrl;
            string text = Loc("share_text", "ZombieBar - a Windows 11 taskbar replacement");

            if (kind == "copy")
            {
                try { Clipboard.SetText(url); } catch { /* clipboard may be busy */ }
                try
                {
                    _tray.ShowBalloonTip(2000, Loc("share_app", "Share the app"),
                        Loc("share_link_copied", "Link copied to clipboard"), ToolTipIcon.Info);
                }
                catch { }
                return;
            }

            string E(string s) => Uri.EscapeDataString(s);
            string? target = kind switch
            {
                "x" => $"https://twitter.com/intent/tweet?text={E(text)}&url={E(url)}",
                "telegram" => $"https://t.me/share/url?url={E(url)}&text={E(text)}",
                "whatsapp" => $"https://wa.me/?text={E(text + " " + url)}",
                "facebook" => $"https://www.facebook.com/sharer/sharer.php?u={E(url)}",
                "linkedin" => $"https://www.linkedin.com/sharing/share-offsite/?url={E(url)}",
                "reddit" => $"https://www.reddit.com/submit?url={E(url)}&title={E(text)}",
                "email" => $"mailto:?subject={E(text)}&body={E(text + "\n\n" + url)}",
                _ => null
            };

            if (target != null)
            {
                OpenUrl(target);
            }
        }

        // Opens a URL in the user's default browser.
        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // === Icon drawing =================================================================
        // Menu icon edge length, scaled for the current DPI so the tiles stay crisp.
        private static int IconSize => Math.Max(16, SystemInformation.SmallIconSize.Width);

        private static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

        // A brand-colored rounded tile with a white letter (Segoe UI).
        private static Image Tile(string text, Color bg, float emRatio = 0.6f) =>
            DrawTile(text, bg, Color.White, "Segoe UI", emRatio);

        // A tile using a Unicode symbol glyph (Segoe UI Symbol) - e.g. a plane / phone.
        private static Image Symbol(string glyph, Color bg, float emRatio = 0.54f) =>
            DrawTile(glyph, bg, Color.White, "Segoe UI Symbol", emRatio);

        // A tile using a Segoe MDL2 Assets glyph (link, mail, share).
        private static Image Mdl2(string glyph, Color bg, float emRatio = 0.56f) =>
            DrawTile(glyph, bg, Color.White, "Segoe MDL2 Assets", emRatio);

        private static Image DrawTile(string glyph, Color bg, Color fg, string fontFamily, float emRatio)
        {
            int size = IconSize;
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
                using (GraphicsPath path = RoundedRect(rect, size * 0.28f))
                using (var brush = new SolidBrush(bg))
                {
                    g.FillPath(brush, path);
                }

                using var font = new Font(fontFamily, size * emRatio, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(fg);
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, font, text, new RectangleF(0, -size * 0.02f, size, size), fmt);
            }
            return bmp;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static string Loc(string key, string fallback)
        {
            return System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
        }

        // Tray tooltip: the (localized) product name followed by the product version.
        private static string BuildTooltip()
        {
            string name = Loc("tray_tooltip", "DragThrough");
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{name} {version}" : name;
        }

        private void TrayMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            ContextMenuStrip? menu = _tray.ContextMenuStrip;
            if (menu == null)
            {
                return;
            }

            if (menu.Visible)
            {
                menu.Close(ToolStripDropDownCloseReason.AppClicked);
                return;
            }

            ShowMenuAboveCursor(menu);
        }

        private static void ShowMenuAboveCursor(ContextMenuStrip menu)
        {
            Size size = menu.GetPreferredSize(Size.Empty);
            Point cursor = Cursor.Position;
            Rectangle screen = Screen.FromPoint(cursor).WorkingArea;

            int x = cursor.X - size.Width;
            int y = cursor.Y - size.Height - 8;

            x = Math.Max(screen.Left, Math.Min(x, screen.Right - size.Width));
            y = Math.Max(screen.Top, Math.Min(y, screen.Bottom - size.Height));

            // When the menu is shown manually (left click), it does not receive the foreground
            // focus that WinForms gives it automatically on right click. Without that, clicking
            // elsewhere does not dismiss the menu. Setting the foreground window restores the
            // expected "click outside to close" behavior.
            ManagedShell.Interop.NativeMethods.SetForegroundWindow(menu.Handle);
            menu.Show(new Point(x, y));
        }

        private static Icon LoadTrayIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string? name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("tray_icon.ico", StringComparison.OrdinalIgnoreCase));

                if (name != null)
                {
                    using Stream? stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        return new Icon(stream, SystemInformation.SmallIconSize);
                    }
                }
            }
            catch { }

            return SystemIcons.Application;
        }

        public void Dispose()
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }
}
