namespace Anchor.Core;

public static class Geometry
{
    /// <summary>
    /// Maps a rect saved relative to <paramref name="saved"/> onto <paramref name="now"/>.
    /// Same size monitor: same spot. Different resolution: scaled, then kept inside the work area.
    /// </summary>
    public static RectI Map(RectI rel, MonitorInfo saved, MonitorInfo now)
    {
        RectI r;
        if (saved.Bounds.Width == now.Bounds.Width && saved.Bounds.Height == now.Bounds.Height)
        {
            r = rel.Offset(now.Bounds.Left, now.Bounds.Top);
        }
        else
        {
            double sx = (double)now.Bounds.Width / Math.Max(1, saved.Bounds.Width);
            double sy = (double)now.Bounds.Height / Math.Max(1, saved.Bounds.Height);
            r = new RectI(
                now.Bounds.Left + (int)Math.Round(rel.Left * sx),
                now.Bounds.Top + (int)Math.Round(rel.Top * sy),
                now.Bounds.Left + (int)Math.Round(rel.Right * sx),
                now.Bounds.Top + (int)Math.Round(rel.Bottom * sy));
            r = KeepInside(r, now.Work);
        }
        return r;
    }

    /// <summary>Shrinks and nudges <paramref name="r"/> so it fits in <paramref name="area"/>.</summary>
    public static RectI KeepInside(RectI r, RectI area)
    {
        int w = Math.Min(r.Width, area.Width), h = Math.Min(r.Height, area.Height);
        int left = Math.Clamp(r.Left, area.Left, area.Right - w);
        int top = Math.Clamp(r.Top, area.Top, area.Bottom - h);
        return new RectI(left, top, left + w, top + h);
    }

    public static RectI ToRelative(RectI abs, MonitorInfo m) => abs.Offset(-m.Bounds.Left, -m.Bounds.Top);

    /// <summary>The monitor a rect overlaps most (nearest one if it's off-screen).</summary>
    public static MonitorInfo? MonitorFor(RectI r, IReadOnlyList<MonitorInfo> mons)
    {
        MonitorInfo? best = null; long bestArea = -1;
        foreach (var m in mons)
        {
            long a = r.IntersectArea(m.Bounds);
            if (a > bestArea) { bestArea = a; best = m; }
        }
        if (bestArea > 0 || mons.Count == 0) return best;
        // Off-screen: nearest by centre distance.
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return mons.MinBy(m =>
        {
            long dx = Math.Max(0, Math.Max(m.Bounds.Left - cx, cx - m.Bounds.Right));
            long dy = Math.Max(0, Math.Max(m.Bounds.Top - cy, cy - m.Bounds.Bottom));
            return dx * dx + dy * dy;
        });
    }

    /// <summary>Key for a monitor setup: the sorted monitor ids. Resolution isn't part of it,
    /// so a game changing resolution doesn't look like a new setup.</summary>
    public static string SetupKey(IEnumerable<MonitorInfo> mons) =>
        string.Join("|", mons.Select(m => m.Id).OrderBy(s => s, StringComparer.Ordinal));

    public static string SetupName(IReadOnlyList<MonitorInfo> mons) =>
        mons.Count == 0 ? "No monitors" :
        string.Join(" + ", mons.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).Select(m => m.Name));
}
