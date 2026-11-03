namespace Lumen.Core.Startup;

/// <summary>Command line switches. Unknown switches are ignored.</summary>
public sealed record StartupOptions
{
    /// <summary>Started by the sign-in autostart entry (not by the user).</summary>
    public bool AutoStart { get; init; }

    /// <summary>Windowed, no autostart side effects, recovery screen shown.</summary>
    public bool SafeMode { get; init; }

    /// <summary>Windowed instead of fullscreen (development).</summary>
    public bool Windowed { get; init; }

    /// <summary>Open the quick-access overlay immediately (used by a second instance / hotkey launcher).</summary>
    public bool ShowOverlay { get; init; }

    public const string AutoStartSwitch = "--autostart";

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        var o = new StartupOptions();
        foreach (var raw in args)
        {
            var a = raw.Trim().ToLowerInvariant();
            o = a switch
            {
                "--autostart" or "/autostart" => o with { AutoStart = true },
                "--safe-mode" or "--safemode" or "/safe" => o with { SafeMode = true },
                "--windowed" => o with { Windowed = true },
                "--overlay" => o with { ShowOverlay = true },
                _ => o,
            };
        }
        return o;
    }
}
