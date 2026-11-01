// PodBattery - tiny tray app that reads AirPods battery from Apple BLE advertisements.
// Build: build.cmd (uses the C# compiler that ships with Windows, no SDK needed).
//
// Credits: the advertisement layout and the "strongest recent signal" approach follow the
// reverse-engineering published by OpenPods (https://github.com/adolfintel/OpenPods).
// Model IDs cross-checked against AirPodsDesktop (https://github.com/SpriteOvO/AirPodsDesktop).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

class PodStatus
{
    public string Model;
    public bool Single;                            // one battery (AirPods Max), stored in Left
    public int Left = -1, Right = -1, Case = -1;   // percent, -1 = unknown
    public bool LeftCharging, RightCharging, CaseCharging;
    public short Rssi;
    public DateTime Seen;
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new System.Threading.Mutex(true, "PodBattery_SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }
}

class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "PodBattery";

    readonly NotifyIcon tray = new NotifyIcon();
    readonly BluetoothLEAdvertisementWatcher watcher = new BluetoothLEAdvertisementWatcher();
    readonly Timer timer = new Timer();
    readonly object gate = new object();
    PodStatus latest;        // written from BLE thread, read on UI thread
    PodStatus shown;
    PopupForm popup;
    ToolStripMenuItem startupItem;
    string lastIconKey;
    bool scanFailed;

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, delegate { ShowPopup(); });
        startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
        startupItem.Checked = IsStartupEnabled();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { ExitThread(); });

        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowPopup(); };
        tray.Visible = true;

        watcher.ScanningMode = BluetoothLEScanningMode.Passive;
        watcher.Received += OnReceived;
        EnsureScanning();
        UpdateTray();

        timer.Interval = 1000;
        timer.Tick += delegate { EnsureScanning(); UpdateTray(); };
        timer.Start();
    }

    // (Re)start scanning; the watcher aborts when Bluetooth is turned off, so retry until it's back.
    void EnsureScanning()
    {
        var st = watcher.Status;
        if (st == BluetoothLEAdvertisementWatcherStatus.Started || st == BluetoothLEAdvertisementWatcherStatus.Stopping)
        {
            scanFailed = false;
            return;
        }
        try { watcher.Start(); scanFailed = false; }
        catch { scanFailed = true; }
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            foreach (var md in args.Advertisement.GetManufacturerDataByCompanyId(0x004C))
            {
                var bytes = new byte[md.Data.Length];
                DataReader.FromBuffer(md.Data).ReadBytes(bytes);
                var p = Decode(bytes, args.RawSignalStrengthInDBm);
                if (p == null) continue;
                lock (gate)
                {
                    // Advertisements use rotating addresses, so we can't tell whose AirPods these are.
                    // Keep the strongest signal; replace it if it goes stale.
                    bool stale = latest == null || (DateTime.Now - latest.Seen).TotalSeconds > 10;
                    if (stale || p.Rssi >= latest.Rssi - 5) latest = p;
                }
            }
        }
        catch { }   // a bad packet must never take the app down (this runs on a background thread)
    }

    // Apple "proximity pairing" message: type 0x07, 25 bytes after the type/length header.
    static PodStatus Decode(byte[] d, short rssi)
    {
        if (d.Length < 27 || d[0] != 0x07 || d[1] != 25) return null;
        if (rssi < -75) return null;   // ignore far-away devices

        int model = (d[3] << 8) | d[4];
        bool flip = (d[5] & 0x20) == 0;
        int a = d[6] >> 4, b = d[6] & 0x0F;
        int caseVal = d[7] & 0x0F;
        int chg = d[7] >> 4;

        var p = new PodStatus();
        p.Model = ModelName(model);
        if (model == 0x0A20 || model == 0x1F20)
        {
            p.Single = true;   // AirPods Max: one battery, no case
            p.Left = Pct(b);
            p.LeftCharging = (chg & 0x1) != 0;
        }
        else
        {
            p.Left = Pct(flip ? a : b);
            p.Right = Pct(flip ? b : a);
            p.Case = Pct(caseVal);
            p.LeftCharging = (chg & (flip ? 0x2 : 0x1)) != 0;
            p.RightCharging = (chg & (flip ? 0x1 : 0x2)) != 0;
            p.CaseCharging = (chg & 0x4) != 0;
        }
        p.Rssi = rssi;
        p.Seen = DateTime.Now;
        if (p.Left < 0 && p.Right < 0 && p.Case < 0) return null;
        return p;
    }

    static int Pct(int v) { return v <= 10 ? v * 10 : -1; }   // 0-10 = tens of percent, 15 = n/a

    static string ModelName(int id)
    {
        switch (id)
        {
            case 0x0220: return "AirPods (1st gen)";
            case 0x0F20: return "AirPods (2nd gen)";
            case 0x1320: return "AirPods (3rd gen)";
            case 0x1920: return "AirPods 4";
            case 0x1B20: return "AirPods 4 (ANC)";
            case 0x0E20: return "AirPods Pro";
            case 0x1420: return "AirPods Pro 2";
            case 0x2420: return "AirPods Pro 2 (USB-C)";
            case 0x2720: return "AirPods Pro 3";
            case 0x0A20: return "AirPods Max";
            case 0x1F20: return "AirPods Max (USB-C)";
            default: return "AirPods";
        }
    }

    void UpdateTray()
    {
        PodStatus p;
        lock (gate) { p = latest; }
        if (p != null && (DateTime.Now - p.Seen).TotalSeconds > 60) p = null;
        shown = p;

        string tip;
        int level = -1;
        bool charging = false;
        if (p == null)
            tip = scanFailed ? "PodBattery - Bluetooth is off or unavailable"
                             : "PodBattery - no AirPods seen\n(open the case or take them out)";
        else if (p.Single)
        {
            level = p.Left;
            charging = p.LeftCharging;
            tip = p.Model + "\nBattery " + Fmt(p.Left, p.LeftCharging);
        }
        else
        {
            var buds = new List<int>();
            if (p.Left >= 0) buds.Add(p.Left);
            if (p.Right >= 0) buds.Add(p.Right);
            level = buds.Count > 0 ? Math.Min(buds[0], buds[buds.Count - 1]) : p.Case;
            charging = p.LeftCharging || p.RightCharging;
            tip = p.Model + "\nL " + Fmt(p.Left, p.LeftCharging) + "  R " + Fmt(p.Right, p.RightCharging) +
                  "  Case " + Fmt(p.Case, p.CaseCharging);
        }
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

        string key = level + "|" + charging;
        if (key != lastIconKey)
        {
            lastIconKey = key;
            var old = tray.Icon;
            tray.Icon = MakeIcon(level, charging);
            if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
        }
        if (popup != null && popup.Visible) popup.SetStatus(p);
    }

    internal static string Fmt(int v, bool chg) { return v < 0 ? "--" : v + "%" + (chg ? "+" : ""); }

    internal static Color LevelColor(int v)
    {
        if (v < 0) return Color.Gray;
        if (v <= 20) return Color.FromArgb(235, 80, 70);
        if (v <= 40) return Color.FromArgb(240, 180, 40);
        return Color.FromArgb(70, 200, 110);
    }

    static Icon MakeIcon(int level, bool charging)
    {
        int s = SystemInformation.SmallIconSize.Width;
        using (var bmp = new Bitmap(s, s))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(charging ? Color.FromArgb(40, 120, 220) : Color.FromArgb(32, 32, 32)))
                g.FillEllipse(bg, 0, 0, s - 1, s - 1);
            using (var ring = new Pen(LevelColor(level), Math.Max(1.5f, s / 10f)))
                g.DrawEllipse(ring, 1, 1, s - 3, s - 3);
            string txt = level < 0 ? "-" : level >= 100 ? "F" : (level / 10).ToString();
            using (var f = new Font("Segoe UI", s * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(txt, f, Brushes.White, new RectangleF(0, 0, s, s), sf);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void ShowPopup()
    {
        if (popup == null || popup.IsDisposed) popup = new PopupForm();
        popup.SetStatus(shown);
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        popup.Location = new Point(wa.Right - popup.Width - 12, wa.Bottom - popup.Height - 12);
        popup.Show();
        popup.Activate();
    }

    bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsStartupEnabled()) k.DeleteValue(AppName, false);
            else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
        }
        startupItem.Checked = IsStartupEnabled();
    }

    protected override void ExitThreadCore()
    {
        try { watcher.Stop(); } catch { }
        timer.Stop();
        tray.Visible = false;
        if (tray.Icon != null) DestroyIcon(tray.Icon.Handle);
        tray.Dispose();
        base.ExitThreadCore();
    }
}

class PopupForm : Form
{
    PodStatus status;
    static readonly Color Bg = Color.FromArgb(30, 30, 32), Fg = Color.FromArgb(235, 235, 235), Dim = Color.FromArgb(140, 140, 145);

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        Size = new Size(280, 172);
        Font = new Font("Segoe UI", 10f);
        Deactivate += delegate { Hide(); };
        KeyPreview = true;
        KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    public void SetStatus(PodStatus p) { status = p; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(Color.FromArgb(70, 70, 75)))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using (var title = new Font("Segoe UI Semibold", 11f))
        using (var fg = new SolidBrush(Fg))
        using (var dim = new SolidBrush(Dim))
        {
            if (status == null)
            {
                g.DrawString("No AirPods seen", title, fg, 16, 14);
                g.DrawString("Open the case near this PC, or take\nthe AirPods out. Bluetooth must be on.", Font, dim, 16, 48);
                return;
            }
            g.DrawString(status.Model, title, fg, 16, 10);
            int age = (int)(DateTime.Now - status.Seen).TotalSeconds;
            string sub = "Updated " + (age < 2 ? "just now" : age + "s ago");
            using (var small = new Font("Segoe UI", 8.5f))
                g.DrawString(sub, small, dim, 17, 33);

            if (status.Single)
            {
                Row(g, "Battery", status.Left, status.LeftCharging, 64, fg, dim);
                return;
            }
            Row(g, "Left", status.Left, status.LeftCharging, 64, fg, dim);
            Row(g, "Right", status.Right, status.RightCharging, 98, fg, dim);
            Row(g, "Case", status.Case, status.CaseCharging, 132, fg, dim);
        }
    }

    void Row(Graphics g, string label, int v, bool chg, int y, Brush fg, Brush dim)
    {
        g.DrawString(label, Font, dim, 16, y);
        var bar = new Rectangle(72, y + 5, 130, 10);
        using (var track = new SolidBrush(Color.FromArgb(60, 60, 64)))
            g.FillRectangle(track, bar);
        if (v > 0)
            using (var fill = new SolidBrush(TrayApp.LevelColor(v)))
                g.FillRectangle(fill, bar.X, bar.Y, bar.Width * v / 100, bar.Height);
        g.DrawString(TrayApp.Fmt(v, false) + (chg ? " ⚡" : ""), Font, fg, 212, y);
    }
}
