using Lumen.Core.Artwork;
using Lumen.Core.Diagnostics;
using Lumen.Core.Personalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Lumen.App.Services;

/// <summary>
/// Applies accent colour, OLED black and UI sounds at runtime by recolouring the shared theme brushes
/// (every control that uses them updates immediately - no restart).
/// </summary>
public static class ThemeService
{
    public static void Apply(Lumen.Core.ShellSettings settings)
    {
        try
        {
            var p = Themes.Resolve(settings.AccentId, settings.OledTheme);
            Set("LumenBackgroundBrush", p.Background);
            Set("LumenSurfaceBrush", p.Surface);
            Set("LumenSurfaceHighBrush", p.SurfaceHigh);
            Set("LumenStrokeBrush", p.Stroke);
            Set("LumenAccentBrush", p.Accent);
            Set("LumenAccentSoftBrush", p.Accent, p.AccentSoftAlpha);

            // Built-in controls (toggles, sliders, accent buttons) read these theme brushes.
            foreach (var dict in AllDictionaries(Application.Current.Resources))
            {
                foreach (var theme in new[] { "Dark", "Default" })
                {
                    if (!dict.ThemeDictionaries.TryGetValue(theme, out var td) || td is not ResourceDictionary themeDict) continue;
                    SetIn(themeDict, "AccentFillColorDefaultBrush", p.Accent, 0xFF);
                    SetIn(themeDict, "AccentFillColorSecondaryBrush", p.Accent, 0xE6);
                    SetIn(themeDict, "AccentFillColorTertiaryBrush", p.Accent, 0xCC);
                    SetIn(themeDict, "TextOnAccentFillColorPrimaryBrush", p.TextOnAccent, 0xFF);
                }
            }

            ElementSoundPlayer.State = settings.UiSounds ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
            ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.Off;
        }
        catch (Exception ex)
        {
            Log.Warn("Applying theme failed.", ex);
        }
    }

    /// <summary>Navigation sounds for controller moves (built-in controls play their own on click).</summary>
    public static void Play(ElementSoundKind kind)
    {
        if (ElementSoundPlayer.State != ElementSoundPlayerState.On) return;
        try { ElementSoundPlayer.Play(kind); } catch { /* audio device busy - ignore */ }
    }

    private static void Set(string key, RgbColor c, byte alpha = 0xFF)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is SolidColorBrush b)
            b.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    private static void SetIn(ResourceDictionary dict, string key, RgbColor c, byte alpha)
    {
        if (dict.TryGetValue(key, out var v) && v is SolidColorBrush b)
            b.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    private static IEnumerable<ResourceDictionary> AllDictionaries(ResourceDictionary root)
    {
        yield return root;
        foreach (var merged in root.MergedDictionaries)
            foreach (var d in AllDictionaries(merged))
                yield return d;
    }
}
