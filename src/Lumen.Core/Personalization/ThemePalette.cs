using Lumen.Core.Artwork;

namespace Lumen.Core.Personalization;

public sealed record AccentChoice(string Id, string Name, RgbColor Color);

/// <summary>All colours the shell paints with. Alpha in 0..255 for the soft accent.</summary>
public sealed record ThemePalette(
    RgbColor Background,
    RgbColor Surface,
    RgbColor SurfaceHigh,
    RgbColor Stroke,
    RgbColor Accent,
    byte AccentSoftAlpha,
    RgbColor TextOnAccent);

/// <summary>Accent colours and the OLED (true black) variant. Pure and tested (contrast).</summary>
public static class Themes
{
    public static readonly IReadOnlyList<AccentChoice> Accents =
    [
        new("ember", "Ember", new RgbColor(0xFF, 0x8A, 0x3D)),
        new("aurora", "Aurora", new RgbColor(0x3D, 0xD6, 0xC1)),
        new("sky", "Sky", new RgbColor(0x5A, 0xA9, 0xFF)),
        new("violet", "Violet", new RgbColor(0xA7, 0x8B, 0xFA)),
        new("rose", "Rose", new RgbColor(0xFF, 0x6F, 0xA8)),
        new("lime", "Lime", new RgbColor(0xA3, 0xE6, 0x35)),
    ];

    public static AccentChoice Accent(string? id) =>
        Accents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Accents[0];

    public static ThemePalette Resolve(string? accentId, bool oled)
    {
        var accent = Accent(accentId).Color;
        var dark = new RgbColor(0x0A, 0x0C, 0x11);
        return new ThemePalette(
            Background: oled ? new RgbColor(0, 0, 0) : dark,
            Surface: oled ? new RgbColor(0x0B, 0x0D, 0x12) : new RgbColor(0x14, 0x18, 0x21),
            SurfaceHigh: oled ? new RgbColor(0x15, 0x18, 0x20) : new RgbColor(0x1D, 0x22, 0x30),
            Stroke: oled ? new RgbColor(0x22, 0x27, 0x33) : new RgbColor(0x2A, 0x31, 0x42),
            Accent: accent,
            AccentSoftAlpha: 0x33,
            TextOnAccent: ContrastRatio(accent, dark) >= 4.5 ? dark : new RgbColor(0xFF, 0xFF, 0xFF));
    }

    /// <summary>WCAG relative-luminance contrast ratio (1..21).</summary>
    public static double ContrastRatio(RgbColor a, RgbColor b)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double L(RgbColor c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (l1, l2) = (L(a), L(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
