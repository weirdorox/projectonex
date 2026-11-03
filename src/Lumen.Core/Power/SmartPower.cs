using Lumen.Core.Hardware;

namespace Lumen.Core.Power;

/// <summary>What the device can actually do - Smart Power only uses these.</summary>
public sealed record SmartPowerCapabilities(
    TdpRange? Tdp,
    IReadOnlyList<int> RefreshRates,
    IReadOnlyList<PowerPlan> PowerPlans);

/// <summary>Concrete settings for a slider position. Null = leave unchanged / not supported.</summary>
public sealed record SmartPowerPlan(
    int Position,
    string Label,
    double? TdpWatts,
    int? RefreshHz,
    Guid? PowerPlanId,
    int FpsTarget,
    string FpsNote);

/// <summary>
/// Smart Power Control: one 0-100 slider (battery life ↔ performance) mapped onto the settings the
/// device supports. Deterministic and unit-tested; the UI applies the result through the same
/// providers as everything else, and per-game profiles can store a slider position instead of
/// individual values.
///
/// Anchors: 0 % = max battery, 50 % = balanced (~20 W), 100 % = max performance (up to 35 W if the CPU
/// allows). On battery the TDP range is unchanged but the refresh rate stays one step lower above 50 %.
/// The FPS target is advisory (Lumen does not inject a limiter) - shown so the user can set it in game.
/// </summary>
public static class SmartPower
{
    public static readonly Guid PowerSaverPlan = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid BalancedPlan = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformancePlan = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformancePlan = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const double LowWatts = 8, MidWatts = 20, HighWatts = 35;

    public static SmartPowerPlan Compute(int position, SmartPowerCapabilities caps, bool onBattery)
    {
        position = Math.Clamp(position, 0, 100);
        var t = position / 100.0;

        double? tdp = null;
        if (caps.Tdp is { } range)
        {
            var target = t <= 0.5
                ? LowWatts + (MidWatts - LowWatts) * (t / 0.5)
                : MidWatts + (HighWatts - MidWatts) * ((t - 0.5) / 0.5);
            tdp = Math.Round(range.Clamp(target));
        }

        int? refresh = null;
        if (caps.RefreshRates.Count > 1)
        {
            var rates = caps.RefreshRates.OrderBy(r => r).ToList();
            var max = rates[^1];
            if (position < 34) refresh = Nearest(rates, 60);
            else if (position < 67) refresh = Nearest(rates, Math.Min(90, max));
            else refresh = onBattery ? Nearest(rates, Math.Min(120, max)) : max;
        }

        Guid? plan = null;
        if (caps.PowerPlans.Count > 1)
        {
            bool Has(Guid g) => caps.PowerPlans.Any(p => p.Id == g);
            if (position < 34) plan = Has(PowerSaverPlan) ? PowerSaverPlan : Has(BalancedPlan) ? BalancedPlan : null;
            else if (position < 67) plan = Has(BalancedPlan) ? BalancedPlan : null;
            else plan = Has(HighPerformancePlan) ? HighPerformancePlan : Has(UltimatePerformancePlan) ? UltimatePerformancePlan : Has(BalancedPlan) ? BalancedPlan : null;
        }

        var fps = position < 34 ? 30 : position < 67 ? (position < 50 ? 40 : 60) : Math.Max(60, refresh ?? 60);
        var label = position < 20 ? "Maximum battery" : position < 45 ? "Battery saver" : position <= 55 ? "Balanced" : position < 85 ? "Performance" : "Maximum performance";

        return new SmartPowerPlan(position, label, tdp, refresh, plan, fps,
            "Target for the game's own frame limiter - Lumen does not inject a limiter into games.");
    }

    private static int Nearest(IReadOnlyList<int> rates, int target) =>
        rates.OrderBy(r => Math.Abs(r - target)).ThenByDescending(r => r).First();

    /// <summary>
    /// Runtime estimate from real battery telemetry. When the user previews a different TDP, the
    /// difference to the *measured* package power is added to the measured system draw. Returns null
    /// when there is not enough data (plugged in, no discharge rate yet).
    /// </summary>
    public static TimeSpan? EstimateRuntime(double? remainingWh, double? systemDischargeWatts, double? measuredPackageWatts = null, double? plannedTdpWatts = null)
    {
        if (remainingWh is not > 0 || systemDischargeWatts is not > 0.5) return null;
        var draw = systemDischargeWatts.Value;
        if (measuredPackageWatts is { } pkg && plannedTdpWatts is { } planned && planned < pkg)
            draw = Math.Max(draw - (pkg - planned), draw * 0.3); // lower cap reduces draw; never below 30 % of today
        return TimeSpan.FromHours(remainingWh.Value / draw);
    }
}

/// <summary>Exponential moving average for noisy battery discharge readings.</summary>
public sealed class DischargeAverager
{
    private readonly double _alpha;
    public DischargeAverager(double alpha = 0.25) => _alpha = Math.Clamp(alpha, 0.01, 1);

    public double? Value { get; private set; }

    public double? Add(double? watts)
    {
        if (watts is null or <= 0) { Value = null; return null; } // charging / unknown resets the average
        Value = Value is null ? watts : Value + _alpha * (watts.Value - Value.Value);
        return Value;
    }
}
