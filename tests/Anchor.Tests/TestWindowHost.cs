using System.Runtime.InteropServices;

namespace Anchor.Tests;

/// <summary>Opens N ordinary top-level windows in its own process, prints their handles,
/// and keeps them alive until stdin closes. The tests move these windows from another process,
/// the same way Anchor moves real apps.</summary>
public static class TestWindowHost
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public nint hInstance; public nint hIcon; public nint hCursor; public nint hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; }

    delegate nint WndProc(nint h, uint m, nint w, nint l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern nint DefWindowProc(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, nint h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);

    static WndProc? _proc; // kept alive for the life of the windows

    public static int Run(int n)
    {
        _proc = (h, m, w, l) => DefWindowProc(h, m, w, l);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hbrBackground = (nint)6, // COLOR_WINDOW + 1
            lpszClassName = "AnchorTestWindow",
        };
        RegisterClassEx(ref wc);

        const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        for (int i = 0; i < n; i++)
        {
            nint h = CreateWindowEx(0, "AnchorTestWindow", $"Anchor test window {(char)('A' + i)}", WS_OVERLAPPEDWINDOW,
                80 + i * 40, 80 + i * 40, 400, 300, 0, 0, wc.hInstance, 0);
            ShowWindow(h, 4 /* SW_SHOWNOACTIVATE */);
            Console.WriteLine(h.ToInt64());
        }
        Console.Out.Flush();

        uint tid = GetCurrentThreadId();
        new Thread(() => { try { Console.In.ReadToEnd(); } catch { } PostThreadMessage(tid, 0x0012 /* WM_QUIT */, 0, 0); })
        { IsBackground = true }.Start();

        while (GetMessage(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        return 0;
    }
}
