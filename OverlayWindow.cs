using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameHUD;

/// <summary>Transparent, click-through, always-on-top HUD. Never takes focus from the game.</summary>
public sealed class OverlayWindow : Window
{
    public static Brush B(string hex)
    {
        try { var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!; b.Freeze(); return b; }
        catch { return Brushes.White; }
    }

    static readonly Brush Warm = B("#FFC857"), Hot = B("#FF5C5C"), Good = B("#7CE38B");
    static readonly FontFamily LabelFont = new("Bahnschrift SemiBold, Segoe UI Semibold");
    static readonly FontFamily ValueFont = new("Segoe UI Semibold");
    static readonly FontFamily TextFont = new("Segoe UI");

    sealed class Cell
    {
        public readonly TextBlock Tb;
        readonly Run _prefix, _value, _unit;
        readonly Brush _valueBrush;
        public Cell(Brush value, Brush unit, bool horizontal)
        {
            _valueBrush = value;
            _prefix = new Run { Foreground = unit, FontSize = 11 };
            _value = new Run { Foreground = value, FontSize = 14 };
            _unit = new Run { Foreground = unit, FontSize = 10 };
            Tb = new TextBlock
            {
                FontFamily = ValueFont, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(horizontal ? 7 : 10, 0, 0, 0),
            };
            Typography.SetNumeralAlignment(Tb, FontNumeralAlignment.Tabular);
            Tb.Inlines.Add(_prefix); Tb.Inlines.Add(_value); Tb.Inlines.Add(_unit);
        }
        public void Set(string? value, string unit = "", Brush? color = null, string prefix = "")
        {
            _prefix.Text = prefix;
            _value.Text = value ?? "—";
            _unit.Text = value == null ? "" : unit;
            _value.Foreground = color ?? _valueBrush;
            Tb.Visibility = Visibility.Visible;
        }
        public void Clear() { Set("", ""); Tb.Visibility = Visibility.Collapsed; }
    }

    sealed class Row
    {
        public readonly Grid Grid = new() { Margin = new Thickness(0, 1, 0, 1) };
        public readonly TextBlock Name;
        public readonly Cell[] Cells;
        public Row(string label, Brush labelBrush, HudBrushes c, bool horizontal)
        {
            Cells = new[] { new Cell(c.Value, c.Unit, horizontal), new Cell(c.Value, c.Unit, horizontal), new Cell(c.Value, c.Unit, horizontal), new Cell(c.Value, c.Unit, horizontal) };
            string[] groups = { "L", "N", "V1", "V2", "V3", "V4" };
            foreach (var g in groups)
                Grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = GridLength.Auto,
                    SharedSizeGroup = horizontal ? null : g,
                    MinWidth = !horizontal && g.StartsWith('V') ? 44 : 0,
                });
            var lbl = new TextBlock { Text = label, Foreground = labelBrush, FontFamily = LabelFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, horizontal ? 2 : 8, 0) };
            Name = new TextBlock
            {
                Foreground = c.Name, FontFamily = TextFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 130,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 4, 0),
                Visibility = horizontal ? Visibility.Collapsed : Visibility.Visible,
            };
            Grid.Children.Add(lbl);
            Grid.SetColumn(Name, 1); Grid.Children.Add(Name);
            for (int i = 0; i < 4; i++) { Grid.SetColumn(Cells[i].Tb, i + 2); Grid.Children.Add(Cells[i].Tb); }
        }
        public bool Visible { set => Grid.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    sealed record HudBrushes(Brush Value, Brush Unit, Brush Name);

    readonly Border _root;
    Row _gpu = null!, _cpu = null!, _fps = null!, _net = null!, _vram = null!, _ram = null!;
    Border _warn = null!;
    TextBlock _warnText = null!, _status = null!;
    bool _horizontal;
    IntPtr _hwnd;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "GameHUD";

        _root = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 7),
            BorderBrush = B("#22FFFFFF"),
            BorderThickness = new Thickness(1),
        };
        Content = _root;
        ApplyLook(new Settings());
    }

    public IntPtr Handle => _hwnd != IntPtr.Zero ? _hwnd : (_hwnd = new WindowInteropHelper(this).EnsureHandle());

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = Handle;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Rebuilds the HUD for the current colors / layout / size.</summary>
    public void ApplyLook(Settings s)
    {
        var col = s.Colors;
        var bg = (Color)ColorConverter.ConvertFromString(SafeHex(col.Background, "#0E1014"));
        _root.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(s.BackgroundOpacity, 0, 1) * 255), bg.R, bg.G, bg.B));
        _root.LayoutTransform = new ScaleTransform(s.Scale, s.Scale);
        _horizontal = s.Layout == "Horizontal";

        var c = new HudBrushes(B(col.Value), B(col.Unit), B(col.Name));
        _fps = new Row("FPS", B(col.Fps), c, _horizontal);
        _gpu = new Row("GPU", B(col.Gpu), c, _horizontal);
        _cpu = new Row("CPU", B(col.Cpu), c, _horizontal);
        _vram = new Row("VRAM", B(col.Vram), c, _horizontal);
        _ram = new Row("RAM", B(col.Ram), c, _horizontal);
        _net = new Row("NET", B(col.Net), c, _horizontal);

        var rows = new StackPanel { Orientation = _horizontal ? Orientation.Horizontal : Orientation.Vertical };
        Grid.SetIsSharedSizeScope(rows, !_horizontal);
        var all = new[] { _fps, _gpu, _cpu, _vram, _ram, _net };
        for (int i = 0; i < all.Length; i++)
        {
            if (_horizontal) all[i].Grid.Margin = new Thickness(i == 0 ? 0 : 14, 0, 0, 0);
            rows.Children.Add(all[i].Grid);
        }

        _warnText = new TextBlock
        {
            Foreground = Brushes.White, FontFamily = TextFont, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.RightToLeft, TextAlignment = TextAlignment.Left, LineHeight = 18,
        };
        _warn = new Border
        {
            Background = B("#B3401818"), BorderBrush = B("#FF5C5C"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 5), Margin = new Thickness(0, 6, 0, 0),
            Child = _warnText, Visibility = Visibility.Collapsed, MaxWidth = 420, HorizontalAlignment = _horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
        };
        _status = new TextBlock
        {
            Foreground = Warm, FontFamily = TextFont, FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
            FlowDirection = FlowDirection.RightToLeft, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left,
        };

        var content = new StackPanel();
        content.Children.Add(rows);
        content.Children.Add(_warn);
        content.Children.Add(_status);
        _root.Child = content;
        _root.Padding = _horizontal ? new Thickness(10, 4, 10, 5) : new Thickness(10, 6, 10, 7);
    }

    static string SafeHex(string hex, string fallback)
    {
        try { ColorConverter.ConvertFromString(hex); return hex; } catch { return fallback; }
    }

    public void Render(HwSnapshot hw, NetSnapshot net, FpsStats fps, string gameTitle, Settings s, string? warning, string? status)
    {
        _fps.Visible = s.ShowFps;
        _gpu.Visible = s.ShowGpu;
        _cpu.Visible = s.ShowCpu;
        _net.Visible = s.ShowNet;
        _vram.Visible = s.Details;
        _ram.Visible = s.Details;

        // FPS
        _fps.Name.Text = Shorten(gameTitle, 22);
        _fps.Cells[0].Set(fps.Fps?.ToString("0"), " fps", fps.Fps is double f && s.FpsWarn > 0 && f < s.FpsWarn ? Warm : null);
        _fps.Cells[1].Set(fps.FrameTimeMs?.ToString("0.0"), " ms");
        if (fps.Low1 is double low) _fps.Cells[2].Set(low.ToString("0"), " 1%low"); else _fps.Cells[2].Clear();
        _fps.Cells[3].Clear();

        // GPU
        _gpu.Name.Text = hw.GpuName;
        _gpu.Cells[0].Set(hw.GpuTemp?.ToString("0"), "°C", TempColor(hw.GpuTemp, 75, 84));
        _gpu.Cells[1].Set(hw.GpuLoad?.ToString("0"), "%");
        _gpu.Cells[2].Set(hw.GpuPower?.ToString("0"), "W");
        if (_horizontal) _gpu.Cells[3].Clear(); else _gpu.Cells[3].Set(hw.GpuClock?.ToString("0"), " MHz");

        // CPU
        _cpu.Name.Text = hw.CpuName;
        _cpu.Cells[0].Set(hw.CpuTemp?.ToString("0"), "°C", TempColor(hw.CpuTemp, 80, 90));
        _cpu.Cells[1].Set(hw.CpuLoad?.ToString("0"), "%");
        _cpu.Cells[2].Set(hw.CpuPower?.ToString("0"), "W");
        if (_horizontal) _cpu.Cells[3].Clear(); else _cpu.Cells[3].Set(hw.CpuClock is float c ? (c / 1000).ToString("0.00") : null, " GHz");

        // Details
        if (s.Details)
        {
            _vram.Cells[0].Set(hw.VramUsedMB is float u && hw.VramTotalMB is float t ? $"{u / 1024:0.0}/{t / 1024:0}" : null, " GB",
                hw.VramUsedMB is float u2 && hw.VramTotalMB is float t2 && t2 > 0 && u2 / t2 > 0.95 ? Warm : null);
            if (hw.GpuHotspot is float hs && !_horizontal) _vram.Cells[1].Set(hs.ToString("0"), "°C hot", TempColor(hs, 90, 100));
            else _vram.Cells[1].Clear(); // RTX 50 cards don't report a hot spot
            if (_horizontal) _vram.Cells[2].Clear();
            else if (hw.GpuFanRpm is float rpm) _vram.Cells[2].Set(rpm.ToString("0"), " rpm");
            else _vram.Cells[2].Set(hw.GpuFanPct?.ToString("0"), "% fan");
            if (_horizontal) _vram.Cells[3].Clear(); else _vram.Cells[3].Set(hw.GpuMemClock?.ToString("0"), " MHz");

            _ram.Cells[0].Set($"{hw.RamUsedGB:0.0}/{hw.RamTotalGB:0}", " GB");
            _ram.Cells[1].Set(hw.RamLoad.ToString(), "%", hw.RamLoad >= 90 ? Hot : hw.RamLoad >= 80 ? Warm : null);
            _ram.Cells[2].Clear();
            _ram.Cells[3].Clear();
        }

        // Network
        _net.Name.Text = (net.Wifi ? "Wi-Fi" : "Ethernet") + (net.Vpn != null ? " · VPN" : "");
        var (dv, du) = Rate(net.DownBps);
        var (uv, uu) = Rate(net.UpBps);
        _net.Cells[0].Set(dv, du, prefix: "↓ ");
        _net.Cells[1].Set(uv, uu, prefix: "↑ ");
        if (net.PingMs is double p) _net.Cells[2].Set(p.ToString("0"), " ms", PingColor(p, s.PingWarnMs));
        else _net.Cells[2].Set(string.IsNullOrEmpty(net.PingHost) ? null : "✕", " ping", Hot);
        if (net.LossPct >= 1) _net.Cells[3].Set(net.LossPct.ToString("0"), "% loss", net.LossPct >= 10 ? Hot : Warm);
        else if (_horizontal) _net.Cells[3].Clear();
        else _net.Cells[3].Set(net.Jitter >= 1 ? net.Jitter.ToString("0") : "0", " jit");

        _warn.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        if (warning != null) _warnText.Text = warning;
        _status.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
        if (status != null) _status.Text = status;
    }

    /// <summary>Puts the HUD in a corner of the given monitor (physical pixels) and keeps it above the game.</summary>
    public void PlaceOn(Native.RECT mon, string corner)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int m = (int)(14 * dpi.DpiScaleX);
        int x = corner switch
        {
            "TopCenter" or "BottomCenter" => mon.Left + (mon.Width - w) / 2,
            _ when corner.EndsWith("Left") => mon.Left + m,
            _ => mon.Right - w - m,
        };
        int y = corner.StartsWith("Top") ? mon.Top + m : mon.Bottom - h - m;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void SavePng(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX) + 2, h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY) + 2;
        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x55, 0x6B)), null, new Rect(0, 0, ActualWidth + 2, ActualHeight + 2));
            dc.DrawRectangle(new VisualBrush(_root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    static Brush? TempColor(float? t, float warm, float hot) => t is float v ? (v >= hot ? Hot : v >= warm ? Warm : null) : null;
    static Brush PingColor(double p, int warn) => p >= warn ? Hot : p >= Math.Min(100, warn / 2.0) ? Warm : Good;

    public static (string value, string unit) Rate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return ((bytesPerSec / 1048576).ToString(bytesPerSec >= 100 * 1048576 ? "0" : "0.0"), " MB/s");
        return ((bytesPerSec / 1024).ToString("0"), " KB/s");
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
