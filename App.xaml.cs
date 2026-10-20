using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GameHUD;

public partial class App : Application, IHudHost
{
    Mutex? _mutex;
    Settings _settings = new();
    HardwareService? _hw;
    NetworkService? _net;
    readonly EtwService _etw = new();
    OverlayWindow? _overlay;
    ControlPanel? _panel;
    Forms.NotifyIcon? _tray;
    DispatcherTimer? _timer;

    readonly Queue<Dictionary<int, (long Rx, long Tx)>> _netHistory = new();
    ForegroundInfo? _fg;
    string? _pingWarn, _fpsWarn;
    DateTime _pingWarnUntil, _fpsWarnUntil;
    int _lowFpsTicks;
    string? _status;
    DateTime _statusUntil;
    int _ticks;

    const int HK_MODE = 1, HK_DETAILS = 2, HK_GAME = 3, HK_LAYOUT = 4, HK_PANEL = 5;

    // ---------- IHudHost ----------

    public Settings Settings => _settings;
    public HwSnapshot Hw => _hw?.Latest ?? new HwSnapshot();
    public bool PawnIoInstalled => _hw?.PawnIoInstalled ?? true;

    public void SettingsChanged(bool rebuildLook)
    {
        _settings.Save();
        if (rebuildLook) _overlay?.ApplyLook(_settings);
        Tick();
    }

    // ---------- Startup ----------

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Global\GameHUD_SingleInstance", out bool first);
        if (!first)
        {
            // Already running: ask the running copy to open its window.
            try { using var ev = EventWaitHandle.OpenExisting(@"Global\GameHUD_Show"); ev.Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject as Exception);

        _settings = Settings.Load();
        _hw = new HardwareService();
        _net = new NetworkService(_settings);
        _etw.Start();

        _overlay = new OverlayWindow();
        _overlay.ApplyLook(_settings);
        HwndSource.FromHwnd(_overlay.Handle).AddHook(WndProc);
        uint mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(_overlay.Handle, HK_MODE, mods, 0x4F);    // O
        Native.RegisterHotKey(_overlay.Handle, HK_DETAILS, mods, 0x44); // D
        Native.RegisterHotKey(_overlay.Handle, HK_GAME, mods, 0x47);    // G
        Native.RegisterHotKey(_overlay.Handle, HK_LAYOUT, mods, 0x4C);  // L
        Native.RegisterHotKey(_overlay.Handle, HK_PANEL, mods, 0x48);   // H

        SetupTray();
        ListenForShowRequests();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        int shots = Array.IndexOf(e.Args, "--shots");
        if (shots >= 0 && shots + 1 < e.Args.Length) { Screenshots(e.Args[shots + 1]); return; }

        RepairStartupTask();
        bool silent = e.Args.Contains("--tray");
        if (_settings.FirstRun)
        {
            _settings.FirstRun = false;
            _settings.Save();
            OpenPanel();
        }
        else if (!silent) OpenPanel();
    }

    void ListenForShowRequests()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\GameHUD_Show");
        var t = new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.BeginInvoke(() => OpenPanel());
        }) { IsBackground = true };
        t.Start();
    }

    // ---------- Main loop ----------

    void Tick()
    {
        if (_overlay == null || _hw == null || _net == null) return;
        _ticks++;
        _netHistory.Enqueue(_etw.DrainNet());
        while (_netHistory.Count > 5) _netHistory.Dequeue();
        if (_ticks % 30 == 0) _etw.CleanupDeadPids();

        _fg = GameDetector.Detect(_settings, _etw);
        var net = _net.Latest;
        var hw = _hw.Latest;
        var fps = _fg.Pid > 0 ? _etw.GetFps(_fg.Pid) : default;

        string? warning = JoinWarnings(DiagnosePing(net, _fg), DiagnoseFps(fps, hw, _fg));
        string? status = DateTime.UtcNow < _statusUntil ? _status : null;
        bool panelOpen = _panel is { IsVisible: true };

        bool show = _settings.Mode switch
        {
            DisplayMode.Always => true,
            DisplayMode.Off => false,
            _ => _fg.IsGame,
        } || status != null || panelOpen;

        if (!show)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }

        string title = _fg.IsGame ? (string.IsNullOrWhiteSpace(_fg.Title) ? _fg.ProcName : _fg.Title) : "";
        _overlay.Render(hw, net, fps, title, _settings, warning, status);
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.PlaceOn(_fg.Monitor, _settings.Corner);
    }

    static string? JoinWarnings(string? a, string? b) => a == null ? b : b == null ? a : a + "\n\n" + b;

    // ---------- High ping diagnosis ----------

    string? DiagnosePing(NetSnapshot net, ForegroundInfo fg)
    {
        var recent = net.Recent.TakeLast(5).ToList();
        int bad = recent.Count(p => p == null || p > _settings.PingWarnMs);
        if (recent.Count >= 3 && bad >= 3)
        {
            _pingWarn = BuildPingDiagnosis(net, fg);
            _pingWarnUntil = DateTime.UtcNow.AddSeconds(6);
        }
        return DateTime.UtcNow < _pingWarnUntil ? _pingWarn : null;
    }

    string BuildPingDiagnosis(NetSnapshot net, ForegroundInfo fg)
    {
        double secs = Math.Max(1, _netHistory.Count);
        var users = _netHistory
            .SelectMany(d => d)
            .GroupBy(kv => ProcessNames.Friendly(kv.Key))
            .Select(g => new
            {
                Name = g.Key,
                IsGame = g.Any(kv => kv.Key == fg.Pid),
                Rx = g.Sum(kv => kv.Value.Rx) / secs,
                Tx = g.Sum(kv => kv.Value.Tx) / secs,
            })
            .Select(x => new { x.Name, x.IsGame, x.Rx, x.Tx, Total = x.Rx + x.Tx })
            .Where(x => x.Total >= 150 * 1024)
            .OrderByDescending(x => x.Total)
            .Take(2)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("⚠ البينغ عالي (").Append(net.PingMs is double p ? $"{p:0} ms" : "ما في رد").Append(')');
        if (net.LossPct >= 10) sb.Append($" · ضياع باكيتات {net.LossPct:0}%");
        sb.Append('\n');

        if (users.Count > 0)
        {
            var top = users[0];
            if (top.IsGame)
                sb.Append($"اللعبة نفسها عم تسحب {FmtRate(top.Total)} — ممكن عم تنزّل تحديث أو ملفات");
            else
                sb.Append($"السبب: «{top.Name}» عم يسحب {FmtRate(top.Total)} {Direction(top.Rx, top.Tx)}");
            if (users.Count > 1)
                sb.Append($"\nوكمان: «{users[1].Name}» {FmtRate(users[1].Total)}");
            if (!top.IsGame) sb.Append("\nسكّره أو وقّف التنزيل لحتى يرجع البينغ طبيعي");
            if (net.Vpn != null) sb.Append($"\nوكمان إنت شغّال VPN ({net.Vpn}) وهاد بيزيد البينغ");
        }
        else if (net.Vpn != null)
        {
            sb.Append($"السبب الأرجح: إنت شغّال VPN ({net.Vpn}) — طفّيه وقت اللعب، أو اختار سيرفر VPN أقرب");
        }
        else if (net.GatewayPingMs is double gw && gw > 40)
        {
            sb.Append("المشكلة من الشبكة بالبيت (الراوتر").Append(net.Wifi ? " / الواي فاي" : "")
              .Append(") — غالباً جهاز تاني على نفس النت عم يسحب");
            if (net.Wifi) sb.Append("\nنصيحة: جرّب كيبل أو قرّب من الراوتر");
        }
        else
        {
            sb.Append("ما في برنامج على جهازك عم يسحب، والراوتر تمام — المشكلة غالباً من مزود الإنترنت أو سيرفر اللعبة");
        }
        return sb.ToString();
    }

    static string FmtRate(double bps) { var (v, u) = OverlayWindow.Rate(bps); return v + u; }
    static string Direction(double rx, double tx) => rx > tx * 2 ? "(تنزيل)" : tx > rx * 2 ? "(رفع)" : "(تنزيل ورفع)";

    // ---------- Low FPS advice ----------

    string? DiagnoseFps(FpsStats fps, HwSnapshot hw, ForegroundInfo fg)
    {
        if (_settings.FpsWarn > 0 && fg.IsGame && fps.Fps is double f && f > 0 && f < _settings.FpsWarn)
            _lowFpsTicks++;
        else
            _lowFpsTicks = 0;

        // Wait ~5 s of low FPS so loading screens and short dips don't trigger it.
        if (_lowFpsTicks >= 5)
        {
            _fpsWarn = BuildFpsAdvice(fps.Fps!.Value, hw);
            _fpsWarnUntil = DateTime.UtcNow.AddSeconds(8);
        }
        return DateTime.UtcNow < _fpsWarnUntil ? _fpsWarn : null;
    }

    string BuildFpsAdvice(double fps, HwSnapshot hw)
    {
        var sb = new StringBuilder($"⚠ الفريمات نازلة ({fps:0} FPS، تحت {_settings.FpsWarn})\n");
        float gpu = hw.GpuLoad ?? 0, cpu = hw.CpuLoad ?? 0, core = hw.CpuMaxCore ?? cpu;
        bool vramFull = hw.VramUsedMB is float u && hw.VramTotalMB is float t && t > 0 && u / t > 0.95;

        if (hw.GpuTemp >= 85)
            sb.Append($"الكرت سخن كتير ({hw.GpuTemp:0}°C) وعم يخفّف سرعته — تأكد إن المراوح شغّالة ونظّف الغبار");
        else if (hw.CpuTemp >= 95)
            sb.Append($"المعالج سخن كتير ({hw.CpuTemp:0}°C) وعم يخفّف سرعته — تأكد من المبرّد");
        else if (vramFull)
            sb.Append("ذاكرة الكرت (VRAM) مليانة — نزّل جودة الـTextures درجة، أو سكّر البرامج اللي عم تاخد VRAM (Ctrl+Shift+H)");
        else if (hw.RamLoad >= 90)
            sb.Append($"الرام شبه مليانة ({hw.RamLoad}%) — سكّر البرامج اللي ما بتحتاجها (Ctrl+Shift+H)");
        else if (gpu >= 90)
            sb.Append("الكرت شغّال عالآخر — نصيحتي: نزّل الدقة أو الظلال أو Ray Tracing، أو فعّل DLSS / FSR على Quality أو Balanced");
        else if (core >= 85 || cpu >= 85)
            sb.Append("المعالج هو اللي ماسك الفريمات — نزّل إعدادات المعالج: مسافة الرؤية، كثافة الناس والزحمة، والفيزياء، وسكّر البرامج بالخلفية");
        else
            sb.Append("لا الكرت ولا المعالج مضغوطين — غالباً في حد للفريمات (V-Sync أو Frame Limit باللعبة)، أو اللعبة عم تحمّل");
        return sb.ToString();
    }

    // ---------- Hotkeys ----------

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HK_MODE:
                SetMode(_settings.Mode switch { DisplayMode.Auto => DisplayMode.Always, DisplayMode.Always => DisplayMode.Off, _ => DisplayMode.Auto });
                break;
            case HK_DETAILS:
                _settings.Details = !_settings.Details;
                _settings.Save();
                Flash(_settings.Details ? "تفاصيل أكتر: شغّالة" : "تفاصيل أكتر: مطفية");
                break;
            case HK_LAYOUT:
                _settings.Layout = _settings.Layout == "Horizontal" ? "Vertical" : "Horizontal";
                _settings.Save();
                _overlay?.ApplyLook(_settings);
                Flash(_settings.Layout == "Horizontal" ? "الشكل: عرضي" : "الشكل: طولي");
                break;
            case HK_GAME:
                ToggleCurrentGame();
                break;
            case HK_PANEL:
                OpenPanel();
                break;
        }
        _panel?.Reload();
        Tick();
        return IntPtr.Zero;
    }

    void SetMode(DisplayMode mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        Flash(mode switch
        {
            DisplayMode.Auto => "طريقة العرض: تلقائي (بالألعاب بس)",
            DisplayMode.Always => "طريقة العرض: دايماً ظاهر",
            _ => "طريقة العرض: مخفي",
        });
        _panel?.Reload();
    }

    void ToggleCurrentGame()
    {
        var fg = _fg;
        if (fg == null || string.IsNullOrEmpty(fg.ProcName) || fg.Pid == Environment.ProcessId) return;
        string key = fg.ProcName.ToLowerInvariant();
        if (fg.IsGame)
        {
            _settings.AlwaysShow.Remove(key);
            if (!_settings.NeverShow.Contains(key)) _settings.NeverShow.Add(key);
            Flash($"ما رح يظهر مع {fg.ProcName}");
        }
        else
        {
            _settings.NeverShow.Remove(key);
            if (!_settings.AlwaysShow.Contains(key)) _settings.AlwaysShow.Add(key);
            Flash($"{fg.ProcName} صار معتبر لعبة — رح يظهر معه دايماً");
        }
        _settings.Save();
    }

    void Flash(string text)
    {
        _status = text;
        _statusUntil = DateTime.UtcNow.AddSeconds(2.5);
    }

    // ---------- Control panel ----------

    void OpenPanel(string? page = null)
    {
        if (_panel == null || !_panel.IsLoaded)
        {
            _panel = new ControlPanel(this);
            _panel.Closed += (_, _) => { _panel = null; Tick(); };
            _panel.Show();
        }
        if (_panel.WindowState == WindowState.Minimized) _panel.WindowState = WindowState.Normal;
        if (page != null) _panel.ShowPage(page);
        _panel.Activate();
        Tick();
    }

    // ---------- Tray ----------

    void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "GameHUD",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip
            {
                RightToLeft = Forms.RightToLeft.Yes,
                ShowImageMargin = false,
                Renderer = new DarkMenuRenderer(),
                Font = new Drawing.Font("Segoe UI", 10f),
                Padding = new Forms.Padding(4, 6, 4, 6),
            },
        };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        BuildMenu(_tray.ContextMenuStrip);
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenPanel(); };
    }

    void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("فتح لوحة التحكم", () => OpenPanel(), bold: true));
        menu.Items.Add(Item("البرامج والذاكرة", () => OpenPanel("apps")));
        menu.Items.Add(Item("الشكل والألوان", () => OpenPanel("look")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("تلقائي (بالألعاب بس)", () => SetMode(DisplayMode.Auto), _settings.Mode == DisplayMode.Auto));
        menu.Items.Add(Item("دايماً ظاهر", () => SetMode(DisplayMode.Always), _settings.Mode == DisplayMode.Always));
        menu.Items.Add(Item("مخفي", () => SetMode(DisplayMode.Off), _settings.Mode == DisplayMode.Off));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("خروج", ExitApp));
    }

    Forms.ToolStripMenuItem Item(string text, Action onClick, bool isChecked = false, bool bold = false)
    {
        var it = new Forms.ToolStripMenuItem(text, null, (_, _) => { onClick(); Tick(); })
        {
            Checked = isChecked,
            Padding = new Forms.Padding(8, 5, 8, 5),
        };
        if (bold) it.Font = new Drawing.Font("Segoe UI Semibold", 10f);
        return it;
    }

    static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null) return new Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    // ---------- Startup with Windows (scheduled task: starts elevated without a UAC prompt) ----------

    public bool StartupEnabled() => RunSchtasks(out _, "/Query", "/TN", "GameHUD") == 0;

    public void SetStartup(bool on)
    {
        if (on)
        {
            string exe = Environment.ProcessPath ?? "";
            int rc = RunSchtasks(out _, "/Create", "/TN", "GameHUD", "/TR", $"\"{exe}\" --tray", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
            Flash(rc == 0 ? "رح يشتغل لحاله مع ويندوز" : "ما زبط تفعيل التشغيل مع ويندوز");
        }
        else
        {
            RunSchtasks(out _, "/Delete", "/TN", "GameHUD", "/F");
            Flash("ما رح يشتغل مع ويندوز");
        }
        Tick();
    }

    /// <summary>If the startup task points at an old copy of the exe, point it at this one.</summary>
    void RepairStartupTask()
    {
        if (RunSchtasks(out var xml, "/Query", "/TN", "GameHUD", "/XML") != 0) return;
        string exe = Environment.ProcessPath ?? "";
        if (!xml.Contains(exe, StringComparison.OrdinalIgnoreCase)) SetStartup(true);
    }

    static int RunSchtasks(out string output, params string[] args)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static void Log(Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(Settings.FilePath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }

    // ---------- Screenshots for checking the design (--shots <dir>) ----------

    void Screenshots(string dir)
    {
        _timer!.Stop();
        Directory.CreateDirectory(dir);
        var saved = _settings;
        _settings = new Settings { Mode = DisplayMode.Always, Details = true, FirstRun = false };
        int n = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (++n < 7) return;
            t.Stop();
            try
            {
                var hw = _hw!.Latest; var net = _net!.Latest;
                var fps = new FpsStats(54, 18.5, 41);
                var fake = new HwSnapshot();
                foreach (var f in typeof(HwSnapshot).GetFields()) f.SetValue(fake, f.GetValue(hw));
                fake.GpuLoad = 98;
                string adv = BuildFpsAdvice(54, fake);
                _overlay!.ApplyLook(_settings);
                _overlay.Render(hw, net, fps, "Cyberpunk 2077", _settings, adv, null);
                _overlay.Show();
                _overlay.SavePng(Path.Combine(dir, "hud-vertical.png"));
                _settings.Layout = "Horizontal"; _settings.Details = false;
                _settings.Colors = HudColors.Presets[1].Colors.Clone();
                _overlay.ApplyLook(_settings);
                _overlay.Render(hw, net, fps, "Cyberpunk 2077", _settings, null, null);
                _overlay.SavePng(Path.Combine(dir, "hud-horizontal.png"));
                _overlay.Hide();

                _settings = saved;
                var panel = new ControlPanel(this) { Left = 40, Top = 40 };
                panel.Show();
                foreach (var page in new[] { "apps", "look", "alerts", "general" })
                {
                    panel.ShowPage(page);
                    var until = DateTime.Now.AddSeconds(page == "apps" ? 8 : 1.5);
                    while (DateTime.Now < until) { Dispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(50); }
                    SaveWindowPng(panel, Path.Combine(dir, $"panel-{page}.png"));
                }
                panel.Close();
            }
            catch (Exception ex) { Log(ex); }
            ExitApp();
        };
        t.Start();
    }

    /// <summary>Copies the window's pixels from the screen (exactly what the user sees).</summary>
    static void SaveWindowPng(Window w, string path)
    {
        w.Topmost = true;
        w.UpdateLayout();
        var h = new WindowInteropHelper(w).Handle;
        Native.GetWindowRect(h, out var r);
        using var bmp = new Drawing.Bitmap(r.Width, r.Height);
        using (var g = Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Drawing.Size(r.Width, r.Height));
        bmp.Save(path, Drawing.Imaging.ImageFormat.Png);
    }

    // ---------- Exit ----------

    public void ExitApp()
    {
        _timer?.Stop();
        try { _panel?.Close(); } catch { }
        if (_overlay != null)
            foreach (var id in new[] { HK_MODE, HK_DETAILS, HK_GAME, HK_LAYOUT, HK_PANEL })
                Native.UnregisterHotKey(_overlay.Handle, id);
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _etw.Dispose();
        _net?.Dispose();
        _hw?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _etw.Dispose(); } catch { }
        base.OnExit(e);
    }
}

/// <summary>Dark, roomy tray menu that matches the control panel.</summary>
sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x1A, 0x1D, 0x23);
    static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2A, 0x2F, 0x3A);
    static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x2C, 0x31, 0x3B);
    static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xEC, 0xEE, 0xF2);
    static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x8B, 0x5C, 0xF6);

    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = true; }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var b = new Drawing.SolidBrush(Bg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var p = new Drawing.Pen(Line);
        e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var r = new Drawing.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = new Drawing.Drawing2D.GraphicsPath();
        int d = 10;
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var b = new Drawing.SolidBrush(Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Drawing.Color.FromArgb(0x8E, 0x97, 0xA6);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var b = new Drawing.SolidBrush(Accent);
        e.Graphics.FillEllipse(b, r.X + r.Width / 2 - 4, r.Y + r.Height / 2 - 4, 8, 8);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Drawing.Pen(Line);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Drawing.Color.Transparent;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
    }
}
