using Lumen.Core.Diagnostics;
using Windows.Devices.Radios;

namespace Lumen.Platform.Windows.Connectivity;

public enum RadioType { WiFi, Bluetooth }

/// <summary>
/// Wi-Fi / Bluetooth on-off via the documented Windows.Devices.Radios API. Returns nothing (and the
/// UI hides the toggle) when the device has no such radio or the user denied radio access in
/// Settings > Privacy & security > Radios.
/// </summary>
public sealed class RadioControl
{
    private readonly Dictionary<RadioType, Radio> _radios = new();

    private RadioControl() { }

    public static async Task<RadioControl?> TryCreateAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Log.Info($"Radio access: {access}.");
                return null;
            }
            var control = new RadioControl();
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                var type = radio.Kind switch
                {
                    RadioKind.WiFi => RadioType.WiFi,
                    RadioKind.Bluetooth => RadioType.Bluetooth,
                    _ => (RadioType?)null,
                };
                if (type is { } t) control._radios.TryAdd(t, radio);
            }
            return control._radios.Count == 0 ? null : control;
        }
        catch (Exception ex)
        {
            Log.Info($"Radio control unavailable: {ex.Message}");
            return null;
        }
    }

    public bool Has(RadioType type) => _radios.ContainsKey(type);

    public bool? IsOn(RadioType type) =>
        _radios.TryGetValue(type, out var r) ? r.State == RadioState.On : null;

    public async Task<bool> SetAsync(RadioType type, bool on)
    {
        if (!_radios.TryGetValue(type, out var r)) return false;
        try
        {
            var result = await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
            Log.Info($"{type} -> {(on ? "on" : "off")}: {result}");
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Warn($"Changing {type} failed.", ex);
            return false;
        }
    }
}
