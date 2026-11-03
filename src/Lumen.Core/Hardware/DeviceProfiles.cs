namespace Lumen.Core.Hardware;

/// <summary>
/// A device profile decides which providers are allowed on a given machine and supplies static
/// traits. Profiles never enable undocumented controls; they can only *restrict* or annotate.
/// </summary>
public interface IDeviceProfile
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Higher wins when several profiles match.</summary>
    int Priority { get; }

    DeviceTraits Traits { get; }

    bool Matches(DeviceIdentity identity);

    /// <summary>
    /// Features this profile permits. Generic documented Windows APIs are permitted everywhere;
    /// vendor-specific features (TDP, fan) must be listed explicitly AND have a verified provider.
    /// </summary>
    IReadOnlySet<HardwareFeature> PermittedFeatures { get; }

    /// <summary>Human-readable notes about each unsupported feature, shown in the UI.</summary>
    string ExplainUnsupported(HardwareFeature feature);
}

public abstract class DeviceProfileBase : IDeviceProfile
{
    protected static readonly HardwareFeature[] DocumentedWindowsFeatures =
    [
        HardwareFeature.Battery, HardwareFeature.BatteryTelemetry, HardwareFeature.Volume, HardwareFeature.Brightness,
        HardwareFeature.RefreshRate, HardwareFeature.PowerPlan, HardwareFeature.CpuUsage,
        HardwareFeature.GpuUsage, HardwareFeature.MemoryUsage, HardwareFeature.ThermalZoneTemperature,
    ];

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual int Priority => 0;
    public virtual DeviceTraits Traits => new();
    public abstract bool Matches(DeviceIdentity identity);
    public virtual IReadOnlySet<HardwareFeature> PermittedFeatures { get; } = new HashSet<HardwareFeature>(DocumentedWindowsFeatures);

    public virtual string ExplainUnsupported(HardwareFeature feature) => feature switch
    {
        HardwareFeature.TdpControl =>
            "Real TDP control needs the optional PawnIO driver and the Lumen power helper (Settings > Performance). On Intel it writes the documented package power limits; firmware may lock or override them.",
        HardwareFeature.FanControl =>
            "Fan control needs direct embedded-controller access that is not publicly documented for this device. Use the manufacturer's tool.",
        HardwareFeature.CpuTemperature or HardwareFeature.GpuTemperature =>
            "Windows has no documented API for CPU/GPU die temperature. Only the ACPI thermal zone (approximate) is shown when available.",
        HardwareFeature.Brightness =>
            "The display did not accept brightness changes through the Windows WMI brightness interface.",
        _ => "Not available on this device.",
    };
}

/// <summary>Any Windows PC. Uses only documented Windows APIs.</summary>
public sealed class GenericWindowsProfile : DeviceProfileBase
{
    public override string Id => "generic";
    public override string DisplayName => "Generic Windows PC";
    public override int Priority => int.MinValue;
    public override bool Matches(DeviceIdentity identity) => true;
}

/// <summary>
/// Any ONE-NETBOOK / OneXPlayer device. Identification is by SMBIOS manufacturer string.
/// Uses the same documented Windows providers as the generic profile; vendor controls (TDP, fan,
/// RGB, button remapping) are handled by the vendor's OneXConsole app and are NOT driven by Lumen.
/// </summary>
public class OneXPlayerProfile : DeviceProfileBase
{
    public override string Id => "onexplayer";
    public override string DisplayName => "OneXPlayer (family)";
    public override int Priority => 10;

    public override bool Matches(DeviceIdentity identity) =>
        Contains(identity.Manufacturer, "ONE-NETBOOK") ||
        Contains(identity.Manufacturer, "ONEXPLAYER") ||
        Contains(identity.ProductName, "ONEXPLAYER");

    protected static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    protected static string Squash(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// OneXPlayer 3 (Intel Arc G3 Extreme, 8.8" 1920x1200 144 Hz OLED, detachable controllers).
/// The SMBIOS product string has not been verified on hardware; matching is deliberately lenient
/// ("ONEXPLAYER 3", "ONEXPLAYER3", "OneXPlayer 3 ..."). Settings > Device shows the detected
/// identity so the match can be confirmed or corrected.
/// </summary>
public sealed class OneXPlayer3Profile : OneXPlayerProfile
{
    public override string Id => "onexplayer-3";
    public override string DisplayName => "OneXPlayer 3";
    public override int Priority => 20;

    public override DeviceTraits Traits { get; } = new(
        NativeWidth: 1920,
        NativeHeight: 1200,
        DiagonalInches: 8.8,
        MaxRefreshHz: 144,
        DetachableControllers: true,
        Notes: "Detachable controllers may appear as one or two XInput devices; Lumen merges them.");

    public override bool Matches(DeviceIdentity identity)
    {
        if (!base.Matches(identity)) return false;
        var product = Squash(identity.ProductName);
        var family = Squash(identity.SystemFamily);
        return product.StartsWith("ONEXPLAYER3", StringComparison.Ordinal)
            || family.StartsWith("ONEXPLAYER3", StringComparison.Ordinal)
            || product is "ONEXPLAYER3" or "OX3";
    }
}

public sealed class DeviceProfileRegistry
{
    private readonly List<IDeviceProfile> _profiles;

    public DeviceProfileRegistry(IEnumerable<IDeviceProfile>? profiles = null)
    {
        _profiles = (profiles ?? Default()).ToList();
        if (!_profiles.Any(p => p is GenericWindowsProfile)) _profiles.Add(new GenericWindowsProfile());
    }

    public static IEnumerable<IDeviceProfile> Default() =>
        [new OneXPlayer3Profile(), new OneXPlayerProfile(), new GenericWindowsProfile()];

    public IDeviceProfile Resolve(DeviceIdentity identity) =>
        _profiles.Where(p => p.Matches(identity)).OrderByDescending(p => p.Priority).First();
}
