using System.Diagnostics;
using System.Runtime.InteropServices;
using Anchor.Core;

namespace Anchor.Tests;

/// <summary>Moves real windows owned by another process and checks Anchor puts them back.</summary>
public static class RealWindowTests
{
    public static void All(Runner t)
    {
        Console.WriteLine("\nReal windows (Windows only)");

        var mons = Monitors.GetAll();
        foreach (var m in mons)
            Console.WriteLine($"        monitor: {m.Name} [{m.Id}] {m.Bounds} work {m.Work} dpi {m.Dpi}{(m.Primary ? " primary" : "")}");
        t.Test("Finds at least one monitor with an id", () =>
        {
            Assert.True(mons.Count > 0, "no monitors");
            Assert.True(mons.All(m => m.Id.Length > 0 && !m.Bounds.IsEmpty), "empty id or bounds");
            Assert.Equal(1, mons.Count(m => m.Primary), "exactly one primary");
        });

        var host = StartHost(4, out var hwnds, out int pid);
        string dir = Path.Combine(Path.GetTempPath(), "anchor-it-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = mons.First(m => m.Primary);
            var w = p.Work;
            RectI R(int x, int y, int ww, int hh) => new(w.Left + x, w.Top + y, w.Left + x + ww, w.Top + y + hh);

            nint A = hwnds[0], B = hwnds[1], C = hwnds[2], D = hwnds[3];
            var aRect = R(60, 40, 420, 300);
            var cNormal = R(150, 120, 380, 260);
            var dNormal = R(200, 150, 300, 200);
            var dSnapped = new RectI(w.Left, w.Top, w.Left + w.Width / 2, w.Bottom);

            Place(A, ShowState.Normal, aRect, aRect);
            Place(B, ShowState.Maximized, R(100, 100, 400, 300), R(100, 100, 400, 300));
            Place(C, ShowState.Minimized, cNormal, cNormal);
            Place(D, ShowState.Normal, dSnapped, dNormal); // like a window snapped to the left half
            Thread.Sleep(400);

            var engine = new Engine(dir);
            var snap = OnlyTestWindows(engine.Capture(), pid);

            t.Test("Snapshot sees the test windows with the right states", () =>
            {
                Assert.Equal(4, snap.Windows.Count, "test windows found");
                Assert.Equal(ShowState.Normal, Get(snap, A).State, "A");
                Assert.Equal(ShowState.Maximized, Get(snap, B).State, "B");
                Assert.Equal(ShowState.Minimized, Get(snap, C).State, "C");
            });

            // Scramble everything, like a monitor coming back from sleep.
            var aScrambled = R(10, 10, 300, 200);
            Place(A, ShowState.Normal, aScrambled, aScrambled);
            Place(B, ShowState.Normal, R(30, 30, 300, 200), R(30, 30, 300, 200));
            Place(C, ShowState.Normal, R(40, 40, 300, 200), R(40, 40, 300, 200));
            Place(D, ShowState.Normal, R(50, 50, 300, 200), R(50, 50, 300, 200));
            Thread.Sleep(300);

            var result = engine.Restore(snap, looseMatch: false);
            WaitFor(() => Native.IsZoomed(B) && Native.IsIconic(C) && RectOf(A).Near(aRect) && RectOf(D).Near(dSnapped), 3000);

            t.Test("Restore puts a normal window back exactly", () => Assert.Near(aRect, RectOf(A), "A"));
            t.Test("Restore re-maximizes a maximized window", () => Assert.True(Native.IsZoomed(B), "B not maximized"));
            t.Test("Restore re-minimizes a minimized window with its restore size", () =>
            {
                Assert.True(Native.IsIconic(C), "C not minimized");
                Assert.Near(cNormal, NormalOf(C), "C restore rect");
            });
            t.Test("Restore puts a half-screen window back", () => Assert.Near(dSnapped, RectOf(D), "D rect"));
            t.Test("Restore reports what it did", () => Assert.Equal(4, result.Moved, $"moved ({result.Summary()})"));

            t.Test("Restoring again changes nothing", () =>
            {
                var again = engine.Restore(snap, looseMatch: false);
                Assert.Equal(0, again.Moved, "moved");
                Assert.Equal(4, again.AlreadyRight, "already right");
            });

            // Restore once more from a scrambled state, then undo it.
            Place(A, ShowState.Normal, aScrambled, aScrambled);
            Thread.Sleep(200);
            engine.Restore(snap, looseMatch: false);
            WaitFor(() => RectOf(A).Near(aRect), 2000);
            var undo = engine.UndoLast();
            WaitFor(() => RectOf(A).Near(aScrambled), 2000);
            t.Test("Undo puts windows back to just before the restore", () =>
            {
                Assert.True(undo != null, "nothing to undo");
                Assert.Near(aScrambled, RectOf(A), "A after undo");
                Assert.True(Native.IsZoomed(B), "B untouched by undo (it was already right)");
                Assert.True(engine.LastUndo == null, "undo is one step");
            });

            t.Test("A saved layout still applies after an app restart (matched by title)", () =>
            {
                var old = OnlyTestWindows(engine.Capture(), pid);
                var e = Get(old, A);
                e.Hwnd = 999999; e.Pid = 1; // pretend the handle belonged to a previous run
                e.Rect = Geometry.ToRelative(aRect, mons.First(m => m.Id == e.MonitorId));
                e.Normal = e.Rect;
                var r = engine.Restore(new Snapshot { SetupKey = old.SetupKey, Monitors = old.Monitors, Windows = { e } }, looseMatch: true);
                WaitFor(() => RectOf(A).Near(aRect), 2000);
                if (!RectOf(A).Near(aRect)) Console.WriteLine(TailLog(dir));
                Assert.Near(aRect, RectOf(A), $"A ({r.Summary()})");
            });

            t.Test("Windows that aren't open are skipped quietly", () =>
            {
                var ghost = Get(snap, A).Clone();
                ghost.Hwnd = 123457; ghost.Title = "Not open"; ghost.Exe = "nothing.exe";
                var r = engine.Restore(new Snapshot { SetupKey = snap.SetupKey, Monitors = snap.Monitors, Windows = { ghost } }, true);
                Assert.Equal(0, r.Moved, "moved");
                Assert.Equal(1, r.NotOpen, "not open");
            });

            t.Test("Save layout writes layouts.json and Restore now uses it", () =>
            {
                engine.SaveLayout();
                Assert.True(File.Exists(Path.Combine(dir, "layouts.json")), "file");
                Assert.True(engine.HasSavedLayout(), "saved");
            });

            t.Test("Anchor's own snapshots skip its own process", () =>
            {
                var s = engine.Capture();
                Assert.True(s.Windows.All(x => x.Pid != Environment.ProcessId), "own windows listed");
            });
        }
        finally
        {
            try { host.StandardInput.Close(); if (!host.WaitForExit(3000)) host.Kill(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static Process StartHost(int n, out List<nint> hwnds, out int pid)
    {
        // Started as "dotnet Anchor.Tests.dll" or as "Anchor.Tests.exe": relaunch the same way.
        string exe = Environment.ProcessPath!;
        string argsText = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{typeof(RealWindowTests).Assembly.Location}\" --host {n}"
            : $"--host {n}";
        var psi = new ProcessStartInfo(exe, argsText)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        var p = Process.Start(psi)!;
        hwnds = new List<nint>();
        for (int i = 0; i < n; i++)
        {
            string? line = p.StandardOutput.ReadLine();
            if (line == null || !long.TryParse(line, out long h))
                throw new Exception($"Test window helper didn't start (said: {line ?? "nothing"})");
            hwnds.Add((nint)h);
        }
        pid = p.Id;
        Thread.Sleep(300);
        return p;
    }

    static Snapshot OnlyTestWindows(Snapshot s, int pid) =>
        new() { TakenUtc = s.TakenUtc, SetupKey = s.SetupKey, Monitors = s.Monitors, Windows = s.Windows.Where(w => w.Pid == pid).ToList() };

    static WindowEntry Get(Snapshot s, nint h) => s.Windows.First(w => w.Hwnd == h);

    static RectI RectOf(nint h) { Native.GetWindowRect(h, out var r); return Native.ToRect(r); }

    static RectI NormalOf(nint h)
    {
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(h, ref wp);
        var p = Monitors.GetAll().First(m => m.Primary);
        return Native.ToRect(wp.rcNormalPosition).Offset(p.Work.Left - p.Bounds.Left, p.Work.Top - p.Bounds.Top);
    }

    /// <summary>Puts a test window somewhere directly, without Anchor.</summary>
    static void Place(nint h, ShowState state, RectI rect, RectI normal)
    {
        var p = Monitors.GetAll().First(m => m.Primary);
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(h, ref wp);
        wp.flags = 0;
        wp.rcNormalPosition = Native.ToRECT(normal.Offset(-(p.Work.Left - p.Bounds.Left), -(p.Work.Top - p.Bounds.Top)));
        wp.showCmd = state switch
        {
            ShowState.Maximized => Native.SW_SHOWMAXIMIZED,
            ShowState.Minimized => Native.SW_SHOWMINNOACTIVE,
            _ => Native.SW_SHOWNOACTIVATE,
        };
        Native.SetWindowPlacement(h, ref wp);
        if (state == ShowState.Normal && !rect.Near(normal))
            Native.SetWindowPos(h, 0, rect.Left, rect.Top, rect.Width, rect.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    static string TailLog(string dir)
    {
        try { return string.Join("\n", File.ReadAllLines(Path.Combine(dir, "anchor.log")).TakeLast(15).Select(l => "        log: " + l)); }
        catch { return "        (no log)"; }
    }

    static void WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(50);
    }
}
