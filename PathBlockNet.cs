using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool fresh; 
        var mutex = new Mutex(true, "PathBlockNet_SingleInstance_v2", out fresh);
        if (!fresh) return;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }
}

class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);

    const int WM_HOTKEY = 0x0312;
    const int HK_KILL = 1, HK_LAG = 2;
    const string RULE_IN = "PathBlockNet_BlockIn", RULE_OUT = "PathBlockNet_BlockOut";

    string dir = AppDomain.CurrentDomain.BaseDirectory;
    string iniPath;
    dynamic policy;
    bool netOff =false;
    Lag lag = new Lag();

    Keys killKey = Keys.Divide, lagKey = Keys.Multiply;

    NotifyIcon tray;
    TextBox txtKill, txtLag;
    NumericUpDown numMs, numJit;
    Button btnKill, btnLag;
    Label lblInfo;
    bool allowShow = false;

    public MainForm()
    {
        iniPath = Path.Combine(dir, "PathBlockNet.ini");
        policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        Unblock();

        Text = "PathBlockNet";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(230, 154);
        Font = new Font("Segoe UI", 8.25f);

        AddLabel("Kill internet:", 8, 10);
        txtKill = AddKeyBox(100, 7);
        AddLabel("Lag hotkey:", 8, 36);
        txtLag = AddKeyBox(100, 33);
        AddLabel("Ping (ms):", 8, 62);
        numMs = new NumericUpDown { Location = new Point(100, 59), Width = 122, Minimum = 0, Maximum = 5000, Value = 150, Increment = 10 };
        numMs.ValueChanged += (s, e) => { lag.DelayMs = (int)numMs.Value; Save(); };
        Controls.Add(numMs);
        AddLabel("Jitter (± ms):", 8, 88);
        numJit = new NumericUpDown { Location = new Point(100, 85), Width = 122, Minimum = 0, Maximum = 500, Value = 10, Increment = 1 };
        numJit.ValueChanged += (s, e) => { lag.JitterMs = (int)numJit.Value; Save(); };
        Controls.Add(numJit);

        btnKill = new Button { Location = new Point(8, 112), Size = new Size(104, 24) };
        btnKill.Click += (s, e) => ToggleKill();
        btnLag = new Button { Location = new Point(118, 112), Size = new Size(104, 24) };
        btnLag.Click += (s,e) => ToggleLag();
        Controls.Add(btnKill); Controls.Add(btnLag);

        lblInfo = new Label { Location = new Point(8, 138), AutoSize = true, ForeColor = Color.Gray, Text = "Click a box to change the hotkey" };
        Controls.Add(lblInfo);


        Load_();
        txtKill.Text = KeyName(killKey);
        txtLag.Text = KeyName(lagKey);
        lag.DelayMs = (int)numMs.Value;
        lag.JitterMs = (int)numJit.Value;  

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (s, e) => ShowPanel());
        menu.Items.Add("Exit", null, (s, e) => ExitApp());
        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (Visible) Hide(); else ShowPanel(); } };

        UpdateUi();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow) { value = false; if (!IsHandleCreated) CreateHandle(); }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        RegisterAll();
    }

    void ShowPanel()
    {

        allowShow = true;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 8, wa.Bottom - Height - 8);
        Show(); Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        ActiveControl = null;
        RegisterAll();
    }

    void ExitApp()
    {
        //MessageBox.Show("exit");
		//lag.Stop();
        lag.Stop();
        Unblock();
        tray.Visible = false;
        Application.Exit();
    }

    void AddLabel(string t, int x, int y) { Controls.Add(new Label { Text = t, Location = new Point(x, y + 3), AutoSize = true }); }

    TextBox AddKeyBox(int x, int y)
    {
        var tb = new TextBox { Location = new Point(x, y), Width = 122, ReadOnly = true, BackColor = SystemColors.Window, Cursor = Cursors.Hand };
        tb.Enter += (s, e) => { UnregisterAll(); tb.Text = "Press a key..."; };
        tb.Leave += (s, e) => { tb.Text = KeyName(tb == txtKill ? killKey : lagKey); RegisterAll(); };
        tb.KeyDown += (s, e) =>
        {
            e.SuppressKeyPress = true; e.Handled = true;
            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return;
            if (k == Keys.Escape) { ActiveControl = null; return; }
            Keys full = k | e.Modifiers;
            Keys other = tb == txtKill ? lagKey : killKey;
            if (full == other) { lblInfo.Text = "That key is already in use"; return; }
            if (tb == txtKill) killKey = full; else lagKey = full;
            Save();
            lblInfo.Text = "Saved";
            ActiveControl = null;
        };
        Controls.Add(tb);
        return tb;
    }

    static string KeyName(Keys k)
    {
        var sb = new StringBuilder();
        if ((k & Keys.Control) != 0) sb.Append("Ctrl + ");
        if ((k & Keys.Alt) != 0) sb.Append("Alt + ");
        if ((k & Keys.Shift) != 0) sb.Append("Shift + ");
        Keys c = k & Keys.KeyCode;
        switch (c)
        {
            case Keys.Divide: sb.Append("Num /"); break;
            case Keys.Multiply: sb.Append("Num *"); break;
            case Keys.Subtract: sb.Append("Num -"); break;
            case Keys.Add: sb.Append("Num +"); break;
            case Keys.Decimal: sb.Append("Num ,"); break;
            default:
                if (c >= Keys.NumPad0 && c <= Keys.NumPad9) sb.Append("Num " + (c - Keys.NumPad0));
                else if (c >= Keys.D0 && c <= Keys.D9) sb.Append((char)('0' + (c - Keys.D0)));
                else sb.Append(c.ToString());
                break;
        }
        return sb.ToString();
    }


    bool registered = false;
    void RegisterAll()
    {
        if (registered || !IsHandleCreated) return;
        bool a = Reg(HK_KILL, killKey), b = Reg(HK_LAG, lagKey);
        registered = true;
        if (!a || !b) lblInfo.Text = "Hotkey is used by another program";
    }
    bool Reg(int id, Keys k)
    {
        uint mods = 0x4000;
        if ((k & Keys.Alt) != 0) mods |= 1;
        if ((k & Keys.Control) != 0)  mods |= 2;
        if ((k & Keys.Shift) != 0) mods |= 4;
        return RegisterHotKey(Handle, id, mods, (uint)(k & Keys.KeyCode));
    }
    void UnregisterAll()
    {
        UnregisterHotKey(Handle, HK_KILL);UnregisterHotKey(Handle, HK_LAG);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HK_KILL) ToggleKill();
            else if (id == HK_LAG) ToggleLag();
        }
        base.WndProc(ref m);
    }

    void ToggleKill()
    {
        if (!netOff) {
            Play("fnaf-open-camera-sound.mp3");
            Block();
        }
        else { Unblock(); Play("fnaf-4-flashlight-turned-on.mp3"); }
        netOff = !netOff;
        UpdateUi();
    }

    void Block() { AddRule(RULE_OUT, 2);  AddRule(RULE_IN, 1); }

    void AddRule(string name, int direction)
    {
        dynamic r = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
        r.Name = name;
        r.Direction = direction; //1 in 2 out
        r.Action = 0;
        r.Profiles = 0x7FFFFFFF;
        r.Enabled = true;
        policy.Rules.Add(r);
    }

    void Unblock()
    {
        foreach (var n in new[] { RULE_OUT, RULE_IN })
            for (int i = 0; i < 10; i++) { try { policy.Rules.Item(n); policy.Rules.Remove(n); } catch { break; } }
    }

    void ToggleLag()
    {
        if (lag.Running) { lag.Stop(); Play("fnaf-light-sound.mp3"); }
        else
        {
            string err = lag.Start();
            if (err != null) { lblInfo.Text = "Could not start lag"; tray.ShowBalloonTip(4000, "PathBlockNet", err, ToolTipIcon.Error); }
            else Play("fnaf-nose-honk_DMxbbz7.mp3");
        }
        UpdateUi();
    }

    void Play(string file)
    {
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;
        mciSendString("close snd", null, 0, IntPtr.Zero);
        mciSendString("open \"" + path + "\" type mpegvideo alias snd", null, 0, IntPtr.Zero);
        mciSendString("setaudio snd volume to 10", null, 0, IntPtr.Zero);
        mciSendString("play snd", null, 0, IntPtr.Zero);
    }

    IntPtr lastIcon = IntPtr.Zero;
    void UpdateUi()
    {
        btnKill.Text = netOff ? "Internet: OFF" : "Internet: ON";
        btnKill.BackColor = netOff ? Color.FromArgb(255, 200, 200) : SystemColors.Control;
        btnLag.Text = lag.Running ? "Lag: ON" : "Lag: OFF";
        btnLag.BackColor = lag.Running ? Color.FromArgb(255, 235, 180) : SystemColors.Control;

        Color c = netOff ? Color.FromArgb(220, 50, 50) : lag.Running ? Color.FromArgb(240, 170, 0) : Color.FromArgb(40, 170, 80);
        int sz = SystemInformation.SmallIconSize.Width;
        //int sz = 16;
        using (var bmp = new Bitmap(sz, sz))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            Image img = TrayImage();
            if (img != null) g.DrawImage(img, 0, 0, sz, sz);
            else using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, sz - 2, sz - 2);
            int d = sz/2;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, sz - d, sz - d, d - 1, d - 1);
            using (var p = new Pen(Color.White, 1)) g.DrawEllipse(p, sz - d, sz - d, d - 1, d - 1);
			//using (var p = new Pen(Color.White, 5)) g.DrawEllipse(p, sz - d, sz - d, d - 1, d - 1);
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (lastIcon != IntPtr.Zero) DestroyIcon(lastIcon);
            lastIcon = h;
        }
        tray.Text = "PathBlockNet - " + (netOff ? "internet off" : "internet on") + (lag.Running ? ", ping +" + lag.DelayMs + "ms" : "");
    }

    Image trayImg;
    bool trayImgTried;
    Image TrayImage()
    {
        if (trayImgTried) return trayImg;
        trayImgTried = true;
        try
        {
            string p = Path.Combine(dir, "images.jpg");
            if (File.Exists(p)) using (var tmp = Image.FromFile(p)) trayImg = new Bitmap(tmp);
            else
            {
                var s = typeof(MainForm).Assembly.GetManifestResourceStream("images.jpg");
                if (s != null) trayImg = new Bitmap(s);
            }
        }
        catch { }
        return trayImg;
    }

    void Save()
    {
        try { File.WriteAllText(iniPath, "KillKey=" + (int)killKey + "\r\nLagKey=" + (int)lagKey + "\r\nLagMs=" + numMs.Value + "\r\nJitterMs=" + numJit.Value + "\r\n"); } catch { }
    }
    void Load_()
    {
        try
        {
            if (!File.Exists(iniPath)) return;
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var p = line.Split('=');
                if(p.Length != 2) continue;
                int v; if (!int.TryParse(p[1].Trim(), out v)) continue;
                if (p[0] == "KillKey") killKey = (Keys)v;
                else if (p[0] == "LagKey") lagKey = (Keys)v;
                else if (p[0] == "LagMs") numMs.Value = Math.Max(0, Math.Min(5000, v));
                else if (p[0] == "JitterMs") numJit.Value = Math.Max(0, Math.Min(500, v));
            }
        }
        catch { }
    }
}

class Lag
{
    [DllImport("WinDivert.dll", CharSet = CharSet.Ansi)] static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);
    [DllImport("WinDivert.dll")] static extern bool WinDivertRecv(IntPtr h, byte[] pkt, uint len, out uint recvLen, byte[] addr);
    [DllImport("WinDivert.dll")] static extern bool WinDivertSend(IntPtr h, byte[] pkt, uint len, out uint sendLen, byte[] addr);
    [DllImport("WinDivert.dll")] static extern bool WinDivertClose(IntPtr h);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

    class Pkt { public byte[] data; public uint len; public byte[] addr; public long due; }

    public volatile int DelayMs = 150;
    public volatile int JitterMs = 10;

    Random rnd = new Random();
    double jFrom, jTo;
    long jStart, jEnd, lastDue;

    int Jitter(long now)
    {
        int j = JitterMs;
        if (j <= 0) return 0;
        if (now >= jEnd)
        {
            jFrom = jTo;
            jTo = (rnd.NextDouble() * 2 - 1) * j;
            jStart =  now;
            jEnd = now + 300 + rnd.Next(700);
        }
        double t = (now - jStart) / (double)(jEnd - jStart);
        t = t * t * (3 - 2 * t);
        return (int)Math.Round(jFrom + (jTo - jFrom) * t);
    }

    public bool Running { get { return running; } }
    volatile bool running;
    IntPtr h = IntPtr.Zero;
    Queue<Pkt> q = new Queue<Pkt>();
    Stopwatch sw = Stopwatch.StartNew();
    Thread tRecv, tSend;

    public string Start()
    {
        if (running) return null;
        string dllDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(dllDir, "WinDivert.dll")) || !File.Exists(Path.Combine(dllDir, "WinDivert64.sys")))
            return "WinDivert.dll and WinDivert64.sys must be next to the exe.";
        try { h = WinDivertOpen("outbound and !loopback", 0, 0, 0); }
        catch (Exception ex) { return "Failed to load WinDivert: " + ex.Message; }
        if (h == new IntPtr(-1) || h == IntPtr.Zero) { h = IntPtr.Zero; return "Failed to open WinDivert (error " + Marshal.GetLastWin32Error() + ")."; }

        timeBeginPeriod(1);
	
        running = true;
        lock (q) q.Clear();
        tRecv = new Thread(RecvLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
        tSend = new Thread(SendLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
        tRecv.Start();
        tSend.Start();
        return null;
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        lock (q) Monitor.PulseAll(q);
        tSend.Join(500);
        lock (q) { while (q.Count > 0) { var p = q.Dequeue(); uint s; WinDivertSend(h, p.data, p.len, out s, p.addr); } }
        WinDivertClose(h);
        tRecv.Join(500);
        h = IntPtr.Zero;
        timeEndPeriod(1);
    }

    void RecvLoop()
    {
        var buf = new byte[0xFFFF];
        while (running)
        {
            var addr = new byte[80]; // WINDIVERT_ADDRESS size
            uint len;
            if (!WinDivertRecv(h, buf, (uint)buf.Length, out len, addr)) { if (!running) break; continue; }
            var data = new byte[len];
            Buffer.BlockCopy(buf, 0, data, 0, (int)len);
            long now = sw.ElapsedMilliseconds;
            long due = now + Math.Max(0, DelayMs + Jitter(now));
            if (due < lastDue) due = lastDue;
            lastDue = due;
            var p = new Pkt { data = data, len = len, addr = addr, due = due };
            lock (q) { q.Enqueue(p); Monitor.Pulse(q); }
        }
    }

    void SendLoop()
    {
        while (running)
        {
            Pkt p;
            lock (q)
            {
                while (running && q.Count == 0) Monitor.Wait(q);
                if (!running) break;
                p = q.Peek();
            }
            long wait = p.due - sw.ElapsedMilliseconds; 
            if (wait > 0)
            {
                Thread.Sleep((int)Math.Min(wait, 20));
                continue;
            }
            lock (q) { if (q.Count > 0 && q.Peek() == p) q.Dequeue(); else continue; }
            uint s;
            WinDivertSend(h, p.data, p.len, out s, p.addr);
        }
    }
}
