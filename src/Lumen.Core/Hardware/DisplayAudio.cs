namespace Lumen.Core.Hardware;

// ---------------- resolution ----------------

public interface IResolutionControl
{
    /// <summary>All modes the driver reports for the primary display.</summary>
    IReadOnlyList<DisplayMode> GetModes();

    /// <summary>Tests the mode with the driver first; returns false (and changes nothing) if rejected.</summary>
    /// <param name="persist">False = applies until reboot/sign-out only (used while waiting for "Keep?").</param>
    bool SetResolution(int width, int height, int? refreshHz = null, bool persist = true);
}

public sealed record ResolutionOption(int Width, int Height, int BestRefreshHz, bool IsNative, bool IsCurrent)
{
    public string Label => $"{Width}×{Height}";
}

/// <summary>Picks the handful of resolutions worth offering on a handheld (pure, tested).</summary>
public static class ResolutionChooser
{
    /// <summary>
    /// Same aspect ratio as the native (largest) mode within 2 %, height ≥ 600, at most <paramref name="max"/>
    /// options, largest first. Native and current are always kept.
    /// </summary>
    public static IReadOnlyList<ResolutionOption> Options(IEnumerable<DisplayMode> modes, DisplayMode? current, int max = 6)
    {
        var list = modes.Where(m => m.Width > 0 && m.Height > 0).ToList();
        if (list.Count == 0) return [];
        var native = list.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.RefreshHz).First();
        var aspect = (double)native.Width / native.Height;

        var groups = list
            .GroupBy(m => (m.Width, m.Height))
            .Select(g => new ResolutionOption(
                g.Key.Width, g.Key.Height,
                g.Max(m => m.RefreshHz),
                g.Key.Width == native.Width && g.Key.Height == native.Height,
                current is not null && g.Key.Width == current.Width && g.Key.Height == current.Height))
            .Where(o => o.IsNative || o.IsCurrent ||
                        (Math.Abs((double)o.Width / o.Height - aspect) / aspect <= 0.02 && Math.Min(o.Width, o.Height) >= 600))
            .OrderByDescending(o => (long)o.Width * o.Height)
            .ToList();

        if (groups.Count <= max) return groups;
        // Keep native + current, then spread the remainder evenly.
        var keep = groups.Where(o => o.IsNative || o.IsCurrent).ToList();
        var rest = groups.Except(keep).ToList();
        var slots = Math.Max(0, max - keep.Count);
        for (var i = 0; i < slots && rest.Count > 0; i++)
            keep.Add(rest[(int)Math.Round(i * (rest.Count - 1) / (double)Math.Max(1, slots - 1))]);
        return keep.Distinct().OrderByDescending(o => (long)o.Width * o.Height).ToList();
    }
}

// ---------------- displays / HDR / dock ----------------

public sealed record DisplayInfo(
    string Id,
    string Name,
    bool IsInternal,
    bool IsPrimary,
    int Width,
    int Height,
    bool HdrSupported,
    bool HdrEnabled,
    bool HdrForceDisabled = false,
    int X = 0,
    int Y = 0);

public interface IDisplayTopology
{
    IReadOnlyList<DisplayInfo> GetActive();

    /// <summary>Turns Windows HDR ("Use HDR") on/off for the display. Returns false if Windows refused.</summary>
    bool SetHdr(DisplayInfo display, bool enable);
}

public enum DockState { Handheld, Docked, ExternalOnly }

/// <summary>Docked = an external display is active (pure, tested).</summary>
public static class DockDetector
{
    public static DockState Evaluate(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return DockState.Handheld;
        var external = displays.Any(d => !d.IsInternal);
        if (!external) return DockState.Handheld;
        return displays.Any(d => d.IsInternal) ? DockState.Docked : DockState.ExternalOnly;
    }

    /// <summary>The display Lumen should fill when docked: the primary if external, else the largest external.</summary>
    public static DisplayInfo? Target(IReadOnlyList<DisplayInfo> displays) =>
        displays.FirstOrDefault(d => d.IsPrimary && !d.IsInternal)
        ?? displays.Where(d => !d.IsInternal).OrderByDescending(d => (long)d.Width * d.Height).FirstOrDefault();

    public static string Describe(DockState state, IReadOnlyList<DisplayInfo> displays) => state switch
    {
        DockState.Handheld => "Handheld",
        DockState.Docked => $"Docked · {Target(displays)?.Name ?? "external display"}",
        _ => $"External display only · {Target(displays)?.Name ?? "external display"}",
    };
}

// ---------------- audio ----------------

public sealed record AudioEndpoint(string Id, string Name, bool IsDefault);

public interface IAudioDevices
{
    /// <summary>Null when there is no microphone.</summary>
    bool? GetMicMuted();
    bool SetMicMuted(bool muted);
    IReadOnlyList<AudioEndpoint> GetOutputs();

    /// <summary>
    /// Windows has no documented API to change the default output. Implementations may return false;
    /// the UI then opens Windows sound settings instead.
    /// </summary>
    bool SetDefaultOutput(string id);
}
