using Lumen.Core.Hardware;
using Lumen.Core.Input;
using Lumen.Core.Performance;

namespace Lumen.Core.Tests;

public class MouseEmulatorTests
{
    private static GamepadState Pad(GamepadButtons b = GamepadButtons.None, float lx = 0, float ly = 0, float ry = 0, float rt = 0) =>
        new(true, b, lx, ly, 0, ry, 0, rt);

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void Deadzone_and_full_deflection()
    {
        var m = new MouseEmulator();
        Assert.True(m.Update(Pad(lx: 0.1f, ly: -0.1f), Frame).IsEmpty);
        var o = m.Update(Pad(lx: 1f, ly: 1f), Frame);
        Assert.Equal(16, o.Dx);   // 1600 px/s * 10 ms
        Assert.Equal(-16, o.Dy);  // up
    }

    [Fact]
    public void Fractions_accumulate_and_precision_slows_down()
    {
        var m = new MouseEmulator();
        var total = 0;
        for (var i = 0; i < 100; i++) total += m.Update(Pad(lx: 0.5f), Frame).Dx;
        // ((0.5-0.15)/0.85)^2 * 1600 px/s * 1 s ≈ 271
        Assert.InRange(total, 268, 272);
        var precise = new MouseEmulator();
        Assert.Equal(4, precise.Update(Pad(lx: 1f, rt: 1f), Frame).Dx); // 30 %
    }

    [Fact]
    public void Buttons_edges_ignore_initial_hold_and_modifiers()
    {
        var m = new MouseEmulator();
        Assert.Null(m.Update(Pad(GamepadButtons.A), Frame).Left);   // held when mode started
        Assert.Equal(false, m.Update(Pad(), Frame).Left);
        Assert.Equal(true, m.Update(Pad(GamepadButtons.A), Frame).Left);
        Assert.Null(m.Update(Pad(GamepadButtons.A), Frame).Left);
        Assert.Equal(false, m.Update(Pad(GamepadButtons.A | GamepadButtons.View), Frame).Left); // shortcut, not a click
        Assert.Equal(true, m.Update(Pad(GamepadButtons.B), Frame).Right);
        Assert.Equal(false, m.Release().Right);
    }

    [Fact]
    public void Right_stick_scrolls_in_whole_notches()
    {
        var m = new MouseEmulator();
        var wheel = 0;
        for (var i = 0; i < 101; i++) wheel += m.Update(Pad(ry: 1f), Frame).Wheel;
        Assert.Equal(12 * 120, wheel);
        Assert.All(new[] { wheel }, w => Assert.Equal(0, w % 120));
    }
}

public class PerformanceAdvisorTests
{
    private static FrameStats Stats(double fps, double p99Ms, double? gpu = null, double? cpu = null) =>
        new(fps, 1000 / p99Ms, fps, 1000 / fps, p99Ms, gpu, cpu, null, null, null, 300, []);

    private static AdvisorInputs In(FrameStats? fps, bool battery = true, int? pct = 60, int hz = 120, TdpStatus? tdp = null, bool helper = true) =>
        new(fps, battery, pct, hz, [60, 90, 120], tdp, helper);

    private static string[] Ids(AdvisorInputs i) => PerformanceAdvisor.Evaluate(i).Select(a => a.Id).ToArray();

    [Fact]
    public void Suggests_60hz_when_fps_is_far_below_refresh_on_battery()
    {
        Assert.Contains("refresh", Ids(In(Stats(45, 25))));
        Assert.DoesNotContain("refresh", Ids(In(Stats(45, 25), battery: false)));
        Assert.DoesNotContain("refresh", Ids(In(Stats(100, 12))));
    }

    [Fact]
    public void Headroom_needs_measured_package_power()
    {
        var tdp = new TdpStatus { Available = true, PackageWatts = 20 };
        Assert.Contains("headroom", Ids(In(Stats(119, 9), tdp: tdp)));
        Assert.DoesNotContain("headroom", Ids(In(Stats(119, 9))));
    }

    [Fact]
    public void Bottleneck_stutter_heat_override_and_no_data()
    {
        Assert.Contains("gpu-bound", Ids(In(Stats(40, 30, gpu: 24.5))));
        Assert.Contains("cpu-bound", Ids(In(Stats(40, 30, gpu: 10, cpu: 24.5))));
        Assert.Contains("stutter", Ids(In(Stats(60, 50))));
        Assert.Contains("hot", Ids(In(null, tdp: new TdpStatus { Available = true, PackageCelsius = 97 })));
        Assert.Contains("tdp-override", Ids(In(null, tdp: new TdpStatus { Available = true, FirmwareOverride = true })));
        Assert.Equal(new[] { "no-fps" }, Ids(In(null)));
        Assert.Empty(Ids(In(null, helper: false)));
        Assert.Contains("low-battery", Ids(In(null, pct: 15)));
    }
}
