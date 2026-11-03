namespace Lumen.Core.Artwork;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Ink { get; } = new(10, 12, 17);

    /// <summary>
    /// A version of the colour suitable as a large background tint under white text: same hue,
    /// saturation capped, brightness clamped into a dark band.
    /// </summary>
    public RgbColor ForBackdrop()
    {
        var (h, s, v) = ArtworkPalette.ToHsv(this);
        return ArtworkPalette.FromHsv(h, Math.Min(s, 0.75), Math.Clamp(v, 0.22, 0.42));
    }
}

/// <summary>
/// Picks a representative accent colour from a small thumbnail (BGRA8 pixels). Prefers vivid,
/// mid-bright colours (what a designer would pick from key art), falling back to the average colour
/// for greyscale images. Pure code - unit tested; the Windows layer only decodes the thumbnail.
/// </summary>
public static class ArtworkPalette
{
    private const int HueBuckets = 24;

    public static RgbColor Dominant(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var count = width * height;
        if (count <= 0 || bgra.Length < count * 4) return RgbColor.Ink;

        Span<double> weight = stackalloc double[HueBuckets];
        Span<double> sumR = stackalloc double[HueBuckets];
        Span<double> sumG = stackalloc double[HueBuckets];
        Span<double> sumB = stackalloc double[HueBuckets];
        double avgR = 0, avgG = 0, avgB = 0;
        var used = 0;

        for (var i = 0; i < count; i++)
        {
            int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < 128) continue;
            used++;
            avgR += r; avgG += g; avgB += b;

            var (h, s, v) = ToHsv(new RgbColor((byte)r, (byte)g, (byte)b));
            if (s < 0.25 || v < 0.18) continue; // greys, near-white and near-black

            // Vivid, mid-bright pixels count most.
            var w = s * s * (1.0 - Math.Abs(v - 0.65));
            var bucket = (int)(h / 360.0 * HueBuckets) % HueBuckets;
            weight[bucket] += w;
            sumR[bucket] += r * w; sumG[bucket] += g * w; sumB[bucket] += b * w;
        }

        if (used == 0) return RgbColor.Ink;

        var best = -1;
        double bestWeight = 0, total = 0;
        for (var k = 0; k < HueBuckets; k++)
        {
            // Smooth with neighbours so a hue split across two buckets still wins.
            var smoothed = weight[k] + 0.5 * (weight[(k + 1) % HueBuckets] + weight[(k + HueBuckets - 1) % HueBuckets]);
            total += weight[k];
            if (smoothed > bestWeight) { bestWeight = smoothed; best = k; }
        }

        // Not enough colour in the image: use the plain average.
        if (best < 0 || total < used * 0.02)
            return new RgbColor((byte)(avgR / used), (byte)(avgG / used), (byte)(avgB / used));

        return new RgbColor(
            (byte)Math.Round(sumR[best] / weight[best]),
            (byte)Math.Round(sumG[best] / weight[best]),
            (byte)Math.Round(sumB[best] / weight[best]));
    }

    public static (double H, double S, double V) ToHsv(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        var s = max == 0 ? 0 : d / max;
        return (h, s, max);
    }

    public static RgbColor FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60d => (c, x, 0d),
            < 120d => (x, c, 0d),
            < 180d => (0d, c, x),
            < 240d => (0d, x, c),
            < 300d => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new RgbColor((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
