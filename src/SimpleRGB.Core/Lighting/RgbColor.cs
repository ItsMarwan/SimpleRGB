using System.Globalization;

namespace SimpleRGB.Core.Lighting;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);
    public static readonly RgbColor White = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    public override string ToString() => ToHex();

    /// <summary>Parses "#RRGGBB", "RRGGBB", "#RGB" or "RGB". Returns false for anything else.</summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbColor Parse(string? text, RgbColor fallback) => TryParse(text, out var c) ? c : fallback;

    /// <summary>Scales all channels by <paramref name="factor"/> (0..1).</summary>
    public RgbColor Scale(double factor)
    {
        if (factor >= 1) return this;
        if (factor <= 0) return Black;
        return new RgbColor(
            (byte)Math.Round(R * factor),
            (byte)Math.Round(G * factor),
            (byte)Math.Round(B * factor));
    }

    /// <summary>h in [0,1), s and v in [0,1].</summary>
    public static RgbColor FromHsv(double h, double s, double v)
    {
        h -= Math.Floor(h);
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var hh = h * 6.0;
        var i = (int)Math.Floor(hh) % 6;
        var f = hh - Math.Floor(hh);
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return new RgbColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        var s = max == 0 ? 0 : d / max;
        return (h, s, max);
    }
}
