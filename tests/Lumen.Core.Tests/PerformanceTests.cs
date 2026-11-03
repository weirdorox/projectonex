using Lumen.Core.Hardware;
using Lumen.Core.Performance;

namespace Lumen.Core.Tests;

public class PresentMonCsvTests
{
    private const string HeaderNew =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,MsPCLatency,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,DisplayLatency,DisplayedTime,MsAnimationError";

    [Fact]
    public void Parses_rows_by_header_name()
    {
        var csv = new PresentMonCsv();
        Assert.Null(csv.Feed(HeaderNew));
        Assert.True(csv.HasHeader);
        var s = csv.Feed("game.exe,4242,0x1,DXGI,0,0,1,Hardware: Independent Flip,1.5,NA,16.67,16.70,0.2,10.0,12.0,NA,16.6,8.1,8.5,0.3,9.0,8.8,0.2,20.5,16.70,0.01")!.Value;
        Assert.Equal(16.67, s.FrameMs);
        Assert.Equal(16.70, s.DisplayedMs);
        Assert.Equal(8.8, s.GpuBusyMs);
        Assert.Equal(8.1, s.CpuBusyMs);
        Assert.Equal(20.5, s.LatencyMs);
        Assert.Equal("Hardware: Independent Flip", s.PresentMode);
        Assert.Equal("DXGI", s.Runtime);
        Assert.Equal(4242, csv.ProcessId("game.exe,4242,0x1"));
    }

    [Fact]
    public void Legacy_v2_columns_and_dropped_frames()
    {
        var csv = new PresentMonCsv();
        csv.Feed("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime");
        var s = csv.Feed("g.exe,1,0x1,DXGI,1,0,0,Composed: Flip,Application,0.0,33.3,10,23,1,12,11,1,40,NA")!.Value;
        Assert.Equal(33.3, s.FrameMs);
        Assert.Null(s.DisplayedMs);
        Assert.Equal(11, s.GpuBusyMs);
    }

    [Fact]
    public void Ignores_noise_before_header_and_bad_rows()
    {
        var csv = new PresentMonCsv();
        Assert.Null(csv.Feed("Started recording"));
        Assert.Null(csv.Feed("g.exe,1,2,3"));
        csv.Feed("Application,ProcessID,MsBetweenPresents");
        Assert.Null(csv.Feed("g.exe,1,NA"));
        Assert.Null(csv.Feed("g.exe,1,0"));
        Assert.Null(csv.Feed(""));
        Assert.NotNull(csv.Feed("g.exe,1,8.3"));
    }
}

public class FrameWindowTests
{
    private static FrameSample F(double ms, bool displayed = true) => new(ms, displayed ? ms : null, 5, 4, 20, "Hardware: Independent Flip", "DXGI");

    [Fact]
    public void Steady_60()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 300; i++) w.Add(F(1000.0 / 60));
        var s = w.Summarize()!;
        Assert.Equal(60, s.Fps, 0);
        Assert.Equal(60, s.OnePercentLowFps, 0);
        Assert.InRange(w.Count, 119, 121); // 2 s window
        Assert.InRange(s.RecentFrameMs.Count, 119, 120);
    }

    [Fact]
    public void Stutters_show_in_one_percent_low_not_average()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 400; i++) w.Add(F(i % 50 == 0 ? 50 : 10));
        var s = w.Summarize()!;
        Assert.InRange(s.Fps, 92, 93);
        Assert.Equal(20, s.OnePercentLowFps); // 1000 / 50
        Assert.Equal(50, s.P99FrameMs);
    }

    [Fact]
    public void No_stats_without_enough_frames_and_dropped_frames_lower_displayed_fps()
    {
        var w = new FrameWindow(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 5; i++) w.Add(F(10));
        Assert.Null(w.Summarize());
        for (var i = 0; i < 95; i++) w.Add(F(10, displayed: i % 2 == 0));
        var s = w.Summarize()!;
        Assert.Equal(100, s.Fps);
        Assert.InRange(s.DisplayedFps!.Value, 50, 55);
        Assert.Equal(1, FrameWindow.Percentile([1.0], 0.99));
    }
}

public class OverlayContentTests
{
    private static readonly FrameStats Stats = new(60, 45, 60, 16.7, 22.2, 9, 7, 30, "Hardware: Independent Flip", "DXGI", 300, [16.7]);
    private static OverlayInputs Inputs(FpsReading? fps) => new(
        fps,
        new SystemSnapshot(DateTimeOffset.UnixEpoch, 40, 80, 8L << 30, 16L << 30, null, null, null),
        new TdpStatus { Available = true, PackageWatts = 17.5, PackageCelsius = 71 },
        new BatteryStatus(true, 64, PowerSource.Battery, false, false, null),
        new BatteryTelemetry(-22.4, 30, 60, 65));

    [Fact]
    public void Modes_grow_and_off_is_empty()
    {
        var input = Inputs(new FpsReading(true, null, 1, Stats));
        Assert.Empty(OverlayContent.Lines(OverlayMode.Off, input));
        Assert.Equal(new[] { "FPS" }, OverlayContent.Lines(OverlayMode.Minimal, input).Select(l => l.Label));
        var basic = OverlayContent.Lines(OverlayMode.Basic, input).Count;
        var detailed = OverlayContent.Lines(OverlayMode.Detailed, input);
        var advanced = OverlayContent.Lines(OverlayMode.Advanced, input).Count;
        Assert.True(basic < detailed.Count && detailed.Count < advanced);
        Assert.Contains(detailed, l => l is { Label: "CPU pkg", Value: "17.5 W" });
        Assert.Contains(detailed, l => l is { Label: "Draw", Value: "22.4 W" });
        Assert.Equal(OverlayMode.Off, OverlayContent.Next(OverlayMode.Advanced));
        Assert.True(OverlayContent.ShowsGraph(OverlayMode.Advanced));
    }

    [Fact]
    public void Never_shows_a_number_for_unmeasured_fps()
    {
        var lines = OverlayContent.Lines(OverlayMode.Detailed, Inputs(FpsReading.Unavailable("FPS needs the Lumen helper")));
        Assert.Equal("—", lines[0].Value);
        Assert.Contains(lines, l => l.Value == "FPS needs the Lumen helper");
        Assert.DoesNotContain(lines, l => l.Label == "1% low");
    }
}
