using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GameHUD;

public enum DisplayMode { Auto, Always, Off }

public sealed class Settings
{
    public DisplayMode Mode { get; set; } = DisplayMode.Auto;
    public bool Details { get; set; }
    public string Corner { get; set; } = "TopLeft";
    public double Scale { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.55;
    public string PingHost { get; set; } = "1.1.1.1";
    public int PingWarnMs { get; set; } = 200;
    /// <summary>Warn when FPS stays under this (0 = off).</summary>
    public int FpsWarn { get; set; } = 60;
    /// <summary>Process names (no .exe, lower case) that always count as a game, e.g. windowed games.</summary>
    public List<string> AlwaysShow { get; set; } = new();
    /// <summary>Process names that never count as a game, even fullscreen.</summary>
    public List<string> NeverShow { get; set; } = new();
    public bool FirstRun { get; set; } = true;

    /// <summary>"Vertical" (rows) or "Horizontal" (one bar).</summary>
    public string Layout { get; set; } = "Vertical";
    public bool ShowFps { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowNet { get; set; } = true;
    public HudColors Colors { get; set; } = new();

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHUD");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}

public sealed class HudColors
{
    public string Value { get; set; } = "#F2F4F8";
    public string Unit { get; set; } = "#8A93A3";
    public string Name { get; set; } = "#9AA3B2";
    public string Background { get; set; } = "#0E1014";
    public string Fps { get; set; } = "#FFB74D";
    public string Gpu { get; set; } = "#76E05A";
    public string Cpu { get; set; } = "#4FC3F7";
    public string Net { get; set; } = "#CE93D8";
    public string Vram { get; set; } = "#76E05A";
    public string Ram { get; set; } = "#4FC3F7";

    public HudColors Clone() => (HudColors)MemberwiseClone();

    public static readonly (string Name, HudColors Colors)[] Presets =
    {
        ("كلاسيك", new HudColors()),
        ("نيون", new HudColors { Value = "#FFFFFF", Unit = "#7DD3FC", Name = "#A5B4FC", Background = "#0B0620",
            Fps = "#F472B6", Gpu = "#22D3EE", Cpu = "#A78BFA", Net = "#34D399", Vram = "#22D3EE", Ram = "#A78BFA" }),
        ("أحمر غيمنغ", new HudColors { Value = "#FFFFFF", Unit = "#A1A1AA", Name = "#D4D4D8", Background = "#120A0A",
            Fps = "#FF4D4D", Gpu = "#FF4D4D", Cpu = "#FF8A4D", Net = "#FFC24D", Vram = "#FF4D4D", Ram = "#FF8A4D" }),
        ("أبيض بسيط", new HudColors { Value = "#FFFFFF", Unit = "#9CA3AF", Name = "#D1D5DB", Background = "#000000",
            Fps = "#FFFFFF", Gpu = "#FFFFFF", Cpu = "#FFFFFF", Net = "#FFFFFF", Vram = "#FFFFFF", Ram = "#FFFFFF" }),
        ("ماتريكس", new HudColors { Value = "#B6FFB0", Unit = "#3F8F3A", Name = "#6FCF68", Background = "#020A02",
            Fps = "#39FF14", Gpu = "#39FF14", Cpu = "#39FF14", Net = "#39FF14", Vram = "#39FF14", Ram = "#39FF14" }),
        ("ذهبي", new HudColors { Value = "#FFF7E0", Unit = "#B59A5B", Name = "#D9C08A", Background = "#100C04",
            Fps = "#FFC94D", Gpu = "#FFC94D", Cpu = "#FFE08A", Net = "#FFB020", Vram = "#FFC94D", Ram = "#FFE08A" }),
    };
}

/// <summary>Cached pid → process name / friendly description lookups.</summary>
public static class ProcessNames
{
    sealed record Entry(string Name, string Friendly, DateTime Seen);
    static readonly Dictionary<int, Entry> Cache = new();
    static readonly object Lock = new();

    static readonly Dictionary<string, string> KnownFriendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost"] = "خدمات ويندوز (تحديثات؟)",
        ["System"] = "ويندوز (System)",
        ["steamwebhelper"] = "Steam",
        ["steam"] = "Steam",
        ["EpicGamesLauncher"] = "Epic Games",
        ["EpicWebHelper"] = "Epic Games",
        ["Battle.net"] = "Battle.net",
        ["msedgewebview2"] = "WebView (تطبيق ويندوز)",
        ["OneDrive"] = "OneDrive",
        ["MoUsoCoreWorker"] = "Windows Update",
        ["TiWorker"] = "Windows Update",
        ["XboxPcApp"] = "Xbox App",
        ["gamingservices"] = "Xbox Gaming Services",
    };

    public static string Name(int pid) => Get(pid).Name;
    public static string Friendly(int pid) => Get(pid).Friendly;

    static Entry Get(int pid)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(pid, out var e) && (DateTime.UtcNow - e.Seen).TotalMinutes < 5) return e;
        }
        string name = pid == 4 ? "System" : pid == 0 ? "Idle" : "pid " + pid;
        string friendly = name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            friendly = name;
            if (KnownFriendly.TryGetValue(name, out var k)) friendly = k;
            else
            {
                try
                {
                    var desc = p.MainModule?.FileVersionInfo.FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(desc) && desc.Length <= 32) friendly = desc;
                }
                catch { }
            }
        }
        catch { }
        var entry = new Entry(name, friendly, DateTime.UtcNow);
        lock (Lock) Cache[pid] = entry;
        return entry;
    }
}
