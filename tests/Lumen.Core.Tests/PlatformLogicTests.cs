using Lumen.Core.Hardware;
using Lumen.Core.Power;
using Lumen.Core.Startup;

namespace Lumen.Core.Tests;

public class DeviceProfileTests
{
    private static DeviceIdentity Id(string manufacturer, string product, string family = "") =>
        new(manufacturer, product, "", family, "1.0");

    [Theory]
    [InlineData("ONE-NETBOOK", "ONEXPLAYER 3", "onexplayer-3")]
    [InlineData("ONE-NETBOOK TECHNOLOGY CO., LTD.", "ONEXPLAYER3 G1", "onexplayer-3")]
    [InlineData("ONE-NETBOOK", "ONEXPLAYER X1", "onexplayer")]
    [InlineData("ONE-NETBOOK", "ONEXPLAYER F1Pro", "onexplayer")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Ally RC71L", "generic")]
    [InlineData("", "", "generic")]
    public void Resolves_expected_profile(string manufacturer, string product, string expected)
    {
        var profile = new DeviceProfileRegistry().Resolve(Id(manufacturer, product));
        Assert.Equal(expected, profile.Id);
    }

    [Fact]
    public void OneXPlayer3_traits_match_published_specs()
    {
        var t = new OneXPlayer3Profile().Traits;
        Assert.Equal(1920, t.NativeWidth);
        Assert.Equal(1200, t.NativeHeight);
        Assert.Equal(144, t.MaxRefreshHz);
        Assert.True(t.DetachableControllers);
    }

    [Fact]
    public void No_profile_permits_undocumented_controls()
    {
        foreach (var p in DeviceProfileRegistry.Default())
        {
            Assert.DoesNotContain(HardwareFeature.TdpControl, p.PermittedFeatures);
            Assert.DoesNotContain(HardwareFeature.FanControl, p.PermittedFeatures);
            Assert.False(string.IsNullOrWhiteSpace(p.ExplainUnsupported(HardwareFeature.TdpControl)));
        }
    }

    [Fact]
    public void Composer_drops_providers_the_profile_does_not_permit()
    {
        var services = HardwareComposer.Compose(
            new OneXPlayer3Profile(), DeviceIdentity.Unknown,
            new ProviderCandidates(Tdp: new FakeTdp(), Fan: new FakeFan(), Battery: new FakeBattery()));

        Assert.Null(services.Tdp);
        Assert.Null(services.Fan);
        Assert.NotNull(services.Battery);
        Assert.False(services.Supports(HardwareFeature.TdpControl));
        Assert.True(services.Supports(HardwareFeature.Battery));
        Assert.False(services.Supports(HardwareFeature.Volume)); // no candidate
    }

    private sealed class FakeTdp : ITdpControl
    {
        public (int MinWatts, int MaxWatts) Range => (5, 30);
        public int? GetWatts() => 15;
        public bool SetWatts(int watts) => true;
    }

    private sealed class FakeFan : IFanControl
    {
        public int? GetRpm() => 1000;
        public bool SetAuto() => true;
        public bool SetPercent(int percent) => true;
    }

    private sealed class FakeBattery : IBatteryInfo
    {
        public BatteryStatus Read() => BatteryStatus.Unknown;
    }
}

public class PowerPolicyTests
{
    [Fact]
    public void Chooses_profile_from_battery_state()
    {
        Assert.Same(PowerPolicy.PluggedIn, PowerPolicy.Choose(BatteryStatus.Unknown));
        Assert.Same(PowerPolicy.PluggedIn, PowerPolicy.Choose(new(true, 10, PowerSource.AC, true, false, null)));
        Assert.Same(PowerPolicy.OnBattery, PowerPolicy.Choose(new(true, 80, PowerSource.Battery, false, false, null)));
        Assert.Same(PowerPolicy.LowBattery, PowerPolicy.Choose(new(true, 15, PowerSource.Battery, false, false, null)));
        Assert.Same(PowerPolicy.LowBattery, PowerPolicy.Choose(new(true, 90, PowerSource.Battery, false, true, null)));
        Assert.False(PowerPolicy.LowBattery.AnimationsEnabled);
    }
}

public class CrashLoopGuardTests
{
    [Fact]
    public void Clean_exits_never_trigger_safe_mode()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "state.json");
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var g = new CrashLoopGuard(file);
            Assert.Equal(StartupDecision.Normal, g.OnStartup(t.AddSeconds(i)));
            g.MarkCleanExit();
        }
    }

    [Fact]
    public void Three_crashes_in_window_trigger_safe_mode()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "state.json");
        var t = DateTimeOffset.UtcNow;

        // start #1..#3 crash (never call MarkCleanExit)
        Assert.Equal(StartupDecision.Normal, new CrashLoopGuard(file).OnStartup(t));
        Assert.Equal(StartupDecision.Normal, new CrashLoopGuard(file).OnStartup(t.AddSeconds(10)));
        Assert.Equal(StartupDecision.Normal, new CrashLoopGuard(file).OnStartup(t.AddSeconds(20)));
        // start #4 sees 3 crashes
        Assert.Equal(StartupDecision.SafeMode, new CrashLoopGuard(file).OnStartup(t.AddSeconds(30)));
    }

    [Fact]
    public void Old_crashes_expire_and_stability_clears_history()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "state.json");
        var t = DateTimeOffset.UtcNow;

        new CrashLoopGuard(file).OnStartup(t);
        new CrashLoopGuard(file).OnStartup(t.AddSeconds(1));
        new CrashLoopGuard(file).OnStartup(t.AddSeconds(2));
        // much later: crashes outside the window are forgotten
        var g = new CrashLoopGuard(file);
        Assert.Equal(StartupDecision.Normal, g.OnStartup(t.AddHours(1)));
        g.MarkStable();
        Assert.Equal(0, g.RecentCrashCount);
    }

    [Fact]
    public void Recovery_request_forces_safe_mode()
    {
        using var dir = new TempDir();
        var g = new CrashLoopGuard(Path.Combine(dir.Path, "s.json"));
        Assert.Equal(StartupDecision.SafeMode, g.OnStartup(DateTimeOffset.UtcNow, recoveryRequested: true));
    }

    [Fact]
    public void Corrupt_state_file_is_tolerated()
    {
        using var dir = new TempDir();
        var file = dir.Write("s.json", "{{{ nope");
        Assert.Equal(StartupDecision.Normal, new CrashLoopGuard(file).OnStartup(DateTimeOffset.UtcNow));
    }
}

public class StartupOptionsTests
{
    [Fact]
    public void Parses_known_switches()
    {
        var o = StartupOptions.Parse(["--autostart", "--SAFE-MODE", "--windowed", "--unknown"]);
        Assert.True(o.AutoStart);
        Assert.True(o.SafeMode);
        Assert.True(o.Windowed);
        Assert.False(o.ShowOverlay);
    }
}

public class ShellSettingsTests
{
    [Fact]
    public void Round_trips_and_defaults()
    {
        var s = new ShellSettings(new InMemorySettingsStore());
        Assert.True(s.Fullscreen);
        Assert.Empty(s.LibraryFolders);
        s.LibraryFolders = [@"D:\Games", @"d:\games", @"E:\Emu"];
        Assert.Equal(2, s.LibraryFolders.Count);
        s.Fullscreen = false;
        Assert.False(s.Fullscreen);
    }
}
