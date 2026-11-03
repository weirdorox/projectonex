using Lumen.Core.Diagnostics;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace Lumen.Platform.Windows.Connectivity;

public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected);

/// <summary>
/// Wi-Fi networks through the documented Windows.Devices.WiFi API: scan, connect (with password),
/// disconnect. Windows 11 requires location access for Wi-Fi scans ("Let desktop apps access your
/// location"); without it the list stays empty and Lumen says why.
/// </summary>
public sealed class WifiManager
{
    private readonly WiFiAdapter _adapter;

    private WifiManager(WiFiAdapter adapter) => _adapter = adapter;

    public static async Task<(WifiManager? Manager, string? Problem)> TryCreateAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
                return (null, "Windows did not allow Wi-Fi access (Settings › Privacy & security › Location: let desktop apps access your location).");
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            return adapters.Count == 0 ? (null, "No Wi-Fi adapter found.") : (new WifiManager(adapters[0]), null);
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi API unavailable.", ex);
            return (null, "Wi-Fi control is not available on this PC.");
        }
    }

    public async Task<string?> ConnectedSsidAsync()
    {
        try
        {
            var profile = await _adapter.NetworkAdapter.GetConnectedProfileAsync();
            return profile?.IsWlanConnectionProfile == true ? profile.WlanConnectionProfileDetails.GetConnectedSsid() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WifiNetwork>> ScanAsync()
    {
        await _adapter.ScanAsync();
        var connected = await ConnectedSsidAsync();
        return _adapter.NetworkReport.AvailableNetworks
            .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
            .GroupBy(n => n.Ssid)
            .Select(g => g.OrderByDescending(n => n.SignalBars).First())
            .Select(n => new WifiNetwork(n.Ssid, n.SignalBars,
                n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                n.Ssid == connected))
            .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars).ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Connects (saving the network for automatic reconnection). Password null = open or already saved.</summary>
    public async Task<(bool Ok, bool NeedsPassword, string Message)> ConnectAsync(string ssid, string? password)
    {
        var network = _adapter.NetworkReport.AvailableNetworks.Where(n => n.Ssid == ssid).OrderByDescending(n => n.SignalBars).FirstOrDefault();
        if (network is null) return (false, false, "Network not in range any more.");
        try
        {
            var result = password is null
                ? await _adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            Log.Info($"Wi-Fi connect: {result.ConnectionStatus}.");
            return result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => (true, false, $"Connected to {ssid}."),
                WiFiConnectionStatus.InvalidCredential => (false, true, password is null ? "This network needs a password." : "Wrong password."),
                WiFiConnectionStatus.AccessRevoked => (false, false, "Windows revoked Wi-Fi access."),
                WiFiConnectionStatus.Timeout => (false, false, "Connecting timed out."),
                WiFiConnectionStatus.UnsupportedAuthenticationProtocol => (false, false, "This network type needs Windows Settings (e.g. enterprise login)."),
                _ => (false, false, "Could not connect."),
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi connect failed.", ex);
            return (false, false, "Could not connect.");
        }
    }

    public void Disconnect() => _adapter.Disconnect();
}
