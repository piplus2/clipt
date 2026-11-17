using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Clipt.Services;

public static partial class ColorParser
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();

        var hex = HexRegex().Match(t);
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            if (h.Length is 3 or 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            uint v = uint.Parse(h, NumberStyles.HexNumber);
            color = h.Length == 6
                ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8)); // CSS #RRGGBBAA
            return true;
        }

        var rgb = RgbRegex().Match(t);
        if (rgb.Success)
        {
            byte r = Clamp(rgb.Groups[1].Value), g = Clamp(rgb.Groups[2].Value), b = Clamp(rgb.Groups[3].Value);
            byte a = 255;
            if (rgb.Groups[4].Success && double.TryParse(rgb.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var av))
                a = (byte)Math.Round(Math.Clamp(av <= 1 ? av * 255 : av, 0, 255));
            color = Color.FromArgb(a, r, g, b);
            return true;
        }
        return false;
    }

    public static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;
        double d = max - min;
        if (d == 0) s = 0;
        else
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s * 100, l * 100);
    }

    private static byte Clamp(string s) =>
        (byte)Math.Clamp(double.Parse(s, CultureInfo.InvariantCulture), 0, 255);

    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3}(?:\.\d+)?)\s*[, ]\s*(\d{1,3}(?:\.\d+)?)\s*[, ]\s*(\d{1,3}(?:\.\d+)?)\s*(?:[,/]\s*(\d*\.?\d+))?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbRegex();
}
