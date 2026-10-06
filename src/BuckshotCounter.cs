// 恶魔轮盘 · 记弹器  (Buckshot Roulette Shell Counter)
//
// 手动标注弹匣：
//   1. 先设定这一局装了几发实弹、几发空弹，点【装弹】→ 下面出现对应数量的灰色"未知弹"
//   2. 左键点某颗 = 标成红色实弹；右键点某颗 = 标成蓝色空弹
//   3. 点的是【最前面那颗】= 它已经击发了 → 直接删掉，剩余数自动减
//      （击发时你必然知道它是实弹还是空弹，所以这一下同时记录了类型）
//   4. 放大镜看到的是后面某颗 → 就地标色
//
// 窗口始终置顶，支持全局热键，游戏全屏时也能操作。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BuckshotCounter
{
    public enum ShellState { Unknown = 0, Live = 1, Blank = 2 }

    internal static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayForm(Config.Load()));
        }
    }

    // ------------------------------------------------------------------
    // 配置
    // ------------------------------------------------------------------
    public class Config
    {
        public int X = -1, Y = -1;
        public int OpacityPct = 95;
        public bool TopMost = true;
        public bool ClickThrough = false;
        public int LiveCount = 3;      // 这一局装几发实弹
        public int BlankCount = 2;     // 这一局装几发空弹
        public int ClickCooldownMs = 1000;  // 标记/击发的防误触间隔（毫秒），0=关闭
        public bool ShowSettings = false;   // 设置栏是否展开

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"); }
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int iv; bool bv;
                    bool isI = int.TryParse(v, out iv), isB = bool.TryParse(v, out bv);
                    switch (k)
                    {
                        case "X": if (isI) c.X = iv; break;
                        case "Y": if (isI) c.Y = iv; break;
                        case "OpacityPct": if (isI) c.OpacityPct = iv; break;
                        case "TopMost": if (isB) c.TopMost = bv; break;
                        case "ClickThrough": if (isB) c.ClickThrough = bv; break;
                        case "LiveCount": if (isI) c.LiveCount = iv; break;
                        case "BlankCount": if (isI) c.BlankCount = iv; break;
                        case "ClickCooldownMs": if (isI) c.ClickCooldownMs = iv; break;
                        case "ShowSettings": if (isB) c.ShowSettings = bv; break;
                    }
                }
            }
            catch { }
            c.Clamp();
            c.ClickThrough = false;   // 穿透状态不延续，免得一启动就点不到窗口
            return c;
        }

        public void Clamp()
        {
            OpacityPct = Math.Max(35, Math.Min(100, OpacityPct));
            LiveCount = Math.Max(0, Math.Min(8, LiveCount));
            BlankCount = Math.Max(0, Math.Min(8, BlankCount));
            ClickCooldownMs = Math.Max(0, Math.Min(5000, ClickCooldownMs));
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 恶魔轮盘记弹器配置");
                sb.AppendLine("X=" + X); sb.AppendLine("Y=" + Y);
                sb.AppendLine("OpacityPct=" + OpacityPct);
                sb.AppendLine("TopMost=" + TopMost);
                sb.AppendLine("ClickThrough=" + ClickThrough);
                sb.AppendLine("LiveCount=" + LiveCount);
                sb.AppendLine("BlankCount=" + BlankCount);
                sb.AppendLine("ClickCooldownMs=" + ClickCooldownMs);
                sb.AppendLine("ShowSettings=" + ShowSettings);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // 弹匣模型
    // ------------------------------------------------------------------
    public class Rack
    {
        public readonly List<ShellState> Shells = new List<ShellState>();
        public int InitialLive, InitialBlank;
        public int FiredLive, FiredBlank;

        public int LiveLeft { get { return Math.Max(0, InitialLive - FiredLive); } }
        public int BlankLeft { get { return Math.Max(0, InitialBlank - FiredBlank); } }
        public int Total { get { return Shells.Count; } }

        public int UnknownCount { get { int n = 0; foreach (var s in Shells) if (s == ShellState.Unknown) n++; return n; } }
        public int KnownLive { get { int n = 0; foreach (var s in Shells) if (s == ShellState.Live) n++; return n; } }
        public int KnownBlank { get { int n = 0; foreach (var s in Shells) if (s == ShellState.Blank) n++; return n; } }

        /// <summary>某一颗"未知弹"是实弹的概率。
        /// 还剩 LiveLeft 发实弹，其中 KnownLive 发已经标出来了，剩下 (LiveLeft-KnownLive) 发
        /// 必然分散在 UnknownCount 个未知位置里；没有任何其它顺序信息时，每个位置机会均等。</summary>
        public double UnknownLiveChance
        {
            get
            {
                int u = UnknownCount;
                if (u <= 0) return 0;
                int liveInUnknown = LiveLeft - KnownLive;
                if (liveInUnknown < 0) liveInUnknown = 0;
                if (liveInUnknown > u) liveInUnknown = u;
                return (double)liveInUnknown / u;
            }
        }

        public void Load(int live, int blank)
        {
            InitialLive = live; InitialBlank = blank;
            FiredLive = 0; FiredBlank = 0;
            Shells.Clear();
            for (int i = 0; i < live + blank; i++) Shells.Add(ShellState.Unknown);
        }

        public void Clear() { Shells.Clear(); InitialLive = 0; InitialBlank = 0; FiredLive = 0; FiredBlank = 0; }

        /// <summary>击发最前面那颗。标记类型后删除，并累计已击发数。</summary>
        public bool Fire(ShellState kind)
        {
            if (Shells.Count == 0) return false;
            if (kind == ShellState.Live) FiredLive++; else FiredBlank++;
            Shells.RemoveAt(0);
            return true;
        }

        public string Snapshot()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(InitialLive).Append(',').Append(InitialBlank).Append(',')
              .Append(FiredLive).Append(',').Append(FiredBlank).Append('|');
            foreach (var s in Shells) sb.Append((int)s);
            return sb.ToString();
        }

        public void Restore(string s)
        {
            try
            {
                string[] parts = s.Split('|');
                string[] nums = parts[0].Split(',');
                InitialLive = int.Parse(nums[0]); InitialBlank = int.Parse(nums[1]);
                FiredLive = int.Parse(nums[2]); FiredBlank = int.Parse(nums[3]);
                Shells.Clear();
                if (parts.Length > 1)
                    foreach (char ch in parts[1]) Shells.Add((ShellState)(ch - '0'));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // 矢量小图标（不依赖字体里有没有对应字符，画出来一定是对的）
    // ------------------------------------------------------------------
    internal static class Icons
    {
        internal static GraphicsPath RRf(RectangleF r, float rad)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = rad * 2f;
            if (d <= 0.5f) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        /// <summary>置顶：一枚图钉</summary>
        internal static void Pin(Graphics g, Rectangle box, Color c)
        {
            float s = Math.Min(box.Width, box.Height);
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            using (SolidBrush b = new SolidBrush(c))
            {
                using (Pen p = new Pen(c, Math.Max(1.6f, s * 0.085f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, cx, cy + s * 0.04f, cx, cy + s * 0.36f);          // 针
                }
                PointF[] body = new PointF[4];                                       // 钉身
                body[0] = new PointF(cx - s * 0.19f, cy - s * 0.19f);
                body[1] = new PointF(cx + s * 0.19f, cy - s * 0.19f);
                body[2] = new PointF(cx + s * 0.10f, cy + s * 0.08f);
                body[3] = new PointF(cx - s * 0.10f, cy + s * 0.08f);
                g.FillPolygon(b, body);
                using (GraphicsPath gp = RRf(new RectangleF(cx - s * 0.32f, cy - s * 0.36f, s * 0.64f, s * 0.16f), s * 0.07f))
                    g.FillPath(b, gp);                                               // 钉帽
            }
        }

        /// <summary>设置：一个齿轮</summary>
        internal static void Gear(Graphics g, Rectangle box, Color c, Color bg)
        {
            float s = Math.Min(box.Width, box.Height);
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float rBody = s * 0.30f, hole = s * 0.125f;
            using (SolidBrush b = new SolidBrush(c))
            {
                const int teeth = 8;
                float tw = s * 0.15f, tl = s * 0.21f;
                for (int i = 0; i < teeth; i++)
                {
                    double ang = i * Math.PI * 2 / teeth;
                    System.Drawing.Drawing2D.Matrix old = g.Transform;
                    g.TranslateTransform(cx + (float)Math.Cos(ang) * rBody, cy + (float)Math.Sin(ang) * rBody);
                    g.RotateTransform((float)(ang * 180.0 / Math.PI));
                    using (GraphicsPath gp = RRf(new RectangleF(-tw / 2f, -tl / 2f, tw, tl), tw * 0.34f))
                        g.FillPath(b, gp);
                    g.Transform = old;
                }
                g.FillEllipse(b, cx - rBody, cy - rBody, rBody * 2f, rBody * 2f);
            }
            using (SolidBrush b = new SolidBrush(bg))     // 中心孔
                g.FillEllipse(b, cx - hole, cy - hole, hole * 2f, hole * 2f);
        }

        /// <summary>鼠标穿透：一个虚线窗口 + 穿过去的鼠标指针</summary>
        internal static void Through(Graphics g, Rectangle box, Color c)
        {
            float s = Math.Min(box.Width, box.Height);
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float bw = s * 0.70f, bh = s * 0.54f;
            RectangleF win = new RectangleF(cx - bw / 2f + s * 0.08f, cy - bh / 2f - s * 0.04f, bw, bh);
            using (Pen p = new Pen(Color.FromArgb(175, c), Math.Max(1.2f, s * 0.055f)))
            {
                p.DashStyle = DashStyle.Dash;
                using (GraphicsPath gp = RRf(win, s * 0.08f)) g.DrawPath(p, gp);
            }
            float ax = cx - s * 0.30f, ay = cy - s * 0.32f;
            PointF[] cur = new PointF[]
            {
                new PointF(ax,               ay),
                new PointF(ax,               ay + s * 0.60f),
                new PointF(ax + s * 0.145f,  ay + s * 0.455f),
                new PointF(ax + s * 0.255f,  ay + s * 0.715f),
                new PointF(ax + s * 0.365f,  ay + s * 0.665f),
                new PointF(ax + s * 0.255f,  ay + s * 0.415f),
                new PointF(ax + s * 0.435f,  ay + s * 0.415f),
            };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, cur);
            using (Pen p = new Pen(Color.FromArgb(255, 20, 20, 26), Math.Max(1f, s * 0.045f)))
                g.DrawPolygon(p, cur);
        }
    }

    // ------------------------------------------------------------------
    // 穿透时仍然可点的「穿」按钮（独立小窗口，避免把界面锁死）
    // ------------------------------------------------------------------
    public class ThroughToggleForm : Form
    {
        private readonly OverlayForm host;
        private float S = 1f;
        private bool hover;
        public ThroughToggleForm(OverlayForm host)
        {
            this.host = host;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; TopMost = true;
            BackColor = Color.FromArgb(15, 15, 19);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000; cp.ExStyle |= 0x00000080; return cp; }
        }
        public void PlaceAt(Rectangle r, float scale, int opacityPct) { S = scale; Bounds = r; Opacity = opacityPct / 100.0; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { host.TurnOffClickThrough(); base.OnMouseDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 15, 15, 19))) g.FillRectangle(b, 0, 0, Width, Height);
            int rad = Math.Max(2, (int)Math.Round(6 * S));
            using (GraphicsPath gp = RR(r, rad))
            using (SolidBrush b = new SolidBrush(hover ? Color.FromArgb(255, 90, 110, 145) : Color.FromArgb(255, 62, 72, 96))) g.FillPath(b, gp);
            using (GraphicsPath gp = RR(r, rad))
            using (Pen p = new Pen(Color.FromArgb(250, 120, 200, 255), 2f)) g.DrawPath(p, gp);
            int ip = Math.Max(3, (int)Math.Round(6 * S));
            Icons.Through(g, new Rectangle(ip, ip, Width - ip * 2, Height - ip * 2), Color.FromArgb(255, 190, 228, 255));
        }
        internal static GraphicsPath RR(Rectangle r, int rad)
        {
            GraphicsPath gp = new GraphicsPath(); int d = rad * 2;
            if (d <= 0) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180, 90); gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure(); return gp;
        }
    }

    // ------------------------------------------------------------------
    // 主窗口
    // ------------------------------------------------------------------
    public class OverlayForm : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        private const int ID_FIRE_LIVE = 1, ID_FIRE_BLANK = 2, ID_UNDO = 3, ID_RELOAD = 4, ID_TOGGLE = 5, ID_THROUGH = 6;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, int mod, int vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

        private static readonly Color LiveCol = Color.FromArgb(226, 76, 72);
        private static readonly Color BlankCol = Color.FromArgb(80, 148, 226);
        private static readonly Color UnknownCol = Color.FromArgb(150, 156, 168);
        private static readonly Color BgCol = Color.FromArgb(255, 15, 15, 19);

        private readonly Config cfg;
        private readonly Rack rack = new Rack();
        private readonly List<string> history = new List<string>();

        private float S = 1f;
        private Font fTitle, fLabel, fTiny, fBig, fNow, fBtn, fChip;

        private Rectangle rTop, rThrough, rSettings, rClose;
        private Rectangle rLiveBox, rBlankBox, rUnknownBox;
        private Rectangle rRackArea, rProbArea;
        private readonly List<Rectangle> rChips = new List<Rectangle>();
        private Rectangle rLiveMinus, rLivePlus, rSetLive, rBlankMinus, rBlankPlus, rSetBlank, rLoad;
        private Rectangle rUndo, rReload;
        // 设置栏
        private Rectangle rSetPanel;
        private Rectangle rCoolMinus, rCoolVal, rCoolPlus;
        private Rectangle rOpMinus, rOpVal, rOpPlus;
        private Rectangle rTopToggle, rThroughToggle, rResetPos;

        private bool dragging;
        private Point dragOrigin;
        private string status = "";
        private int setLive = 3, setBlank = 2;

        private ThroughToggleForm throughBtn;
        private System.Windows.Forms.Timer topWatch;

        public OverlayForm(Config c)
        {
            cfg = c;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            Text = "恶魔轮盘 · 记弹器";
            BackColor = Color.FromArgb(16, 16, 20);
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            setLive = cfg.LiveCount; setBlank = cfg.BlankCount;

            IntPtr force = Handle;
            ApplyDpi();
            ApplyTopMost();
            ApplyOpacity();

            Rectangle vs = SystemInformation.VirtualScreen;
            if (cfg.X < 0 || cfg.Y < 0 || cfg.X > vs.Right - 60 || cfg.Y > vs.Bottom - 60)
            {
                cfg.X = vs.Right - Width - (int)(18 * S);
                cfg.Y = vs.Top + (int)(18 * S);
            }
            Location = new Point(cfg.X, cfg.Y);
            ApplyClickThrough();
            ApplyTopMost();

            rack.Load(setLive, setBlank);
            status = "";
        }

        private int Px(double v) { return (int)Math.Round(v * S); }

        private static Font Mk(float px, FontStyle st)
        {
            try { return new Font("Microsoft YaHei UI", px, st, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, st, GraphicsUnit.Pixel); }
        }

        private int RealDpi()
        {
            try { if (Handle != IntPtr.Zero) { uint d = GetDpiForWindow(Handle); if (d >= 48 && d <= 960) return (int)d; } } catch { }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { int d = (int)Math.Round(g.DpiX); if (d >= 48 && d <= 960) return d; } } catch { }
            return 96;
        }

        private void ApplyDpi()
        {
            S = RealDpi() / 96f;
            ClientSize = new Size(Px(470), Px(cfg.ShowSettings ? 462 : 282));
            if (fTitle != null) { fTitle.Dispose(); fLabel.Dispose(); fTiny.Dispose(); fBig.Dispose(); fNow.Dispose(); fBtn.Dispose(); fChip.Dispose(); }
            fTitle = Mk(13 * S, FontStyle.Regular);
            fLabel = Mk(12.5f * S, FontStyle.Regular);
            fTiny = Mk(11 * S, FontStyle.Regular);
            fBig = Mk(29 * S, FontStyle.Bold);
            fNow = Mk(15 * S, FontStyle.Bold);
            fBtn = Mk(12.5f * S, FontStyle.Regular);
            fChip = Mk(13.5f * S, FontStyle.Bold);
        }

        private void ApplyTopMost()
        {
            if (TopMost != cfg.TopMost) TopMost = cfg.TopMost;
            if (IsHandleCreated)
                SetWindowPos(Handle, cfg.TopMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void ApplyOpacity() { Opacity = cfg.OpacityPct / 100.0; if (cfg.ClickThrough) SyncThroughButton(); }

        private void ApplyClickThrough()
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            if (cfg.ClickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
            ApplyTopMost();   // SetWindowLong 会清掉 topmost 位，立刻补回
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKeys();
            ApplyClickThrough();
            StartTopMostWatch();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            for (int i = 1; i <= 6; i++) UnregisterHotKey(Handle, i);
            base.OnHandleDestroyed(e);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); ApplyTopMost(); }

        private void RegisterHotKeys()
        {
            int m = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
            RegisterHotKey(Handle, ID_FIRE_LIVE, m, (int)Keys.D1);
            RegisterHotKey(Handle, ID_FIRE_BLANK, m, (int)Keys.D2);
            RegisterHotKey(Handle, ID_UNDO, m, (int)Keys.D3);
            RegisterHotKey(Handle, ID_RELOAD, m, (int)Keys.D4);
            RegisterHotKey(Handle, ID_TOGGLE, m, (int)Keys.H);
            RegisterHotKey(Handle, ID_THROUGH, m, (int)Keys.D6);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                switch (m.WParam.ToInt32())
                {
                    case ID_FIRE_LIVE: DoFire(ShellState.Live); return;
                    case ID_FIRE_BLANK: DoFire(ShellState.Blank); return;
                    case ID_UNDO: DoUndo(); return;
                    case ID_RELOAD: DoLoad(); return;
                    case ID_TOGGLE: Visible = !Visible; if (Visible) ApplyTopMost(); return;
                    case ID_THROUGH: SetClickThrough(!cfg.ClickThrough); return;
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------- 操作 ----------------
        private DateTime lastChipAction = DateTime.MinValue;
        private System.Windows.Forms.Timer cooldownTimer;

        /// <summary>标记/击发是否还在防误触冷却中。</summary>
        private bool Cooling
        {
            get
            {
                if (cfg.ClickCooldownMs <= 0) return false;
                return (DateTime.Now - lastChipAction).TotalMilliseconds < cfg.ClickCooldownMs;
            }
        }

        private void StartCooldown()
        {
            lastChipAction = DateTime.Now;
            if (cfg.ClickCooldownMs <= 0) return;
            if (cooldownTimer == null)
            {
                cooldownTimer = new System.Windows.Forms.Timer();
                cooldownTimer.Tick += delegate
                {
                    cooldownTimer.Stop();
                    Invalidate();   // 冷却结束后把弹匣恢复成可点状态
                };
            }
            cooldownTimer.Stop();
            cooldownTimer.Interval = cfg.ClickCooldownMs + 30;
            cooldownTimer.Start();
        }

        /// <summary>撤销 / 装弹不受冷却限制，并且顺手把冷却清掉，方便误触后马上补救。</summary>
        private void ClearCooldown()
        {
            lastChipAction = DateTime.MinValue;
            if (cooldownTimer != null) cooldownTimer.Stop();
        }

        private bool BlockedByCooldown()
        {
            if (!Cooling) return false;
            double left = (cfg.ClickCooldownMs - (DateTime.Now - lastChipAction).TotalMilliseconds) / 1000.0;
            status = "防误触：太快了，再等 " + Math.Max(0.1, left).ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
            Invalidate();
            return true;
        }

        private void PushHistory()
        {
            history.Add(rack.Snapshot());
            if (history.Count > 200) history.RemoveAt(0);
        }

        private void DoLoad()
        {
            ClearCooldown();
            PushHistory();
            rack.Load(setLive, setBlank);
            status = "已装弹：" + setLive + " 实弹 + " + setBlank + " 空弹 = " + rack.Total + " 发（灰色=未知）";
            Invalidate();
        }

        private void DoFire(ShellState kind)
        {
            if (BlockedByCooldown()) return;
            if (rack.Total == 0) { status = "弹匣是空的，先点【装弹】"; Invalidate(); return; }
            PushHistory();
            rack.Fire(kind);
            StartCooldown();
            status = "已击发第 1 发（" + KindName(kind) + "），还剩 " + rack.Total + " 发";
            Invalidate();
        }

        /// <summary>左键=实弹，右键=空弹。点第 1 颗视为已击发（直接删掉）。</summary>
        private void DoMark(int index, ShellState kind)
        {
            if (index < 0 || index >= rack.Total) return;
            if (BlockedByCooldown()) return;
            PushHistory();
            if (index == 0)
            {
                rack.Fire(kind);
                status = "第 1 发视为已击发（" + KindName(kind) + "），已删除，还剩 " + rack.Total + " 发";
            }
            else
            {
                if (rack.Shells[index] == kind) { rack.Shells[index] = ShellState.Unknown; status = "第 " + (index + 1) + " 发已取消标注"; }
                else { rack.Shells[index] = kind; status = "第 " + (index + 1) + " 发标注为 " + KindName(kind); }
            }
            StartCooldown();
            Invalidate();
        }

        private void DoUndo()
        {
            ClearCooldown();
            if (history.Count == 0) { status = "没有可撤销的操作"; Invalidate(); return; }
            rack.Restore(history[history.Count - 1]);
            history.RemoveAt(history.Count - 1);
            status = "已撤销";
            Invalidate();
        }

        private static string KindName(ShellState s) { return s == ShellState.Live ? "实弹" : "空弹"; }

        // ---------------- 布局 ----------------
        private void DoLayout()
        {
            int pad = Px(10), w = ClientSize.Width;
            int bs = Px(24), hy = Px(8), hh = Px(24);
            // 右上角：[顶] [穿] [设] [×]
            rClose = new Rectangle(w - pad - bs, hy, bs, hh);
            rSettings = new Rectangle(rClose.X - bs - Px(2), hy, bs, hh);
            rThrough = new Rectangle(rSettings.X - bs - Px(2), hy, bs, hh);
            rTop = new Rectangle(rThrough.X - bs - Px(2), hy, bs, hh);

            int y1 = Px(38), h1 = Px(64), gap = Px(8);
            int bw = (w - pad * 2 - gap * 2) / 3;
            rLiveBox = new Rectangle(pad, y1, bw, h1);
            rBlankBox = new Rectangle(rLiveBox.Right + gap, y1, bw, h1);
            rUnknownBox = new Rectangle(rBlankBox.Right + gap, y1, bw, h1);

            rRackArea = new Rectangle(pad, Px(126), w - pad * 2, Px(42));
            rProbArea = new Rectangle(pad, Px(168), w - pad * 2, Px(16));

            int sy = Px(194), sh = Px(30);
            int x = pad;
            int lw = Px(34), bw2 = Px(28), vw = Px(34);
            rLiveMinus = new Rectangle(x + lw, sy, bw2, sh);
            rSetLive = new Rectangle(rLiveMinus.Right, sy, vw, sh);
            rLivePlus = new Rectangle(rSetLive.Right, sy, bw2, sh);
            x = rLivePlus.Right + Px(14);
            rBlankMinus = new Rectangle(x + lw, sy, bw2, sh);
            rSetBlank = new Rectangle(rBlankMinus.Right, sy, vw, sh);
            rBlankPlus = new Rectangle(rSetBlank.Right, sy, bw2, sh);
            x = rBlankPlus.Right + Px(14);
            rLoad = new Rectangle(x, sy, w - pad - x, sh);

            int ay = Px(230), ah = Px(30);
            int aw = (w - pad * 2 - Px(8)) / 2;
            rUndo = new Rectangle(pad, ay, aw, ah);
            rReload = new Rectangle(rUndo.Right + Px(8), ay, aw, ah);

            // ---- 设置栏（里面最上面是使用说明）----
            rSetPanel = new Rectangle(0, Px(286), w, ClientSize.Height - Px(286));
            int ry1 = Px(352), rh = Px(28);
            int lw2 = Px(96), sb = Px(28), vw2 = Px(76);
            rCoolMinus = new Rectangle(pad + lw2, ry1, sb, rh);
            rCoolVal = new Rectangle(rCoolMinus.Right, ry1, vw2, rh);
            rCoolPlus = new Rectangle(rCoolVal.Right, ry1, sb, rh);

            int ry2 = ry1 + rh + Px(8);
            rOpMinus = new Rectangle(pad + lw2, ry2, sb, rh);
            rOpVal = new Rectangle(rOpMinus.Right, ry2, vw2, rh);
            rOpPlus = new Rectangle(rOpVal.Right, ry2, sb, rh);

            int ry3 = ry2 + rh + Px(8);
            int tw = (w - pad * 2 - Px(8) * 2) / 3;
            rTopToggle = new Rectangle(pad, ry3, tw, rh);
            rThroughToggle = new Rectangle(rTopToggle.Right + Px(8), ry3, tw, rh);
            rResetPos = new Rectangle(rThroughToggle.Right + Px(8), ry3, tw, rh);
        }

        // ---------------- 绘制 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            DoLayout();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int w = ClientSize.Width, h = ClientSize.Height;
            using (SolidBrush b = new SolidBrush(BgCol)) g.FillRectangle(b, 0, 0, w, h);
            using (Pen p = new Pen(Color.FromArgb(95, 70, 78, 96), 1f)) g.DrawRectangle(p, 0, 0, w - 1, h - 1);

            Txt(g, "恶魔轮盘 · 记弹器", fTitle, Color.FromArgb(220, 222, 230, 240), new Rectangle(Px(10), Px(8), rTop.X - Px(16), Px(24)), ContentAlignment.MiddleLeft);
            IconBtn(g, rTop, cfg.TopMost, 0);          // 图钉 = 置顶
            IconBtn(g, rThrough, cfg.ClickThrough, 1); // 虚线窗+鼠标 = 穿透
            IconBtn(g, rSettings, cfg.ShowSettings, 2);// 齿轮 = 设置
            TopBtn(g, rClose, "×", false);

            CountBox(g, rLiveBox, "剩余实弹", rack.LiveLeft, LiveCol);
            CountBox(g, rBlankBox, "剩余空弹", rack.BlankLeft, BlankCol);
            CountBox(g, rUnknownBox, "还没标注", rack.UnknownCount, UnknownCol);

            // 下一发提示
            string now;
            Color nowCol;
            if (rack.Total == 0)
            {
                now = "弹匣是空的";
                nowCol = Color.FromArgb(170, 176, 188, 202);
            }
            else
            {
                ShellState f = rack.Shells[0];
                if (f == ShellState.Unknown) { now = "共 " + rack.Total + " 发"; nowCol = UnknownCol; }
                else { now = "第 1 发（下一发）→ " + KindName(f) + "　共 " + rack.Total + " 发"; nowCol = f == ShellState.Live ? LiveCol : BlankCol; }
            }
            Txt(g, now, fNow, nowCol, new Rectangle(Px(10), Px(104), w - Px(20), Px(20)), ContentAlignment.MiddleLeft);

            DrawRack(g);
            DrawProbabilities(g);

            // 装弹设置行
            Txt(g, "实弹", fLabel, LiveCol, new Rectangle(Px(10), rLiveMinus.Y, Px(34), rLiveMinus.Height), ContentAlignment.MiddleLeft);
            Spinner(g, rLiveMinus, "−"); Spinner(g, rLivePlus, "+");
            Txt(g, setLive.ToString(CultureInfo.InvariantCulture), fLabel, Color.FromArgb(235, 240, 244, 250), rSetLive, ContentAlignment.MiddleCenter);
            Txt(g, "空弹", fLabel, BlankCol, new Rectangle(rLivePlus.Right + Px(14), rBlankMinus.Y, Px(34), rBlankMinus.Height), ContentAlignment.MiddleLeft);
            Spinner(g, rBlankMinus, "−"); Spinner(g, rBlankPlus, "+");
            Txt(g, setBlank.ToString(CultureInfo.InvariantCulture), fLabel, Color.FromArgb(235, 240, 244, 250), rSetBlank, ContentAlignment.MiddleCenter);
            Btn(g, rLoad, "装弹（按上面数量）", LiveCol, true);

            Btn(g, rUndo, "撤销", Color.FromArgb(180, 190, 202), false);
            Btn(g, rReload, "重新装弹", Color.FromArgb(180, 190, 202), false);

            string st = status;
            Txt(g, st, fTiny, Color.FromArgb(165, 150, 200, 235), new Rectangle(Px(10), Px(264), w - Px(20), Px(16)), ContentAlignment.MiddleLeft);

            if (cfg.ShowSettings) DrawSettings(g);
        }

        /// <summary>在每颗"未知弹"下面标出它是实弹的概率。</summary>
        private void DrawProbabilities(Graphics g)
        {
            int n = rack.Total;
            if (n == 0 || rack.UnknownCount == 0) return;
            double p = rack.UnknownLiveChance;
            int pct = (int)Math.Round(p * 100);
            // 概率越高越红，越低越蓝
            Color c = Blend(BlankCol, LiveCol, p);
            for (int i = 0; i < n && i < rChips.Count; i++)
            {
                if (rack.Shells[i] != ShellState.Unknown) continue;
                Rectangle r = rChips[i];
                Rectangle pr = new Rectangle(r.X - Px(6), rProbArea.Y, r.Width + Px(12), rProbArea.Height);
                Txt(g, pct + "%", fTiny, c, pr, ContentAlignment.MiddleCenter);
            }
        }

        private static Color Blend(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private void DrawSettings(Graphics g)
        {
            int w = ClientSize.Width;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 20, 20, 26))) g.FillRectangle(b, rSetPanel);
            using (Pen p = new Pen(Color.FromArgb(110, 70, 78, 96), 1f)) g.DrawLine(p, 0, rSetPanel.Y, w, rSetPanel.Y);

            // ---- 最上面：使用说明 ----
            Txt(g, "使用说明", fTiny, Color.FromArgb(200, 150, 200, 235), new Rectangle(Px(10), Px(290), Px(90), Px(15)), ContentAlignment.MiddleLeft);
            Txt(g, "左键标记为实弹，右键标记为虚弹", fLabel, Color.FromArgb(225, 228, 236, 246), new Rectangle(Px(10), Px(306), w - Px(20), Px(16)), ContentAlignment.MiddleLeft);
            Txt(g, "标记第一发视为标记并击发", fLabel, Color.FromArgb(225, 228, 236, 246), new Rectangle(Px(10), Px(323), w - Px(20), Px(16)), ContentAlignment.MiddleLeft);
            using (Pen p = new Pen(Color.FromArgb(70, 70, 78, 96), 1f)) g.DrawLine(p, Px(10), Px(344), w - Px(10), Px(344));

            Txt(g, "防误触间隔", fLabel, Color.FromArgb(215, 220, 228, 240), new Rectangle(Px(10), rCoolMinus.Y, Px(96), rCoolMinus.Height), ContentAlignment.MiddleLeft);
            Spinner(g, rCoolMinus, "−"); Spinner(g, rCoolPlus, "+");
            Txt(g, (cfg.ClickCooldownMs == 0 ? "关闭" : cfg.ClickCooldownMs + " 毫秒"), fLabel, Color.FromArgb(235, 240, 244, 250), rCoolVal, ContentAlignment.MiddleCenter);

            Txt(g, "窗口不透明度", fLabel, Color.FromArgb(215, 220, 228, 240), new Rectangle(Px(10), rOpMinus.Y, Px(96), rOpMinus.Height), ContentAlignment.MiddleLeft);
            Spinner(g, rOpMinus, "−"); Spinner(g, rOpPlus, "+");
            Txt(g, cfg.OpacityPct + " %", fLabel, Color.FromArgb(235, 240, 244, 250), rOpVal, ContentAlignment.MiddleCenter);

            Toggle(g, rTopToggle, "窗口置顶：", cfg.TopMost);
            Toggle(g, rThroughToggle, "鼠标穿透：", cfg.ClickThrough);
            Btn(g, rResetPos, "窗口归位", Color.FromArgb(180, 190, 202), false);
        }

        private void Toggle(Graphics g, Rectangle r, string label, bool on)
        {
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(7)))
            using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(255, 34, 66, 48) : Color.FromArgb(255, 31, 31, 39))) g.FillPath(b, gp);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(7)))
            using (Pen p = new Pen(on ? Color.FromArgb(210, 90, 220, 140) : Color.FromArgb(120, 150, 158, 172), 1f)) g.DrawPath(p, gp);
            string t = label + (on ? "开" : "关");
            Txt(g, t, fBtn, on ? Color.FromArgb(235, 150, 240, 190) : Color.FromArgb(215, 180, 188, 202), r, ContentAlignment.MiddleCenter);
        }

        private void DrawRack(Graphics g)
        {
            rChips.Clear();
            int n = rack.Total;
            if (n == 0) return;
            int gap = Px(6);
            int cw = Math.Min(Px(52), (rRackArea.Width - gap * (n - 1)) / Math.Max(1, n));
            if (cw < Px(24)) { cw = Math.Max(Px(14), (rRackArea.Width - Px(2) * (n - 1)) / Math.Max(1, n)); gap = Px(2); }
            int chh = rRackArea.Height - Px(6);
            int total = cw * n + gap * (n - 1);
            int x = rRackArea.X + (rRackArea.Width - total) / 2;
            int y = rRackArea.Y + Px(5);

            bool cool = Cooling;
            for (int i = 0; i < n; i++)
            {
                Rectangle r = new Rectangle(x + i * (cw + gap), y, cw, chh);
                rChips.Add(r);
                ShellState s = rack.Shells[i];
                Color c = s == ShellState.Live ? LiveCol : (s == ShellState.Blank ? BlankCol : UnknownCol);
                bool first = (i == 0);

                // 防误触冷却期间整体压暗，表示"现在点不动"
                int aFill = cool ? 90 : (s == ShellState.Unknown ? 255 : 230);
                int aEdge = cool ? 60 : ((first && !cool) ? 240 : 150);
                int aText = cool ? 110 : (s == ShellState.Unknown ? 200 : 245);

                using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(7)))
                using (SolidBrush b = new SolidBrush(s == ShellState.Unknown ? Color.FromArgb(aFill, 34, 36, 44) : Color.FromArgb(aFill, Color.FromArgb(40, c)))) g.FillPath(b, gp);
                using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(7)))
                using (Pen p = new Pen(Color.FromArgb(aEdge, first ? Color.FromArgb(255, 214, 110) : c), first ? 2.2f : 1.2f)) g.DrawPath(p, gp);

                string txt = s == ShellState.Live ? "实" : (s == ShellState.Blank ? "空" : "?");
                Txt(g, txt, fChip, Color.FromArgb(aText, 250, 250, 252), r, ContentAlignment.MiddleCenter);

                if (first && !cool)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 255, 214, 110)))
                    {
                        PointF[] tri = new PointF[3];
                        tri[0] = new PointF(r.X + r.Width / 2f, r.Y - Px(2));
                        tri[1] = new PointF(r.X + r.Width / 2f - Px(5), r.Y - Px(9));
                        tri[2] = new PointF(r.X + r.Width / 2f + Px(5), r.Y - Px(9));
                        g.FillPolygon(b, tri);
                    }
                    Txt(g, (i + 1).ToString(), fTiny, Color.FromArgb(235, 255, 214, 110), new Rectangle(r.X, r.Bottom - Px(14), r.Width, Px(12)), ContentAlignment.MiddleCenter);
                }
            }
        }

        private void CountBox(Graphics g, Rectangle r, string label, int val, Color col)
        {
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(9)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 25, 25, 32))) g.FillPath(b, gp);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(9)))
            using (Pen p = new Pen(Color.FromArgb(130, col), 1.2f)) g.DrawPath(p, gp);
            Txt(g, label, fLabel, Color.FromArgb(205, col), new Rectangle(r.X, r.Y + Px(5), r.Width, Px(16)), ContentAlignment.MiddleCenter);
            Txt(g, val.ToString(), fBig, col, new Rectangle(r.X, r.Y + Px(20), r.Width, Px(40)), ContentAlignment.MiddleCenter);
        }

        /// <summary>顶部图标按钮（置顶 / 穿透 / 设置）。</summary>
        private void IconBtn(Graphics g, Rectangle r, bool on, int kind)
        {
            Color bg = on ? Color.FromArgb(255, 52, 62, 82) : Color.FromArgb(255, 30, 30, 38);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(6)))
            using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, gp);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(6)))
            using (Pen p = new Pen(on ? Color.FromArgb(230, 110, 190, 255) : Color.FromArgb(95, 82, 88, 102), 1f)) g.DrawPath(p, gp);
            Rectangle ir = new Rectangle(r.X + Px(5), r.Y + Px(5), r.Width - Px(10), r.Height - Px(10));
            Color ic = on ? Color.FromArgb(245, 168, 214, 255) : Color.FromArgb(215, 178, 186, 200);
            if (kind == 0) Icons.Pin(g, ir, ic);
            else if (kind == 1) Icons.Through(g, ir, ic);
            else Icons.Gear(g, ir, ic, bg);
        }

        private void TopBtn(Graphics g, Rectangle r, string text, bool on)        {
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(6)))
            using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(255, 52, 62, 82) : Color.FromArgb(255, 30, 30, 38))) g.FillPath(b, gp);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(6)))
            using (Pen p = new Pen(on ? Color.FromArgb(230, 110, 190, 255) : Color.FromArgb(95, 82, 88, 102), 1f)) g.DrawPath(p, gp);
            Txt(g, text, fLabel, on ? Color.FromArgb(235, 165, 212, 255) : Color.FromArgb(195, 178, 186, 200), r, ContentAlignment.MiddleCenter);
        }

        private void Spinner(Graphics g, Rectangle r, string text)
        {
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(6)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 38, 38, 48))) g.FillPath(b, gp);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(6)))
            using (Pen p = new Pen(Color.FromArgb(120, 110, 118, 134), 1f)) g.DrawPath(p, gp);
            Txt(g, text, fBtn, Color.FromArgb(225, 228, 234, 242), r, ContentAlignment.MiddleCenter);
        }

        private void Btn(Graphics g, Rectangle r, string text, Color col, bool strong)
        {
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(7)))
            using (SolidBrush b = new SolidBrush(strong ? Color.FromArgb(255, Color.FromArgb(34, col)) : Color.FromArgb(255, 31, 31, 39))) g.FillPath(b, gp);
            using (GraphicsPath gp = ThroughToggleForm.RR(r, Px(7)))
            using (Pen p = new Pen(Color.FromArgb(strong ? 220 : 120, col), strong ? 1.6f : 1f)) g.DrawPath(p, gp);
            Txt(g, text, fBtn, strong ? Color.FromArgb(245, 248, 250, 252) : Color.FromArgb(225, col), r, ContentAlignment.MiddleCenter);
        }

        private void Txt(Graphics g, string s, Font f, Color c, Rectangle r, ContentAlignment a)
        {
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Alignment = (a == ContentAlignment.MiddleLeft) ? StringAlignment.Near : (a == ContentAlignment.MiddleCenter ? StringAlignment.Center : StringAlignment.Far);
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
            }
        }

        // ---------------- 鼠标 ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Point p = e.Location;

            // 弹匣上的左/右键优先
            for (int i = 0; i < rChips.Count; i++)
            {
                if (!rChips[i].Contains(p)) continue;
                if (e.Button == MouseButtons.Left) { DoMark(i, ShellState.Live); return; }
                if (e.Button == MouseButtons.Right) { DoMark(i, ShellState.Blank); return; }
            }

            if (e.Button != MouseButtons.Left) return;

            if (rClose.Contains(p)) { SaveCfg(); Close(); return; }
            if (rSettings.Contains(p))
            {
                cfg.ShowSettings = !cfg.ShowSettings;
                ApplyDpi();                       // 窗口高度随设置栏展开/收起变化
                ApplyTopMost();
                cfg.Save();
                status = cfg.ShowSettings ? "设置栏已展开" : "设置栏已收起";
                Invalidate(); return;
            }
            if (rTop.Contains(p)) { cfg.TopMost = !cfg.TopMost; ApplyTopMost(); cfg.Save(); Invalidate(); return; }
            if (rThrough.Contains(p)) { SetClickThrough(!cfg.ClickThrough); return; }

            // ---- 设置栏里的控件 ----
            if (cfg.ShowSettings)
            {
                if (rCoolMinus.Contains(p)) { cfg.ClickCooldownMs = Math.Max(0, cfg.ClickCooldownMs - 100); cfg.Save(); status = "防误触间隔：" + (cfg.ClickCooldownMs == 0 ? "已关闭" : cfg.ClickCooldownMs + " 毫秒"); Invalidate(); return; }
                if (rCoolPlus.Contains(p)) { cfg.ClickCooldownMs = Math.Min(3000, cfg.ClickCooldownMs + 100); cfg.Save(); status = "防误触间隔：" + cfg.ClickCooldownMs + " 毫秒（0 = 关闭）"; Invalidate(); return; }
                if (rOpMinus.Contains(p)) { cfg.OpacityPct -= 5; cfg.Clamp(); ApplyOpacity(); cfg.Save(); status = "窗口不透明度：" + cfg.OpacityPct + "%"; Invalidate(); return; }
                if (rOpPlus.Contains(p)) { cfg.OpacityPct += 5; cfg.Clamp(); ApplyOpacity(); cfg.Save(); status = "窗口不透明度：" + cfg.OpacityPct + "%"; Invalidate(); return; }
                if (rTopToggle.Contains(p)) { cfg.TopMost = !cfg.TopMost; ApplyTopMost(); cfg.Save(); status = cfg.TopMost ? "已置顶" : "已取消置顶"; Invalidate(); return; }
                if (rThroughToggle.Contains(p)) { SetClickThrough(!cfg.ClickThrough); return; }
                if (rResetPos.Contains(p))
                {
                    Rectangle vs = SystemInformation.VirtualScreen;
                    Location = new Point(vs.Right - Width - (int)(18 * S), vs.Top + (int)(18 * S));
                    cfg.X = Location.X; cfg.Y = Location.Y; cfg.Save();
                    status = "窗口已回到右上角"; Invalidate(); return;
                }
            }

            if (rLiveMinus.Contains(p)) { setLive = Math.Max(0, setLive - 1); cfg.LiveCount = setLive; cfg.Save(); Invalidate(); return; }
            if (rLivePlus.Contains(p)) { setLive = Math.Min(8, setLive + 1); cfg.LiveCount = setLive; cfg.Save(); Invalidate(); return; }
            if (rBlankMinus.Contains(p)) { setBlank = Math.Max(0, setBlank - 1); cfg.BlankCount = setBlank; cfg.Save(); Invalidate(); return; }
            if (rBlankPlus.Contains(p)) { setBlank = Math.Min(8, setBlank + 1); cfg.BlankCount = setBlank; cfg.Save(); Invalidate(); return; }
            if (rLoad.Contains(p)) { DoLoad(); return; }
            if (rUndo.Contains(p)) { DoUndo(); return; }
            if (rReload.Contains(p)) { DoLoad(); return; }

            dragging = true; dragOrigin = new Point(e.X, e.Y);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Point sp = PointToScreen(new Point(e.X, e.Y));
                Location = new Point(sp.X - dragOrigin.X, sp.Y - dragOrigin.Y);
                cfg.X = Location.X; cfg.Y = Location.Y;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; base.OnMouseUp(e); }
        protected override void OnMove(EventArgs e) { base.OnMove(e); if (cfg.ClickThrough) SyncThroughButton(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); SyncThroughButton(); }

        private void SetClickThrough(bool on)
        {
            cfg.ClickThrough = on;
            ApplyClickThrough();
            cfg.Save();
            SyncThroughButton();
            status = on ? "鼠标穿透：开 —— 点右上角的穿透图标可关掉" : "鼠标穿透：关";
            Invalidate();
        }

        public void TurnOffClickThrough() { if (cfg.ClickThrough) SetClickThrough(false); }

        private void SyncThroughButton()
        {
            try
            {
                if (cfg.ClickThrough && Visible)
                {
                    DoLayout();
                    if (throughBtn == null) throughBtn = new ThroughToggleForm(this);
                    throughBtn.PlaceAt(RectangleToScreen(rThrough), S, cfg.OpacityPct);
                    if (!throughBtn.Visible) throughBtn.Show(this);
                    throughBtn.BringToFront();
                    throughBtn.TopMost = true;
                }
                else if (throughBtn != null && throughBtn.Visible) throughBtn.Hide();
            }
            catch { }
        }

        private void StartTopMostWatch()
        {
            if (topWatch != null) return;
            topWatch = new System.Windows.Forms.Timer();
            topWatch.Interval = 1500;
            topWatch.Tick += delegate
            {
                if (!IsHandleCreated) return;
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                if (cfg.TopMost && (ex & 0x8) == 0)
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            };
            topWatch.Start();
        }

        private void SaveCfg()
        {
            cfg.X = Location.X; cfg.Y = Location.Y;
            cfg.LiveCount = setLive; cfg.BlankCount = setBlank;
            cfg.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveCfg();
            try { if (throughBtn != null) { throughBtn.Close(); throughBtn.Dispose(); throughBtn = null; } } catch { }
            base.OnFormClosing(e);
        }
    }
}
