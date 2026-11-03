using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace Lumen.App.Services;

/// <summary>
/// Single switch for all decorative motion. Off when Windows "Animation effects" is off
/// (accessibility) or when the power policy asks for it (low battery / battery saver).
/// Every animation in Lumen uses composition-thread transitions (Opacity/Scale/Translation
/// transitions), so they stay at display refresh rate even while the UI thread is busy.
/// </summary>
public static class Motion
{
    private static readonly UISettings Settings = new();

    public static bool PowerAllows { get; set; } = true;

    public static bool Enabled
    {
        get
        {
            if (!PowerAllows) return false;
            try { return Settings.AnimationsEnabled; }
            catch { return true; }
        }
    }

    public static ScalarTransition? Fade(int milliseconds) =>
        Enabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(milliseconds) } : null;

    public static Vector3Transition? Vector(int milliseconds) =>
        Enabled ? new Vector3Transition { Duration = TimeSpan.FromMilliseconds(milliseconds) } : null;
}
