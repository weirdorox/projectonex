using Lumen.Core.Hardware;

namespace Lumen.Core.Performance;

public enum AdviceLevel { Info, Suggestion, Warning }

public sealed record Advice(string Id, AdviceLevel Level, string Title, string Detail);

public sealed record AdvisorInputs(
    FrameStats? Fps,
    bool OnBattery,
    int? BatteryPercent,
    int? RefreshHz,
    IReadOnlyList<int> SupportedRefreshRates,
    TdpStatus? Tdp,
    bool HelperInstalled);

/// <summary>
/// Rule-based, explainable recommendations from real readings only (pure, tested). It never changes
/// anything by itself - every tip says what was observed.
/// </summary>
public static class PerformanceAdvisor
{
    public static IReadOnlyList<Advice> Evaluate(AdvisorInputs i)
    {
        var list = new List<Advice>();
        var fps = i.Fps;

        if (i.Tdp is { FirmwareOverride: true })
            list.Add(new("tdp-override", AdviceLevel.Warning, "Firmware overrides the TDP",
                "The CPU did not keep the requested power limit. The device firmware or vendor software is enforcing its own limit - set the mode in the manufacturer's tool instead."));

        if (i.Tdp is { PackageCelsius: >= 95 } hot)
            list.Add(new("hot", AdviceLevel.Warning, "Running very hot",
                $"CPU package at {hot.PackageCelsius:0}°C - the processor will throttle. Lower TDP or the in-game settings."));

        if (i.OnBattery && i.BatteryPercent is <= 20)
            list.Add(new("low-battery", AdviceLevel.Suggestion, "Low battery",
                $"{i.BatteryPercent}% left. Move Smart Power to the left (or 60 Hz / a lower TDP) to stretch the remaining time."));

        if (fps is null)
        {
            if (i.HelperInstalled)
                list.Add(new("no-fps", AdviceLevel.Info, "No frame data", "Turn on the performance overlay while playing to get FPS-based tips."));
            return list;
        }

        var lowest = i.SupportedRefreshRates.Where(r => r >= 40).DefaultIfEmpty(0).Min();
        if (i.OnBattery && i.RefreshHz is { } hz && hz > 60 && lowest is > 0 and <= 60 && fps.Fps < hz * 0.6)
            list.Add(new("refresh", AdviceLevel.Suggestion, $"Use {lowest} Hz",
                $"The game runs at {fps.Fps:0} FPS but the screen refreshes at {hz} Hz. {lowest} Hz saves power and paces frames more evenly."));

        if (i.OnBattery && i.RefreshHz is { } cap && fps.Fps >= cap * 0.95 && i.Tdp is { Available: true, PackageWatts: > 12 } t)
            list.Add(new("headroom", AdviceLevel.Suggestion, "Headroom to save battery",
                $"The game already reaches the {cap} Hz refresh rate while the CPU package draws {t.PackageWatts:0} W. A lower TDP preset will likely keep the same FPS for longer."));

        if (fps.P99FrameMs > fps.AvgFrameMs * 2.5 && fps.Frames >= 60)
            list.Add(new("stutter", AdviceLevel.Suggestion, "Uneven frame pacing",
                $"1% lows ({fps.OnePercentLowFps:0} FPS) are far below the average ({fps.Fps:0} FPS). An in-game FPS cap slightly below the average usually feels smoother."));

        if (fps.AvgGpuBusyMs is { } gpu && gpu >= fps.AvgFrameMs * 0.9)
            list.Add(new("gpu-bound", AdviceLevel.Info, "GPU-limited",
                "The GPU is busy for almost the whole frame. Lower resolution, upscaling (XeSS/FSR) or graphics settings help more than a higher TDP."));
        else if (fps.AvgCpuBusyMs is { } cpu && cpu >= fps.AvgFrameMs * 0.9)
            list.Add(new("cpu-bound", AdviceLevel.Info, "CPU-limited",
                "The CPU is the bottleneck. Lower simulation/crowd/view-distance settings; GPU settings will not change much."));

        return list;
    }
}
