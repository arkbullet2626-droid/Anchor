namespace Anchor.Core;

/// <summary>Anchor's brain: takes snapshots, restores them and remembers how to undo.</summary>
public sealed class Engine
{
    readonly LayoutStore _store;
    readonly object _gate = new();
    DateTime _lastPersistUtc = DateTime.MinValue;
    bool _dirty;

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
            Monitors = mons,
            Windows = Windows.Capture(mons, IncludeOwnProcess),
        };
        CurrentSetupName = Geometry.SetupName(mons);
        CurrentMonitorCount = mons.Count;
        LastWindowCount = snap.Windows.Count;
        return snap;
    }

    /// <summary>Called every few seconds while things are calm: remember where everything is.</summary>
    public Snapshot TakeAutoSnapshot()
    {
        var snap = Capture();
        lock (_gate)
        {
            var mem = _store.Get(snap.SetupKey, snap.Monitors);
            if (!snap.SameLayout(mem.Auto)) _dirty = true;
            mem.Auto = snap;
            // Writing to disk is only needed so a layout survives Anchor restarting; keep it occasional.
            if (_dirty && DateTime.UtcNow - _lastPersistUtc > TimeSpan.FromSeconds(30))
            {
                _store.Save(); _dirty = false; _lastPersistUtc = DateTime.UtcNow;
            }
        }
        return snap;
    }

    /// <summary>"Save layout": remember the current layout for this monitor setup.</summary>
    public Snapshot SaveLayout()
    {
        var snap = Capture();
        lock (_gate)
        {
            var mem = _store.Get(snap.SetupKey, snap.Monitors);
            mem.Saved = snap;
            mem.Auto = snap;
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
        Snapshot? src; bool saved;
        lock (_gate)
        {
            var mem = _store.Get(Geometry.SetupKey(mons), mons);
            saved = mem.Saved != null;
            src = mem.Saved ?? mem.Auto;
        }
        if (src == null) return null;
        Log.Write($"Restore now from {(saved ? "saved layout" : "last snapshot")} ({src.Windows.Count} windows)");
        return Restore(src, looseMatch: saved);
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
        var undo = new Snapshot { TakenUtc = DateTime.UtcNow, SetupKey = Geometry.SetupKey(mons), Monitors = mons };
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
