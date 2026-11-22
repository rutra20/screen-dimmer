// Screen Dimmer - a compact dark screen dimmer for Windows
// Darkens the display below the hardware minimum and shifts its colour
// temperature towards warm, using click-through layered overlays.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenDimmer
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;
    }

    /// <summary>Full-screen colour layer that never takes focus or clicks.</summary>
    internal sealed class OverlayForm : Form
    {
        public OverlayForm(Rectangle bounds, Color color)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = color;
            ShowInTaskbar = false;
            TopMost = true;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Opacity = 0.0;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT - click-through
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW - no Alt+Tab entry
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_MOUSEACTIVATE = 0x0021;

            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = new IntPtr(-1); // HTTRANSPARENT
                return;
            }
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref m);
        }

        public void SetOpacity(double value)
        {
            if (value < 0.0) value = 0.0;
            if (value > 1.0) value = 1.0;
            Opacity = value;
        }
    }

    /// <summary>Hand-drawn slider that matches the dark panel.</summary>
    internal sealed class LevelSlider : Control
    {
        private int minimum;
        private int maximum = 95;
        private int current = 40;
        private bool dragging;
        private bool hovering;
        private Color accent = Color.FromArgb(250, 204, 21);

        public event EventHandler ValueChanged;
        public event EventHandler DragFinished;

        public LevelSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
            Height = 20;
            Cursor = Cursors.Hand;
        }

        public Color Accent
        {
            get { return accent; }
            set { accent = value; Invalidate(); }
        }

        public int Minimum
        {
            get { return minimum; }
            set { minimum = value; Value = current; }
        }

        public int Maximum
        {
            get { return maximum; }
            set { maximum = value; Value = current; }
        }

        public int Value
        {
            get { return current; }
            set
            {
                int clamped = Math.Max(minimum, Math.Min(maximum, value));
                if (clamped == current) return;
                current = clamped;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int left = 5;
            int right = Width - 5;
            int cy = Height / 2;
            const int trackHeight = 5;

            using (GraphicsPath groove = Rounded(new Rectangle(left, cy - trackHeight / 2, right - left, trackHeight), trackHeight / 2))
            using (SolidBrush brush = new SolidBrush(Theme.Track))
            {
                g.FillPath(brush, groove);
            }

            double ratio = maximum == minimum ? 0.0 : (double)(current - minimum) / (maximum - minimum);
            int knobX = left + (int)Math.Round((right - left) * ratio);
            int filledWidth = Math.Max(trackHeight, knobX - left);

            using (GraphicsPath filled = Rounded(new Rectangle(left, cy - trackHeight / 2, filledWidth, trackHeight), trackHeight / 2))
            using (SolidBrush brush = new SolidBrush(accent))
            {
                g.FillPath(brush, filled);
            }

            int radius = hovering || dragging ? 7 : 6;
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(55, accent)))
            {
                g.FillEllipse(glow, knobX - radius - 4, cy - radius - 4, (radius + 4) * 2, (radius + 4) * 2);
            }
            using (SolidBrush knob = new SolidBrush(accent))
            {
                g.FillEllipse(knob, knobX - radius, cy - radius, radius * 2, radius * 2);
            }
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                Capture = true;
                SetFromX(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                Capture = false;
                if (DragFinished != null) DragFinished(this, EventArgs.Empty);
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value = current + (e.Delta > 0 ? 5 : -5);
            base.OnMouseWheel(e);
        }

        private void SetFromX(int x)
        {
            const int left = 5;
            int right = Width - 5;
            double ratio = (double)(x - left) / Math.Max(1, right - left);
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            Value = minimum + (int)Math.Round((maximum - minimum) * ratio);
        }
    }

    /// <summary>Subtle on-screen display (OSD) HUD shown when adjusting settings via hotkeys.</summary>
    internal sealed class OsdForm : Form
    {
        private string titleText = string.Empty;
        private string valueText = string.Empty;
        private int currentPercent;
        private Color accentColor = Theme.AccentDim;
        private readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer();

        public OsdForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Size = new Size(200, 52);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            Opacity = 0.0;

            hideTimer.Interval = 1200;
            hideTimer.Tick += delegate
            {
                hideTimer.Stop();
                fadeTimer.Start();
            };

            fadeTimer.Interval = 15;
            fadeTimer.Tick += delegate
            {
                double next = Opacity - 0.12;
                if (next <= 0.0)
                {
                    Opacity = 0.0;
                    fadeTimer.Stop();
                    Hide();
                }
                else
                {
                    Opacity = next;
                }
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT - click-through
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW - no Alt+Tab entry
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE - never steal focus
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_MOUSEACTIVATE = 0x0021;
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = new IntPtr(-1); // HTTRANSPARENT
                return;
            }
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref m);
        }

        public void ShowOsd(string title, string value, int percent, Color accent)
        {
            titleText = title;
            valueText = value;
            currentPercent = Math.Max(0, Math.Min(100, percent));
            accentColor = accent;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 52);

            fadeTimer.Stop();
            Opacity = 0.95;
            Invalidate();

            if (!Visible)
            {
                Show();
            }

            TopMost = false;
            TopMost = true;

            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (SolidBrush bg = new SolidBrush(Theme.Background))
            using (Pen border = new Pen(Theme.Border))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            using (Font captionFont = Theme.Font(8.25f, FontStyle.Regular))
            using (Font valFont = Theme.Font(8.25f, FontStyle.Bold))
            using (SolidBrush mutedBrush = new SolidBrush(Theme.Muted))
            using (SolidBrush textBrush = new SolidBrush(Theme.Text))
            {
                g.DrawString(titleText, captionFont, mutedBrush, 14, 8);
                SizeF valSize = g.MeasureString(valueText, valFont);
                g.DrawString(valueText, valFont, textBrush, Width - 14 - valSize.Width, 8);
            }

            const int trackX = 14;
            const int trackY = 32;
            int trackW = Width - 28;
            const int trackH = 5;

            using (GraphicsPath groove = RoundedPath(new Rectangle(trackX, trackY, trackW, trackH), 2))
            using (SolidBrush trackBrush = new SolidBrush(Theme.Track))
            {
                g.FillPath(trackBrush, groove);
            }

            int fillW = (int)Math.Round(trackW * (currentPercent / 100.0));
            if (fillW > 0)
            {
                using (GraphicsPath filled = RoundedPath(new Rectangle(trackX, trackY, Math.Max(trackH, fillW), trackH), 2))
                using (SolidBrush barBrush = new SolidBrush(accentColor))
                {
                    g.FillPath(barBrush, filled);
                }
            }
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hideTimer.Stop();
                hideTimer.Dispose();
                fadeTimer.Stop();
                fadeTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Colours and metrics of the panel.</summary>
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(22, 22, 28);
        public static readonly Color Border = Color.FromArgb(44, 44, 54);
        public static readonly Color Track = Color.FromArgb(52, 52, 64);
        public static readonly Color Text = Color.FromArgb(236, 236, 242);
        public static readonly Color Muted = Color.FromArgb(142, 142, 158);
        public static readonly Color ButtonFace = Color.FromArgb(34, 34, 42);
        public static readonly Color ButtonHover = Color.FromArgb(46, 46, 57);
        public static readonly Color ButtonDown = Color.FromArgb(58, 58, 72);
        public static readonly Color AccentDim = Color.FromArgb(250, 204, 21);
        public static readonly Color AccentWarm = Color.FromArgb(251, 146, 60);
        public static readonly Color CloseHover = Color.FromArgb(66, 34, 38);
        public static readonly Color CloseText = Color.FromArgb(255, 160, 160);

        public static Font Font(float size, FontStyle style)
        {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }
    }

    internal sealed class PanelForm : Form
    {
        private const int HotkeyDim = 1;
        private const int HotkeyBright = 2;
        private const int HotkeyShow = 3;
        private const int HotkeyQuit = 4;
        private const int HotkeyWarm = 5;
        private const int HotkeyCool = 6;
        private const int HotkeyOff = 7;

        private const int MinLevel = 0;
        private const int MaxLevel = 95;
        private const int StepLevel = 5;

        private const int MinWarm = 0;
        private const int MaxWarm = 100;
        private const int StepWarm = 5;

        private const double TintStrength = 0.20;
        private const int KelvinNeutral = 6500;
        private const int KelvinWarmest = 2700;

        private const int PanelWidth = 268;
        private const int PanelHeight = 166;
        private const int CornerRadius = 14;

        private readonly List<OverlayForm> shadeOverlays = new List<OverlayForm>();
        private readonly List<OverlayForm> tintOverlays = new List<OverlayForm>();
        private readonly string settingsPath;

        private LevelSlider dimSlider;
        private LevelSlider warmSlider;
        private Label dimValue;
        private Label warmValue;
        private ToolTip tips;
        private NotifyIcon tray;
        private ToolStripMenuItem autoStartItem;
        private readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer();
        private OsdForm osd;

        private int level = 40;
        private int warm;
        private int targetLevel = -1;
        private int targetWarm = -1;
        private bool updating;
        private bool draggingPanel;
        private bool allowVisible;
        private bool usesDwmRounding;
        private Point dragStartCursor;
        private Point dragStartLocation;

        public PanelForm(bool startInTray)
        {
            allowVisible = !startInTray;

            settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScreenDimmer",
                "settings.txt");

            Icon = BuildIcon();
            Text = "Screen Dimmer";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(PanelWidth, PanelHeight);
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            LoadSettings();
            tips = new ToolTip();
            tips.InitialDelay = 350;
            tips.ReshowDelay = 80;
            BuildUi();
            BuildTray();
            osd = new OsdForm();
            CreateOverlays();
            MoveToSavedPlace();

            fadeTimer.Interval = 15;
            fadeTimer.Tick += OnFadeTick;

            saveTimer.Interval = 400;
            saveTimer.Tick += delegate
            {
                saveTimer.Stop();
                SaveSettings();
            };

            animTimer.Interval = 15;
            animTimer.Tick += OnAnimTick;

            Opacity = 1.0;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        // ---------- chrome ----------

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowStyle();
            RegisterHotkeys();
        }

        private void ApplyWindowStyle()
        {
            try
            {
                int dark = 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int cornerPref = Native.DWMWCP_ROUND;
                int hr = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
                if (hr == 0)
                {
                    usesDwmRounding = true;
                }
            }
            catch
            {
            }

            if (!usesDwmRounding)
            {
                ApplyRoundedShape();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!usesDwmRounding)
            {
                ApplyRoundedShape();
            }
        }

        private void ApplyRoundedShape()
        {
            if (usesDwmRounding) return;
            if (Width <= 0 || Height <= 0 || !IsHandleCreated) return;

            int diameter = CornerRadius * 2;
            IntPtr region = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, diameter, diameter);
            Native.SetWindowRgn(Handle, region, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Theme.Border))
            using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Hide();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- interface ----------

        private void BuildUi()
        {
            Panel dragBar = new Panel();
            dragBar.SetBounds(0, 0, PanelWidth, 34);
            dragBar.BackColor = Theme.Background;
            dragBar.Cursor = Cursors.SizeAll;
            dragBar.MouseDown += OnDragStart;
            dragBar.MouseMove += OnDragMove;
            dragBar.MouseUp += OnDragEnd;

            Label title = new Label();
            title.Text = "Screen Dimmer";
            title.Font = Theme.Font(8.75f, FontStyle.Bold);
            title.ForeColor = Theme.Text;
            title.BackColor = Color.Transparent;
            title.SetBounds(16, 9, 160, 18);
            title.MouseDown += OnDragStart;
            title.MouseMove += OnDragMove;
            title.MouseUp += OnDragEnd;

            Button close = MakeCloseButton(PanelWidth - 34, 7);
            close.Click += delegate { Hide(); };
            tips.SetToolTip(close, "Hide to tray (Esc)");

            Label dimCaption = new Label();
            dimCaption.Text = "Dim";
            dimCaption.Font = Theme.Font(8.25f, FontStyle.Regular);
            dimCaption.ForeColor = Theme.Muted;
            dimCaption.BackColor = Color.Transparent;
            dimCaption.SetBounds(16, 38, 120, 16);

            dimValue = new Label();
            dimValue.TextAlign = ContentAlignment.MiddleRight;
            dimValue.Font = Theme.Font(8.25f, FontStyle.Bold);
            dimValue.ForeColor = Theme.Text;
            dimValue.BackColor = Color.Transparent;
            dimValue.SetBounds(PanelWidth - 86, 38, 54, 16);

            dimSlider = new LevelSlider();
            dimSlider.SetBounds(10, 55, PanelWidth - 20, 20);
            dimSlider.Minimum = MinLevel;
            dimSlider.Maximum = MaxLevel;
            dimSlider.Accent = Theme.AccentDim;
            dimSlider.Value = level;
            dimSlider.ValueChanged += OnDimChanged;
            dimSlider.DragFinished += delegate { SaveSettings(); };

            Label warmCaption = new Label();
            warmCaption.Text = "Warmth";
            warmCaption.Font = Theme.Font(8.25f, FontStyle.Regular);
            warmCaption.ForeColor = Theme.Muted;
            warmCaption.BackColor = Color.Transparent;
            warmCaption.SetBounds(16, 82, 120, 16);

            warmValue = new Label();
            warmValue.TextAlign = ContentAlignment.MiddleRight;
            warmValue.Font = Theme.Font(8.25f, FontStyle.Bold);
            warmValue.ForeColor = Theme.Text;
            warmValue.BackColor = Color.Transparent;
            warmValue.SetBounds(PanelWidth - 126, 82, 94, 16);

            warmSlider = new LevelSlider();
            warmSlider.SetBounds(10, 99, PanelWidth - 20, 20);
            warmSlider.Minimum = MinWarm;
            warmSlider.Maximum = MaxWarm;
            warmSlider.Accent = Theme.AccentWarm;
            warmSlider.Value = warm;
            warmSlider.ValueChanged += OnWarmChanged;
            warmSlider.DragFinished += delegate { SaveSettings(); };

            Button offButton = MakeButton("Screen off", 10, 128, 144);
            offButton.Click += delegate { TurnOffScreen(); };
            tips.SetToolTip(offButton, "Turn the monitor off (Ctrl+Alt+O)");

            Button hideButton = MakeButton("Hide", 164, 128, 94);
            hideButton.Click += delegate { Hide(); };
            tips.SetToolTip(hideButton, "Hide to tray (Esc)");

            Controls.Add(title);
            Controls.Add(close);
            Controls.Add(dimCaption);
            Controls.Add(dimValue);
            Controls.Add(dimSlider);
            Controls.Add(warmCaption);
            Controls.Add(warmValue);
            Controls.Add(warmSlider);
            Controls.Add(offButton);
            Controls.Add(hideButton);
            Controls.Add(dragBar);
        }

        private Button MakeButton(string text, int x, int y, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.SetBounds(x, y, width, 26);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Theme.ButtonFace;
            button.ForeColor = Theme.Text;
            button.Font = Theme.Font(8.25f, FontStyle.Regular);
            button.TabStop = false;
            button.FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
            button.FlatAppearance.MouseDownBackColor = Theme.ButtonDown;
            RoundControl(button, 8);
            return button;
        }

        private Button MakeCloseButton(int x, int y)
        {
            Button button = new Button();
            button.Text = string.Empty;
            button.SetBounds(x, y, 22, 22);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Theme.Background;
            button.ForeColor = Theme.Muted;
            button.TabStop = false;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.MouseOverBackColor = Theme.CloseHover;
            button.FlatAppearance.MouseDownBackColor = Theme.CloseHover;
            button.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int cx = button.Width / 2;
                int cy = button.Height / 2;
                const int reach = 4;
                using (Pen pen = new Pen(button.ForeColor, 1.5f))
                {
                    e.Graphics.DrawLine(pen, cx - reach, cy - reach, cx + reach, cy + reach);
                    e.Graphics.DrawLine(pen, cx + reach, cy - reach, cx - reach, cy + reach);
                }
            };
            button.MouseEnter += delegate { button.ForeColor = Theme.CloseText; button.Invalidate(); };
            button.MouseLeave += delegate { button.ForeColor = Theme.Muted; button.Invalidate(); };
            RoundControl(button, 7);
            return button;
        }

        private static void RoundControl(Control control, int radius)
        {
            Action apply = delegate
            {
                if (control.Width <= 0 || control.Height <= 0) return;
                IntPtr rgn = Native.CreateRoundRectRgn(0, 0, control.Width + 1, control.Height + 1, radius * 2, radius * 2);
                Native.SetWindowRgn(control.Handle, rgn, true);
            };
            control.HandleCreated += delegate { apply(); };
            control.Resize += delegate { apply(); };
        }

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            menu.Items.Add("Show panel\tCtrl+Alt+D", null, delegate { ShowPanel(); });
            menu.Items.Add("Dim off", null, delegate { AnimateTo(0, warm); });
            menu.Items.Add("Dim 40%", null, delegate { AnimateTo(40, warm); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Neutral (6500 K)", null, delegate { AnimateTo(level, 0); });
            menu.Items.Add("Warm (3700 K)", null, delegate { AnimateTo(level, 74); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Screen off\tCtrl+Alt+O", null, delegate { TurnOffScreen(); });
            autoStartItem = new ToolStripMenuItem("Start with Windows");
            autoStartItem.CheckOnClick = true;
            autoStartItem.Checked = IsAutoStartEnabled();
            autoStartItem.Click += delegate { SetAutoStart(autoStartItem.Checked); };
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit\tCtrl+Alt+Q", null, delegate { Close(); });

            tray = new NotifyIcon();
            tray.Icon = BuildTrayIcon();
            tray.Text = "Screen Dimmer";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowPanel(); };
        }

        private static Icon LoadEmbeddedIcon(int size)
        {
            try
            {
                System.Reflection.Assembly asm = typeof(Program).Assembly;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream stream = asm.GetManifestResourceStream(name))
                        {
                            if (stream != null)
                            {
                                return new Icon(stream, size, size);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null)
                {
                    if (exeIcon.Width == size && exeIcon.Height == size) return exeIcon;
                    return new Icon(exeIcon, size, size);
                }
            }
            catch
            {
            }

            return null;
        }

        private static Icon BuildTrayIcon()
        {
            Icon loaded = LoadEmbeddedIcon(16);
            if (loaded != null) return loaded;
            return BuildIcon();
        }

        private static Icon BuildIcon()
        {
            Icon loaded = LoadEmbeddedIcon(32);
            if (loaded != null) return loaded;

            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush sun = new SolidBrush(Color.FromArgb(255, 250, 204, 21)))
                {
                    g.FillEllipse(sun, 4, 4, 24, 24);
                }
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(255, 22, 22, 28)))
                {
                    g.FillPie(shade, 4, 4, 24, 24, -90, 180);
                }
                using (Pen edge = new Pen(Color.FromArgb(255, 60, 60, 74), 1.2f))
                {
                    g.DrawEllipse(edge, 4, 4, 24, 24);
                }
            }
            IntPtr handle = bmp.GetHicon();
            Icon icon = Icon.FromHandle(handle);
            bmp.Dispose();
            return icon;
        }

        // ---------- placement ----------

        private void MoveToSavedPlace()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Point target = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

            if (hasSavedPosition && IsOnScreen(savedPosition))
            {
                target = savedPosition;
            }
            Location = target;
        }

        private static bool IsOnScreen(Point point)
        {
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            return virtualScreen.Contains(point)
                && point.X + PanelWidth > virtualScreen.Left
                && point.Y + PanelHeight > virtualScreen.Top;
        }

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            draggingPanel = true;
            dragStartCursor = Cursor.Position;
            dragStartLocation = Location;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!draggingPanel) return;
            Point now = Cursor.Position;
            Location = new Point(
                dragStartLocation.X + (now.X - dragStartCursor.X),
                dragStartLocation.Y + (now.Y - dragStartCursor.Y));
        }

        private void OnDragEnd(object sender, MouseEventArgs e)
        {
            if (!draggingPanel) return;
            draggingPanel = false;
            savedPosition = Location;
            hasSavedPosition = true;
            SaveSettings();
        }

        // ---------- behaviour ----------

        private void CreateOverlays()
        {
            DisposeOverlays();

            foreach (Screen screen in Screen.AllScreens)
            {
                // Shade layer first: pure black, only lowers brightness.
                OverlayForm shade = new OverlayForm(screen.Bounds, Color.Black);
                shade.Show();
                shadeOverlays.Add(shade);

                // Tint layer above it, so warmth still reads at strong dimming.
                OverlayForm tint = new OverlayForm(screen.Bounds, TintColor);
                tint.Show();
                tintOverlays.Add(tint);
            }

            Apply(false);
        }

        private void DisposeOverlays()
        {
            foreach (OverlayForm overlay in shadeOverlays)
            {
                overlay.Hide();
                overlay.Dispose();
            }
            shadeOverlays.Clear();

            foreach (OverlayForm overlay in tintOverlays)
            {
                overlay.Hide();
                overlay.Dispose();
            }
            tintOverlays.Clear();
        }

        private void Apply(bool save)
        {
            level = Clamp(level, MinLevel, MaxLevel);
            warm = Clamp(warm, MinWarm, MaxWarm);

            updating = true;
            dimSlider.Value = level;
            warmSlider.Value = warm;
            updating = false;

            dimValue.Text = level + "%";
            warmValue.Text = DescribeWarmth(warm);

            double tintAlpha = TintStrength * (warm / 100.0);
            foreach (OverlayForm overlay in shadeOverlays) overlay.SetOpacity(level / 100.0);
            foreach (OverlayForm overlay in tintOverlays) overlay.SetOpacity(tintAlpha);

            RaisePanel();
            if (save) SaveSettings();
        }

        private static Color TintColor
        {
            get { return Color.FromArgb(255, 255, 149, 44); }
        }

        private static string DescribeWarmth(int percent)
        {
            if (percent <= 0) return KelvinNeutral + " K";
            int kelvin = (int)Math.Round(KelvinNeutral - (KelvinNeutral - KelvinWarmest) * (percent / 100.0));
            return kelvin + " K";
        }

        private void SetLevel(int value)
        {
            level = Clamp(value, MinLevel, MaxLevel);
            Apply(false);
            ScheduleSaveSettings();
        }

        private void SetWarm(int value)
        {
            warm = Clamp(value, MinWarm, MaxWarm);
            Apply(false);
            ScheduleSaveSettings();
        }

        private void AnimateTo(int newLevel, int newWarm)
        {
            targetLevel = Clamp(newLevel, MinLevel, MaxLevel);
            targetWarm = Clamp(newWarm, MinWarm, MaxWarm);
            animTimer.Start();
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            bool doneLevel = true;
            bool doneWarm = true;

            if (targetLevel >= 0)
            {
                int diff = targetLevel - level;
                if (Math.Abs(diff) <= 2)
                {
                    level = targetLevel;
                    targetLevel = -1;
                }
                else
                {
                    level += Math.Sign(diff) * Math.Max(2, Math.Abs(diff) / 4);
                    doneLevel = false;
                }
            }

            if (targetWarm >= 0)
            {
                int diff = targetWarm - warm;
                if (Math.Abs(diff) <= 2)
                {
                    warm = targetWarm;
                    targetWarm = -1;
                }
                else
                {
                    warm += Math.Sign(diff) * Math.Max(2, Math.Abs(diff) / 4);
                    doneWarm = false;
                }
            }

            Apply(false);

            if (doneLevel && doneWarm)
            {
                animTimer.Stop();
                ScheduleSaveSettings();
            }
        }

        private void ScheduleSaveSettings()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        /// <summary>Turns the monitor off, like the laptop's own button; any input wakes it.</summary>
        public void TurnOffScreen()
        {
            Log("screen off");
            saveTimer.Stop();
            SaveSettings();
            Native.SendMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND,
                new IntPtr(Native.SC_MONITORPOWER), new IntPtr(2));
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void RaisePanel()
        {
            if (!Visible || IsDisposed) return;
            TopMost = false;
            TopMost = true;
        }

        public void ShowPanel()
        {
            allowVisible = true;
            Show();
            if (!fadeTimer.Enabled)
            {
                Opacity = 1.0;
            }
            Activate();
            BringToFront();
            RaisePanel();
        }

        private void OnFadeTick(object sender, EventArgs e)
        {
            double next = Opacity + 0.16;
            if (next >= 1.0)
            {
                Opacity = 1.0;
                fadeTimer.Stop();
                return;
            }
            Opacity = next;
        }

        private void OnDimChanged(object sender, EventArgs e)
        {
            if (updating) return;
            animTimer.Stop();
            level = dimSlider.Value;
            Apply(false);
            ScheduleSaveSettings();
        }

        private void OnWarmChanged(object sender, EventArgs e)
        {
            if (updating) return;
            animTimer.Stop();
            warm = warmSlider.Value;
            Apply(false);
            ScheduleSaveSettings();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Log("display settings changed");
            CreateOverlays();
            if (hasSavedPosition && !IsOnScreen(savedPosition))
            {
                MoveToSavedPlace();
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                Log("system resumed from sleep");
                CreateOverlays();
                RaisePanel();
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Log("session unlocked");
                RaisePanel();
            }
        }

        // ---------- global hotkeys ----------

        private void RegisterHotkeys()
        {
            uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            RegisterHotkey(Handle, HotkeyDim, mods, 0x28, "Ctrl+Alt+Down");
            RegisterHotkey(Handle, HotkeyBright, mods, 0x26, "Ctrl+Alt+Up");
            RegisterHotkey(Handle, HotkeyShow, mods, 0x44, "Ctrl+Alt+D");
            RegisterHotkey(Handle, HotkeyQuit, mods, 0x51, "Ctrl+Alt+Q");
            RegisterHotkey(Handle, HotkeyWarm, mods, 0x27, "Ctrl+Alt+Right");
            RegisterHotkey(Handle, HotkeyCool, mods, 0x25, "Ctrl+Alt+Left");
            RegisterHotkey(Handle, HotkeyOff, mods, 0x4F, "Ctrl+Alt+O");
        }

        private static void RegisterHotkey(IntPtr handle, int id, uint mods, uint vk, string name)
        {
            if (!Native.RegisterHotKey(handle, id, mods, vk))
            {
                Log("could not register " + name + " (probably taken by another app)");
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;

            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyDim)
                {
                    animTimer.Stop();
                    SetLevel(level + StepLevel);
                    if (!Visible) osd.ShowOsd("Dim", level + "%", level, Theme.AccentDim);
                    return;
                }
                if (id == HotkeyBright)
                {
                    animTimer.Stop();
                    SetLevel(level - StepLevel);
                    if (!Visible) osd.ShowOsd("Dim", level + "%", level, Theme.AccentDim);
                    return;
                }
                if (id == HotkeyWarm)
                {
                    animTimer.Stop();
                    SetWarm(warm + StepWarm);
                    if (!Visible) osd.ShowOsd("Warmth", DescribeWarmth(warm), warm, Theme.AccentWarm);
                    return;
                }
                if (id == HotkeyCool)
                {
                    animTimer.Stop();
                    SetWarm(warm - StepWarm);
                    if (!Visible) osd.ShowOsd("Warmth", DescribeWarmth(warm), warm, Theme.AccentWarm);
                    return;
                }
                if (id == HotkeyOff) { TurnOffScreen(); return; }
                if (id == HotkeyShow) { ShowPanel(); return; }
                if (id == HotkeyQuit) { Close(); return; }
            }
            base.WndProc(ref m);
        }

        // ---------- settings ----------

        private Point savedPosition;
        private bool hasSavedPosition;

        private void LoadSettings()
        {
            int posX = 0;
            int posY = 0;
            bool hasX = false;
            bool hasY = false;

            try
            {
                if (File.Exists(settingsPath))
                {
                    foreach (string line in File.ReadAllLines(settingsPath))
                    {
                        int split = line.IndexOf('=');
                        if (split <= 0) continue;
                        string key = line.Substring(0, split).Trim();
                        string value = line.Substring(split + 1).Trim();
                        int parsed;
                        if (key == "level" && int.TryParse(value, out parsed)) level = parsed;
                        else if (key == "warm" && int.TryParse(value, out parsed)) warm = parsed;
                        else if (key == "x" && int.TryParse(value, out parsed)) { posX = parsed; hasX = true; }
                        else if (key == "y" && int.TryParse(value, out parsed)) { posY = parsed; hasY = true; }
                    }
                }
            }
            catch
            {
                // a corrupted setting must not stop the app
            }

            if (hasX && hasY)
            {
                savedPosition = new Point(posX, posY);
                hasSavedPosition = true;
            }

            level = Clamp(level, MinLevel, MaxLevel);
            warm = Clamp(warm, MinWarm, MaxWarm);
        }

        private void SaveSettings()
        {
            try
            {
                string folder = Path.GetDirectoryName(settingsPath);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(settingsPath,
                    "level=" + level + Environment.NewLine
                    + "warm=" + warm + Environment.NewLine
                    + "x=" + Location.X + Environment.NewLine
                    + "y=" + Location.Y + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string ExePath
        {
            get { return Application.ExecutablePath; }
        }

        public static void Log(string message)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ScreenDimmer");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string logPath = Path.Combine(folder, "dimmer.log");
                FileInfo info = new FileInfo(logPath);
                if (info.Exists && info.Length > 128 * 1024) info.Delete();
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    return key.GetValue("ScreenDimmer") != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue("ScreenDimmer", "\"" + ExePath + "\" --tray");
                    else key.DeleteValue("ScreenDimmer", false);
                }
            }
            catch
            {
                MessageBox.Show(this, "Could not change the start-up setting.", "Screen Dimmer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------- cleanup ----------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Opacity = 0.0;
            fadeTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log("closing, reason=" + e.CloseReason);
            saveTimer.Stop();
            SaveSettings();
            Native.UnregisterHotKey(Handle, HotkeyDim);
            Native.UnregisterHotKey(Handle, HotkeyBright);
            Native.UnregisterHotKey(Handle, HotkeyShow);
            Native.UnregisterHotKey(Handle, HotkeyQuit);
            Native.UnregisterHotKey(Handle, HotkeyWarm);
            Native.UnregisterHotKey(Handle, HotkeyCool);
            Native.UnregisterHotKey(Handle, HotkeyOff);
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fadeTimer.Stop();
                fadeTimer.Dispose();
                saveTimer.Stop();
                saveTimer.Dispose();
                animTimer.Stop();
                animTimer.Dispose();
                if (osd != null)
                {
                    osd.Dispose();
                    osd = null;
                }
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.Dispose();
                    tray = null;
                }
                if (tips != null)
                {
                    tips.Dispose();
                    tips = null;
                }
                DisposeOverlays();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        private const string ShowEventName = "ScreenDimmer.ShowPanel";
        private const string MutexName = "ScreenDimmer.SingleInstance";

        [STAThread]
        private static void Main()
        {
            try
            {
                if (!Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    Native.SetProcessDPIAware();
                }
            }
            catch
            {
                Native.SetProcessDPIAware();
            }

            bool isFirst;
            using (Mutex mutex = new Mutex(true, MutexName, out isFirst))
            {
                if (!isFirst)
                {
                    SignalExistingInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    PanelForm.Log("UI error: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    PanelForm.Log("fatal error: " + e.ExceptionObject);
                };
                PanelForm.Log("start");

                bool startInTray = false;
                string[] cmdArgs = Environment.GetCommandLineArgs();
                for (int i = 1; i < cmdArgs.Length; i++)
                {
                    string arg = cmdArgs[i];
                    if (string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "/tray", StringComparison.OrdinalIgnoreCase))
                    {
                        startInTray = true;
                        break;
                    }
                }

                PanelForm form = new PanelForm(startInTray);
                StartShowListener(form);
                Application.Run(form);

                GC.KeepAlive(mutex);
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting(ShowEventName))
                {
                    handle.Set();
                }
            }
            catch
            {
            }
        }

        private static void StartShowListener(PanelForm form)
        {
            try
            {
                EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                Thread listener = new Thread(delegate()
                {
                    while (true)
                    {
                        showSignal.WaitOne();
                        try
                        {
                            if (form.IsDisposed) return;
                            form.BeginInvoke(new Action(form.ShowPanel));
                        }
                        catch
                        {
                            return;
                        }
                    }
                });
                listener.IsBackground = true;
                listener.Start();
            }
            catch
            {
            }
        }
    }
}
