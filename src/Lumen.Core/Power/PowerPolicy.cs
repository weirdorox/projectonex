using Lumen.Core.Hardware;

namespace Lumen.Core.Power;

/// <summary>Resource budget for the shell itself, chosen from power state.</summary>
public sealed record ShellPerformanceProfile(
    string Name,
    int ForegroundInputHz,
    int BackgroundInputHz,
    TimeSpan MonitorInterval,
    bool AnimationsEnabled)
{
    public TimeSpan ForegroundInputInterval => TimeSpan.FromMilliseconds(1000.0 / ForegroundInputHz);
    public TimeSpan BackgroundInputInterval => TimeSpan.FromMilliseconds(1000.0 / BackgroundInputHz);
}

/// <summary>
/// Battery-aware behaviour. The shell is idle most of the time (a game is running), so the big
/// wins are polling less in the background and doing less animation on low battery.
/// </summary>
public static class PowerPolicy
{
    public static readonly ShellPerformanceProfile PluggedIn = new("Plugged in", 60, 15, TimeSpan.FromSeconds(1), true);
    public static readonly ShellPerformanceProfile OnBattery = new("Battery", 60, 10, TimeSpan.FromSeconds(2), true);
    public static readonly ShellPerformanceProfile LowBattery = new("Low battery", 30, 5, TimeSpan.FromSeconds(5), false);

    public const int LowBatteryPercent = 20;

    public static ShellPerformanceProfile Choose(BatteryStatus status)
    {
        if (!status.HasBattery || status.Source == PowerSource.AC) return PluggedIn;
        if (status.BatterySaverOn) return LowBattery;
        if (status.Percent is { } p && p <= LowBatteryPercent) return LowBattery;
        return OnBattery;
    }
}
