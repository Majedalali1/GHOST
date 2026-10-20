using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameHUD;

public sealed class NetSnapshot
{
    public double DownBps, UpBps;
    public double? PingMs;          // last ping to the internet host (null = timeout)
    public double? AvgPingMs;       // average of recent successful pings
    public double? GatewayPingMs;   // ping to the router
    public double Jitter;
    public double LossPct;
    public bool Wifi;
    public string? Vpn;             // name of an active VPN adapter, if any
    public string PingHost = "";
    public IReadOnlyList<double?> Recent = Array.Empty<double?>();
}

/// <summary>Total up/down speed of the internet-facing adapters, ping to a host and to the router.</summary>
public sealed class NetworkService : IDisposable
{
    readonly Settings _settings;
    readonly CancellationTokenSource _cts = new();
    volatile NetSnapshot _latest = new();
    readonly List<double?> _pings = new();
    readonly List<double?> _gwPings = new();
    long _lastRx, _lastTx;
    DateTime _lastSample;
    IPAddress? _gateway;
    bool _wifi;
    string? _vpn;
    static readonly System.Text.RegularExpressions.Regex VpnRx = new(
        @"VPN|WireGuard|Wintun|TAP-Windows|OpenVPN|Proton|Nord|Express|Mullvad|Surfshark|Cloudflare WARP|Windscribe|PIA|Hotspot Shield",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    DateTime _lastAdapterScan;

    public NetSnapshot Latest => _latest;

    public NetworkService(Settings settings)
    {
        _settings = settings;
        Task.Run(() => Loop(_cts.Token));
    }

    async Task Loop(CancellationToken ct)
    {
        using var ping = new Ping();
        using var gwPing = new Ping();
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                var (rx, tx, adapters) = ReadCounters();
                var now = DateTime.UtcNow;
                double secs = (now - _lastSample).TotalSeconds;
                double down = 0, up = 0;
                if (_lastSample != default && secs > 0.2)
                {
                    down = Math.Max(0, (rx - _lastRx) / secs);
                    up = Math.Max(0, (tx - _lastTx) / secs);
                }
                _lastRx = rx; _lastTx = tx; _lastSample = now;

                var pingTask = SendPing(ping, _settings.PingHost);
                Task<double?> gwTask = (tick % 2 == 0 && _gateway != null) ? SendPing(gwPing, _gateway.ToString()) : Task.FromResult<double?>(double.NaN);
                await Task.WhenAll(pingTask, gwTask);

                Push(_pings, pingTask.Result, 10);
                if (gwTask.Result is not double.NaN) Push(_gwPings, gwTask.Result, 5);

                var ok = _pings.Where(p => p.HasValue).Select(p => p!.Value).ToList();
                double jitter = 0;
                for (int i = 1; i < ok.Count; i++) jitter += Math.Abs(ok[i] - ok[i - 1]);
                if (ok.Count > 1) jitter /= ok.Count - 1;
                var gwOk = _gwPings.Where(p => p.HasValue).Select(p => p!.Value).ToList();

                _latest = new NetSnapshot
                {
                    DownBps = down,
                    UpBps = up,
                    PingMs = pingTask.Result,
                    AvgPingMs = ok.Count > 0 ? ok.Average() : null,
                    GatewayPingMs = gwOk.Count > 0 ? gwOk.Average() : (_gwPings.Count > 0 ? 999 : null),
                    Jitter = jitter,
                    LossPct = _pings.Count == 0 ? 0 : 100.0 * _pings.Count(p => !p.HasValue) / _pings.Count,
                    Wifi = _wifi,
                    Vpn = _vpn,
                    PingHost = _settings.PingHost,
                    Recent = _pings.ToArray(),
                };
            }
            catch { }
            tick++;
            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ContinueWith(_ => { });
        }
    }

    static void Push(List<double?> list, double? v, int max)
    {
        list.Add(v);
        while (list.Count > max) list.RemoveAt(0);
    }

    static async Task<double?> SendPing(Ping p, string host)
    {
        try
        {
            var r = await p.SendPingAsync(host, 1000);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    List<NetworkInterface> _adapters = new();

    (long rx, long tx, int count) ReadCounters()
    {
        if ((DateTime.UtcNow - _lastAdapterScan).TotalSeconds > 15 || _adapters.Count == 0)
        {
            _lastAdapterScan = DateTime.UtcNow;
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
            var vpn = up.FirstOrDefault(n => VpnRx.IsMatch(n.Description) || VpnRx.IsMatch(n.Name));
            _vpn = vpn == null ? null : VpnRx.IsMatch(vpn.Name) ? vpn.Name : vpn.Description;
            // Internet-facing physical adapters = not a tunnel/VPN, and have a default gateway (VPN traffic would be counted twice).
            _adapters = up
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Tunnel && n != vpn
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .ToList();
            var primary = _adapters.FirstOrDefault();
            _wifi = primary?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            _gateway = primary?.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        long rx = 0, tx = 0;
        foreach (var n in _adapters)
        {
            try
            {
                var st = n.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
            catch { }
        }
        return (rx, tx, _adapters.Count);
    }

    public void Dispose() => _cts.Cancel();
}
