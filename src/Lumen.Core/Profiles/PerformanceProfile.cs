namespace Lumen.Core.Profiles;

public enum ProfileField { RefreshRate, PowerPlan, Brightness, Tdp }

public enum FieldOutcome
{
    /// <summary>The value was changed.</summary>
    Applied,
    /// <summary>Already at the requested value - nothing touched.</summary>
    Unchanged,
    /// <summary>No supported provider on this device; skipped.</summary>
    Unsupported,
    /// <summary>The provider refused the value (e.g. driver rejected a refresh rate).</summary>
    Failed,
}

/// <summary>
/// Settings to use while a specific game runs. Null fields mean "leave as is".
/// TDP is part of the model so profiles stay portable, but it is only ever applied when a verified
/// provider exists - none ships for the OneXPlayer 3.
/// </summary>
public sealed record PerformanceProfile
{
    public required string GameId { get; init; }
    public int? RefreshHz { get; init; }
    public Guid? PowerPlanId { get; init; }
    public int? BrightnessPercent { get; init; }
    public int? TdpWatts { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsEmpty => RefreshHz is null && PowerPlanId is null && BrightnessPercent is null && TdpWatts is null;

    public IEnumerable<ProfileField> DefinedFields()
    {
        if (RefreshHz is not null) yield return ProfileField.RefreshRate;
        if (PowerPlanId is not null) yield return ProfileField.PowerPlan;
        if (BrightnessPercent is not null) yield return ProfileField.Brightness;
        if (TdpWatts is not null) yield return ProfileField.Tdp;
    }
}

/// <summary>Live hardware values (null = unknown / unsupported).</summary>
public sealed record HardwareSnapshot(int? RefreshHz, Guid? PowerPlanId, int? BrightnessPercent, int? TdpWatts)
{
    public static HardwareSnapshot Empty { get; } = new(null, null, null, null);
}

public sealed record ProfileApplyReport(IReadOnlyDictionary<ProfileField, FieldOutcome> Fields)
{
    public static ProfileApplyReport None { get; } = new(new Dictionary<ProfileField, FieldOutcome>());

    public bool AnyApplied => Fields.Values.Any(v => v == FieldOutcome.Applied);
    public IEnumerable<ProfileField> Unsupported => Fields.Where(f => f.Value == FieldOutcome.Unsupported).Select(f => f.Key);
    public IEnumerable<ProfileField> Failed => Fields.Where(f => f.Value == FieldOutcome.Failed).Select(f => f.Key);
}

/// <summary>Hardware as the profile engine sees it. Implemented over HardwareServices; faked in tests.</summary>
public interface IProfileHardware
{
    bool Supports(ProfileField field);
    HardwareSnapshot Capture();
    bool SetRefreshRate(int hz);
    bool SetPowerPlan(Guid id);
    bool SetBrightness(int percent);
    bool SetTdp(int watts);
}

public interface IProfileRepository
{
    Task<PerformanceProfile?> GetAsync(string gameId, CancellationToken ct = default);
    Task<IReadOnlyList<PerformanceProfile>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(PerformanceProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string gameId, CancellationToken ct = default);
}

public sealed class InMemoryProfileRepository : IProfileRepository
{
    private readonly Dictionary<string, PerformanceProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<PerformanceProfile?> GetAsync(string gameId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<PerformanceProfile?>(_profiles.TryGetValue(gameId, out var p) ? p : null);
    }

    public Task<IReadOnlyList<PerformanceProfile>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PerformanceProfile>>(_profiles.Values.ToList());
    }

    public Task SaveAsync(PerformanceProfile profile, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (profile.IsEmpty) _profiles.Remove(profile.GameId);
            else _profiles[profile.GameId] = profile;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string gameId, CancellationToken ct = default)
    {
        lock (_gate) _profiles.Remove(gameId);
        return Task.CompletedTask;
    }
}
