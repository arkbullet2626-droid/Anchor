namespace Anchor.Core;

/// <summary>
/// Decides when to take snapshots and when to restore. Pure logic with a clock you can fake,
/// so every timing rule is unit-tested.
///
/// Calm: snapshot every few seconds.
/// Disturbed (display change, sleep, lock, screen off, monitors changed): stop snapshotting so the
///   last good layout is kept, and wait until things have been still for a moment.
/// Then restore, wait a little and restore once more (Windows sometimes moves windows late),
///   then go back to calm.
/// </summary>
public sealed class Watcher
{
    public enum Action { None, Snapshot, Restore, FollowUp }
    public enum Phase { Calm, Disturbed, Restoring }

    public static readonly TimeSpan SnapshotEvery = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SettleAfterWake = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan FollowUpAfter = TimeSpan.FromSeconds(3);

    readonly Func<DateTime> _now;
    DateTime _lastEvent, _lastConfigChange, _lastSnapshot = DateTime.MinValue, _followUpAt;
    string? _sig;
    bool _longSettle, _locked, _asleep, _screenOff;

    public Watcher(Func<DateTime> now) { _now = now; }

    public Phase State { get; private set; } = Phase.Calm;
    public bool Paused { get; set; }
    public string LastReason { get; private set; } = "";

    /// <summary>Something happened that may scramble windows.</summary>
    public void Disturb(string reason, bool afterWake = false)
    {
        if (Paused) return;
        _lastEvent = _now();
        _longSettle |= afterWake;
        if (State != Phase.Disturbed) Log.Write($"Disturbed: {reason}");
        LastReason = reason;
        State = Phase.Disturbed;
    }

    public void Sleep() { _asleep = true; Disturb("going to sleep"); }
    public void Wake() { _asleep = false; Disturb("woke up", afterWake: true); }
    public void Lock() { _locked = true; Disturb("locked"); }
    public void Unlock() { _locked = false; Disturb("unlocked", afterWake: true); }
    public void ScreenOff() { _screenOff = true; Disturb("screen turned off"); }
    public void ScreenOn() { if (!_screenOff) return; _screenOff = false; Disturb("screen turned on", afterWake: true); }

    /// <summary>Call about once a second with the current monitor signature (ids + resolutions + positions).</summary>
    /// <param name="holdOff">True while something says "not now" (e.g. a fullscreen game is in front).</param>
    public Action Tick(string signature, bool holdOff = false)
    {
        var now = _now();
        if (_sig != signature)
        {
            bool first = _sig == null;
            _sig = signature;
            _lastConfigChange = now;
            if (!first) Disturb("monitors changed");
        }
        if (Paused)
        {
            State = Phase.Calm;
            return Action.None;
        }

        switch (State)
        {
            case Phase.Disturbed:
                if (_locked || _asleep || _screenOff) return Action.None;
                var wait = _longSettle ? SettleAfterWake : Settle;
                if (now - _lastEvent < wait || now - _lastConfigChange < wait) return Action.None;
                if (holdOff) return Action.None;
                State = Phase.Restoring;
                _followUpAt = now + FollowUpAfter;
                _longSettle = false;
                return Action.Restore;

            case Phase.Restoring:
                if (now < _followUpAt) return Action.None;
                State = Phase.Calm;
                _lastSnapshot = now; // next snapshot after a full interval
                return Action.FollowUp;

            default:
                if (now - _lastSnapshot < SnapshotEvery) return Action.None;
                _lastSnapshot = now;
                return Action.Snapshot;
        }
    }
}
