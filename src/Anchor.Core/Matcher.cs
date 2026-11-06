namespace Anchor.Core;

/// <summary>Pairs remembered windows with the windows that are open now.</summary>
public static class Matcher
{
    /// <summary>
    /// 1. Same window handle, same process and same app (the normal case after sleep or a monitor change).
    /// 2. Same app, same kind of window, same title (e.g. a layout saved before a restart).
    /// 3. Same app and kind of window, in stacking order (several Chrome windows whose titles changed).
    /// Each open window is used at most once. Saved windows with no match are skipped quietly.
    /// </summary>
    public static List<(WindowEntry saved, WindowEntry live)> Match(
        IReadOnlyList<WindowEntry> saved, IReadOnlyList<WindowEntry> live, bool allowLooseMatch)
    {
        var result = new List<(WindowEntry, WindowEntry)>();
        var usedLive = new HashSet<WindowEntry>(ReferenceEqualityComparer.Instance);
        var usedSaved = new HashSet<WindowEntry>(ReferenceEqualityComparer.Instance);

        var byHwnd = live.GroupBy(l => l.Hwnd).ToDictionary(g => g.Key, g => g.First());
        foreach (var s in saved)
        {
            if (byHwnd.TryGetValue(s.Hwnd, out var l) && l.Pid == s.Pid && Same(l.Exe, s.Exe) && !usedLive.Contains(l))
            {
                result.Add((s, l)); usedLive.Add(l); usedSaved.Add(s);
            }
        }
        if (!allowLooseMatch) return result;

        foreach (var s in saved.Where(x => !usedSaved.Contains(x)).OrderBy(x => x.Z))
        {
            var l = live.Where(x => !usedLive.Contains(x) && Same(x.Exe, s.Exe) && x.Class == s.Class && x.Title == s.Title)
                        .OrderBy(x => x.Z).FirstOrDefault();
            if (l != null) { result.Add((s, l)); usedLive.Add(l); usedSaved.Add(s); }
        }
        foreach (var s in saved.Where(x => !usedSaved.Contains(x)).OrderBy(x => x.Z))
        {
            var l = live.Where(x => !usedLive.Contains(x) && Same(x.Exe, s.Exe) && x.Class == s.Class)
                        .OrderBy(x => x.Z).FirstOrDefault();
            if (l != null) { result.Add((s, l)); usedLive.Add(l); usedSaved.Add(s); }
        }
        return result;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
