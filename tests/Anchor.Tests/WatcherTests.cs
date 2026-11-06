using Anchor.Core;

namespace Anchor.Tests;

public static class WatcherTests
{
    sealed class Clock { public DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc); public void Add(double s) => Now = Now.AddSeconds(s); }

    /// <summary>Ticks once a second for <paramref name="seconds"/>, returning every action that came out.</summary>
    static List<(double t, Watcher.Action a)> Run(Watcher w, Clock c, double seconds, string sig, bool holdOff = false)
    {
        var list = new List<(double, Watcher.Action)>();
        for (double t = 0; t < seconds; t += 1)
        {
            c.Add(1);
            var a = w.Tick(sig, holdOff);
            if (a != Watcher.Action.None) list.Add((t + 1, a));
        }
        return list;
    }

    public static void All(Runner t)
    {
        Console.WriteLine("\nWhen to snapshot and restore");

        t.Test("Calm: snapshots every 3 seconds, never restores", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            var acts = Run(w, c, 12, "S");
            Assert.True(acts.All(x => x.a == Watcher.Action.Snapshot), "only snapshots");
            Assert.True(acts.Count is >= 4 and <= 5, $"about 4 snapshots, got {acts.Count}");
        });

        t.Test("Display change: no snapshots while unsettled, restore ~2 s after it's still, follow-up 3 s later", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 5, "S");
            w.Disturb("display changed");
            var acts = Run(w, c, 10, "S");
            Assert.True(!acts.TakeWhile(x => x.a != Watcher.Action.Restore).Any(x => x.a == Watcher.Action.Snapshot), "no snapshot before restore");
            var restore = acts.First(x => x.a == Watcher.Action.Restore);
            Assert.True(restore.t is >= 2 and <= 3, $"restore at {restore.t}s");
            var follow = acts.First(x => x.a == Watcher.Action.FollowUp);
            Assert.True(follow.t - restore.t is >= 3 and <= 4, $"follow-up {follow.t - restore.t}s later");
            Assert.Equal(Watcher.Phase.Calm, w.State, "calm again");
        });

        t.Test("Monitor switching off then on: waits until the monitors stop changing", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 5, "two");
            var a1 = Run(w, c, 1, "one");               // a monitor vanished (noticed by polling)
            var a2 = Run(w, c, 1, "one");
            var a3 = Run(w, c, 1, "two");               // it came back before settling
            var a4 = Run(w, c, 6, "two");
            Assert.True(!a1.Concat(a2).Concat(a3).Any(), "nothing while changing");
            Assert.True(a4.Any(x => x.a == Watcher.Action.Restore && x.t >= 2), "restores after it's been still");
        });

        t.Test("Sleep: nothing at all until wake, then a longer settle (4 s)", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 5, "S");
            w.Sleep();
            Assert.True(!Run(w, c, 30, "S").Any(), "frozen while asleep");
            w.Wake();
            var acts = Run(w, c, 10, "S");
            var restore = acts.First(x => x.a == Watcher.Action.Restore);
            Assert.True(restore.t is >= 4 and <= 5, $"restore at {restore.t}s");
        });

        t.Test("Lock and screen off: frozen until unlocked / screen on", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 5, "S");
            w.Lock(); w.ScreenOff();
            Assert.True(!Run(w, c, 20, "S").Any(), "frozen");
            w.Unlock();
            Assert.True(!Run(w, c, 10, "S").Any(), "still frozen while screen is off");
            w.ScreenOn();
            Assert.True(Run(w, c, 10, "S").Any(x => x.a == Watcher.Action.Restore), "restores once screen is on");
        });

        t.Test("Screen-on with no screen-off first is ignored (Windows sends it at start-up)", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 2, "S");
            w.ScreenOn();
            Assert.Equal(Watcher.Phase.Calm, w.State, "still calm");
        });

        t.Test("A fullscreen game in front holds the restore until it's gone", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 5, "S");
            w.Disturb("display changed");
            Assert.True(!Run(w, c, 20, "S", holdOff: true).Any(), "waits");
            Assert.True(Run(w, c, 2, "S").Any(x => x.a == Watcher.Action.Restore), "restores when the game is gone");
        });

        t.Test("Paused: ignores everything; resuming doesn't trigger a restore", () =>
        {
            var c = new Clock(); var w = new Watcher(() => c.Now);
            Run(w, c, 3, "S");
            w.Paused = true;
            w.Disturb("display changed");
            Assert.True(!Run(w, c, 5, "G").Any(), "nothing while paused");
            w.Paused = false;
            var acts = Run(w, c, 6, "G");
            Assert.True(acts.All(x => x.a == Watcher.Action.Snapshot), "just snapshots after resuming");
        });

        Console.WriteLine("\nWhich layout an automatic restore uses");

        static SetupMemory Mem(params (string sig, double calm)[] arrs)
        {
            var m = new SetupMemory();
            foreach (var (sig, calm) in arrs) m.Arrangements[sig] = new Arrangement { Auto = new Snapshot { Signature = sig }, CalmSeconds = calm };
            return m;
        }

        t.Test("Restores into the usual arrangement", () =>
            Assert.Equal("S", Engine.ChooseAuto(Mem(("S", 5000), ("G", 900)), "S", out _)?.Signature, "snapshot"));

        t.Test("Leaves a game's temporary resolution alone", () =>
        {
            Assert.True(Engine.ChooseAuto(Mem(("S", 5000), ("G", 900)), "G", out var why) == null, "should skip");
            Assert.True(why.Contains("usual"), why);
        });

        t.Test("Doesn't trust an arrangement seen only briefly (one monitor waking first)", () =>
            Assert.True(Engine.ChooseAuto(Mem(("one", 6)), "one", out _) == null, "should skip"));

        t.Test("Nothing remembered for this arrangement: skips quietly", () =>
            Assert.True(Engine.ChooseAuto(Mem(("S", 5000)), "new", out _) == null, "should skip"));
    }
}
