using System.Net.Http.Headers;
using System.Text.Json;
using Anchor.Core;

namespace Anchor;

/// <summary>
/// The only thing Anchor ever does online, and only if "Check for updates" is on:
/// asks GitHub for the newest release number of Anchor. Nothing about you or your PC is sent.
/// </summary>
static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/arkbullet2626-droid/Anchor/releases/latest";
    const string Api = "https://api.github.com/repos/arkbullet2626-droid/Anchor/releases/latest";

    /// <summary>Returns the newer version number (e.g. "1.0.1") or null if Anchor is up to date or the check failed.</summary>
    public static async Task<string?> NewerVersionAsync(string current)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Anchor-update-check", current));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await http.GetAsync(Api);
            if (!resp.IsSuccessStatusCode) { Log.Write($"Update check: HTTP {(int)resp.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            return IsNewer(tag, current) ? tag.TrimStart('v', 'V') : null;
        }
        catch (Exception ex) { Log.Write("Update check failed: " + ex.Message); return null; }
    }

    public static bool IsNewer(string tag, string current) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var latest) &&
        Version.TryParse(current, out var mine) && latest > mine;
}
