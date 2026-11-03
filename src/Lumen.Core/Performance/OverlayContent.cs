using Lumen.Core.Hardware;

namespace Lumen.Core.Performance;

public enum OverlayMode { Off, Minimal, Basic, Detailed, Advanced }

public sealed record OverlayLine(string Label, string Value, bool Dim = false);

/// <summary>Inputs for the on-screen performance overlay. Any of them can be missing.</summary>
public sealed record OverlayInputs(
    FpsReading? Fps,
    SystemSnapshot? System,
    TdpStatus? Tdp,
    BatteryStatus? Battery,
    BatteryTelemetry? BatteryPower);

/// <summary>
/// Decides what each overlay mode shows (pure, tested). Missing data is shown as "—" or explained;
/// an FPS value appears only if it was measured.
/// </summary>
public static class OverlayContent
{
    public static OverlayMode Next(OverlayMode mode) => mode == OverlayMode.Advanced ? OverlayMode.Off : mode + 1;

    public static bool ShowsGraph(OverlayMode mode) => mode == OverlayMode.Advanced;

    public static IReadOnlyList<OverlayLine> Lines(OverlayMode mode, OverlayInputs i)
    {
        if (mode == OverlayMode.Off) return [];
        var stats = i.Fps?.Stats;
        var lines = new List<OverlayLine>
        {
            stats is not null
                ? new("FPS", $"{stats.Fps:0}")
                : new("FPS", "—", Dim: true),
        };
        if (mode == OverlayMode.Minimal) return lines;

        lines.Add(new("Frame", stats is null ? "—" : $"{stats.AvgFrameMs:0.0} ms", stats is null));
        if (i.Battery is { HasBattery: true, Percent: { } pct } b)
            lines.Add(new("Battery", $"{pct}%{(b.Source == PowerSource.AC ? " ⚡" : "")}"));

        if (mode >= OverlayMode.Detailed)
        {
            if (stats is not null) lines.Insert(1, new("1% low", $"{stats.OnePercentLowFps:0}"));
            if (i.System is { } s)
            {
                if (s.CpuPercent is { } cpu) lines.Add(new("CPU", $"{cpu:0}%"));
                if (s.GpuPercent is { } gpu) lines.Add(new("GPU", $"{gpu:0}%"));
            }
            if (i.Tdp is { Available: true } t)
            {
                if (t.PackageWatts is { } w) lines.Add(new("CPU pkg", $"{w:0.0} W"));
                if (t.PackageCelsius is { } c) lines.Add(new("CPU temp", $"{c:0}°C"));
            }
            if (i.BatteryPower?.DischargeWatts is { } dw and > 0) lines.Add(new("Draw", $"{dw:0.0} W"));
        }

        if (mode == OverlayMode.Advanced)
        {
            if (stats is not null)
            {
                lines.Add(new("99th", $"{stats.P99FrameMs:0.0} ms"));
                if (stats.DisplayedFps is { } d) lines.Add(new("Shown", $"{d:0} fps"));
                if (stats.AvgGpuBusyMs is { } g) lines.Add(new("GPU busy", $"{g:0.0} ms"));
                if (stats.AvgCpuBusyMs is { } c) lines.Add(new("CPU busy", $"{c:0.0} ms"));
                if (stats.AvgLatencyMs is { } l) lines.Add(new("Latency", $"{l:0} ms"));
                if (stats.PresentMode is { } m) lines.Add(new("Mode", Shorten(m), Dim: true));
            }
            if (i.System?.MemoryPercent is { } ram) lines.Add(new("RAM", $"{ram:0}%"));
        }

        if (stats is null)
            lines.Add(new("", i.Fps is { Available: false, Reason: { } r } ? r : "FPS not measured yet", Dim: true));
        return lines;
    }

    private static string Shorten(string mode) => mode
        .Replace("Hardware: ", "HW ", StringComparison.Ordinal)
        .Replace("Composed: ", "Comp ", StringComparison.Ordinal)
        .Replace("Independent Flip", "iFlip", StringComparison.Ordinal);
}
