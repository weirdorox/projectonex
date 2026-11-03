namespace Lumen.Core.Hardware;

/// <summary>Providers that the platform layer *could* offer on this machine.</summary>
public sealed record ProviderCandidates(
    IBatteryInfo? Battery = null,
    IBatteryTelemetry? BatteryTelemetry = null,
    IVolumeControl? Volume = null,
    IBrightnessControl? Brightness = null,
    IDisplayModeControl? Display = null,
    IPowerPlanControl? PowerPlans = null,
    ISystemMonitor? Monitor = null,
    ITdpControl? Tdp = null,
    IFanControl? Fan = null,
    IResolutionControl? Resolution = null,
    IDisplayTopology? Displays = null,
    IAudioDevices? Audio = null);

public static class HardwareComposer
{
    /// <summary>
    /// Intersects what the platform can do with what the device profile permits.
    /// A provider is exposed only if it exists AND the profile permits the feature.
    /// </summary>
    public static HardwareServices Compose(IDeviceProfile profile, DeviceIdentity identity, ProviderCandidates c)
    {
        bool P(HardwareFeature f) => profile.PermittedFeatures.Contains(f);

        var monitorAllowed = P(HardwareFeature.CpuUsage) || P(HardwareFeature.GpuUsage) || P(HardwareFeature.MemoryUsage);

        return new HardwareServices
        {
            Profile = profile,
            Identity = identity,
            Battery = P(HardwareFeature.Battery) ? c.Battery : null,
            BatteryTelemetry = P(HardwareFeature.BatteryTelemetry) ? c.BatteryTelemetry : null,
            Volume = P(HardwareFeature.Volume) ? c.Volume : null,
            Brightness = P(HardwareFeature.Brightness) ? c.Brightness : null,
            Display = P(HardwareFeature.RefreshRate) ? c.Display : null,
            PowerPlans = P(HardwareFeature.PowerPlan) ? c.PowerPlans : null,
            Monitor = monitorAllowed ? c.Monitor : null,
            Tdp = P(HardwareFeature.TdpControl) ? c.Tdp : null,
            Fan = P(HardwareFeature.FanControl) ? c.Fan : null,
            // Generic, documented Windows display/audio APIs - not device specific.
            Resolution = P(HardwareFeature.RefreshRate) ? c.Resolution : null,
            Displays = c.Displays,
            Audio = c.Audio,
        };
    }
}
