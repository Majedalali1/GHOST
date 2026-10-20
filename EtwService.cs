using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace GameHUD;

public readonly record struct FpsStats(double? Fps, double? FrameTimeMs, double? Low1);

/// <summary>
/// One real-time ETW session that gives us:
///  - frame presents per process (DXGI / D3D9 / DxgKrnl) → FPS, frame time, 1% low
///  - bytes sent/received per process (Kernel-Network, TCP + UDP) → who is using the internet
/// Needs admin.
/// </summary>
public sealed class EtwService : IDisposable
{
    static readonly Guid DXGI = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DXGKRNL = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    static readonly Guid KNET = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    const string SessionName = "GameHUD-Session";
    const double KeepMs = 12_000;

    TraceEventSession? _session;
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    sealed class PresentBuf { public readonly Queue<double> Q = new(); public double Last; }

    // pid → [source] → present timestamps (ms). Source 0 = DXGI, 1 = D3D9, 2 = DxgKrnl.
    readonly Dictionary<int, PresentBuf[]> _presents = new();
    readonly object _presentLock = new();

    Dictionary<int, (long Rx, long Tx)> _net = new();
    readonly object _netLock = new();

    public void Start()
    {
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(KNET, TraceEventLevel.Informational, 0x30); // IPv4 | IPv6
            _session.EnableProvider(DXGI, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(DXGKRNL, TraceEventLevel.Informational, 0x8000000); // Present keyword

            var registered = new RegisteredTraceEventParser(_session.Source);
            registered.All += OnEvent;
            _session.Source.UnhandledEvents += OnEvent;

            var t = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; }
                IsRunning = false;
            }) { IsBackground = true, Name = "ETW" };
            IsRunning = true;
            t.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsRunning = false;
        }
    }

    static double NowMs => DateTime.Now.Ticks / 10_000.0;

    void OnEvent(TraceEvent e)
    {
        var g = e.ProviderGuid;
        int id = (int)e.ID;
        if (g == KNET)
        {
            bool send = id is 10 or 26 or 42 or 58;
            bool recv = id is 11 or 27 or 43 or 59;
            if (!send && !recv) return;
            if (IsLoopback(e.PayloadByName("daddr")) || IsLoopback(e.PayloadByName("saddr"))) return;
            int pid = ToInt(e.PayloadByName("PID")) ?? e.ProcessID;
            long size = ToInt(e.PayloadByName("size")) ?? 0;
            if (pid <= 0 || size <= 0) return;
            lock (_netLock)
            {
                _net.TryGetValue(pid, out var cur);
                _net[pid] = send ? (cur.Rx, cur.Tx + size) : (cur.Rx + size, cur.Tx);
            }
        }
        else if (g == DXGI) { if (id is 42 or 55) AddPresent(e.ProcessID, 0, e.TimeStamp); }
        else if (g == D3D9) { if (id == 1) AddPresent(e.ProcessID, 1, e.TimeStamp); }
        else if (g == DXGKRNL) { if (id == 184) AddPresent(e.ProcessID, 2, e.TimeStamp); }
    }

    void AddPresent(int pid, int src, DateTime ts)
    {
        if (pid <= 0) return;
        double t = ts.Ticks / 10_000.0;
        lock (_presentLock)
        {
            if (!_presents.TryGetValue(pid, out var arr))
                _presents[pid] = arr = new[] { new PresentBuf(), new PresentBuf(), new PresentBuf() };
            var b = arr[src];
            b.Last = t;
            var q = b.Q;
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > KeepMs) q.Dequeue();
        }
    }

    public bool HasRecentPresents(int pid, double withinMs = 5000)
    {
        double now = NowMs;
        lock (_presentLock)
        {
            if (!_presents.TryGetValue(pid, out var arr)) return false;
            return arr.Any(b => now - b.Last < withinMs);
        }
    }

    public FpsStats GetFps(int pid)
    {
        double[] ts;
        lock (_presentLock)
        {
            if (!_presents.TryGetValue(pid, out var arr)) return default;
            double now = NowMs;
            // Use the most specific source that is active, so presents aren't counted twice.
            var b = arr.FirstOrDefault(x => x.Q.Count > 1 && now - x.Last < 3000);
            if (b == null) return default;
            ts = b.Q.ToArray();
        }
        double last = ts[^1];
        int inLastSecond = ts.Count(t => last - t < 1000);
        if (inLastSecond < 1) return default;

        var frameTimes = new List<double>(ts.Length);
        for (int i = 1; i < ts.Length; i++)
        {
            double d = ts[i] - ts[i - 1];
            if (d > 0 && d < 1000) frameTimes.Add(d);
        }
        double? low1 = null;
        if (frameTimes.Count >= 50)
        {
            frameTimes.Sort();
            double p99 = frameTimes[(int)Math.Floor(frameTimes.Count * 0.99)];
            low1 = 1000.0 / p99;
        }
        double fps = inLastSecond;
        if (low1 > fps) low1 = fps;
        return new FpsStats(fps, 1000.0 / fps, low1);
    }

    public List<(int Pid, double Fps)> TopPresenters()
    {
        List<int> pids;
        lock (_presentLock) pids = _presents.Keys.ToList();
        return pids.Select(p => (p, GetFps(p).Fps ?? 0)).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(6).ToList();
    }

    /// <summary>Returns bytes per process since the previous call.</summary>
    public Dictionary<int, (long Rx, long Tx)> DrainNet()
    {
        lock (_netLock)
        {
            var d = _net;
            _net = new Dictionary<int, (long, long)>();
            return d;
        }
    }

    public void CleanupDeadPids()
    {
        double now = NowMs;
        lock (_presentLock)
        {
            foreach (var pid in _presents.Where(kv => kv.Value.All(b => now - b.Last > KeepMs)).Select(kv => kv.Key).ToList())
                _presents.Remove(pid);
        }
    }

    static int? ToInt(object? o) => o switch
    {
        int i => i, uint u => (int)u, long l => (int)l, ulong ul => (int)ul, short s => s, ushort us => us,
        _ => null,
    };

    static bool IsLoopback(object? o) => o switch
    {
        IPAddress ip => IPAddress.IsLoopback(ip),
        int i => (i & 0xFF) == 127,
        uint u => (u & 0xFF) == 127,
        _ => false,
    };

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        IsRunning = false;
    }
}
