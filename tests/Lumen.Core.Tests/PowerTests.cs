using Lumen.Core.Hardware;
using Lumen.Core.Power;

namespace Lumen.Core.Tests;

public class IntelRaplTests
{
    // Typical client value: power unit 1/8 W (3), energy 1/2^14 J (14), time 1/2^10 s (10).
    private const ulong Units606 = 0x000A0E03;
    private static readonly RaplUnits U = IntelRapl.DecodeUnits(Units606);

    [Fact]
    public void Decodes_units()
    {
        Assert.Equal(0.125, U.PowerWatts);
        Assert.Equal(1.0 / 16384, U.EnergyJoules);
        Assert.Equal(1.0 / 1024, U.TimeSeconds);
    }

    [Fact]
    public void Decodes_and_reencodes_limits_preserving_other_bits()
    {
        // PL1 = 25 W (200 units), enabled, clamp, time 0x6E; PL2 = 64 W (512), enabled, time 0x21; locked.
        ulong v = 200UL | (1UL << 15) | (1UL << 16) | (0x6EUL << 17)
                | (512UL << 32) | (1UL << 47) | (0x21UL << 49) | (1UL << 63);
        var d = IntelRapl.DecodeLimit(v, U);
        Assert.Equal(25, d.Pl1Watts);
        Assert.Equal(64, d.Pl2Watts);
        Assert.True(d.Pl1Enabled && d.Pl1Clamp && d.Pl2Enabled && d.Locked);
        Assert.Equal(0x6E, d.Pl1TimeRaw);
        Assert.Equal(0x21, d.Pl2TimeRaw);

        var encoded = IntelRapl.EncodeLimit(v, 15, 20, U);
        var back = IntelRapl.DecodeLimit(encoded, U);
        Assert.Equal(15, back.Pl1Watts);
        Assert.Equal(20, back.Pl2Watts);
        Assert.Equal(d.Pl1TimeRaw, back.Pl1TimeRaw);
        Assert.Equal(d.Pl2TimeRaw, back.Pl2TimeRaw);
        Assert.Equal(d.Pl1Clamp, back.Pl1Clamp);
        Assert.True(back.Locked); // lock bit is never cleared by Lumen
        Assert.Equal(v & ~(0x7FFFUL | (0x7FFFUL << 32)), encoded & ~(0x7FFFUL | (0x7FFFUL << 32)));
    }

    [Fact]
    public void Encode_sets_enable_bits_and_clamps_field_width()
    {
        var encoded = IntelRapl.EncodeLimit(0, 5000, 10, U); // absurd PL1 is clamped to the 15-bit field
        var d = IntelRapl.DecodeLimit(encoded, U);
        Assert.True(d.Pl1Enabled && d.Pl2Enabled);
        Assert.Equal(0x7FFF * 0.125, d.Pl1Watts);
    }

    [Fact]
    public void Range_prefers_cpu_reported_limits()
    {
        var info = IntelRapl.DecodeInfo(120UL | (40UL << 16) | (240UL << 32), U); // TDP 15 W, min 5 W, max 30 W
        var firmware = new PackagePowerLimit(25, true, false, 0, 40, true, false, 0, false);
        var r = IntelRapl.ComputeRange(info, firmware);
        Assert.Equal(5, r.MinWatts);
        Assert.Equal(30, r.MaxWatts);
        Assert.Contains("0x614", r.Source);
    }

    [Fact]
    public void Range_falls_back_to_firmware_defaults_and_safety_floor()
    {
        var info = new PackagePowerInfo(0, 0, 0);
        var firmware = new PackagePowerLimit(28, true, false, 0, 37, true, false, 0, false);
        var r = IntelRapl.ComputeRange(info, firmware);
        Assert.Equal(IntelRapl.SafetyFloorWatts, r.MinWatts);
        Assert.Equal(37, r.MaxWatts);
        Assert.Equal(20, r.Clamp(20));
        Assert.Equal(37, r.Clamp(60));
    }

    [Fact]
    public void Package_power_from_energy_counter_handles_wraparound()
    {
        var oneJoule = (ulong)(1 / U.EnergyJoules);
        Assert.Equal(20, IntelRapl.PackageWatts(1000, 1000 + 20 * oneJoule, TimeSpan.FromSeconds(1), U)!.Value, 3);
        Assert.Equal(10, IntelRapl.PackageWatts(0xFFFFFFF0, (0xFFFFFFF0 + 10 * oneJoule) & 0xFFFFFFFF, TimeSpan.FromSeconds(1), U)!.Value, 3); // counter wrapped
        Assert.Null(IntelRapl.PackageWatts(0, 1, TimeSpan.Zero, U));
    }

    [Fact]
    public void Package_temperature()
    {
        ulong target = 100UL << 16;
        ulong status = (1UL << 31) | (35UL << 16);
        Assert.Equal(65, IntelRapl.PackageCelsius(status, target));
        Assert.Null(IntelRapl.PackageCelsius(35UL << 16, target)); // invalid reading
    }

    [Fact]
    public void Presets_outside_range_are_disabled_with_reason()
    {
        var list = TdpPresets.Resolve(new TdpRange(8, 30, "test"));
        Assert.True(list.Single(p => p.Preset.Name == "Eco").Available);
        Assert.True(list.Single(p => p.Preset.Name == "Performance").Available);
        var turbo = list.Single(p => p.Preset.Name == "Turbo");
        Assert.False(turbo.Available);
        Assert.Contains("30", turbo.Reason);
        Assert.All(TdpPresets.Resolve(null), p => Assert.False(p.Available));
    }
}

public class SmartPowerTests
{
    private static readonly PowerPlan Saver = new(SmartPower.PowerSaverPlan, "Power saver");
    private static readonly PowerPlan Balanced = new(SmartPower.BalancedPlan, "Balanced");
    private static readonly PowerPlan High = new(SmartPower.HighPerformancePlan, "High performance");

    private static SmartPowerCapabilities Full(double maxW = 35) =>
        new(new TdpRange(5, maxW, "test"), [60, 90, 120, 144], [Saver, Balanced, High]);

    [Fact]
    public void Anchors_map_to_expected_settings()
    {
        var lo = SmartPower.Compute(0, Full(), onBattery: true);
        var mid = SmartPower.Compute(50, Full(), onBattery: true);
        var hi = SmartPower.Compute(100, Full(), onBattery: false);

        Assert.Equal(8, lo.TdpWatts);
        Assert.Equal(60, lo.RefreshHz);
        Assert.Equal(SmartPower.PowerSaverPlan, lo.PowerPlanId);
        Assert.Equal(30, lo.FpsTarget);

        Assert.Equal(20, mid.TdpWatts);
        Assert.Equal(90, mid.RefreshHz);
        Assert.Equal(SmartPower.BalancedPlan, mid.PowerPlanId);

        Assert.Equal(35, hi.TdpWatts);
        Assert.Equal(144, hi.RefreshHz);
        Assert.Equal(SmartPower.HighPerformancePlan, hi.PowerPlanId);
    }

    [Fact]
    public void Tdp_is_monotonic_and_respects_cpu_maximum()
    {
        double? last = 0;
        for (var p = 0; p <= 100; p += 5)
        {
            var w = SmartPower.Compute(p, Full(28), false).TdpWatts;
            Assert.True(w >= last);
            Assert.True(w <= 28);
            last = w;
        }
    }

    [Fact]
    public void Unsupported_capabilities_stay_null()
    {
        var plan = SmartPower.Compute(80, new SmartPowerCapabilities(null, [60], [Balanced]), false);
        Assert.Null(plan.TdpWatts);
        Assert.Null(plan.RefreshHz);
        Assert.Null(plan.PowerPlanId);
    }

    [Fact]
    public void Battery_keeps_refresh_one_step_lower_at_max()
    {
        Assert.Equal(120, SmartPower.Compute(100, Full(), onBattery: true).RefreshHz);
    }

    [Fact]
    public void Runtime_estimate_uses_real_telemetry_only()
    {
        Assert.Null(SmartPower.EstimateRuntime(null, 10));
        Assert.Null(SmartPower.EstimateRuntime(50, null));     // no discharge reading (e.g. plugged in)
        Assert.Equal(TimeSpan.FromHours(5), SmartPower.EstimateRuntime(50, 10));
        // previewing a 10 W cap while the package measures 18 W of a 20 W system draw: draw drops by 8 W
        Assert.Equal(50 / 12.0, SmartPower.EstimateRuntime(50, 20, measuredPackageWatts: 18, plannedTdpWatts: 10)!.Value.TotalHours, 3);
        // never assumes the system draw falls below 30 % of what is measured now
        Assert.Equal(50 / 3.0, SmartPower.EstimateRuntime(50, 10, measuredPackageWatts: 18, plannedTdpWatts: 5)!.Value.TotalHours, 3);
        var eight = SmartPower.EstimateRuntime(50, 10, measuredPackageWatts: 18, plannedTdpWatts: 18);
        Assert.Equal(TimeSpan.FromHours(5), eight);
    }

    [Fact]
    public void Discharge_average_smooths_and_resets()
    {
        var a = new DischargeAverager(0.5);
        Assert.Equal(10, a.Add(10));
        Assert.Equal(15, a.Add(20));
        Assert.Null(a.Add(-5)); // charging
        Assert.Equal(8, a.Add(8));
    }
}

public class PowerHelperProtocolTests
{
    private static readonly TdpRange Range = new(5, 30, "test");

    [Fact]
    public void Round_trips_requests_and_status()
    {
        var req = PowerHelperProtocol.ParseRequest(PowerHelperProtocol.Serialize(new PowerHelperRequest(PowerHelperRequest.Set, 18)));
        Assert.Equal(new PowerHelperRequest("set", 18), req);

        var status = new TdpStatus { Available = true, Backend = "Intel RAPL", MinWatts = 5, MaxWatts = 30, RequestedWatts = 18, Locked = false };
        var back = PowerHelperProtocol.ParseStatus(PowerHelperProtocol.Serialize(status));
        Assert.Equal(status, back);
    }

    [Theory]
    [InlineData("""{"command":"set","watts":20}""", null)]
    [InlineData("""{"command":"status"}""", null)]
    [InlineData("""{"command":"set","watts":31}""", "outside")]
    [InlineData("""{"command":"set","watts":2}""", "outside")]
    [InlineData("""{"command":"set"}""", "Missing")]
    [InlineData("""{"command":"wrmsr","watts":1}""", "Unknown")]
    [InlineData("not json", "Malformed")]
    public void Elevated_side_validates_everything(string line, string? errorContains)
    {
        var error = PowerHelperProtocol.Validate(PowerHelperProtocol.ParseRequest(line), Range);
        if (errorContains is null) Assert.Null(error);
        else Assert.Contains(errorContains, error);
    }

    [Fact]
    public void Oversized_lines_are_rejected()
    {
        Assert.Null(PowerHelperProtocol.ParseRequest(new string('x', 600)));
    }

    [Fact]
    public void Pipe_name_is_per_user()
    {
        Assert.NotEqual(PowerHelperProtocol.PipeName("S-1-5-21-1"), PowerHelperProtocol.PipeName("S-1-5-21-2"));
    }
}

public class BatteryTelemetryTests
{
    [Fact]
    public void Derives_discharge_and_health()
    {
        var t = new BatteryTelemetry(-14.2, 40, 80, 85);
        Assert.Equal(14.2, t.DischargeWatts);
        Assert.Null(t.ChargeWatts);
        Assert.Equal(94.1, t.HealthPercent);

        var charging = new BatteryTelemetry(30, 40, 80, null);
        Assert.Null(charging.DischargeWatts);
        Assert.Equal(30, charging.ChargeWatts);
        Assert.Null(charging.HealthPercent);
    }
}
