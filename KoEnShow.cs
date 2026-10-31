// KoEnShow - 텍스트 입력 상태가 되면 마우스 커서 옆에 현재 한/영 상태를 표시
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KoEnShow
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        public const int WM_IME_CONTROL = 0x283;
        public const int IMC_GETCONVERSIONMODE = 0x1;
        public const int IMC_GETOPENSTATUS = 0x5;
        public const int IME_CMODE_NATIVE = 0x1;
        public const uint SMTO_ABORTIFHUNG = 0x2;

        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
            WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    }

    // 포커스된 요소가 편집 가능한지 UI Automation으로 추적 (크롬/엣지/UWP 등 시스템 캐럿을 쓰지 않는 앱용)
    sealed class UiaFocusTracker
    {
        sealed class Snapshot
        {
            public int Pid;
            public bool Editable;
            public int Serial;
            public AutomationElement Element;
        }

        volatile Snapshot current = new Snapshot();
        int serial;

        public void Start()
        {
            var t = new Thread(() =>
            {
                // 등록은 UIA 초기화 경합 등으로 E_FAIL이 날 수 있어, 실패하면 이벤트를 영영 못 받으므로 성공할 때까지 재시도
                while (true)
                {
                    try
                    {
                        Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);
                        return;
                    }
                    catch { }
                    Thread.Sleep(1000);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void Stop()
        {
            try { Automation.RemoveAllEventHandlers(); } catch { }
        }

        // 포그라운드 창이 바뀌면 포커스 요소를 직접 조회. 이미 입력란에 포커스가 있던 창은 이벤트가 오지 않고,
        // Chromium 계열은 첫 UIA 조회 뒤에야 접근성 트리를 켜므로 잠시 후 한 번 더 조회한다.
        public void Refresh()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Query();
                Thread.Sleep(500);
                Query();
            });
        }

        void Query()
        {
            try { Update(AutomationElement.FocusedElement); }
            catch { }
        }

        void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
        {
            Update(sender as AutomationElement);
        }

        void Update(AutomationElement el)
        {
            if (el == null) return;
            var snap = new Snapshot();
            snap.Element = el;
            try
            {
                snap.Pid = el.Current.ProcessId;
                snap.Editable = IsEditable(el);
                // 같은 요소를 다시 조회한 경우는 포커스 변경으로 보지 않음
                Snapshot prev = current;
                if (prev.Element != null && prev.Editable == snap.Editable && Automation.Compare(prev.Element, el))
                    return;
            }
            catch { return; }
            snap.Serial = Interlocked.Increment(ref serial);
            current = snap;
        }

        static bool IsEditable(AutomationElement el)
        {
            ControlType ct = el.Current.ControlType;
            object p;
            if (ct == ControlType.Edit)
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out p))
                    return !((ValuePattern)p).Current.IsReadOnly;
                return true;
            }
            if (ct == ControlType.Document || ct == ControlType.ComboBox || ct == ControlType.Custom ||
                ct == ControlType.Pane || ct == ControlType.Group)
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out p))
                    return !((ValuePattern)p).Current.IsReadOnly;
            }
            return false;
        }

        // 현재 포그라운드 프로세스의 포커스가 편집 가능한 요소인지. serial은 포커스가 바뀔 때마다 증가.
        public bool IsEditableIn(uint pid, out int focusSerial)
        {
            Snapshot s = current;
            focusSerial = s.Serial;
            return s.Pid == (int)pid && s.Editable;
        }
    }

    sealed class IndicatorForm : Form
    {
        bool hangul;
        bool shown;
        readonly float scale;

        public IndicatorForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.92;
            DoubleBuffered = true;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size((int)(18 * scale), (int)(16 * scale));
            using (var path = RoundRect(new Rectangle(0, 0, Width, Height), (int)(4 * scale)))
                Region = new Region(path);
            Font = new Font("Malgun Gothic", 7.5f, FontStyle.Bold);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public bool Hangul
        {
            get { return hangul; }
            set { if (hangul != value) { hangul = value; Invalidate(); } }
        }

        public void ShowAt(Point cursor)
        {
            Rectangle wa = Screen.FromPoint(cursor).WorkingArea;
            // 화살표 커서 몸통 바로 오른쪽에 붙임
            int x = cursor.X + (int)(12 * scale);
            int y = cursor.Y + (int)(8 * scale);
            if (x + Width > wa.Right) x = cursor.X - Width - (int)(2 * scale);
            if (y + Height > wa.Bottom) y = cursor.Y - Height - (int)(2 * scale);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (!shown)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                shown = true;
            }
        }

        public void HideIndicator()
        {
            if (!shown) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color back = hangul ? Color.FromArgb(37, 99, 235) : Color.FromArgb(64, 64, 64);
            g.Clear(back);
            TextRenderer.DrawText(g, hangul ? "한" : "A", Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    sealed class AppContext : ApplicationContext
    {
        const string RegKey = @"Software\KoEnShow";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const int ShowMs = 1500;

        readonly IndicatorForm indicator = new IndicatorForm();
        readonly UiaFocusTracker uia = new UiaFocusTracker();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem alwaysItem, autostartItem;
        readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        bool alwaysShow;
        bool lastReady, lastHangul;
        IntPtr lastFocusHwnd, lastFg;
        int lastUiaSerial = -1;
        int hideAt;
        bool? trayHangul;

        public AppContext()
        {
            alwaysShow = ReadSetting("AlwaysShow", 0) != 0;

            var menu = new ContextMenuStrip();
            alwaysItem = new ToolStripMenuItem("입력 중 항상 표시", null, (s, e) => ToggleAlways());
            alwaysItem.Checked = alwaysShow;
            autostartItem = new ToolStripMenuItem("Windows 시작 시 실행", null, (s, e) => ToggleAutostart());
            autostartItem.Checked = IsAutostart();
            menu.Items.Add(alwaysItem);
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => ExitThread());
            tray.ContextMenuStrip = menu;
            tray.Text = "KoEnShow - 한/영 표시";
            UpdateTrayIcon(false);
            tray.Visible = true;

            indicator.CreateControl();
            IntPtr force = indicator.Handle;

            uia.Start();
            timer.Interval = 50;
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        void Tick()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint pid = 0;
            uint tid = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out pid);
            if (tid == 0 || pid == myPid) { Reset(); return; }
            if (fg != lastFg)
            {
                lastFg = fg;
                uia.Refresh();
            }

            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(gti);
            if (!Native.GetGUIThreadInfo(tid, ref gti) || gti.hwndMenuOwner != IntPtr.Zero) { Reset(); return; }

            int uiaSerial;
            bool uiaEditable = uia.IsEditableIn(pid, out uiaSerial);
            bool ready = gti.hwndCaret != IntPtr.Zero || uiaEditable;
            if (!ready) { Reset(); return; }

            IntPtr focus = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : fg;
            bool hangul = QueryHangul(focus, tid);

            int now = Environment.TickCount;
            bool trigger = !lastReady || hangul != lastHangul || focus != lastFocusHwnd || uiaSerial != lastUiaSerial;
            if (trigger) hideAt = now + ShowMs;

            lastReady = true;
            lastHangul = hangul;
            lastFocusHwnd = focus;
            lastUiaSerial = uiaSerial;
            UpdateTrayIcon(hangul);

            if (alwaysShow || now - hideAt < 0)
            {
                indicator.Hangul = hangul;
                indicator.ShowAt(Cursor.Position);
            }
            else indicator.HideIndicator();
        }

        void Reset()
        {
            lastReady = false;
            indicator.HideIndicator();
        }

        static bool QueryHangul(IntPtr focus, uint tid)
        {
            long hkl = Native.GetKeyboardLayout(tid).ToInt64();
            if ((hkl & 0xFFFF) != 0x0412) return false; // 한국어 키보드가 아니면 영문

            IntPtr ime = Native.ImmGetDefaultIMEWnd(focus);
            if (ime == IntPtr.Zero) return false;
            IntPtr mode;
            if (Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, (IntPtr)Native.IMC_GETCONVERSIONMODE,
                    IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out mode) == IntPtr.Zero)
                return false;
            return (mode.ToInt64() & Native.IME_CMODE_NATIVE) != 0;
        }

        void UpdateTrayIcon(bool hangul)
        {
            if (trayHangul == hangul) return;
            trayHangul = hangul;
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (var path = IndicatorForm.RoundRect(new Rectangle(0, 0, 31, 31), 7))
                using (var brush = new SolidBrush(hangul ? Color.FromArgb(37, 99, 235) : Color.FromArgb(64, 64, 64)))
                using (var font = new Font("Malgun Gothic", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.FillPath(brush, path);
                    g.DrawString(hangul ? "한" : "A", font, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                }
                IntPtr h = bmp.GetHicon();
                Icon old = tray.Icon;
                tray.Icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                if (old != null) old.Dispose();
            }
        }

        void ToggleAlways()
        {
            alwaysShow = !alwaysShow;
            alwaysItem.Checked = alwaysShow;
            WriteSetting("AlwaysShow", alwaysShow ? 1 : 0);
        }

        static bool IsAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("KoEnShow") != null;
        }

        void ToggleAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsAutostart()) k.DeleteValue("KoEnShow", false);
                else k.SetValue("KoEnShow", "\"" + Application.ExecutablePath + "\"");
            }
            autostartItem.Checked = IsAutostart();
        }

        static int ReadSetting(string name, int def)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v : def;
            }
        }

        static void WriteSetting(string name, int value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey))
                k.SetValue(name, value, RegistryValueKind.DWord);
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            uia.Stop();
            tray.Visible = false;
            tray.Dispose();
            indicator.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "KoEnShow_SingleInstance", out created))
            {
                if (!created) return;
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new AppContext());
            }
        }
    }
}
