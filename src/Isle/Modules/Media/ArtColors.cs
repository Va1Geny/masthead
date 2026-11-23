using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Modules.Media;

/// <summary>Accent colour from album art, and the placeholder gradient for tracks without art.</summary>
public static class ArtColors
{
    /// <summary>Downsamples to 16×16 and averages the most saturated third of the pixels (BGRA input).</summary>
    public static Color Dominant(BitmapSource art)
    {
        var scaled = new TransformedBitmap(art, new ScaleTransform(16.0 / art.PixelWidth, 16.0 / art.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var w = bgra.PixelWidth; var h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        return Dominant(pixels);
    }

    public static Color Dominant(ReadOnlySpan<byte> bgra)
    {
        var n = bgra.Length / 4;
        if (n == 0) return Fallback;
        var list = new List<(double Score, byte R, byte G, byte B)>(n);
        for (var i = 0; i < n; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < 128) continue;
            var (s, l) = SatLight(r, g, b);
            // prefer saturated, mid-light pixels; near-black and near-white say little about the art's colour
            var score = s * (1 - Math.Abs(l - 0.5) * 1.4);
            list.Add((score, r, g, b));
        }
        if (list.Count == 0) return Fallback;
        list.Sort((x, y) => y.Score.CompareTo(x.Score));
        // weighted average of the clearly colourful pixels (within 60% of the best score, at most a third of the image)
        var cut = list[0].Score * 0.6;
        var limit = Math.Max(1, list.Count / 3);
        double sr = 0, sg = 0, sb = 0, sw = 0;
        for (var i = 0; i < list.Count && i < limit && list[i].Score >= cut; i++)
        {
            var w = list[i].Score;
            sr += list[i].R * w; sg += list[i].G * w; sb += list[i].B * w; sw += w;
        }
        var c = sw > 0 ? Color.FromRgb((byte)(sr / sw), (byte)(sg / sw), (byte)(sb / sw)) : Color.FromRgb(list[0].R, list[0].G, list[0].B);
        return list[0].Score < 0.08 ? Fallback : Readable(c);
    }

    /// <summary>Default soft blue, used for greyscale art.</summary>
    public static readonly Color Fallback = Color.FromRgb(0x5A, 0xA9, 0xFF);

    /// <summary>Keeps the accent usable on both black and white: lightness pulled into 0.45–0.65, saturation ≥ 0.45.</summary>
    public static Color Readable(Color c)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, Math.Max(s, 0.45), Math.Clamp(l, 0.45, 0.65));
    }

    /// <summary>Deterministic two-colour gradient from the title (stable across runs).</summary>
    public static (Color A, Color B) PlaceholderColors(string seed)
    {
        uint hash = 2166136261;
        foreach (var ch in seed ?? "") hash = (hash ^ ch) * 16777619;
        var hue = hash % 360;
        return (FromHsl(hue, 0.55, 0.55), FromHsl((hue + 40) % 360, 0.60, 0.38));
    }

    public static Brush Placeholder(string seed)
    {
        var (a, b) = PlaceholderColors(seed);
        var brush = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    static (double S, double L) SatLight(byte r, byte g, byte b)
    {
        double max = Math.Max(r, Math.Max(g, b)) / 255.0, min = Math.Min(r, Math.Min(g, b)) / 255.0;
        var l = (max + min) / 2;
        var d = max - min;
        var s = d < 1e-6 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (s, l);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d < 1e-6) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(To(r + m), To(g + m), To(b + m));
        static byte To(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}
