namespace Lumen.Core.Hardware;

/// <summary>
/// Features the shell can expose. A feature is only shown as available when a concrete,
/// documented provider exists for the current device - nothing is assumed from the model name.
/// </summary>
public enum HardwareFeature
{
    Battery,
    BatteryTelemetry,       // discharge watts, capacity, health (Windows.Devices.Power)
    Volume,
    Brightness,
    RefreshRate,
    PowerPlan,
    CpuUsage,
    GpuUsage,
    MemoryUsage,
    ThermalZoneTemperature, // ACPI thermal zone - approximate, not CPU package temperature
    CpuTemperature,         // requires a vendor/driver backend - not implemented
    GpuTemperature,         // requires a vendor/driver backend - not implemented
    FanControl,             // undocumented EC access on OneXPlayer - not implemented
    TdpControl,             // Intel RAPL via the optional PawnIO driver + elevated helper (runtime-detected)
}

/// <summary>SMBIOS identity as reported by firmware (HKLM\HARDWARE\DESCRIPTION\System\BIOS).</summary>
public sealed record DeviceIdentity(
    string Manufacturer,
    string ProductName,
    string BaseboardProduct,
    string SystemFamily,
    string BiosVersion)
{
    public static DeviceIdentity Unknown { get; } = new("", "", "", "", "");
    public override string ToString() => $"{Manufacturer} {ProductName} (board {BaseboardProduct}, family {SystemFamily}, BIOS {BiosVersion})".Trim();
}

/// <summary>Static, device-specific traits used for layout defaults and documentation only.</summary>
public sealed record DeviceTraits(
    int? NativeWidth = null,
    int? NativeHeight = null,
    double? DiagonalInches = null,
    int? MaxRefreshHz = null,
    bool DetachableControllers = false,
    string? Notes = null);

public enum PowerSource { Unknown, Battery, AC }

public sealed record BatteryStatus(
    bool HasBattery,
    int? Percent,
    PowerSource Source,
    bool IsCharging,
    bool BatterySaverOn,
    TimeSpan? Remaining)
{
    public static BatteryStatus Unknown { get; } = new(false, null, PowerSource.Unknown, false, false, null);
}

/// <summary>Battery report (Windows.Devices.Power.Battery.AggregateBattery). Values are null when unknown.</summary>
public sealed record BatteryTelemetry(double? ChargeRateWatts, double? RemainingWh, double? FullChargeWh, double? DesignWh)
{
    /// <summary>Positive watts drawn from the battery, null while charging/idle/unknown.</summary>
    public double? DischargeWatts => ChargeRateWatts is < 0 ? -ChargeRateWatts : null;

    public double? ChargeWatts => ChargeRateWatts is > 0 ? ChargeRateWatts : null;

    /// <summary>Full-charge capacity as a percentage of design capacity.</summary>
    public double? HealthPercent => FullChargeWh is > 0 && DesignWh is > 0 ? Math.Round(100 * FullChargeWh.Value / DesignWh.Value, 1) : null;
}

public interface IBatteryTelemetry { BatteryTelemetry? Read(); }

/// <summary>Live TDP state as reported by a backend. Every number is read from hardware, never assumed.</summary>
public sealed record TdpStatus
{
    public required bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public string Backend { get; init; } = "";
    public string? Cpu { get; init; }
    public double? MinWatts { get; init; }
    public double? MaxWatts { get; init; }
    public string? RangeSource { get; init; }
    /// <summary>Last value Lumen asked for (null = firmware default).</summary>
    public double? RequestedWatts { get; init; }
    /// <summary>PL1/PL2 read back from the CPU after writing.</summary>
    public double? Pl1Watts { get; init; }
    public double? Pl2Watts { get; init; }
    public double? DefaultPl1Watts { get; init; }
    public double? DefaultPl2Watts { get; init; }
    /// <summary>Measured package power (energy counter).</summary>
    public double? PackageWatts { get; init; }
    public double? PackageCelsius { get; init; }
    public bool Locked { get; init; }
    /// <summary>Firmware keeps rewriting the limit (e.g. Intel DTT); Lumen stopped fighting it.</summary>
    public bool FirmwareOverride { get; init; }
    public string? Message { get; init; }

    public static TdpStatus Unavailable(string reason, string backend = "") => new() { Available = false, UnavailableReason = reason, Backend = backend };
}

/// <summary>A TDP implementation (Intel RAPL via helper; AMD slot reserved). Async because it may cross a process boundary.</summary>
public interface ITdpBackend
{
    string Name { get; }
    Task<TdpStatus> GetStatusAsync(CancellationToken ct = default);
    Task<TdpStatus> SetAsync(double watts, CancellationToken ct = default);
    Task<TdpStatus> RestoreDefaultsAsync(CancellationToken ct = default);
}

public sealed record SystemSnapshot(
    DateTimeOffset Timestamp,
    double? CpuPercent,
    double? GpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? ThermalZoneCelsius,
    double? CpuCelsius,
    double? GpuCelsius)
{
    public double? MemoryPercent =>
        MemoryUsedBytes is { } u && MemoryTotalBytes is { } t && t > 0 ? 100.0 * u / t : null;
}

public sealed record DisplayMode(int Width, int Height, int RefreshHz);

public sealed record PowerPlan(Guid Id, string Name);

// ---- provider contracts: one interface per capability ----

public interface IBatteryInfo { BatteryStatus Read(); }

public interface IVolumeControl
{
    /// <summary>0..1</summary>
    float GetLevel();
    void SetLevel(float level);
    bool GetMuted();
    void SetMuted(bool muted);
}

public interface IBrightnessControl
{
    /// <summary>0..100 or null when it can't be read.</summary>
    int? GetBrightness();
    bool SetBrightness(int percent);
}

public interface IDisplayModeControl
{
    DisplayMode? GetCurrent();
    IReadOnlyList<int> GetSupportedRefreshRates();
    bool SetRefreshRate(int hz);
}

public interface IPowerPlanControl
{
    IReadOnlyList<PowerPlan> GetPlans();
    Guid? GetActivePlan();
    bool SetActivePlan(Guid id);
}

public interface ISystemMonitor : IDisposable
{
    /// <summary>Takes a sample. Rate-based values (CPU/GPU %) compare against the previous call.</summary>
    SystemSnapshot Sample();
}

/// <summary>Reserved for verified vendor backends. No implementation ships - see docs/HARDWARE.md.</summary>
public interface ITdpControl
{
    (int MinWatts, int MaxWatts) Range { get; }
    int? GetWatts();
    bool SetWatts(int watts);
}

/// <summary>Reserved for verified vendor backends. No implementation ships - see docs/HARDWARE.md.</summary>
public interface IFanControl
{
    int? GetRpm();
    bool SetAuto();
    bool SetPercent(int percent);
}

/// <summary>
/// The capability set for the running device. Null members mean "not supported" and the UI hides
/// or greys out the matching control with an explanation.
/// </summary>
public sealed class HardwareServices
{
    public required IDeviceProfile Profile { get; init; }
    public required DeviceIdentity Identity { get; init; }
    public IBatteryInfo? Battery { get; init; }
    public IBatteryTelemetry? BatteryTelemetry { get; init; }
    public IVolumeControl? Volume { get; init; }
    public IBrightnessControl? Brightness { get; init; }
    public IDisplayModeControl? Display { get; init; }
    public IPowerPlanControl? PowerPlans { get; init; }
    public ISystemMonitor? Monitor { get; init; }
    public ITdpControl? Tdp { get; init; }
    public IFanControl? Fan { get; init; }
    public IResolutionControl? Resolution { get; init; }
    public IDisplayTopology? Displays { get; init; }
    public IAudioDevices? Audio { get; init; }

    public bool Supports(HardwareFeature f) => f switch
    {
        HardwareFeature.Battery => Battery is not null,
        HardwareFeature.BatteryTelemetry => BatteryTelemetry is not null,
        HardwareFeature.Volume => Volume is not null,
        HardwareFeature.Brightness => Brightness is not null,
        HardwareFeature.RefreshRate => Display is not null,
        HardwareFeature.PowerPlan => PowerPlans is not null,
        HardwareFeature.CpuUsage or HardwareFeature.GpuUsage or HardwareFeature.MemoryUsage
            or HardwareFeature.ThermalZoneTemperature => Monitor is not null,
        HardwareFeature.TdpControl => Tdp is not null,
        HardwareFeature.FanControl => Fan is not null,
        _ => false,
    };
}
