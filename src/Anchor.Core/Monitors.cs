using Microsoft.Win32;

namespace Anchor.Core;

/// <summary>Finds the connected monitors and identifies each one by model and serial (EDID).</summary>
public static unsafe class Monitors
{
    static readonly Dictionary<string, (string id, string name)> Cache = new();

    public static List<MonitorInfo> GetAll()
    {
        var handles = new List<nint>();
        Native.EnumDisplayMonitors(0, 0, (h, _, _, _) => { handles.Add(h); return true; }, 0);

        var list = new List<MonitorInfo>();
        foreach (var h in handles)
        {
            var mi = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(h, ref mi)) continue;
            uint dpi = 96;
            try { Native.GetDpiForMonitor(h, 0, out dpi, out _); } catch { }
            var (id, name) = Identify(mi.szDevice);
            list.Add(new MonitorInfo(id, name, Native.ToRect(mi.rcMonitor), Native.ToRect(mi.rcWork),
                (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, (int)dpi));
        }

        // Two monitors that can't be told apart: add their position so they don't clash.
        foreach (var g in list.GroupBy(m => m.Id).Where(g => g.Count() > 1).ToList())
        {
            int i = 0;
            foreach (var m in g.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList())
            {
                list[list.IndexOf(m)] = m with { Id = $"{m.Id}#{++i}" };
            }
        }
        return list;
    }

    /// <summary>"\\.\DISPLAY1" → stable id + friendly name, using the monitor's EDID from the registry.</summary>
    static (string id, string name) Identify(string gdiDevice)
    {
        var dd = new Native.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        string? interfacePath = null;
        for (uint i = 0; Native.EnumDisplayDevices(gdiDevice, i, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0) { interfacePath = dd.DeviceID; break; }
            interfacePath ??= dd.DeviceID;
            dd.cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>();
        }
        if (string.IsNullOrEmpty(interfacePath)) return (gdiDevice, gdiDevice.Replace(@"\\.\", ""));

        lock (Cache)
        {
            if (Cache.TryGetValue(interfacePath, out var hit)) return hit;
            var result = IdentifyFromPath(interfacePath, gdiDevice);
            Cache[interfacePath] = result;
            return result;
        }
    }

    static (string id, string name) IdentifyFromPath(string interfacePath, string gdiDevice)
    {
        // \\?\DISPLAY#SAM7176#5&2b1d0a5&0&UID4352#{e6f07b5f-...}
        var parts = interfacePath.Replace(@"\\?\", "").Split('#');
        if (parts.Length < 3) return (interfacePath, gdiDevice);
        string hwId = parts[1], instance = parts[2];
        string fallback = $"{hwId}-{instance}";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hwId}\{instance}\Device Parameters");
            var edid = EdidInfo.Parse(key?.GetValue("EDID") as byte[]);
            if (edid != null)
            {
                string uid = instance.Contains("UID") ? instance[(instance.LastIndexOf("UID", StringComparison.Ordinal))..] : instance;
                string id = edid.MakeId(uid);
                string brand = EdidInfo.Brand(edid.Manufacturer);
                string model = string.IsNullOrWhiteSpace(edid.Name) ? hwId : edid.Name!;
                string name = brand == "Built-in display" ? brand :
                              model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{brand} {model}";
                return (id, name);
            }
        }
        catch (Exception ex) { Log.Write($"EDID read failed for {hwId}: {ex.Message}"); }
        return (fallback, hwId);
    }

    public static void ClearCache() { lock (Cache) Cache.Clear(); }
}
