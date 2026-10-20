using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameHUD;

/// <summary>One app (all its processes grouped by exe name) and how much RAM / VRAM it holds.</summary>
public sealed class AppUsage
{
    public string Key = "";
    public string Name = "";
    public string? Path;
    public List<int> Pids = new();
    public long RamBytes;
    public long VramBytes;
    public bool HasWindow;
    public ImageSource? Icon;
}

public static class ProcessMonitor
{
    // Closing any of these would break Windows, the desktop, drivers or this session.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "RuntimeBroker", "MsMpEng", "NisSrv", "SecurityHealthService",
        "SecurityHealthSystray", "audiodg", "conhost", "dllhost", "taskhostw", "StartMenuExperienceHost", "SearchHost", "SearchIndexer",
        "ShellExperienceHost", "TextInputHost", "LockApp", "spoolsv", "WUDFHost", "WmiPrvSE", "smartscreen", "ApplicationFrameHost",
        "SystemSettingsBroker", "UserOOBEBroker", "Widgets", "WidgetService", "PhoneExperienceHost", "CompPkgSrv", "dasHost",
        "nvcontainer", "NVDisplay.Container", "nvsphelper64", "NVIDIA Web Helper", "atiesrxx", "atieclxx", "amdfendrsr", "RadeonSoftware",
        "AggregatorHost", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "msedgewebview2", "GameHUD", "claude", "Claude", "node",
        "PawnIOService", "RTSS", "MSIAfterburner", "EncoderServer", "RTSSHooksLoader64", "vmmem", "vmcompute", "wslservice", "WmiApSrv",
    };

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<AppUsage> Snapshot()
    {
        var vram = ReadVramPerPid();
        int mySession = Process.GetCurrentProcess().SessionId;
        var apps = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != mySession || Protected.Contains(p.ProcessName)) continue;
                    long ram = p.WorkingSet64;
                    vram.TryGetValue(p.Id, out long v);
                    if (!apps.TryGetValue(p.ProcessName, out var a))
                    {
                        a = new AppUsage { Key = p.ProcessName, Name = p.ProcessName };
                        try
                        {
                            a.Path = p.MainModule?.FileName;
                            var desc = p.MainModule?.FileVersionInfo.FileDescription?.Trim();
                            if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) a.Name = desc;
                        }
                        catch { }
                        apps[p.ProcessName] = a;
                    }
                    a.Pids.Add(p.Id);
                    a.RamBytes += ram;
                    a.VramBytes += v;
                    if (p.MainWindowHandle != IntPtr.Zero) a.HasWindow = true;
                }
                catch { }
            }
        }

        foreach (var a in apps.Values)
            a.Icon = a.Path == null ? null : GetIcon(a.Path);
        return apps.Values.Where(a => a.RamBytes > 30 * 1048576L || a.VramBytes > 30 * 1048576L).ToList();
    }

    /// <summary>Dedicated GPU memory per process, from the "GPU Process Memory" performance counters.</summary>
    static Dictionary<int, long> ReadVramPerPid()
    {
        var result = new Dictionary<int, long>();
        try
        {
            var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
            var coll = data["dedicated usage"];
            if (coll == null) return result;
            foreach (InstanceData d in coll.Values)
            {
                // instance name: pid_1234_luid_0x00000000_0x0000C2B5_phys_0
                var parts = d.InstanceName.Split('_');
                if (parts.Length > 1 && parts[0] == "pid" && int.TryParse(parts[1], out int pid))
                {
                    result.TryGetValue(pid, out long cur);
                    result[pid] = cur + d.RawValue;
                }
            }
        }
        catch { }
        return result;
    }

    static ImageSource? GetIcon(string path)
    {
        lock (IconCache)
            if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                img = bs;
            }
        }
        catch { }
        lock (IconCache) IconCache[path] = img;
        return img;
    }

    /// <summary>Asks the app to close (like clicking X); if it's still running after a few seconds, ends it.</summary>
    public static async Task<int> CloseAsync(IEnumerable<int> pids)
    {
        var procs = new List<Process>();
        foreach (var pid in pids)
        {
            try { procs.Add(Process.GetProcessById(pid)); } catch { }
        }
        foreach (var p in procs)
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
        }
        await Task.Delay(2500);
        int ended = 0;
        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited) { p.Kill(); p.WaitForExit(2000); }
                ended++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return ended;
    }
}
