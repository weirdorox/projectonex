using Lumen.Core.Diagnostics;

namespace Lumen.Core.Profiles;

/// <summary>
/// Applies a game's profile for the length of a play session and puts everything back afterwards.
///
/// - <see cref="BeginSession"/> captures a baseline of the live settings *before* changing anything.
/// - Only fields the profile defines are touched; unsupported fields are reported and skipped, values
///   already in place are left alone (no needless display mode switches).
/// - <see cref="UpdateProfile"/> lets the user edit the profile mid-session (baseline is kept).
/// - <see cref="EndSession"/> restores the baseline for every field the session changed.
/// - <see cref="Reapply"/> re-asserts the profile after sleep (drivers may reset display modes).
/// </summary>
public sealed class ProfileEngine
{
    private readonly IProfileHardware _hw;
    private readonly HashSet<ProfileField> _touched = [];

    public ProfileEngine(IProfileHardware hardware) => _hw = hardware;

    public string? SessionGameId { get; private set; }
    public PerformanceProfile? ActiveProfile { get; private set; }
    public HardwareSnapshot? Baseline { get; private set; }

    public ProfileApplyReport BeginSession(string gameId, PerformanceProfile? profile, HardwareSnapshot? knownBaseline = null)
    {
        if (SessionGameId is not null && !string.Equals(SessionGameId, gameId, StringComparison.OrdinalIgnoreCase))
            EndSession();

        if (SessionGameId is null)
        {
            SessionGameId = gameId;
            Baseline = knownBaseline ?? _hw.Capture();
            _touched.Clear();
            Log.Info($"Profile session started for {gameId}; baseline {Baseline}.");
        }
        return UpdateProfile(profile);
    }

    public ProfileApplyReport UpdateProfile(PerformanceProfile? profile)
    {
        if (SessionGameId is null) return ProfileApplyReport.None;
        var previous = ActiveProfile;
        ActiveProfile = profile is null || profile.IsEmpty ? null : profile;

        // Fields dropped from the profile go back to their baseline value.
        if (previous is not null)
        {
            var dropped = previous.DefinedFields().Except(ActiveProfile?.DefinedFields() ?? []).ToList();
            if (dropped.Count > 0) Restore(dropped);
        }

        return ActiveProfile is null ? ProfileApplyReport.None : Apply(ActiveProfile);
    }

    public ProfileApplyReport Reapply() =>
        SessionGameId is null || ActiveProfile is null ? ProfileApplyReport.None : Apply(ActiveProfile);

    public ProfileApplyReport EndSession()
    {
        if (SessionGameId is null) return ProfileApplyReport.None;
        var report = Restore(_touched.ToList());
        Log.Info($"Profile session for {SessionGameId} ended; restored {string.Join(", ", report.Fields.Where(f => f.Value == FieldOutcome.Applied).Select(f => f.Key))}.");
        SessionGameId = null;
        ActiveProfile = null;
        Baseline = null;
        _touched.Clear();
        return report;
    }

    private ProfileApplyReport Apply(PerformanceProfile p)
    {
        var live = _hw.Capture();
        var result = new Dictionary<ProfileField, FieldOutcome>();
        if (p.RefreshHz is { } hz) result[ProfileField.RefreshRate] = Set(ProfileField.RefreshRate, live.RefreshHz == hz, () => _hw.SetRefreshRate(hz));
        if (p.PowerPlanId is { } plan) result[ProfileField.PowerPlan] = Set(ProfileField.PowerPlan, live.PowerPlanId == plan, () => _hw.SetPowerPlan(plan));
        if (p.BrightnessPercent is { } br) result[ProfileField.Brightness] = Set(ProfileField.Brightness, live.BrightnessPercent == br, () => _hw.SetBrightness(br));
        if (p.TdpWatts is { } w) result[ProfileField.Tdp] = Set(ProfileField.Tdp, live.TdpWatts == w, () => _hw.SetTdp(w));
        return new ProfileApplyReport(result);
    }

    private FieldOutcome Set(ProfileField field, bool alreadySet, Func<bool> set)
    {
        if (!_hw.Supports(field)) return FieldOutcome.Unsupported;
        if (alreadySet)
        {
            _touched.Add(field); // still restore later: the user expects the pre-game value back
            return FieldOutcome.Unchanged;
        }
        bool ok;
        try { ok = set(); }
        catch (Exception ex)
        {
            Log.Warn($"Applying {field} failed.", ex);
            ok = false;
        }
        if (ok) _touched.Add(field);
        return ok ? FieldOutcome.Applied : FieldOutcome.Failed;
    }

    private ProfileApplyReport Restore(IReadOnlyCollection<ProfileField> fields)
    {
        var result = new Dictionary<ProfileField, FieldOutcome>();
        var b = Baseline;
        if (b is null) return new ProfileApplyReport(result);
        var live = _hw.Capture();
        foreach (var f in fields)
        {
            result[f] = f switch
            {
                ProfileField.RefreshRate when b.RefreshHz is { } hz => Set(f, live.RefreshHz == hz, () => _hw.SetRefreshRate(hz)),
                ProfileField.PowerPlan when b.PowerPlanId is { } plan => Set(f, live.PowerPlanId == plan, () => _hw.SetPowerPlan(plan)),
                ProfileField.Brightness when b.BrightnessPercent is { } br => Set(f, live.BrightnessPercent == br, () => _hw.SetBrightness(br)),
                ProfileField.Tdp when b.TdpWatts is { } w => Set(f, live.TdpWatts == w, () => _hw.SetTdp(w)),
                _ => FieldOutcome.Unsupported,
            };
            _touched.Remove(f);
        }
        return new ProfileApplyReport(result);
    }
}
