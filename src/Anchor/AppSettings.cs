using System.Text.Json;
using Anchor.Core;

namespace Anchor;

/// <summary>Anchor's settings, kept in settings.json in Anchor's folder.</summary>
sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Start as administrator at sign-in (via a scheduled task) so admin windows can be moved too.</summary>
    public bool StartAsAdmin { get; set; }
    public bool ShowRestoreNotes { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public string? LastUpdateCheckUtc { get; set; }
    public string? UpdateNotifiedVersion { get; set; }

    static string PathFor(string folder) => Path.Combine(folder, "settings.json");

    public static AppSettings Load(string folder, out bool firstRun)
    {
        firstRun = !File.Exists(PathFor(folder));
        try
        {
            if (!firstRun) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFor(folder))) ?? new();
        }
        catch (Exception ex) { Log.Write("settings.json unreadable, using defaults: " + ex.Message); }
        return new AppSettings();
    }

    /// <summary>Applies the "start with Windows" choice made in the installer, if there is one.</summary>
    public bool TakeInstallerChoice(string folder)
    {
        string f = Path.Combine(folder, "installer-startup.txt");
        try
        {
            if (!File.Exists(f)) return false;
            bool on = File.ReadAllText(f).Trim() == "true";
            File.Delete(f);
            StartWithWindows = on;
            Log.Write($"Installer choice: start with Windows = {on}");
            return true;
        }
        catch { return false; }
    }

    public void Save(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(PathFor(folder), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Couldn't save settings: " + ex.Message); }
    }
}
