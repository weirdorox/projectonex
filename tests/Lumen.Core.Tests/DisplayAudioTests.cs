using Lumen.Core.Hardware;

namespace Lumen.Core.Tests;

public class ResolutionChooserTests
{
    private static DisplayMode M(int w, int h, int hz = 60) => new(w, h, hz);

    [Fact]
    public void Keeps_native_aspect_and_drops_tiny_or_odd_modes()
    {
        var modes = new[]
        {
            M(1920, 1200, 120), M(1920, 1200, 60), M(1680, 1050), M(1280, 800, 120), M(1440, 900),
            M(1920, 1080), M(800, 600), M(640, 400),
        };
        var options = ResolutionChooser.Options(modes, M(1920, 1200, 120));
        Assert.Equal(new[] { "1920×1200", "1680×1050", "1440×900", "1280×800" }, options.Select(o => o.Label));
        Assert.True(options[0].IsNative);
        Assert.True(options[0].IsCurrent);
        Assert.Equal(120, options[0].BestRefreshHz);
        Assert.Equal(120, options[^1].BestRefreshHz);
    }

    [Fact]
    public void Current_odd_mode_is_always_listed()
    {
        var options = ResolutionChooser.Options([M(1920, 1200), M(1920, 1080), M(1280, 800)], M(1920, 1080));
        Assert.Contains(options, o => o.Label == "1920×1080" && o.IsCurrent);
    }

    [Fact]
    public void Caps_the_list_and_keeps_native_and_current()
    {
        var modes = Enumerable.Range(0, 12).Select(i => M(2560 - i * 160, (2560 - i * 160) * 10 / 16)).ToList();
        var current = modes[7];
        var options = ResolutionChooser.Options(modes, current, max: 4);
        Assert.Equal(4, options.Count);
        Assert.Contains(options, o => o.IsNative);
        Assert.Contains(options, o => o.IsCurrent);
        Assert.Empty(ResolutionChooser.Options([], null));
    }
}

public class DockDetectorTests
{
    private static DisplayInfo D(string name, bool @internal, bool primary, int w = 1920, int h = 1080) =>
        new(name, name, @internal, primary, w, h, false, false);

    [Fact]
    public void States()
    {
        Assert.Equal(DockState.Handheld, DockDetector.Evaluate([D("panel", true, true)]));
        Assert.Equal(DockState.Handheld, DockDetector.Evaluate([]));
        Assert.Equal(DockState.Docked, DockDetector.Evaluate([D("panel", true, true), D("TV", false, false)]));
        Assert.Equal(DockState.ExternalOnly, DockDetector.Evaluate([D("TV", false, true)]));
    }

    [Fact]
    public void Target_prefers_external_primary_then_largest()
    {
        var a = D("Monitor", false, false, 2560, 1440);
        var b = D("TV", false, false, 3840, 2160);
        Assert.Equal("TV", DockDetector.Target([D("panel", true, true), a, b])!.Name);
        Assert.Equal("Monitor", DockDetector.Target([D("panel", true, false), a with { IsPrimary = true }, b])!.Name);
        Assert.Null(DockDetector.Target([D("panel", true, true)]));
        Assert.StartsWith("Docked", DockDetector.Describe(DockState.Docked, [D("panel", true, true), b]));
    }
}

public class ThemeTests
{
    [Fact]
    public void Every_accent_is_readable()
    {
        foreach (var accent in Lumen.Core.Personalization.Themes.Accents)
        {
            foreach (var oled in new[] { false, true })
            {
                var p = Lumen.Core.Personalization.Themes.Resolve(accent.Id, oled);
                Assert.True(Lumen.Core.Personalization.Themes.ContrastRatio(p.Accent, p.Background) >= 3, $"{accent.Id} on background");
                Assert.True(Lumen.Core.Personalization.Themes.ContrastRatio(p.TextOnAccent, p.Accent) >= 4.5, $"text on {accent.Id}");
            }
        }
    }

    [Fact]
    public void Oled_is_true_black_and_unknown_accent_falls_back()
    {
        var p = Lumen.Core.Personalization.Themes.Resolve("nope", oled: true);
        Assert.Equal(new Lumen.Core.Artwork.RgbColor(0, 0, 0), p.Background);
        Assert.Equal("ember", Lumen.Core.Personalization.Themes.Accent("nope").Id);
        Assert.Equal(21, Lumen.Core.Personalization.Themes.ContrastRatio(new(0, 0, 0), new(255, 255, 255)), 1);
    }
}
