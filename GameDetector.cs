namespace GameHUD;

public sealed record ForegroundInfo(int Pid, string ProcName, string Title, Native.RECT Monitor, bool Fullscreen, bool IsGame);

/// <summary>
/// Decides whether the app in front is a game: fullscreen (borderless or exclusive), rendering frames,
/// and not a browser/video player — or listed by the user via Ctrl+Shift+G.
/// </summary>
public static class GameDetector
{
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc", "zen",
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "PotPlayerMini64", "PotPlayer64", "Microsoft.Media.Player", "wmplayer",
        "Teams", "ms-teams", "Zoom", "POWERPNT", "WINWORD", "EXCEL", "Acrobat", "AcroRd32",
        "ApplicationFrameHost", "LockApp", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "mstsc", "vmconnect", "obs64", "Discord", "Spotify", "Claude", "GameHUD", "Netflix", "SnippingTool", "ScreenClippingHost",
    };

    static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    static readonly int OwnPid = Environment.ProcessId;
    static ForegroundInfo? _last;

    public static ForegroundInfo Detect(Settings settings, EtwService etw)
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return _last ?? Empty();
        Native.GetWindowThreadProcessId(hwnd, out uint upid);
        int pid = (int)upid;
        if (pid == OwnPid && _last != null) return _last; // our tray menu is open — keep the previous state

        var mon = Native.MonitorRect(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
        string cls = Native.ClassName(hwnd);
        string name = ProcessNames.Name(pid);
        string title = Native.WindowText(hwnd).Trim();

        bool fullscreen = false;
        if (!DesktopClasses.Contains(cls) && !Native.IsIconic(hwnd) && Native.GetWindowRect(hwnd, out var r))
            fullscreen = r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
        if (!fullscreen && Native.SHQueryUserNotificationState(out int quns) == 0 && quns == 3) // QUNS_RUNNING_D3D_FULL_SCREEN
            fullscreen = true;

        string key = name.ToLowerInvariant();
        bool never = settings.NeverShow.Contains(key);
        bool always = settings.AlwaysShow.Contains(key);
        bool rendering = !etw.IsRunning || etw.HasRecentPresents(pid);
        bool isGame = !never && (always || (fullscreen && !NotGames.Contains(name) && !DesktopClasses.Contains(cls) && rendering));

        _last = new ForegroundInfo(pid, name, title, mon, fullscreen, isGame);
        return _last;
    }

    static ForegroundInfo Empty()
    {
        var mon = Native.MonitorRect(Native.MonitorFromPoint(new System.Drawing.Point(0, 0), Native.MONITOR_DEFAULTTOPRIMARY));
        return new ForegroundInfo(0, "", "", mon, false, false);
    }
}
