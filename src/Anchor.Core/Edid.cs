using System.Text;

namespace Anchor.Core;

/// <summary>Reads the bits of a monitor's EDID block that identify it.</summary>
public sealed record EdidInfo(string Manufacturer, int ProductCode, uint SerialNumber, string? SerialText, string? Name)
{
    /// <summary>
    /// A stable id for this physical monitor, e.g. "SAM7176-HNMR400123".
    /// Falls back to the numeric serial, then to <paramref name="fallback"/> (used when two
    /// identical monitors both report no serial).
    /// </summary>
    public string MakeId(string fallback)
    {
        string serial = !string.IsNullOrWhiteSpace(SerialText) ? SerialText!
                      : SerialNumber != 0 ? SerialNumber.ToString("X8")
                      : fallback;
        return $"{Manufacturer}{ProductCode:X4}-{Sanitize(serial)}";
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '&') sb.Append(c);
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    public static EdidInfo? Parse(byte[]? e)
    {
        if (e == null || e.Length < 128) return null;
        // Header 00 FF FF FF FF FF FF 00
        if (e[0] != 0 || e[7] != 0) return null;
        for (int i = 1; i < 7; i++) if (e[i] != 0xFF) return null;

        int m = (e[8] << 8) | e[9];
        string mfg = new(new[]
        {
            (char)('A' - 1 + ((m >> 10) & 0x1F)),
            (char)('A' - 1 + ((m >> 5) & 0x1F)),
            (char)('A' - 1 + (m & 0x1F)),
        });
        int product = e[10] | (e[11] << 8);
        uint serial = (uint)(e[12] | (e[13] << 8) | (e[14] << 16) | (e[15] << 24));

        string? name = null, serialText = null;
        for (int off = 54; off <= 108; off += 18)
        {
            if (e[off] != 0 || e[off + 1] != 0) continue; // timing descriptor, not text
            byte tag = e[off + 3];
            string text = ReadText(e, off + 5);
            if (tag == 0xFC) name = text;
            else if (tag == 0xFF) serialText = text;
        }
        return new EdidInfo(mfg, product, serial, serialText, name);
    }

    static string ReadText(byte[] e, int start)
    {
        var sb = new StringBuilder();
        for (int i = start; i < start + 13 && i < e.Length; i++)
        {
            byte b = e[i];
            if (b == 0x0A) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Friendly brand for common EDID manufacturer codes.</summary>
    public static string Brand(string code) => code switch
    {
        "SAM" or "SEC" => "Samsung",
        "AUS" => "ASUS",
        "ACR" => "Acer",
        "DEL" => "Dell",
        "GSM" => "LG",
        "LEN" => "Lenovo",
        "HWP" or "HPN" => "HP",
        "BNQ" => "BenQ",
        "MSI" => "MSI",
        "AOC" => "AOC",
        "PHL" => "Philips",
        "VSC" => "ViewSonic",
        "GBT" => "Gigabyte",
        "SNY" => "Sony",
        "BOE" or "AUO" or "CMN" or "SHP" or "LGD" or "IVO" or "SDC" => "Built-in display",
        _ => code,
    };
}
