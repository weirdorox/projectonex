using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Builds the <see cref="HardwareServices"/> for this machine: detect identity, pick the device
/// profile, probe each documented provider, and let the profile veto anything it does not permit.
/// Vendor TDP/fan providers are never created: none is verified for OneXPlayer hardware.
/// </summary>
public static class WindowsHardwareFactory
{
    public static HardwareServices Create()
    {
        var identity = SystemIdentityReader.Read(WindowsRegistryReader.Instance);
        var profile = new DeviceProfileRegistry().Resolve(identity);
        Log.Info($"Device: {identity} -> profile '{profile.Id}'.");

        var candidates = new ProviderCandidates(
            Battery: Try("battery", () => new Win32BatteryInfo()),
            BatteryTelemetry: Try("battery telemetry", AggregateBatteryTelemetry.TryCreate),
            Volume: Try("volume", CoreAudioVolumeControl.TryCreate),
            Brightness: Try("brightness", CombinedBrightnessControl.TryCreate),
            Display: Try("display", () => new DisplayModeControl()),
            Resolution: Try("resolution", () => new DisplayModeControl()),
            Displays: Try("display topology", DisplayTopology.TryCreate),
            Audio: Try("audio devices", CoreAudioDevices.TryCreate),
            PowerPlans: Try("power plans", () => new PowerPlanControl()),
            Monitor: Try("monitor", () => new WindowsSystemMonitor()),
            Tdp: null,
            Fan: null);

        var services = HardwareComposer.Compose(profile, identity, candidates);
        foreach (var f in Enum.GetValues<HardwareFeature>())
            Log.Info($"  {f}: {(services.Supports(f) ? "supported" : "not supported")}");
        return services;
    }

    private static T? Try<T>(string name, Func<T?> create) where T : class
    {
        try
        {
            return create();
        }
        catch (Exception ex)
        {
            Log.Warn($"Hardware provider '{name}' unavailable.", ex);
            return null;
        }
    }
}
