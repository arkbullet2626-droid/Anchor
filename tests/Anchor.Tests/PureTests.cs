using Anchor.Core;

namespace Anchor.Tests;

public static class PureTests
{
    static MonitorInfo Mon(string id, int left, int w, int h, bool primary = false, int taskbar = 48) =>
        new(id, id, new RectI(left, 0, left + w, h), new RectI(left, 0, left + w, h - taskbar), primary, 96);

    static WindowEntry Win(long hwnd, int pid, string exe, string cls, string title, int z = 0) =>
        new() { Hwnd = hwnd, Pid = pid, Exe = exe, Class = cls, Title = title, Z = z };

    public static void All(Runner t)
    {
        Console.WriteLine("Pure logic");

        t.Test("EDID: reads maker, model name and serial", () =>
        {
            var e = MakeEdid("SAM", 0x7176, 0, "LC32G7xT", "HNMR400123");
            var info = EdidInfo.Parse(e)!;
            Assert.Equal("SAM", info.Manufacturer, "maker");
            Assert.Equal(0x7176, info.ProductCode, "product");
            Assert.Equal("LC32G7xT", info.Name, "name");
            Assert.Equal("SAM7176-HNMR400123", info.MakeId("UID1"), "id");
        });

        t.Test("EDID: numeric serial used when there's no serial text", () =>
        {
            var info = EdidInfo.Parse(MakeEdid("AUS", 0x27F3, 0x1A2B3C, "VG279QM", null))!;
            Assert.Equal("AUS27F3-001A2B3C", info.MakeId("UID9"), "id");
        });

        t.Test("EDID: falls back to port id when the monitor has no serial at all", () =>
        {
            var info = EdidInfo.Parse(MakeEdid("ACR", 0x0601, 0, "XF240H", null))!;
            Assert.Equal("ACR0601-UID4352", info.MakeId("UID4352"), "id");
        });

        t.Test("EDID: rejects junk", () =>
        {
            Assert.True(EdidInfo.Parse(new byte[10]) == null, "short");
            Assert.True(EdidInfo.Parse(new byte[128]) == null, "bad header");
        });

        t.Test("Setup key ignores order and resolution", () =>
        {
            var a = new[] { Mon("SAM1", 0, 2560, 1440, true), Mon("AUS1", 2560, 1920, 1080) };
            var b = new[] { Mon("AUS1", -1920, 1280, 720), Mon("SAM1", 0, 1920, 1080, true) };
            Assert.Equal(Geometry.SetupKey(a), Geometry.SetupKey(b), "key");
        });

        t.Test("Map: same size monitor that moved keeps the same spot on it", () =>
        {
            var saved = Mon("A", 2560, 1920, 1080);
            var now = Mon("A", -1920, 1920, 1080);
            var r = Geometry.Map(new RectI(100, 50, 900, 650), saved, now);
            Assert.Near(new RectI(-1820, 50, -1020, 650), r, "rect");
        });

        t.Test("Map: lower resolution scales and stays inside the work area", () =>
        {
            var saved = Mon("A", 0, 2560, 1440);
            var now = Mon("A", 0, 1280, 720);
            var r = Geometry.Map(new RectI(1280, 720, 2560, 1440), saved, now);
            Assert.True(r.Right <= now.Work.Right && r.Bottom <= now.Work.Bottom, $"inside work area: {r}");
            Assert.True(r.Width > 500, "kept a sensible size");
        });

        t.Test("KeepInside pulls an off-screen window back", () =>
        {
            var r = Geometry.KeepInside(new RectI(5000, -300, 5800, 300), new RectI(0, 0, 1920, 1040));
            Assert.Near(new RectI(1120, 0, 1920, 600), r, "rect");
        });

        t.Test("MonitorFor picks the monitor with most overlap, or the nearest", () =>
        {
            var mons = new[] { Mon("L", 0, 1920, 1080, true), Mon("R", 1920, 1920, 1080) };
            Assert.Equal("R", Geometry.MonitorFor(new RectI(1800, 0, 2600, 500), mons)!.Id, "overlap");
            Assert.Equal("R", Geometry.MonitorFor(new RectI(9000, 0, 9100, 100), mons)!.Id, "nearest");
        });

        t.Test("Matcher: same window handle wins", () =>
        {
            var saved = new[] { Win(10, 1, "chrome.exe", "Chrome_WidgetWin_1", "A"), Win(11, 1, "chrome.exe", "Chrome_WidgetWin_1", "B") };
            var live = new[] { Win(11, 1, "chrome.exe", "Chrome_WidgetWin_1", "changed"), Win(10, 1, "chrome.exe", "Chrome_WidgetWin_1", "also changed") };
            var m = Matcher.Match(saved, live, allowLooseMatch: false);
            Assert.Equal(2, m.Count, "pairs");
            Assert.True(m.All(p => p.saved.Hwnd == p.live.Hwnd), "by handle");
        });

        t.Test("Matcher: a reused handle from another app doesn't match", () =>
        {
            var m = Matcher.Match(new[] { Win(10, 1, "notepad.exe", "Notepad", "x") },
                                  new[] { Win(10, 2, "discord.exe", "Chrome_WidgetWin_1", "x") }, false);
            Assert.Equal(0, m.Count, "pairs");
        });

        t.Test("Matcher: after a restart, matches by title, then by app in stacking order", () =>
        {
            var saved = new[]
            {
                Win(1, 1, "chrome.exe", "Chrome_WidgetWin_1", "Mail", 0),
                Win(2, 1, "chrome.exe", "Chrome_WidgetWin_1", "Docs", 1),
                Win(3, 1, "Discord.exe", "Chrome_WidgetWin_1", "Discord", 2),
                Win(4, 1, "steam.exe", "SDL_app", "Steam", 3),
            };
            var live = new[]
            {
                Win(50, 9, "chrome.exe", "Chrome_WidgetWin_1", "News", 0),
                Win(51, 9, "CHROME.EXE", "Chrome_WidgetWin_1", "Mail", 1),
                Win(52, 8, "discord.exe", "Chrome_WidgetWin_1", "Discord", 2),
            };
            var m = Matcher.Match(saved, live, allowLooseMatch: true);
            Assert.Equal(3, m.Count, "pairs (Steam isn't open, skipped quietly)");
            Assert.Equal(51L, m.First(p => p.saved.Title == "Mail").live.Hwnd, "Mail by title");
            Assert.Equal(50L, m.First(p => p.saved.Title == "Docs").live.Hwnd, "Docs by app");
            Assert.Equal(52L, m.First(p => p.saved.Exe == "Discord.exe").live.Hwnd, "Discord");
        });

        t.Test("Matcher: strict mode never guesses", () =>
        {
            var m = Matcher.Match(new[] { Win(1, 1, "a.exe", "C", "T") }, new[] { Win(2, 1, "a.exe", "C", "T") }, false);
            Assert.Equal(0, m.Count, "pairs");
        });

        t.Test("Layout store saves and loads per setup", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "anchor-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var mons = new List<MonitorInfo> { Mon("SAM1", 0, 2560, 1440, true) };
                var s = new LayoutStore(dir);
                var mem = s.Get("SAM1", mons);
                mem.Saved = new Snapshot { SetupKey = "SAM1", Monitors = mons, Windows = { Win(5, 1, "a.exe", "C", "T") } };
                mem.Saved.Windows[0].State = ShowState.Maximized;
                s.Save();
                var again = new LayoutStore(dir).Get("SAM1", mons);
                Assert.Equal(1, again.Saved!.Windows.Count, "windows");
                Assert.Equal(ShowState.Maximized, again.Saved.Windows[0].State, "state");
                Assert.Equal(2560, again.Monitors[0].Bounds.Width, "monitor");

                File.WriteAllText(Path.Combine(dir, "layouts.json"), "{ not json");
                Assert.Equal(0, new LayoutStore(dir).Count, "broken file starts fresh");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        t.Test("Restore summary reads like plain English", () =>
        {
            var r = new RestoreResult { Moved = 3 };
            r.SkippedAdmin.Add("Taskmgr");
            Assert.Equal("Put 3 windows back. Skipped 1 admin window (Taskmgr).", r.Summary(), "summary");
            Assert.Equal("Everything was already in place.", new RestoreResult().Summary(), "nothing to do");
        });
    }

    static byte[] MakeEdid(string mfg, int product, uint serial, string? name, string? serialText)
    {
        var e = new byte[128];
        e[0] = 0; for (int i = 1; i < 7; i++) e[i] = 0xFF; e[7] = 0;
        int m = ((mfg[0] - 'A' + 1) << 10) | ((mfg[1] - 'A' + 1) << 5) | (mfg[2] - 'A' + 1);
        e[8] = (byte)(m >> 8); e[9] = (byte)m;
        e[10] = (byte)product; e[11] = (byte)(product >> 8);
        e[12] = (byte)serial; e[13] = (byte)(serial >> 8); e[14] = (byte)(serial >> 16); e[15] = (byte)(serial >> 24);
        e[54] = 1; // first descriptor is a timing block
        void Text(int off, byte tag, string s)
        {
            e[off + 3] = tag;
            for (int i = 0; i < 13; i++) e[off + 5 + i] = i < s.Length ? (byte)s[i] : i == s.Length ? (byte)0x0A : (byte)0x20;
        }
        if (name != null) Text(72, 0xFC, name);
        if (serialText != null) Text(90, 0xFF, serialText);
        return e;
    }
}
