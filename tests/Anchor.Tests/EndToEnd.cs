using System.Diagnostics;
using System.Runtime.InteropServices;
using Anchor.Core;

namespace Anchor.Tests;

/// <summary>
/// Runs the real Anchor.exe and does what a game does: switches the screen to a lower resolution,
/// windows get scrambled, then switches back. Anchor should put the window back by itself.
/// Usage: Anchor.Tests --e2e path\to\Anchor.exe
/// </summary>
public static class EndToEnd
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? dev, ref DEVMODE dm, nint hwnd, uint flags, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? dev, nint dm, nint hwnd, uint flags, nint param);

    const int ENUM_CURRENT_SETTINGS = -1;
    const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, CDS_FULLSCREEN = 0x4;

    public static int Run(string anchorExe)
    {
        var t = new Runner();
        Console.WriteLine("End-to-end: real Anchor.exe, game-style resolution change");

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opifex", "Anchor");
        try { Directory.Delete(folder, true); } catch { }

        // Test windows in their own process.
        var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
            Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{typeof(EndToEnd).Assembly.Location}\" --host 1" : "--host 1")
        { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        nint A = (nint)long.Parse(host.StandardOutput.ReadLine()!);

        var mon = Monitors.GetAll().First(m => m.Primary);
        Console.WriteLine($"        screen {mon.Bounds}");
        // Bottom-right corner: a lower resolution can't hold it there.
        var home = new RectI(mon.Work.Right - 420, mon.Work.Bottom - 320, mon.Work.Right - 20, mon.Work.Bottom - 20);
        var scrambled = new RectI(mon.Work.Left + 10, mon.Work.Top + 10, mon.Work.Left + 310, mon.Work.Top + 210);
        Move(A, home);

        var psi = new ProcessStartInfo(anchorExe) { UseShellExecute = false };
        psi.Environment["ANCHOR_TEST_MIN_CALM_SECONDS"] = "6";
        var anchor = Process.Start(psi)!;
        bool changed = false;
        try
        {
            Thread.Sleep(14000); // a few calm snapshots
            t.Test("Anchor is running and has taken snapshots", () =>
            {
                Assert.True(!anchor.HasExited, "Anchor exited");
                Assert.True(File.Exists(Path.Combine(folder, "layouts.json")), "no layouts.json");
            });

            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm);
            uint w0 = dm.dmPelsWidth, h0 = dm.dmPelsHeight;
            dm.dmPelsWidth = 800; dm.dmPelsHeight = 600; dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            int rc = ChangeDisplaySettingsEx(null, ref dm, 0, CDS_FULLSCREEN, 0);
            changed = rc == 0;
            Console.WriteLine($"        switched {w0}x{h0} -> 800x600: {(rc == 0 ? "ok" : $"failed ({rc})")}");
            t.Test("Screen resolution can be changed on this machine", () => Assert.Equal(0, rc, "ChangeDisplaySettingsEx"));
            if (rc != 0) return t.Finish();

            Thread.Sleep(1500);
            Move(A, scrambled); // the "alt-tab chaos"
            Thread.Sleep(9000);
            t.Test("At the game's resolution Anchor leaves windows alone", () => Assert.Near(scrambled, RectOf(A), "A", 4));

            ChangeDisplaySettingsEx(null, 0, 0, 0, 0); // game exits: back to the normal resolution
            changed = false;
            Console.WriteLine("        switched back");
            var sw = Stopwatch.StartNew();
            while (!RectOf(A).Near(home) && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(200);
            Console.WriteLine($"        window back after {sw.Elapsed.TotalSeconds:N1} s");
            t.Test("Back at the normal resolution, Anchor puts the window back by itself", () => Assert.Near(home, RectOf(A), "A"));
            t.Test("...within a few seconds", () => Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"{sw.Elapsed.TotalSeconds:N1} s"));
            Thread.Sleep(5000);
            t.Test("...and it stays there (follow-up pass doesn't fight it)", () => Assert.Near(home, RectOf(A), "A"));
        }
        finally
        {
            if (changed) ChangeDisplaySettingsEx(null, 0, 0, 0, 0);
            try { anchor.Kill(); } catch { }
            try { host.StandardInput.Close(); host.WaitForExit(3000); } catch { }
            try
            {
                Console.WriteLine("        --- Anchor's log ---");
                foreach (var l in File.ReadAllLines(Path.Combine(folder, "anchor.log"))) Console.WriteLine("        " + l);
            }
            catch { }
        }
        t.Test("No unhandled errors in Anchor's log", () =>
            Assert.True(!File.ReadAllText(Path.Combine(folder, "anchor.log")).Contains("Unhandled"), "found Unhandled"));
        return t.Finish();
    }

    static void Move(nint h, RectI r) =>
        Native.SetWindowPos(h, 0, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    static RectI RectOf(nint h) { Native.GetWindowRect(h, out var r); return Native.ToRect(r); }
}
