using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Anchor.Core;

/// <summary>Lists the windows a person would think of as "their windows", and moves them.</summary>
public static class Windows
{
    static readonly HashSet<string> SkipClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
        "Shell_InputSwitchTopLevelWindow", "MultitaskingViewFrame", "ForegroundStaging",
    };

    static readonly int OwnPid = Environment.ProcessId;
    static readonly Dictionary<int, (string exe, bool admin, DateTime seen)> ProcCache = new();

    /// <summary>Takes a picture of where every window is right now.</summary>
    public static List<WindowEntry> Capture(IReadOnlyList<MonitorInfo> mons, bool includeOwnProcess = false)
    {
        var hwnds = new List<nint>();
        Native.EnumWindows((h, _) => { hwnds.Add(h); return true; }, 0); // top of the stack first

        var primary = mons.FirstOrDefault(m => m.Primary) ?? mons.FirstOrDefault();
        var list = new List<WindowEntry>();
        int z = 0;
        foreach (var h in hwnds)
        {
            var e = Read(h, mons, primary, includeOwnProcess);
            if (e == null) continue;
            e.Z = z++;
            list.Add(e);
        }
        PruneProcCache();
        return list;
    }

    static WindowEntry? Read(nint h, IReadOnlyList<MonitorInfo> mons, MonitorInfo? primary, bool includeOwn)
    {
        if (!Native.IsWindowVisible(h)) return null;
        long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE);
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        if ((style & Native.WS_CHILD) != 0) return null;
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return null;
        if (Native.GetWindow(h, Native.GW_OWNER) != 0 && !appWindow) return null;
        if (IsCloaked(h)) return null;

        int len = Native.GetWindowTextLength(h);
        if (len <= 0) return null;
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        string title = sb.ToString();

        var cls = new StringBuilder(256);
        Native.GetClassName(h, cls, cls.Capacity);
        if (SkipClasses.Contains(cls.ToString())) return null;

        Native.GetWindowThreadProcessId(h, out int pid);
        if (pid == OwnPid && !includeOwn) return null;

        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(h, ref wp)) return null;
        if (!Native.GetWindowRect(h, out var wr)) return null;

        var state = Native.IsIconic(h) ? ShowState.Minimized : Native.IsZoomed(h) ? ShowState.Maximized : ShowState.Normal;
        var rect = Native.ToRect(wr);
        var normal = WorkspaceToScreen(Native.ToRect(wp.rcNormalPosition), primary, ex);
        if (normal.IsEmpty) return null;

        var mon = Geometry.MonitorFor(state == ShowState.Normal ? rect : normal, mons);
        if (mon == null) return null;

        var (exe, _) = ProcInfo(pid);
        return new WindowEntry
        {
            Hwnd = h, Pid = pid, Exe = exe, Class = cls.ToString(), Title = title,
            MonitorId = mon.Id,
            Rect = Geometry.ToRelative(state == ShowState.Normal ? rect : normal, mon),
            Normal = Geometry.ToRelative(normal, mon),
            State = state,
            RestoreMaximized = state == ShowState.Minimized && (wp.flags & Native.WPF_RESTORETOMAXIMIZED) != 0,
        };
    }

    static bool IsCloaked(nint h)
    {
        try { return Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int c, 4) == 0 && c != 0; }
        catch { return false; }
    }

    // GetWindowPlacement's normal rect is in "workspace" coordinates (shifted by a taskbar on the
    // top or left of the primary monitor), except for tool windows.
    static RectI WorkspaceToScreen(RectI r, MonitorInfo? primary, long ex) =>
        primary == null || (ex & Native.WS_EX_TOOLWINDOW) != 0 ? r
        : r.Offset(primary.Work.Left - primary.Bounds.Left, primary.Work.Top - primary.Bounds.Top);

    static RectI ScreenToWorkspace(RectI r, MonitorInfo? primary, long ex) =>
        primary == null || (ex & Native.WS_EX_TOOLWINDOW) != 0 ? r
        : r.Offset(-(primary.Work.Left - primary.Bounds.Left), -(primary.Work.Top - primary.Bounds.Top));

    public static (string exe, bool admin) ProcInfo(int pid)
    {
        lock (ProcCache)
        {
            if (ProcCache.TryGetValue(pid, out var c)) { ProcCache[pid] = (c.exe, c.admin, DateTime.UtcNow); return (c.exe, c.admin); }
        }
        string exe = "";
        bool admin = false;
        nint hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp != 0)
        {
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                if (Native.QueryFullProcessImageName(hp, 0, sb, ref size)) exe = Path.GetFileName(sb.ToString());
                if (!SelfIsAdmin)
                {
                    if (Native.OpenProcessToken(hp, Native.TOKEN_QUERY, out var tok))
                    {
                        try { admin = Native.GetTokenInformation(tok, Native.TokenElevation, out int elevated, 4, out _) && elevated != 0; }
                        finally { Native.CloseHandle(tok); }
                    }
                    else admin = Marshal.GetLastWin32Error() == 5; // access denied: runs as admin or SYSTEM
                }
            }
            finally { Native.CloseHandle(hp); }
        }
        else if (!SelfIsAdmin) admin = Marshal.GetLastWin32Error() == 5;

        lock (ProcCache) ProcCache[pid] = (exe, admin, DateTime.UtcNow);
        return (exe, admin);
    }

    static void PruneProcCache()
    {
        lock (ProcCache)
        {
            var old = ProcCache.Where(kv => DateTime.UtcNow - kv.Value.seen > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList();
            foreach (var k in old) ProcCache.Remove(k);
        }
    }

    static bool? _selfIsAdmin;
    public static bool SelfIsAdmin
    {
        get
        {
            if (_selfIsAdmin is bool b) return b;
            bool r = false;
            try
            {
                if (Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_QUERY, out var tok))
                {
                    try { r = Native.GetTokenInformation(tok, Native.TokenElevation, out int e, 4, out _) && e != 0; }
                    finally { Native.CloseHandle(tok); }
                }
            }
            catch { }
            _selfIsAdmin = r;
            return r;
        }
    }

    public enum MoveOutcome { Moved, AlreadyRight, Admin, Hung, Gone, Failed }

    /// <summary>
    /// Puts one window at <paramref name="target"/> (absolute rects already mapped onto the current monitors).
    /// Never activates the window or changes which window has focus.
    /// </summary>
    public static MoveOutcome Apply(WindowEntry live, ShowState state, RectI rect, RectI normal, bool restoreMax,
        IReadOnlyList<MonitorInfo> mons, MonitorInfo targetMon)
    {
        nint h = (nint)live.Hwnd;
        if (!Native.IsWindow(h)) return MoveOutcome.Gone;
        if (ProcInfo(live.Pid).admin) return MoveOutcome.Admin;
        if (Native.IsHungAppWindow(h)) return MoveOutcome.Hung;

        var primary = mons.FirstOrDefault(m => m.Primary) ?? mons.FirstOrDefault();
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);

        if (IsAlreadyRight(h, state, rect, normal, restoreMax, mons, primary, ex, targetMon)) return MoveOutcome.AlreadyRight;

        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(h, ref wp);
        wp.rcNormalPosition = Native.ToRECT(ScreenToWorkspace(normal, primary, ex));
        wp.flags = Native.WPF_ASYNCWINDOWPLACEMENT;

        bool ok;
        switch (state)
        {
            case ShowState.Minimized:
                if (restoreMax) wp.flags |= Native.WPF_RESTORETOMAXIMIZED;
                wp.showCmd = Native.SW_SHOWMINNOACTIVE;
                ok = Native.SetWindowPlacement(h, ref wp);
                break;

            case ShowState.Maximized:
                // Un-maximize onto the right monitor first, then maximize there. Both requests are
                // queued to the window's own thread in order, so a busy app can't freeze Anchor.
                wp.showCmd = Native.SW_SHOWNOACTIVATE;
                ok = Native.SetWindowPlacement(h, ref wp);
                ok &= Native.ShowWindowAsync(h, Native.SW_MAXIMIZE);
                break;

            default:
                wp.showCmd = Native.SW_SHOWNOACTIVATE;
                ok = Native.SetWindowPlacement(h, ref wp);
                // Snapped windows: the visible rect differs from the "restore" rect.
                ok &= SetPos(h, rect);
                break;
        }
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Write($"Move failed for {live.Exe} \"{Short(live.Title)}\" (error {err})");
            return err == 5 ? MoveOutcome.Admin : MoveOutcome.Failed;
        }
        return MoveOutcome.Moved;
    }

    /// <summary>Second pass after windows had a moment to react (DPI changes can resize a window as it crosses monitors).</summary>
    public static void Settle(WindowEntry live, ShowState state, RectI rect)
    {
        nint h = (nint)live.Hwnd;
        if (state != ShowState.Normal || !Native.IsWindow(h) || Native.IsHungAppWindow(h)) return;
        if (Native.IsZoomed(h) || Native.IsIconic(h)) return;
        if (Native.GetWindowRect(h, out var r) && !Native.ToRect(r).Near(rect)) SetPos(h, rect);
    }

    static bool SetPos(nint h, RectI r) =>
        Native.SetWindowPos(h, 0, r.Left, r.Top, r.Width, r.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS);

    static bool IsAlreadyRight(nint h, ShowState state, RectI rect, RectI normal, bool restoreMax,
        IReadOnlyList<MonitorInfo> mons, MonitorInfo? primary, long ex, MonitorInfo targetMon)
    {
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(h, ref wp) || !Native.GetWindowRect(h, out var wr)) return false;
        var nowNormal = WorkspaceToScreen(Native.ToRect(wp.rcNormalPosition), primary, ex);
        var nowState = Native.IsIconic(h) ? ShowState.Minimized : Native.IsZoomed(h) ? ShowState.Maximized : ShowState.Normal;
        if (nowState != state) return false;
        return state switch
        {
            ShowState.Normal => Native.ToRect(wr).Near(rect),
            ShowState.Maximized => Geometry.MonitorFor(Native.ToRect(wr), mons)?.Id == targetMon.Id && nowNormal.Near(normal),
            _ => nowNormal.Near(normal) && ((wp.flags & Native.WPF_RESTORETOMAXIMIZED) != 0) == restoreMax,
        };
    }

    static void WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(15);
    }

    /// <summary>True while a fullscreen game, video or presentation is in front. Anchor waits rather than
    /// move windows around behind it.</summary>
    public static bool FullscreenAppInFront()
    {
        try
        {
            // 2 = busy (fullscreen app), 3 = Direct3D fullscreen game, 4 = presentation mode
            return Native.SHQueryUserNotificationState(out int s) == 0 && s is 2 or 3 or 4;
        }
        catch { return false; }
    }

    public static string Short(string s) => s.Length <= 40 ? s : s[..37] + "...";
}
