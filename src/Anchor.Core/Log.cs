namespace Anchor.Core;

/// <summary>A small rolling log in Anchor's folder (at most about 1 MB in total).</summary>
public static class Log
{
    const long MaxBytes = 512 * 1024;
    static readonly object Gate = new();
    public static string? Folder { get; set; }

    public static void Write(string msg)
    {
        try
        {
            if (Folder == null) return;
            lock (Gate)
            {
                string path = Path.Combine(Folder, "anchor.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(path, Path.Combine(Folder, "anchor.old.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break anything */ }
    }
}
