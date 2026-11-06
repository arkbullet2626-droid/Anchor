using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anchor.Core;

/// <summary>Keeps one memory per monitor setup in layouts.json in Anchor's folder.</summary>
public sealed class LayoutStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly object _gate = new();
    Dictionary<string, SetupMemory> _setups = new();

    public LayoutStore(string folder)
    {
        _path = Path.Combine(folder, "layouts.json");
        Load();
    }

    public SetupMemory Get(string key, IReadOnlyList<MonitorInfo> mons)
    {
        lock (_gate)
        {
            if (!_setups.TryGetValue(key, out var m))
                _setups[key] = m = new SetupMemory();
            m.Name = Geometry.SetupName(mons);
            m.Monitors = mons.ToList();
            m.LastSeenUtc = DateTime.UtcNow;
            return m;
        }
    }

    public int Count { get { lock (_gate) return _setups.Count; } }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_setups, Json));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Log.Write($"Couldn't save layouts: {ex.Message}"); }
        }
    }

    void Load()
    {
        try
        {
            if (File.Exists(_path))
                _setups = JsonSerializer.Deserialize<Dictionary<string, SetupMemory>>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"layouts.json unreadable, starting fresh: {ex.Message}");
            try { File.Move(_path, _path + ".bad", overwrite: true); } catch { }
            _setups = new();
        }
    }
}
