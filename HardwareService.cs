using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace GameHUD;

public sealed class HwSnapshot
{
    public string CpuName = "CPU", GpuName = "GPU";
    public float? CpuTemp, CpuLoad, CpuMaxCore, CpuPower, CpuClock;
    public float? GpuTemp, GpuHotspot, GpuLoad, GpuPower, GpuClock, GpuMemClock, VramUsedMB, VramTotalMB, GpuFanRpm, GpuFanPct;
    public double RamUsedGB, RamTotalGB;
    public int RamLoad;
}

/// <summary>Reads CPU/GPU sensors via LibreHardwareMonitor on a background thread (1 Hz).</summary>
public sealed class HardwareService : IDisposable
{
    Computer? _computer;
    IHardware? _cpu, _gpu;
    volatile HwSnapshot _latest = new();
    volatile bool _stop;
    readonly Thread _thread;

    public HwSnapshot Latest => _latest;
    public bool Ready { get; private set; }
    public bool PawnIoInstalled { get; private set; } = true;

    public HardwareService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "HW", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Loop()
    {
        try
        {
            try { PawnIoInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();
            _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _gpu = _computer.Hardware
                .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                .OrderByDescending(GpuScore)
                .FirstOrDefault();
            Ready = true;
        }
        catch { Ready = true; }

        while (!_stop)
        {
            try { _latest = Read(); } catch { }
            Thread.Sleep(1000);
        }
    }

    // Prefer the discrete card over the CPU's integrated graphics.
    static int GpuScore(IHardware h)
    {
        bool integrated = Regex.IsMatch(h.Name, @"Radeon\(TM\) Graphics|Radeon Graphics|UHD Graphics|Iris|Intel\(R\) Graphics", RegexOptions.IgnoreCase);
        int score = h.HardwareType switch { HardwareType.GpuNvidia => 30, HardwareType.GpuAmd => 20, _ => 10 };
        return integrated ? score - 25 : score;
    }

    HwSnapshot Read()
    {
        var s = new HwSnapshot();
        if (_cpu != null)
        {
            _cpu.Update();
            s.CpuName = ShortCpu(_cpu.Name);
            s.CpuTemp = Val(_cpu, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Package", "Core Max", "Core Average")
                        ?? First(_cpu, SensorType.Temperature);
            s.CpuLoad = Val(_cpu, SensorType.Load, "CPU Total");
            s.CpuMaxCore = Val(_cpu, SensorType.Load, "CPU Core Max")
                           ?? _cpu.Sensors.Where(x => x.SensorType == SensorType.Load && x.Name.StartsWith("CPU Core #") && x.Value.HasValue)
                                          .Select(x => x.Value).DefaultIfEmpty(null).Max();
            s.CpuPower = Val(_cpu, SensorType.Power, "Package", "CPU Package");
            var clocks = _cpu.Sensors
                .Where(x => x.SensorType == SensorType.Clock && x.Name.StartsWith("Core #") && x.Value is > 0)
                .Select(x => x.Value!.Value).ToList();
            if (clocks.Count > 0) s.CpuClock = clocks.Average();
        }
        if (_gpu != null)
        {
            _gpu.Update();
            s.GpuName = ShortGpu(_gpu.Name);
            s.GpuTemp = Val(_gpu, SensorType.Temperature, "GPU Core") ?? First(_gpu, SensorType.Temperature);
            s.GpuHotspot = Val(_gpu, SensorType.Temperature, "GPU Hot Spot");
            s.GpuLoad = Val(_gpu, SensorType.Load, "GPU Core", "D3D 3D");
            s.GpuPower = Val(_gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? First(_gpu, SensorType.Power);
            s.GpuClock = Val(_gpu, SensorType.Clock, "GPU Core");
            s.GpuMemClock = Val(_gpu, SensorType.Clock, "GPU Memory");
            s.VramUsedMB = Val(_gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            s.VramTotalMB = Val(_gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
            s.GpuFanRpm = First(_gpu, SensorType.Fan);
            s.GpuFanPct = First(_gpu, SensorType.Control);
        }

        // Without the PawnIO driver some CPU sensors read 0 instead of being missing.
        if (s.CpuTemp <= 0) s.CpuTemp = null;
        if (s.CpuPower <= 0) s.CpuPower = null;

        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            s.RamTotalGB = m.ullTotalPhys / 1073741824.0;
            s.RamUsedGB = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            s.RamLoad = (int)m.dwMemoryLoad;
        }
        return s;
    }

    static float? Val(IHardware h, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var sensor = h.Sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (sensor?.Value is float v && !float.IsNaN(v)) return v;
        }
        return null;
    }

    static float? First(IHardware h, SensorType type) =>
        h.Sensors.FirstOrDefault(x => x.SensorType == type && x.Value is float v && !float.IsNaN(v))?.Value;

    public static string ShortCpu(string name)
    {
        name = Regex.Replace(name, @"\(R\)|\(TM\)|CPU|Processor|\d+-Core|@.*$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^(AMD|Intel)\s+", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^Core\s+(?=i\d|Ultra)", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    public static string ShortGpu(string name)
    {
        name = Regex.Replace(name, @"^(NVIDIA\s+)?(GeForce\s+)?", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^AMD\s+Radeon\s+", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^Intel\(R\)\s+", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        try { _computer?.Close(); } catch { }
    }
}
