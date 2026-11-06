using System.Text.Json.Serialization;

namespace Anchor.Core;

/// <summary>A rectangle in physical pixels (Left, Top, Right, Bottom), like Win32 RECT.</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    [JsonIgnore] public int Width => Right - Left;
    [JsonIgnore] public int Height => Bottom - Top;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectI Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public bool Near(RectI o, int tol = 2) =>
        Math.Abs(Left - o.Left) <= tol && Math.Abs(Top - o.Top) <= tol &&
        Math.Abs(Right - o.Right) <= tol && Math.Abs(Bottom - o.Bottom) <= tol;

    public int IntersectArea(RectI o)
    {
        int w = Math.Min(Right, o.Right) - Math.Max(Left, o.Left);
        int h = Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public override string ToString() => $"({Left},{Top} {Width}x{Height})";
}

/// <summary>One connected monitor. Id comes from the monitor's own identity (EDID), not its Windows number.</summary>
public sealed record MonitorInfo(string Id, string Name, RectI Bounds, RectI Work, bool Primary, int Dpi);

public enum ShowState { Normal, Maximized, Minimized }

/// <summary>Where one window was. Rects are relative to the top-left of the monitor it was on.</summary>
public sealed class WindowEntry
{
    public long Hwnd { get; set; }
    public int Pid { get; set; }
    public string Exe { get; set; } = "";
    public string Class { get; set; } = "";
    public string Title { get; set; } = "";
    public string MonitorId { get; set; } = "";
    /// <summary>The window's actual rectangle (differs from Normal when snapped).</summary>
    public RectI Rect { get; set; }
    /// <summary>The "restored" rectangle Windows remembers (used when maximized, minimized or snapped).</summary>
    public RectI Normal { get; set; }
    public ShowState State { get; set; }
    /// <summary>For minimized windows: it was maximized before being minimized.</summary>
    public bool RestoreMaximized { get; set; }
    /// <summary>0 = top of the stack.</summary>
    public int Z { get; set; }

    public bool SamePlace(WindowEntry o, int tol = 2) =>
        MonitorId == o.MonitorId && State == o.State && RestoreMaximized == o.RestoreMaximized &&
        Normal.Near(o.Normal, tol) && (State != ShowState.Normal || Rect.Near(o.Rect, tol));

    public WindowEntry Clone() => (WindowEntry)MemberwiseClone();
}

public sealed class Snapshot
{
    public DateTime TakenUtc { get; set; }
    public string SetupKey { get; set; } = "";
    public List<MonitorInfo> Monitors { get; set; } = new();
    public List<WindowEntry> Windows { get; set; } = new();

    /// <summary>True when every window is where it is in the other snapshot (ignores order and titles).</summary>
    public bool SameLayout(Snapshot? o)
    {
        if (o == null || o.SetupKey != SetupKey || o.Windows.Count != Windows.Count) return false;
        var map = o.Windows.ToDictionary(w => w.Hwnd);
        foreach (var w in Windows)
            if (!map.TryGetValue(w.Hwnd, out var x) || !w.SamePlace(x, 0)) return false;
        return true;
    }
}

/// <summary>What Anchor remembers for one monitor setup (e.g. "desk" vs "laptop only").</summary>
public sealed class SetupMemory
{
    public string Name { get; set; } = "";
    public List<MonitorInfo> Monitors { get; set; } = new();
    /// <summary>The last good automatic snapshot.</summary>
    public Snapshot? Auto { get; set; }
    /// <summary>The layout the person saved with "Save layout".</summary>
    public Snapshot? Saved { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class RestoreResult
{
    public int Moved { get; set; }
    public int AlreadyRight { get; set; }
    public int NotOpen { get; set; }
    public List<string> SkippedAdmin { get; } = new();
    public List<string> SkippedHung { get; } = new();
    public List<string> Failed { get; } = new();
    /// <summary>Where the moved windows were just before this restore.</summary>
    public Snapshot? Undo { get; set; }

    public string Summary()
    {
        var parts = new List<string>();
        parts.Add(Moved == 0 ? "Everything was already in place." :
                  Moved == 1 ? "Put 1 window back." : $"Put {Moved} windows back.");
        if (SkippedAdmin.Count > 0)
            parts.Add($"Skipped {Plural(SkippedAdmin.Count, "admin window")} ({string.Join(", ", SkippedAdmin.Distinct().Take(3))}).");
        if (SkippedHung.Count > 0)
            parts.Add($"Skipped {Plural(SkippedHung.Count, "window")} that isn't responding.");
        if (Failed.Count > 0)
            parts.Add($"Couldn't move {string.Join(", ", Failed.Distinct().Take(3))}.");
        return string.Join(" ", parts);
    }

    static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
