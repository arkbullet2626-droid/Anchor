using System.Runtime;
using System.Runtime.InteropServices;

namespace Anchor.Core;

/// <summary>Keeps Anchor small while it sits in the tray.</summary>
public static class Memory
{
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();

    /// <summary>Frees what Anchor isn't using and hands idle memory back to Windows.</summary>
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            if (OperatingSystem.IsWindows()) SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch { }
    }
}
