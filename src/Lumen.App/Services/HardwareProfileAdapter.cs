using Lumen.Core.Hardware;
using Lumen.Core.Profiles;

namespace Lumen.App.Services;

/// <summary>Maps profile fields onto the device's composed hardware providers. Missing provider = unsupported.</summary>
public sealed class HardwareProfileAdapter : IProfileHardware
{
    private readonly HardwareServices _hw;
    private readonly TdpService? _tdp;

    /// <summary>TDP value meaning "firmware default" (restore instead of set).</summary>
    public const int FirmwareDefaultTdp = 0;

    public HardwareProfileAdapter(HardwareServices hardware, TdpService? tdp = null)
    {
        _hw = hardware;
        _tdp = tdp;
    }

    public HardwareServices Services => _hw;

    public bool Supports(ProfileField field) => field switch
    {
        ProfileField.RefreshRate => _hw.Display is not null,
        ProfileField.PowerPlan => _hw.PowerPlans is not null,
        ProfileField.Brightness => _hw.Brightness is not null,
        ProfileField.Tdp => _tdp?.IsUsable == true,
        _ => false,
    };

    public HardwareSnapshot Capture() => new(
        _hw.Display?.GetCurrent()?.RefreshHz,
        _hw.PowerPlans?.GetActivePlan(),
        _hw.Brightness?.GetBrightness(),
        _tdp?.IsUsable == true ? (int?)Math.Round(_tdp.Status.RequestedWatts ?? FirmwareDefaultTdp) : null);

    public bool SetRefreshRate(int hz) => _hw.Display?.SetRefreshRate(hz) == true;
    public bool SetPowerPlan(Guid id) => _hw.PowerPlans?.SetActivePlan(id) == true;
    public bool SetBrightness(int percent) => _hw.Brightness?.SetBrightness(percent) == true;
    /// <summary>Synchronous bridge for the profile engine (always called off the UI thread).</summary>
    public bool SetTdp(int watts)
    {
        if (_tdp is null) return false;
        var status = watts == FirmwareDefaultTdp
            ? _tdp.RestoreAsync().GetAwaiter().GetResult()
            : _tdp.SetAsync(watts).GetAwaiter().GetResult();
        return status.Available && (watts == FirmwareDefaultTdp ? status.RequestedWatts is null : status.RequestedWatts == watts);
    }
}
