namespace Lumen.Core.Power;

/// <summary>RAPL units from MSR_RAPL_POWER_UNIT (0x606), Intel SDM Vol. 3B §15.10.1.</summary>
public readonly record struct RaplUnits(double PowerWatts, double EnergyJoules, double TimeSeconds);

/// <summary>Decoded MSR_PKG_POWER_LIMIT (0x610).</summary>
public sealed record PackagePowerLimit(
    double Pl1Watts, bool Pl1Enabled, bool Pl1Clamp, int Pl1TimeRaw,
    double Pl2Watts, bool Pl2Enabled, bool Pl2Clamp, int Pl2TimeRaw,
    bool Locked);

/// <summary>Decoded MSR_PKG_POWER_INFO (0x614). Zero means "not reported".</summary>
public sealed record PackagePowerInfo(double ThermalSpecWatts, double MinWatts, double MaxWatts);

/// <summary>The TDP range Lumen will offer, and where the limits came from.</summary>
public sealed record TdpRange(double MinWatts, double MaxWatts, string Source)
{
    public double Clamp(double watts) => Math.Clamp(watts, MinWatts, MaxWatts);
    public bool Contains(double watts) => watts >= MinWatts - 0.01 && watts <= MaxWatts + 0.01;
}

/// <summary>
/// Pure encode/decode logic for Intel package power limits. No hardware access here - the elevated
/// helper reads/writes the MSRs through PawnIO and uses these functions, so every bit manipulation
/// is unit-tested.
/// </summary>
public static class IntelRapl
{
    public const uint MsrRaplPowerUnit = 0x606;
    public const uint MsrPkgPowerLimit = 0x610;
    public const uint MsrPkgEnergyStatus = 0x611;
    public const uint MsrPkgPowerInfo = 0x614;
    public const uint MsrPackageThermStatus = 0x1B1;
    public const uint MsrTemperatureTarget = 0x1A2;

    /// <summary>Lowest limit Lumen will ever request when the CPU does not report a minimum (safety floor).</summary>
    public const double SafetyFloorWatts = 5;

    private const ulong PowerMask = 0x7FFF;

    public static RaplUnits DecodeUnits(ulong msr606) => new(
        PowerWatts: 1.0 / (1UL << (int)(msr606 & 0xF)),
        EnergyJoules: 1.0 / (1UL << (int)((msr606 >> 8) & 0x1F)),
        TimeSeconds: 1.0 / (1UL << (int)((msr606 >> 16) & 0xF)));

    public static PackagePowerLimit DecodeLimit(ulong msr610, RaplUnits u) => new(
        Pl1Watts: (msr610 & PowerMask) * u.PowerWatts,
        Pl1Enabled: (msr610 & (1UL << 15)) != 0,
        Pl1Clamp: (msr610 & (1UL << 16)) != 0,
        Pl1TimeRaw: (int)((msr610 >> 17) & 0x7F),
        Pl2Watts: ((msr610 >> 32) & PowerMask) * u.PowerWatts,
        Pl2Enabled: (msr610 & (1UL << 47)) != 0,
        Pl2Clamp: (msr610 & (1UL << 48)) != 0,
        Pl2TimeRaw: (int)((msr610 >> 49) & 0x7F),
        Locked: (msr610 & (1UL << 63)) != 0);

    /// <summary>
    /// Replaces only the PL1/PL2 power fields of <paramref name="original"/> and sets their enable
    /// bits. Time windows, clamp bits and the lock bit are preserved exactly.
    /// </summary>
    public static ulong EncodeLimit(ulong original, double pl1Watts, double pl2Watts, RaplUnits u)
    {
        if (u.PowerWatts <= 0) throw new ArgumentOutOfRangeException(nameof(u));
        var pl1 = (ulong)Math.Clamp(Math.Round(pl1Watts / u.PowerWatts), 0, PowerMask);
        var pl2 = (ulong)Math.Clamp(Math.Round(pl2Watts / u.PowerWatts), 0, PowerMask);
        var value = original;
        value = (value & ~PowerMask) | pl1;
        value = (value & ~(PowerMask << 32)) | (pl2 << 32);
        value |= 1UL << 15; // PL1 enable
        value |= 1UL << 47; // PL2 enable
        return value;
    }

    public static PackagePowerInfo DecodeInfo(ulong msr614, RaplUnits u) => new(
        ThermalSpecWatts: (msr614 & PowerMask) * u.PowerWatts,
        MinWatts: ((msr614 >> 16) & PowerMask) * u.PowerWatts,
        MaxWatts: ((msr614 >> 32) & PowerMask) * u.PowerWatts);

    /// <summary>
    /// Range offered to the user, derived from what the CPU and firmware report - never hard-coded.
    /// Max = CPU-reported maximum, else the firmware's own PL2/PL1 at boot. Min = CPU-reported minimum,
    /// else the safety floor.
    /// </summary>
    public static TdpRange ComputeRange(PackagePowerInfo info, PackagePowerLimit firmwareDefault)
    {
        double max;
        string source;
        if (info.MaxWatts > 0)
        {
            max = info.MaxWatts;
            source = "CPU maximum power (MSR 0x614)";
        }
        else
        {
            max = Math.Max(firmwareDefault.Pl2Watts, firmwareDefault.Pl1Watts);
            source = "firmware default limits (MSR 0x610 at boot)";
        }

        var min = info.MinWatts > 0 ? info.MinWatts : SafetyFloorWatts;
        if (max < min) max = min;
        return new TdpRange(Math.Round(min, 1), Math.Round(max, 1), source);
    }

    /// <summary>Average package power from two MSR_PKG_ENERGY_STATUS readings (32-bit counter, wraps).</summary>
    public static double? PackageWatts(ulong energyBefore, ulong energyAfter, TimeSpan elapsed, RaplUnits u)
    {
        if (elapsed <= TimeSpan.Zero) return null;
        var delta = (energyAfter - energyBefore) & 0xFFFFFFFF;
        return delta * u.EnergyJoules / elapsed.TotalSeconds;
    }

    /// <summary>Package temperature from IA32_PACKAGE_THERM_STATUS and MSR_TEMPERATURE_TARGET (TjMax - readout).</summary>
    public static double? PackageCelsius(ulong thermStatus, ulong temperatureTarget)
    {
        if ((thermStatus & (1UL << 31)) == 0) return null; // reading not valid
        var tjMax = (temperatureTarget >> 16) & 0xFF;
        if (tjMax == 0) return null;
        var below = (thermStatus >> 16) & 0x7F;
        return tjMax - (double)below;
    }
}

public sealed record TdpPreset(string Name, double TargetWatts);

public sealed record TdpPresetAvailability(TdpPreset Preset, bool Available, double AppliedWatts, string? Reason);

/// <summary>Named targets. Targets outside the detected range are disabled with an explanation, never clamped silently.</summary>
public static class TdpPresets
{
    public static readonly TdpPreset Eco = new("Eco", 10);
    public static readonly TdpPreset Balanced = new("Balanced", 20);
    public static readonly TdpPreset Performance = new("Performance", 28);
    public static readonly TdpPreset Turbo = new("Turbo", 35);

    public static IReadOnlyList<TdpPreset> All { get; } = [Eco, Balanced, Performance, Turbo];

    public static IReadOnlyList<TdpPresetAvailability> Resolve(TdpRange? range) =>
        All.Select(p =>
        {
            if (range is null) return new TdpPresetAvailability(p, false, p.TargetWatts, "TDP control is not available.");
            if (p.TargetWatts > range.MaxWatts + 0.01)
                return new TdpPresetAvailability(p, false, p.TargetWatts, $"Above this CPU's maximum of {range.MaxWatts:0.#} W ({range.Source}).");
            if (p.TargetWatts < range.MinWatts - 0.01)
                return new TdpPresetAvailability(p, false, p.TargetWatts, $"Below this CPU's minimum of {range.MinWatts:0.#} W.");
            return new TdpPresetAvailability(p, true, p.TargetWatts, null);
        }).ToList();
}
