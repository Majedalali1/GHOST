using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GameHUD;

public interface IHudHost
{
    Settings Settings { get; }
    HwSnapshot Hw { get; }
    bool PawnIoInstalled { get; }
    /// <summary>Saves settings and refreshes the HUD; rebuildLook = colors/layout/size changed.</summary>
    void SettingsChanged(bool rebuildLook);
    bool StartupEnabled();
    void SetStartup(bool on);
    void ExitApp();
}

public partial class ControlPanel : Window
{
    readonly IHudHost _host;
    Settings S => _host.Settings;
    bool _loading;
    readonly DispatcherTimer _appsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _refreshing;
    List<AppUsage> _apps = new();
    readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _confirm = new(StringComparer.OrdinalIgnoreCase);
    DateTime _confirmAll;
    Action<string>? _colorTarget;

    static readonly string[] Swatches =
    {
        "#FFFFFF", "#E5E7EB", "#9CA3AF", "#6B7280", "#374151", "#000000",
        "#F43F5E", "#FF4D4D", "#FB923C", "#FFB74D", "#FACC15", "#FFE08A",
        "#A3E635", "#76E05A", "#34D399", "#39FF14", "#2DD4BF", "#22D3EE",
        "#4FC3F7", "#60A5FA", "#818CF8", "#8B5CF6", "#A78BFA", "#CE93D8",
        "#F472B6", "#E879F9", "#0E1014", "#0B0620", "#120A0A", "#0A1A12",
    };

    public ControlPanel(IHudHost host)
    {
        _host = host;
        _loading = true; // sliders fire ValueChanged while the XAML loads
        InitializeComponent();
        try
        {
            var logo = new BitmapImage(new Uri("pack://application:,,,/Assets/icon-256.png"));
            LogoImage.Source = logo;
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch { }

        BuildStaticParts();
        LoadValues();
        NavApps.IsChecked = true;

        _appsTimer.Tick += (_, _) => RefreshApps();
        _appsTimer.Start();
        Loaded += (_, _) => RefreshApps();
        Closed += (_, _) => _appsTimer.Stop();
    }

    // ---------- Window chrome: dark title bar that blends with the window ----------

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int on = 1, caption = 0x00151110 /* #101115 as BGR */, round = 2;
        DwmSetWindowAttribute(h, 20, ref on, 4);       // immersive dark mode
        DwmSetWindowAttribute(h, 35, ref caption, 4);  // caption color
        DwmSetWindowAttribute(h, 33, ref round, 4);    // rounded corners
    }

    // ---------- Navigation ----------

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageApps == null) return;
        PageApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageLook.Visibility = NavLook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAlerts.Visibility = NavAlerts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavGeneral.IsChecked == true) BuildGamesList();
        if (NavApps.IsChecked == true) RefreshApps();
    }

    public void ShowPage(string page)
    {
        (page switch { "look" => NavLook, "alerts" => NavAlerts, "general" => NavGeneral, _ => NavApps }).IsChecked = true;
    }

    // ---------- Build ----------

    void BuildStaticParts()
    {
        var hw = _host.Hw;
        HwText.Text = $"{hw.GpuName}\n{hw.CpuName}";
        PawnIoCard.Visibility = _host.PawnIoInstalled ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (label, val) in new[] { ("فوق يمين", "TopRight"), ("فوق بالنص", "TopCenter"), ("فوق يسار", "TopLeft"), ("تحت يمين", "BottomRight"), ("تحت بالنص", "BottomCenter"), ("تحت يسار", "BottomLeft") })
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Seg"), GroupName = "corner", Content = label, Tag = val, Margin = new Thickness(2),
                FlowDirection = FlowDirection.RightToLeft,
            };
            rb.Checked += (_, _) => { if (_loading) return; S.Corner = val; _host.SettingsChanged(false); };
            CornerGrid.Children.Add(new Border { Background = (Brush)FindResource("Card2"), CornerRadius = new CornerRadius(8), Margin = new Thickness(2), Child = rb });
        }

        foreach (var (name, colors) in HudColors.Presets)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var hex in new[] { colors.Fps, colors.Gpu, colors.Cpu, colors.Net })
                dots.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = OverlayWindow.B(hex), Margin = new Thickness(1.5, 0, 1.5, 0) });
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border { Background = OverlayWindow.B(colors.Background), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 5, 6, 5), Child = dots, Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("Btn"), Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8, 6, 12, 6) };
            b.Click += (_, _) => { S.Colors = colors.Clone(); _host.SettingsChanged(true); BuildColorGrid(); };
            PresetPanel.Children.Add(b);
        }

        foreach (var hex in Swatches)
        {
            var sw = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(4), Cursor = Cursors.Hand, ToolTip = hex,
                Template = SwatchTemplate(hex),
            };
            sw.Click += (_, _) => ApplyColor(hex);
            SwatchPanel.Children.Add(sw);
        }

        BuildSegs(FpsWarnPanel, "fpswarn", new[] { 0, 30, 45, 50, 60, 90, 120, 144 }, v => v == 0 ? "مطفي" : v.ToString(),
            () => S.FpsWarn, v => { S.FpsWarn = v; _host.SettingsChanged(false); });
        BuildSegs(PingWarnPanel, "pingwarn", new[] { 100, 150, 200, 300 }, v => $"{v} ms",
            () => S.PingWarnMs, v => { S.PingWarnMs = v; _host.SettingsChanged(false); });

        foreach (var (label, host) in new[] { ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9") })
        {
            var b = new Button { Style = (Style)FindResource("Btn"), Content = $"{label} · {host}", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5) };
            b.Click += (_, _) => { PingHostBox.Text = host; SaveHost(); };
            HostPresetPanel.Children.Add(b);
        }

        foreach (var (keys, desc) in new[]
        {
            ("Ctrl + Shift + O", "تبديل: تلقائي / دايماً / مخفي"),
            ("Ctrl + Shift + D", "تفاصيل أكتر"),
            ("Ctrl + Shift + L", "تبديل الشكل: طولي / عرضي"),
            ("Ctrl + Shift + G", "اعتبر التطبيق الحالي لعبة / لا تعتبره"),
            ("Ctrl + Shift + H", "فتح هالنافذة"),
        })
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var chip = new Border { Background = (Brush)FindResource("Card2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Width = 150, Margin = new Thickness(0, 0, 12, 0) };
            chip.Child = new TextBlock { Text = keys, FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(chip);
            row.Children.Add(new TextBlock { Text = desc, VerticalAlignment = VerticalAlignment.Center });
            HotkeyList.Children.Add(row);
        }
    }

    void BuildSegs(Panel panel, string group, int[] values, Func<int, string> label, Func<int> get, Action<int> set)
    {
        foreach (var v in values)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Seg"), GroupName = group, Content = label(v), Tag = v };
            rb.Checked += (_, _) => { if (!_loading) set(v); };
            panel.Children.Add(rb);
        }
    }

    static ControlTemplate SwatchTemplate(string hex)
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, OverlayWindow.B(hex));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.BorderBrushProperty, OverlayWindow.B("#40FFFFFF"));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        t.VisualTree = bd;
        return t;
    }

    void BuildColorGrid()
    {
        ColorGrid.Children.Clear();
        var c = S.Colors;
        var items = new (string Label, Func<string> Get, Action<string> Set)[]
        {
            ("الأرقام", () => c.Value, v => c.Value = v),
            ("الوحدات (°C، %، W)", () => c.Unit, v => c.Unit = v),
            ("أسماء القطع", () => c.Name, v => c.Name = v),
            ("الخلفية", () => c.Background, v => c.Background = v),
            ("FPS", () => c.Fps, v => c.Fps = v),
            ("GPU", () => c.Gpu, v => c.Gpu = v),
            ("CPU", () => c.Cpu, v => c.Cpu = v),
            ("NET", () => c.Net, v => c.Net = v),
            ("VRAM", () => c.Vram, v => c.Vram = v),
            ("RAM", () => c.Ram, v => c.Ram = v),
        };
        foreach (var it in items)
        {
            var sw = new Button { Width = 28, Height = 28, Cursor = Cursors.Hand, Template = SwatchTemplate(it.Get()), Margin = new Thickness(0, 0, 10, 0) };
            var hexText = new TextBlock { Text = it.Get().ToUpperInvariant(), Style = (Style)FindResource("DimText"), FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Margin = new Thickness(0, 4, 24, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.Children.Add(sw);
            DockPanel.SetDock(hexText, Dock.Right);
            row.Children.Add(hexText);
            row.Children.Add(new TextBlock { Text = it.Label, VerticalAlignment = VerticalAlignment.Center });
            var set = it.Set;
            void Open()
            {
                _colorTarget = v => { set(v); _host.SettingsChanged(true); BuildColorGrid(); };
                HexBox.Text = it.Get().ToUpperInvariant();
                ColorPopup.PlacementTarget = sw;
                ColorPopup.IsOpen = true;
            }
            sw.Click += (_, _) => Open();
            row.MouseLeftButtonUp += (_, _) => Open();
            ColorGrid.Children.Add(row);
        }
    }

    void ApplyColor(string hex)
    {
        _colorTarget?.Invoke(hex);
        ColorPopup.IsOpen = false;
    }

    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hex = NormalizeHex(HexBox.Text);
        HexPreview.Background = hex == null ? Brushes.Transparent : OverlayWindow.B(hex);
    }

    void HexBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) HexApply_Click(sender, e); }

    void HexApply_Click(object sender, RoutedEventArgs e)
    {
        var hex = NormalizeHex(HexBox.Text);
        if (hex != null) ApplyColor(hex);
    }

    static string? NormalizeHex(string s)
    {
        s = s.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length != 7) return null;
        return int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out _) ? s.ToUpperInvariant() : null;
    }

    void LoadValues()
    {
        _loading = true;
        LayoutVertical.IsChecked = S.Layout != "Horizontal";
        LayoutHorizontal.IsChecked = S.Layout == "Horizontal";
        foreach (Border b in CornerGrid.Children)
            if (b.Child is RadioButton rb) rb.IsChecked = (string)rb.Tag == S.Corner;
        ScaleSlider.Value = S.Scale;
        OpacitySlider.Value = S.BackgroundOpacity;
        ScaleText.Text = $"{S.Scale * 100:0}%";
        OpacityText.Text = $"{S.BackgroundOpacity * 100:0}%";
        ShowFps.IsChecked = S.ShowFps;
        ShowGpu.IsChecked = S.ShowGpu;
        ShowCpu.IsChecked = S.ShowCpu;
        ShowNet.IsChecked = S.ShowNet;
        ShowDetails.IsChecked = S.Details;
        foreach (RadioButton rb in FpsWarnPanel.Children) rb.IsChecked = (int)rb.Tag == S.FpsWarn;
        foreach (RadioButton rb in PingWarnPanel.Children) rb.IsChecked = (int)rb.Tag == S.PingWarnMs;
        PingHostBox.Text = S.PingHost;
        ModeAuto.IsChecked = S.Mode == DisplayMode.Auto;
        ModeAlways.IsChecked = S.Mode == DisplayMode.Always;
        ModeOff.IsChecked = S.Mode == DisplayMode.Off;
        StartupSwitch.IsChecked = _host.StartupEnabled();
        BuildColorGrid();
        _loading = false;
    }

    /// <summary>Called by the app after a hotkey changed something.</summary>
    public void Reload() => LoadValues();

    // ---------- Look ----------

    void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Layout = LayoutHorizontal.IsChecked == true ? "Horizontal" : "Vertical";
        _host.SettingsChanged(true);
    }

    void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleText == null) return;
        ScaleText.Text = $"{e.NewValue * 100:0}%";
        if (_loading) return;
        S.Scale = Math.Round(e.NewValue, 2);
        _host.SettingsChanged(true);
    }

    void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText == null) return;
        OpacityText.Text = $"{e.NewValue * 100:0}%";
        if (_loading) return;
        S.BackgroundOpacity = Math.Round(e.NewValue, 2);
        _host.SettingsChanged(true);
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        S.ShowFps = ShowFps.IsChecked == true;
        S.ShowGpu = ShowGpu.IsChecked == true;
        S.ShowCpu = ShowCpu.IsChecked == true;
        S.ShowNet = ShowNet.IsChecked == true;
        S.Details = ShowDetails.IsChecked == true;
        _host.SettingsChanged(false);
    }

    // ---------- Alerts ----------

    void PingHost_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SaveHost(); }
    void SaveHost_Click(object sender, RoutedEventArgs e) => SaveHost();

    void SaveHost()
    {
        var h = PingHostBox.Text.Trim();
        if (h.Length == 0 || h.Contains(' ')) return;
        S.PingHost = h;
        _host.SettingsChanged(false);
    }

    // ---------- General ----------

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Mode = ModeAlways.IsChecked == true ? DisplayMode.Always : ModeOff.IsChecked == true ? DisplayMode.Off : DisplayMode.Auto;
        _host.SettingsChanged(false);
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        _host.SetStartup(StartupSwitch.IsChecked == true);
        StartupSwitch.IsChecked = _host.StartupEnabled();
    }

    void BuildGamesList()
    {
        GamesList.Children.Clear();
        var entries = S.AlwaysShow.Select(g => (g, true)).Concat(S.NeverShow.Select(g => (g, false))).ToList();
        if (entries.Count == 0)
        {
            GamesList.Children.Add(new TextBlock { Text = "ما في شي مضاف يدوياً.", Style = (Style)FindResource("DimText") });
            return;
        }
        foreach (var (name, always) in entries)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var remove = new Button { Style = (Style)FindResource("DangerBtn"), Content = "شيل", Padding = new Thickness(10, 3, 10, 3) };
            remove.Click += (_, _) =>
            {
                if (always) S.AlwaysShow.Remove(name); else S.NeverShow.Remove(name);
                _host.SettingsChanged(false);
                BuildGamesList();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var tag = new Border
            {
                Background = always ? OverlayWindow.B("#2234D399") : OverlayWindow.B("#22F43F5E"), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = always ? "يظهر دايماً" : "ما يظهر أبداً", FontSize = 11, Foreground = always ? OverlayWindow.B("#34D399") : OverlayWindow.B("#FF8FA3") },
            };
            row.Children.Add(tag);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
            GamesList.Children.Add(row);
        }
    }

    void PawnIo_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); } catch { }
    }

    void Exit_Click(object sender, RoutedEventArgs e) => _host.ExitApp();

    // ---------- Apps (RAM / VRAM) ----------

    void Sort_Checked(object sender, RoutedEventArgs e) { if (AppList != null) RenderApps(); }

    async void RefreshApps()
    {
        if (_refreshing || !IsVisible || PageApps.Visibility != Visibility.Visible) return;
        _refreshing = true;
        try
        {
            var apps = await Task.Run(ProcessMonitor.Snapshot);
            _apps = apps;
            RenderApps();
        }
        catch { }
        finally { _refreshing = false; }
    }

    void RenderApps()
    {
        var hw = _host.Hw;
        RamText.Text = $"{hw.RamUsedGB:0.0} / {hw.RamTotalGB:0} GB";
        RamBar.Value = hw.RamLoad;
        if (hw.VramUsedMB is float vu && hw.VramTotalMB is float vt && vt > 0)
        {
            VramText.Text = $"{vu / 1024:0.0} / {vt / 1024:0} GB";
            VramBar.Value = vu / vt * 100;
        }

        bool byVram = SortVram.IsChecked == true;
        var list = (byVram ? _apps.OrderByDescending(a => a.VramBytes).ThenByDescending(a => a.RamBytes) : _apps.OrderByDescending(a => a.RamBytes))
            .Take(25).ToList();
        long max = Math.Max(1, list.Count == 0 ? 1 : byVram ? list.Max(a => a.VramBytes) : list.Max(a => a.RamBytes));
        _selected.RemoveWhere(k => !_apps.Any(a => a.Key.Equals(k, StringComparison.OrdinalIgnoreCase)));

        AppList.Children.Clear();
        AppsLoading.Visibility = Visibility.Collapsed;
        foreach (var a in list) AppList.Children.Add(BuildAppRow(a, max, byVram));
        UpdateBoostButton();
    }

    UIElement BuildAppRow(AppUsage a, long max, bool byVram)
    {
        var g = new Grid { Margin = new Thickness(18, 0, 18, 0), MinHeight = 46 };
        foreach (var w in new[] { 30.0, -1, 90, 90, 86 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

        var tick = new CheckBox { Style = (Style)FindResource("Tick"), IsChecked = _selected.Contains(a.Key), VerticalAlignment = VerticalAlignment.Center };
        tick.Click += (_, _) =>
        {
            if (tick.IsChecked == true) _selected.Add(a.Key); else _selected.Remove(a.Key);
            UpdateBoostButton();
        };
        g.Children.Add(tick);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(a.Icon != null
            ? new Image { Source = a.Icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) }
            : new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = (Brush)FindResource("Card2"), Margin = new Thickness(0, 0, 10, 0) });
        var nameStack = new StackPanel();
        var title = new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
        if (a.Pids.Count > 1) title.Text += $"  ×{a.Pids.Count}";
        nameStack.Children.Add(title);
        double share = (double)(byVram ? a.VramBytes : a.RamBytes) / max;
        nameStack.Children.Add(new Border
        {
            Height = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
            Width = Math.Max(4, 220 * share), Background = (Brush)FindResource(byVram ? "Accent" : "Accent2"), Opacity = 0.8,
        });
        nameRow.Children.Add(nameStack);
        Grid.SetColumn(nameRow, 1);
        g.Children.Add(nameRow);

        var ram = new TextBlock { Text = Gb(a.RamBytes), VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, FontWeight = byVram ? FontWeights.Normal : FontWeights.SemiBold };
        Grid.SetColumn(ram, 2); g.Children.Add(ram);
        var vram = new TextBlock { Text = a.VramBytes > 0 ? Gb(a.VramBytes) : "—", VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, FontWeight = byVram ? FontWeights.SemiBold : FontWeights.Normal, Foreground = a.VramBytes > 0 ? (Brush)FindResource("Text") : (Brush)FindResource("Dim") };
        Grid.SetColumn(vram, 3); g.Children.Add(vram);

        bool confirming = _confirm.TryGetValue(a.Key, out var until) && until > DateTime.UtcNow;
        var close = new Button
        {
            Style = (Style)FindResource("DangerBtn"), Content = confirming ? "متأكد؟" : "سكّر", VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4),
            ToolTip = "بيسكّر كل نسخ البرنامج. إذا في شغل مش محفوظ رح يروح.",
        };
        if (confirming) { close.Background = (Brush)FindResource("Danger"); close.Foreground = Brushes.White; }
        close.Click += async (_, _) =>
        {
            if (!(_confirm.TryGetValue(a.Key, out var u) && u > DateTime.UtcNow))
            {
                _confirm[a.Key] = DateTime.UtcNow.AddSeconds(3);
                close.Content = "متأكد؟";
                close.Background = (Brush)FindResource("Danger");
                close.Foreground = Brushes.White;
                return;
            }
            _confirm.Remove(a.Key);
            close.IsEnabled = false;
            close.Content = "عم يسكّر…";
            await ProcessMonitor.CloseAsync(a.Pids);
            _selected.Remove(a.Key);
            RefreshApps();
        };
        Grid.SetColumn(close, 4); g.Children.Add(close);

        var row = new Border { Child = g, Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Margin = new Thickness(6, 0, 6, 0) };
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("Card2");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    void UpdateBoostButton()
    {
        var sel = _apps.Where(a => _selected.Contains(a.Key)).ToList();
        CloseSelectedButton.IsEnabled = sel.Count > 0;
        if (sel.Count == 0)
        {
            CloseSelectedText.Text = "سكّر المحدد";
            BoostHint.Text = "علّم البرامج اللي بدك تسكّرها وبعدين اضغط الزر.";
            return;
        }
        long ram = sel.Sum(a => a.RamBytes), vram = sel.Sum(a => a.VramBytes);
        BoostHint.Text = $"رح توفّر تقريباً {Gb(ram)} رام" + (vram > 0 ? $" و{Gb(vram)} VRAM" : "");
        CloseSelectedText.Text = _confirmAll > DateTime.UtcNow ? $"متأكد؟ رح يتسكّر {sel.Count}" : $"سكّر المحدد ({sel.Count})";
    }

    async void CloseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmAll <= DateTime.UtcNow)
        {
            _confirmAll = DateTime.UtcNow.AddSeconds(4);
            UpdateBoostButton();
            return;
        }
        _confirmAll = default;
        var pids = _apps.Where(a => _selected.Contains(a.Key)).SelectMany(a => a.Pids).ToList();
        CloseSelectedButton.IsEnabled = false;
        CloseSelectedText.Text = "عم يسكّر…";
        await ProcessMonitor.CloseAsync(pids);
        _selected.Clear();
        RefreshApps();
    }

    static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes / 1048576.0:0} MB";
}
