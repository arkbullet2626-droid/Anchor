namespace Anchor.Core;

/// <summary>Anchor's brain: takes snapshots, restores them and remembers how to undo.</summary>
public sealed class Engine
{
    /// <summary>An arrangement must have been used this long before Anchor restores into it on its own,
    /// so a brief in-between state (one monitor waking before the other) never counts as "usual".</summary>
    public static readonly double MinCalmSecondsForAuto =
        double.TryParse(Environment.GetEnvironmentVariable("ANCHOR_TEST_MIN_CALM_SECONDS"), out var s) ? s : 120; // tests only
    const int MaxArrangementsPerSetup = 6;

    readonly LayoutStore _store;
    readonly object _gate = new();
    DateTime _lastPersistUtc = DateTime.MinValue;
    bool _dirty;
    Snapshot? _autoUndo; // undo shared by an automatic restore and its follow-up

    public Engine(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        Log.Folder = folder;
        _store = new LayoutStore(folder);
    }

    public string Folder { get; }
    public bool IncludeOwnProcess { get; init; }
    public Snapshot? LastUndo { get; private set; }
    public string CurrentSetupName { get; private set; } = "";
    public int CurrentMonitorCount { get; private set; }
    public int LastWindowCount { get; private set; }

    public Snapshot Capture()
    {
        var mons = Monitors.GetAll();
        var snap = new Snapshot
        {
            TakenUtc = DateTime.UtcNow,
            SetupKey = Geometry.SetupKey(mons),
            Signature = Geometry.Signature(mons),
            Monitors = mons,
            Windows = Windows.Capture(mons, IncludeOwnProcess),
        };
        CurrentSetupName = Geometry.SetupName(mons);
        CurrentMonitorCount = mons.Count;
        LastWindowCount = snap.Windows.Count;
        return snap;
    }

    /// <summary>Called every few seconds while things are calm: remember where everything is.</summary>
    /// <param name="calmSeconds">Time since the previous snapshot, counted towards this arrangement being the usual one.</param>
    public Snapshot TakeAutoSnapshot(double calmSeconds)
    {
        var snap = Capture();
        lock (_gate)
        {
            var mem = _store.Get(snap.SetupKey, snap.Monitors);
            if (!mem.Arrangements.TryGetValue(snap.Signature, out var arr))
            {
                mem.Arrangements[snap.Signature] = arr = new Arrangement();
                Prune(mem);
            }
            if (!snap.SameLayout(arr.Auto)) _dirty = true;
            arr.Auto = snap;
            arr.CalmSeconds += Math.Clamp(calmSeconds, 0, 60);
            arr.LastUsedUtc = DateTime.UtcNow;
            // Writing to disk is only needed so a layout survives Anchor restarting; keep it occasional.
            if (_dirty && DateTime.UtcNow - _lastPersistUtc > TimeSpan.FromSeconds(30))
            {
                _store.Save(); _dirty = false; _lastPersistUtc = DateTime.UtcNow;
            }
        }
        return snap;
    }

    static void Prune(SetupMemory mem)
    {
        while (mem.Arrangements.Count > MaxArrangementsPerSetup)
        {
            string usual = Usual(mem) ?? "";
            var oldest = mem.Arrangements.Where(kv => kv.Key != usual).MinBy(kv => kv.Value.LastUsedUtc).Key;
            mem.Arrangements.Remove(oldest);
        }
    }

    static string? Usual(SetupMemory mem) =>
        mem.Arrangements.Count == 0 ? null : mem.Arrangements.MaxBy(kv => kv.Value.CalmSeconds).Key;

    /// <summary>"Save layout": remember the current layout for this monitor setup.</summary>
    public Snapshot SaveLayout()
    {
        var snap = Capture();
        lock (_gate)
        {
            var mem = _store.Get(snap.SetupKey, snap.Monitors);
            mem.Saved = snap;
            _store.Save(); _dirty = false; _lastPersistUtc = DateTime.UtcNow;
        }
        Log.Write($"Saved layout: {snap.Windows.Count} windows on {CurrentSetupName}");
        return snap;
    }

    public bool HasSavedLayout()
    {
        var mons = Monitors.GetAll();
        lock (_gate) return _store.Get(Geometry.SetupKey(mons), mons).Saved != null;
    }

    /// <summary>"Restore now": the saved layout for this setup, or else the last automatic snapshot.</summary>
    public RestoreResult? RestoreNow()
    {
        var mons = Monitors.GetAll();
        string sig = Geometry.Signature(mons);
        Snapshot? src; bool saved;
        lock (_gate)
        {
            var mem = _store.Get(Geometry.SetupKey(mons), mons);
            saved = mem.Saved != null;
            src = mem.Saved
                  ?? (mem.Arrangements.TryGetValue(sig, out var a) ? a.Auto : null)
                  ?? mem.Arrangements.Values.Where(x => x.Auto != null).MaxBy(x => x.LastUsedUtc)?.Auto;
        }
        if (src == null) return null;
        Log.Write($"Restore now from {(saved ? "saved layout" : "last snapshot")} ({src.Windows.Count} windows)");
        return Restore(src, looseMatch: saved);
    }

    /// <summary>
    /// Automatic restore after things settle. Only restores into the arrangement these monitors are
    /// usually in, so a game's temporary resolution is left alone.
    /// </summary>
    /// <param name="followUp">The second pass a few seconds later; its moves join the same undo.</param>
    public RestoreResult? AutoRestore(bool followUp, out string why)
    {
        var mons = Monitors.GetAll();
        string key = Geometry.SetupKey(mons), sig = Geometry.Signature(mons);
        Snapshot? src;
        lock (_gate)
        {
            var mem = _store.Get(key, mons);
            CurrentSetupName = mem.Name;
            CurrentMonitorCount = mons.Count;
            src = ChooseAuto(mem, sig, out why);
            if (src == null) return null;
        }
        why = "";
        var r = RestoreCore(src, looseMatch: false);
        if (!followUp) _autoUndo = null;
        if (r.Moved > 0 && r.Undo != null)
        {
            if (_autoUndo != null)
            {
                // Same event: keep the earliest "before" position of each window in one undo.
                var have = _autoUndo.Windows.Select(w => w.Hwnd).ToHashSet();
                _autoUndo.Windows.AddRange(r.Undo.Windows.Where(w => !have.Contains(w.Hwnd)));
            }
            else _autoUndo = r.Undo;
            LastUndo = _autoUndo;
        }
        Log.Write($"Auto restore{(followUp ? " (follow-up)" : "")}: {r.Summary()}");
        return r;
    }

    /// <summary>Which snapshot an automatic restore may use right now, or null and why not.</summary>
    public static Snapshot? ChooseAuto(SetupMemory mem, string sig, out string why)
    {
        if (!mem.Arrangements.TryGetValue(sig, out var arr) || arr.Auto == null)
        { why = "no snapshot for this arrangement yet"; return null; }
        if (Usual(mem) != sig)
        { why = "not the usual resolution for these monitors (a game?)"; return null; }
        if (arr.CalmSeconds < MinCalmSecondsForAuto)
        { why = "this arrangement hasn't been used long enough yet"; return null; }
        why = "";
        return arr.Auto;
    }

    /// <summary>Puts windows back where <paramref name="snap"/> says, and keeps an undo.</summary>
    public RestoreResult Restore(Snapshot snap, bool looseMatch)
    {
        var result = RestoreCore(snap, looseMatch);
        if (result.Moved > 0) LastUndo = result.Undo;
        Log.Write("Restore: " + result.Summary());
        return result;
    }

    public RestoreResult? UndoLast()
    {
        var undo = LastUndo;
        if (undo == null) return null;
        LastUndo = null;
        var r = RestoreCore(undo, looseMatch: false);
        Log.Write("Undo: " + r.Summary());
        return r;
    }

    RestoreResult RestoreCore(Snapshot snap, bool looseMatch)
    {
        var result = new RestoreResult();
        var mons = Monitors.GetAll();
        var live = Windows.Capture(mons, IncludeOwnProcess);
        var savedMons = snap.Monitors.ToDictionary(m => m.Id);
        var nowMons = mons.ToDictionary(m => m.Id);
        var undo = new Snapshot
        {
            TakenUtc = DateTime.UtcNow, SetupKey = Geometry.SetupKey(mons), Signature = Geometry.Signature(mons), Monitors = mons,
        };
        var toSettle = new List<(WindowEntry live, RectI rect)>();

        var pairs = Matcher.Match(snap.Windows, live, looseMatch);
        result.NotOpen = snap.Windows.Count - pairs.Count;

        // Bottom of the stack first, so the top windows land last.
        foreach (var (s, l) in pairs.OrderByDescending(p => p.saved.Z))
        {
            if (!savedMons.TryGetValue(s.MonitorId, out var sm)) continue;
            // The monitor it was on isn't connected: leave the window alone.
            if (!nowMons.TryGetValue(s.MonitorId, out var nm)) { result.NotOpen++; continue; }

            var rect = Geometry.Map(s.Rect, sm, nm);
            var normal = Geometry.Map(s.Normal, sm, nm);
            var outcome = Windows.Apply(l, s.State, rect, normal, s.RestoreMaximized, mons, nm);
            string label = string.IsNullOrEmpty(l.Exe) ? Windows.Short(l.Title) : Path.GetFileNameWithoutExtension(l.Exe);
            switch (outcome)
            {
                case Windows.MoveOutcome.Moved:
                    result.Moved++;
                    Log.Write($"  moved {label} \"{Windows.Short(l.Title)}\" [{l.Hwnd}] to {(s.State == ShowState.Normal ? rect : normal)} {s.State} on {nm.Name}");
                    undo.Windows.Add(l.Clone());
                    if (s.State == ShowState.Normal) toSettle.Add((l, rect));
                    break;
                case Windows.MoveOutcome.AlreadyRight: result.AlreadyRight++; break;
                case Windows.MoveOutcome.Admin: result.SkippedAdmin.Add(label); break;
                case Windows.MoveOutcome.Hung: result.SkippedHung.Add(label); break;
                case Windows.MoveOutcome.Gone: result.NotOpen++; break;
                default: result.Failed.Add(label); break;
            }
        }

        if (toSettle.Count > 0)
        {
            Thread.Sleep(300);
            foreach (var (l, rect) in toSettle) Windows.Settle(l, ShowState.Normal, rect);
        }
        result.Undo = undo;
        return result;
    }

    public void Flush()
    {
        lock (_gate) { if (_dirty) { _store.Save(); _dirty = false; } }
    }
}
