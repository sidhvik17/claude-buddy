using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeBuddy
{
    internal sealed class Options
    {
        public bool Dev;
        public string SessionsDir;
        public string ProjectsDir;
        public bool HasForcedState;
        public BuddyState ForcedState;
    }

    // Borderless per-pixel-alpha window that hosts the sprite. It never takes focus,
    // stays out of the taskbar and Alt+Tab, and is redrawn through UpdateLayeredWindow.
    internal sealed class BuddyForm : Form
    {
        const int AnimIntervalMs = 80;
        const int ScanIntervalMs = 1000;
        static readonly int[] BasePixel = new int[] { 2, 3, 4 };
        static readonly string[] SizeNames = new string[] { "Small", "Medium", "Large" };

        readonly Options opts;
        readonly SessionScanner scanner;
        readonly Timer animTimer = new Timer();
        readonly Timer scanTimer = new Timer();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolTip tip = new ToolTip();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Dictionary<BuddyState, Icon> trayIcons = new Dictionary<BuddyState, Icon>();
        readonly List<IntPtr> iconHandles = new List<IntPtr>();

        List<SessionInfo> sessions = new List<SessionInfo>();
        BuddyState state = BuddyState.Sleep;
        int stateTick;
        int doneLeft;
        int dpi = 96;
        int px = 4;
        Bitmap canvas;
        string lastKey;
        string lastSummary = "";
        bool mouseDown;
        bool dragging;
        Point downCursor;
        Point downLocation;

        const int HotkeyId = 1;
        const int PreviewTicks = 75; // 6 s
        Hotkey hotkey;
        bool hotkeyRegistered;
        bool hidden;
        BuddyState previewState;
        int previewLeft;
        bool previewTimer;
        DateTime limitResetUtc = DateTime.MinValue;
        bool dpiSyncQueued;
        bool downIsRepeat;      // this press is the second half of a double-click
        IntPtr menuReturnTo;    // foreground window the sprite's menu took over from
        bool menuLaunched;      // the chosen item opened Claude: leave the foreground alone

        public BuddyForm(Options opts)
        {
            this.opts = opts;
            scanner = new SessionScanner(opts.SessionsDir, opts.ProjectsDir);
            if (Config.LimitResetTicks > 0 && Config.LimitResetTicks < DateTime.MaxValue.Ticks &&
                Config.LimitSeenTicks >= 0 && Config.LimitSeenTicks < DateTime.MaxValue.Ticks)
            {
                scanner.RestoreLimit(new DateTime(Config.LimitResetTicks, DateTimeKind.Utc), new DateTime(Config.LimitSeenTicks, DateTimeKind.Utc));
            }

            Text = "Claude Buddy";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = Config.TopMost;

            tip.ShowAlways = true;
            tip.InitialDelay = 400;

            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                BuildMenu();
                e.Cancel = false;
            };
            menu.Closed += delegate
            {
                // Showing the menu made this never-active window the foreground window.
                // Give the foreground back, unless the chosen item opened Claude.
                IntPtr target = menuReturnTo;
                menuReturnTo = IntPtr.Zero;
                if (target == IntPtr.Zero || !IsHandleCreated) return;
                // Closed is raised before the clicked item's Click handler, so decide afterwards.
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!menuLaunched && IsHandleCreated && Native.GetForegroundWindow() == Handle)
                    {
                        Native.SetForegroundWindow(target);
                    }
                });
            };

            tray.Text = "Claude Buddy";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (hidden) Guard(delegate { SetHidden(false); });
                else Guard(OnBuddyClick);
            };

            animTimer.Interval = AnimIntervalMs;
            animTimer.Tick += delegate { Guard(OnAnimTick); };
            scanTimer.Interval = ScanIntervalMs;
            scanTimer.Tick += delegate { Guard(OnScanTick); };

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                if (Config.TopMost) cp.ExStyle |= Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Layered window: all pixels come from UpdateLayeredWindow.
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered)
            {
                Native.UnregisterHotKey(Handle, HotkeyId);
                hotkeyRegistered = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            dpi = QueryDpi();
            Rescale(false);
            RestorePosition();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BuildTrayIcons();
            Guard(OnScanTick);
            Render();
            tray.Visible = true;
            animTimer.Start();
            scanTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            animTimer.Stop();
            scanTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            foreach (Icon icon in trayIcons.Values) icon.Dispose();
            foreach (IntPtr handle in iconHandles) Native.DestroyIcon(handle);
            ClearMenu();
            menu.Dispose();
            tip.Dispose();
            animTimer.Dispose();
            scanTimer.Dispose();
            if (canvas != null) canvas.Dispose();
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            }
            if (m.Msg == Native.WM_HOTKEY && (int)(long)m.WParam == HotkeyId)
            {
                Guard(delegate { SetHidden(!hidden); });
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == Native.WM_DPICHANGED && !dpiSyncQueued)
            {
                // Resizing here would run SetWindowPos inside the notification, which can
                // raise the next WM_DPICHANGED before this one returns. Settle afterwards.
                dpiSyncQueued = true;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { Guard(SyncDpi); });
                }
                catch (InvalidOperationException)
                {
                    dpiSyncQueued = false;
                }
            }
        }

        // Applies a DPI change once the move or display change that caused it has finished.
        void SyncDpi()
        {
            try
            {
                bool changed = false;
                for (int pass = 0; pass < 3 && IsHandleCreated; pass++)
                {
                    int current = QueryDpi();
                    if (current == dpi) break;
                    dpi = current;
                    Rescale(true);
                    changed = true;
                }
                if (!changed || mouseDown) return; // a drag in progress is settled by OnMouseUp
                ClampToWorkingArea();
                if (Config.X != Config.NoPosition) SavePosition();
            }
            finally
            {
                dpiSyncQueued = false;
            }
        }

        // ---- state ---------------------------------------------------------------------

        static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Write("error: " + ex);
            }
        }

        void OnScanTick()
        {
            List<SessionInfo> list = scanner.Scan();
            BuddyState next = opts.HasForcedState ? opts.ForcedState : SessionScanner.Aggregate(list);
            sessions = list;

            DateTime reset = scanner.LimitResetUtc;
            if (reset != limitResetUtc)
            {
                limitResetUtc = reset;
                // Kept in config.ini so the countdown survives a restart of the buddy.
                Config.LimitResetTicks = reset > DateTime.UtcNow ? reset.Ticks : 0;
                Config.LimitSeenTicks = reset > DateTime.UtcNow ? scanner.LimitSeenUtc.Ticks : 0;
                Config.Save();
                Log.Write(reset > DateTime.UtcNow
                    ? "usage limit in force, resets at " + reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                    : "usage limit no longer in force");
            }

            if (next != state)
            {
                Log.Write("state " + state + " -> " + next + DescribeCause(list, next));
                doneLeft = (state == BuddyState.Working && next == BuddyState.Idle) ? Sprite.DoneLength : 0;
                state = next;
                stateTick = 0;
                Icon icon;
                if (trayIcons.TryGetValue(state, out icon)) tray.Icon = icon;
            }

            string summary = Summary();
            if (summary != lastSummary)
            {
                lastSummary = summary;
                tip.SetToolTip(this, summary);
                tray.Text = summary.Length > 63 ? summary.Substring(0, 63) : summary;
            }

            if (Config.TopMost && IsHandleCreated && !hidden && !menu.Visible)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        void OnAnimTick()
        {
            stateTick = (stateTick + 1) & 0x3FFFFFFF;
            if (doneLeft > 0) doneLeft--;
            if (previewLeft > 0 && --previewLeft == 0) stateTick = 0;
            Render();
        }

        // ---- hide / show ---------------------------------------------------------------

        void RegisterHotkey()
        {
            hotkey = Hotkey.Parse(Config.Hotkey);
            hotkeyRegistered = false;
            if (hotkey == null) return;
            hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.Key);
            Log.Write(hotkeyRegistered
                ? "hotkey " + hotkey.Display + " registered"
                : "hotkey " + hotkey.Display + " is taken by another program; hide/show from the menu or tray icon instead");
        }

        // Hiding only removes the sprite: the tray icon stays and keeps showing the state.
        void SetHidden(bool hide)
        {
            if (hide == hidden) return;
            hidden = hide;
            if (hide)
            {
                animTimer.Stop();
                if (dragging) SavePosition();
                mouseDown = false;
                dragging = false;
                Hide();
            }
            else
            {
                Show();
                lastKey = null;
                Render();
                animTimer.Start();
            }
            Log.Write(hide ? "hidden" : "shown");
        }

        // Plays one state for a few seconds so every light can be seen on demand.
        void StartPreview(BuddyState s)
        {
            if (hidden) SetHidden(false);
            previewTimer = false;
            previewState = s;
            // Working has three acts (laptop, running, thinking): show the whole rotation.
            previewLeft = s == BuddyState.Working ? Sprite.WorkActTicks * Sprite.WorkActs : PreviewTicks;
            stateTick = 0;
        }

        static string DescribeCause(List<SessionInfo> list, BuddyState s)
        {
            foreach (SessionInfo info in list)
            {
                if (info.State == s) return "  (" + info.DisplayName + ": " + info.StateText + ")";
            }
            return "";
        }

        string Summary()
        {
            int waiting = 0, errors = 0, working = 0, idle = 0;
            foreach (SessionInfo s in sessions)
            {
                if (s.State == BuddyState.Waiting) waiting++;
                else if (s.State == BuddyState.Error) errors++;
                else if (s.State == BuddyState.Working) working++;
                else idle++;
            }
            if (sessions.Count == 0) return "Claude Buddy: no sessions running";
            List<string> parts = new List<string>();
            if (waiting > 0) parts.Add(waiting + (waiting == 1 ? " needs you" : " need you"));
            if (errors > 0) parts.Add(errors + (errors == 1 ? " error" : " errors"));
            if (working > 0) parts.Add(working + " working");
            if (idle > 0) parts.Add(idle + " idle");
            string text = "Claude Buddy: " + string.Join(", ", parts.ToArray());
            TimeSpan left = limitResetUtc - DateTime.UtcNow;
            if (left > TimeSpan.Zero) text += ", limit resets in " + Sprite.FormatDuration(left);
            return text;
        }

        // ---- drawing -------------------------------------------------------------------

        int QueryDpi()
        {
            try
            {
                uint value = Native.GetDpiForWindow(Handle);
                if (value >= 48 && value <= 960) return (int)value;
            }
            catch (EntryPointNotFoundException)
            {
            }
            using (Graphics g = CreateGraphics())
            {
                return (int)Math.Round(g.DpiX);
            }
        }

        // Recomputes the pixel scale. Keeps the bottom-right corner in place so the
        // buddy does not drift when the size changes.
        void Rescale(bool keepCorner)
        {
            px = Math.Max(2, (int)Math.Round(BasePixel[Config.Size] * dpi / 96.0, MidpointRounding.AwayFromZero));
            int w = Sprite.CanvasW * px;
            int h = Sprite.CanvasH * px;
            Point corner = new Point(Right, Bottom);

            Bitmap old = canvas;
            canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            if (old != null) old.Dispose();
            lastKey = null;

            if (keepCorner)
            {
                Rectangle target = new Rectangle(corner.X - w, corner.Y - h, w, h);
                // Resizing from the corner must not move the larger half onto another monitor
                // (that would change the DPI again): scale about the centre in that case.
                if (!Screen.FromRectangle(target).Equals(Screen.FromRectangle(Bounds)))
                {
                    target = new Rectangle(Left + (Width - w) / 2, Top + (Height - h) / 2, w, h);
                }
                SetBounds(target.X, target.Y, w, h);
            }
            else
            {
                Size = new Size(w, h);
            }
            if (IsHandleCreated && Visible) Render();
        }

        void Render()
        {
            if (canvas == null || !IsHandleCreated || hidden) return;
            BuddyState shown = (doneLeft > 0 && state == BuddyState.Idle) ? BuddyState.Done : state;
            if (previewLeft > 0) shown = previewState;
            Pose pose = Sprite.GetPose(shown, stateTick);
            string timer = TimerText(shown);
            if (timer != null) Sprite.ApplyTimer(pose, timer);
            string key = pose.Key();
            if (key == lastKey) return;

            using (Graphics g = Graphics.FromImage(canvas))
            {
                Sprite.Render(g, pose, px);
            }
            if (Push()) lastKey = key;
        }

        // Countdown for the forehead while a usage limit is in force. Not shown over the
        // working and waiting animations: if Claude is running, the limit is not the news.
        string TimerText(BuddyState shown)
        {
            if (previewLeft > 0) return previewTimer ? Sprite.FormatCountdown(TimeSpan.FromMinutes(154)) : null;
            if (shown != BuddyState.Idle && shown != BuddyState.Error && shown != BuddyState.Sleep) return null;
            TimeSpan left = limitResetUtc - DateTime.UtcNow;
            return left > TimeSpan.Zero ? Sprite.FormatCountdown(left) : null;
        }

        bool Push()
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = canvas.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);
                Native.SIZE size = new Native.SIZE();
                size.CX = canvas.Width;
                size.CY = canvas.Height;
                Native.POINT source = new Native.POINT();
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                return Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, oldBitmap);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        void BuildTrayIcons()
        {
            int size = SystemInformation.SmallIconSize.Width;
            BuddyState[] states = new BuddyState[] { BuddyState.Sleep, BuddyState.Idle, BuddyState.Working, BuddyState.Error, BuddyState.Waiting };
            foreach (BuddyState s in states)
            {
                using (Bitmap bmp = Sprite.MakeIconBitmap(size, s, s != BuddyState.Sleep))
                {
                    IntPtr handle = bmp.GetHicon();
                    iconHandles.Add(handle);
                    trayIcons[s] = Icon.FromHandle(handle);
                }
            }
            tray.Icon = trayIcons[state];
        }

        // ---- placement -----------------------------------------------------------------

        void ResetPosition(bool save)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int margin = px * 2;
            Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);
            if (save) SavePosition();
        }

        // Saved position if there is one, else the default corner; then the visibility fallback.
        void RestorePosition()
        {
            if (mouseDown) return; // the user is placing it right now
            if (Config.X != Config.NoPosition && Config.Y != Config.NoPosition) Location = new Point(Config.X, Config.Y);
            else ResetPosition(false);
            EnsureOnScreen();
        }

        void ClampToWorkingArea()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        void EnsureOnScreen()
        {
            Rectangle bounds = Bounds;
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle visible = Rectangle.Intersect(screen.WorkingArea, bounds);
                if (visible.Width >= bounds.Width / 2 && visible.Height >= bounds.Height / 2) return;
            }
            ResetPosition(false);
        }

        void SavePosition()
        {
            Config.X = Left;
            Config.Y = Top;
            Config.Save();
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate { Guard(RestorePosition); });
            }
            catch (InvalidOperationException)
            {
            }
        }

        // ---- input ---------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true;
            dragging = false;
            downIsRepeat = e.Clicks > 1;
            downCursor = Cursor.Position;
            downLocation = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!mouseDown) return;
            Point now = Cursor.Position;
            int dx = now.X - downCursor.X;
            int dy = now.Y - downCursor.Y;
            if (!dragging)
            {
                Size slop = SystemInformation.DragSize;
                if (Math.Abs(dx) < slop.Width && Math.Abs(dy) < slop.Height) return;
                dragging = true;
            }
            Location = new Point(downLocation.X + dx, downLocation.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                if (!mouseDown) ShowMenu(); // not in the middle of a left-button press or drag
                return;
            }
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            if (dragging)
            {
                dragging = false;
                EnsureOnScreen();
                SavePosition();
            }
            else if (!downIsRepeat)
            {
                // The second press of a double-click must not open Claude a second time.
                Guard(OnBuddyClick);
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture)
            {
                // A normal release has already cleared dragging; this is a drag cut short.
                bool commit = dragging;
                mouseDown = false;
                dragging = false;
                if (commit)
                {
                    Guard(delegate
                    {
                        EnsureOnScreen();
                        SavePosition();
                    });
                }
            }
        }

        // Left click: jump to whichever session most needs attention, else the Code tab.
        void OnBuddyClick()
        {
            SessionInfo target = null;
            foreach (SessionInfo s in sessions)
            {
                // sessions are sorted most-urgent first
                if (s.State == BuddyState.Waiting || s.State == BuddyState.Error || s.State == BuddyState.Working)
                {
                    target = s;
                    break;
                }
            }
            // Only red that was actually on the lamp counts as seen: while a session is
            // waiting, the lamp is yellow and other sessions' errors were never shown.
            if (target != null && target.State == BuddyState.Error) scanner.Acknowledge(sessions);
            if (target != null) Launcher.OpenSession(target);
            else Launcher.OpenCodeHome();
        }

        void ShowMenu()
        {
            // A menu owned by a never-active window only closes on an outside click if
            // its owner is foreground first. menu.Closed hands the foreground back.
            IntPtr previous = Native.GetForegroundWindow();
            menuReturnTo = previous != Handle ? previous : IntPtr.Zero;
            menuLaunched = false;
            Native.SetForegroundWindow(Handle);
            menu.Show(Cursor.Position);
        }

        void ClearMenu()
        {
            DisposeItems(menu.Items);
        }

        static void DisposeItems(ToolStripItemCollection collection)
        {
            ToolStripItem[] items = new ToolStripItem[collection.Count];
            collection.CopyTo(items, 0);
            collection.Clear();
            foreach (ToolStripItem item in items)
            {
                ToolStripDropDownItem parent = item as ToolStripDropDownItem;
                if (parent != null && parent.HasDropDownItems) DisposeItems(parent.DropDownItems);
                Image image = item.Image;
                item.Image = null;
                if (image != null) image.Dispose();
                item.Dispose();
            }
        }

        void AddPreview(ToolStripMenuItem parent, string text, BuddyState s)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Image = Dot(Sprite.LedColor(s));
            item.Click += delegate { Guard(delegate { StartPreview(s); }); };
            parent.DropDownItems.Add(item);
        }

        void BuildMenu()
        {
            ClearMenu();

            ToolStripMenuItem header = new ToolStripMenuItem(lastSummary.Length > 0 ? lastSummary : "Claude Buddy");
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            foreach (SessionInfo s in sessions)
            {
                SessionInfo session = s;
                string name = session.DisplayName;
                if (name.Length > 44) name = name.Substring(0, 43) + "…";
                ToolStripMenuItem item = new ToolStripMenuItem(name.Replace("&", "&&") + "   —   " + session.StateText.Replace("&", "&&"));
                item.Image = Dot(Sprite.LedColor(session.State));
                item.Click += delegate
                {
                    menuLaunched = true;
                    Guard(delegate { Launcher.OpenSession(session); });
                };
                menu.Items.Add(item);
            }
            if (sessions.Count > 0) menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem open = new ToolStripMenuItem("Open Claude Code");
            open.Click += delegate
            {
                menuLaunched = true;
                Guard(Launcher.OpenCodeHome);
            };
            menu.Items.Add(open);

            ToolStripMenuItem hide = new ToolStripMenuItem(hidden ? "Show buddy" : "Hide buddy");
            if (hotkeyRegistered) hide.ShortcutKeyDisplayString = hotkey.Display;
            hide.Click += delegate { Guard(delegate { SetHidden(!hidden); }); };
            menu.Items.Add(hide);

            ToolStripMenuItem preview = new ToolStripMenuItem("Preview light");
            AddPreview(preview, "Green  \u2014  working", BuddyState.Working);
            AddPreview(preview, "Yellow  \u2014  needs you", BuddyState.Waiting);
            AddPreview(preview, "Red  \u2014  error", BuddyState.Error);
            AddPreview(preview, "Grey  \u2014  idle", BuddyState.Idle);
            AddPreview(preview, "Off  \u2014  no sessions", BuddyState.Sleep);
            ToolStripMenuItem limitItem = new ToolStripMenuItem("Red  \u2014  usage limit timer");
            limitItem.Image = Dot(Sprite.LedRed);
            limitItem.Click += delegate
            {
                Guard(delegate
                {
                    StartPreview(BuddyState.Error);
                    previewTimer = true;
                });
            };
            preview.DropDownItems.Add(limitItem);
            menu.Items.Add(preview);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem sizeMenu = new ToolStripMenuItem("Size");
            for (int i = 0; i < SizeNames.Length; i++)
            {
                int index = i;
                ToolStripMenuItem sizeItem = new ToolStripMenuItem(SizeNames[i]);
                sizeItem.Checked = Config.Size == i;
                sizeItem.Click += delegate
                {
                    Guard(delegate
                    {
                        Config.Size = index;
                        Rescale(true);
                        EnsureOnScreen();
                        SavePosition();
                    });
                };
                sizeMenu.DropDownItems.Add(sizeItem);
            }
            menu.Items.Add(sizeMenu);

            ToolStripMenuItem top = new ToolStripMenuItem("Always on top");
            top.Checked = Config.TopMost;
            top.Click += delegate
            {
                Config.TopMost = !Config.TopMost;
                TopMost = Config.TopMost;
                Config.Save();
            };
            menu.Items.Add(top);

            ToolStripMenuItem auto = new ToolStripMenuItem("Start with Windows");
            auto.Checked = Config.AutoStart;
            auto.Enabled = !opts.Dev;
            auto.Click += delegate
            {
                Config.AutoStart = !Config.AutoStart;
                AutoStart.Apply(Config.AutoStart);
                Config.Save();
            };
            menu.Items.Add(auto);

            ToolStripMenuItem reset = new ToolStripMenuItem("Reset position");
            reset.Click += delegate { Guard(delegate { ResetPosition(true); }); };
            menu.Items.Add(reset);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);
        }

        static Bitmap Dot(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(brush, 3, 3, 10, 10);
            }
            return bmp;
        }
    }
}
