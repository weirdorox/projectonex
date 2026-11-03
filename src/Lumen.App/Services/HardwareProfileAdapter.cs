using Lumen.Core.Hardware;
using Lumen.Core.Profiles;

namespace Lumen.App.Services;

/// <summary>Maps profile fields onto the device's composed hardware providers. Missing provider = unsupported.</summary>
public sealed class HardwareProfileAdapter : IProfileHardware
{
    private readonly HardwareServices _hw;

    public HardwareProfileAdapter(HardwareServices hardware) => _hw = hardware;

    public HardwareServices Services => _hw;

    public bool Supports(ProfileField field) => field switch
    {
        ProfileField.RefreshRate => _hw.Display is not null,
        ProfileField.PowerPlan => _hw.PowerPlans is not null,
        ProfileField.Brightness => _hw.Brightness is not null,
        ProfileField.Tdp => _hw.Tdp is not null,
        _ => false,
    };

    public HardwareSnapshot Capture() => new(
        _hw.Display?.GetCurrent()?.RefreshHz,
        _hw.PowerPlans?.GetActivePlan(),
        _hw.Brightness?.GetBrightness(),
        _hw.Tdp?.GetWatts());

    public bool SetRefreshRate(int hz) => _hw.Display?.SetRefreshRate(hz) == true;
    public bool SetPowerPlan(Guid id) => _hw.PowerPlans?.SetActivePlan(id) == true;
    public bool SetBrightness(int percent) => _hw.Brightness?.SetBrightness(percent) == true;
    public bool SetTdp(int watts) => _hw.Tdp?.SetWatts(watts) == true;
}
