using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Platform.Windows.Connectivity;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App;

/// <summary>System tab: Wi-Fi network list, connect with a controller-friendly password keyboard, disconnect.</summary>
public sealed partial class QuickAccessWindow
{
    private WifiManager? _wifi;
    private string? _wifiProblem;
    private bool _wifiProbed;
    private string? _wifiPendingSsid;
    private OnScreenKeyboard? _wifiKeyboard;

    private async void OnWifiScanClick(object sender, RoutedEventArgs e) => await ScanWifiAsync();

    private async Task ScanWifiAsync()
    {
        WifiScanButton.IsEnabled = false;
        ShowWifiStatus("Searching for networks…");
        try
        {
            if (!_wifiProbed)
            {
                _wifiProbed = true;
                (_wifi, _wifiProblem) = await WifiManager.TryCreateAsync();
            }
            if (_wifi is null)
            {
                ShowWifiStatus(_wifiProblem ?? "Wi-Fi is not available.");
                return;
            }

            var networks = await _wifi.ScanAsync();
            WifiList.Children.Clear();
            if (networks.Count == 0)
            {
                ShowWifiStatus("No networks found. Is Wi-Fi on? Windows also needs location access for desktop apps to list networks (Settings › Privacy & security › Location).");
                return;
            }
            WifiStatusText.Visibility = Visibility.Collapsed;
            foreach (var n in networks.Take(12))
            {
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = $"{Bars(n.Bars)}  {n.Ssid}{(n.Secured ? "  🔒" : "")}{(n.Connected ? "  · connected" : "")}",
                };
                if (n.Connected) Highlight(b);
                var network = n;
                b.Click += async (_, _) => await OnNetworkChosenAsync(network);
                WifiList.Children.Add(b);
            }
            FocusNavigation.Focus(WifiList.Children[0]);
        }
        catch (Exception ex)
        {
            Log.Warn("Wi-Fi scan failed.", ex);
            ShowWifiStatus("Scanning failed. Turn Wi-Fi on and try again.");
        }
        finally
        {
            WifiScanButton.IsEnabled = true;
        }
    }

    private async Task OnNetworkChosenAsync(WifiNetwork network)
    {
        if (_wifi is null) return;
        if (network.Connected)
        {
            _wifi.Disconnect();
            ShowWifiStatus($"Disconnected from {network.Ssid}.");
            await Task.Delay(800);
            await ScanWifiAsync();
            return;
        }

        // Saved or open networks connect directly; otherwise ask for the password.
        ShowWifiStatus($"Connecting to {network.Ssid}…");
        var (ok, needsPassword, message) = await _wifi.ConnectAsync(network.Ssid, null);
        if (ok)
        {
            ShowWifiStatus(message);
            await ScanWifiAsync();
            return;
        }
        if (needsPassword || network.Secured) AskPassword(network.Ssid);
        else ShowWifiStatus(message);
    }

    private void AskPassword(string ssid)
    {
        _wifiPendingSsid = ssid;
        WifiList.Children.Clear();
        WifiStatusText.Visibility = Visibility.Collapsed;
        WifiPasswordTitle.Text = $"Password for {ssid}";
        WifiPasswordBox.Password = string.Empty;
        if (_wifiKeyboard is null)
        {
            _wifiKeyboard = new OnScreenKeyboard(34) { Target = WifiPasswordBox };
            _wifiKeyboard.Done += async (_, _) => await ConnectWithPasswordAsync();
            WifiKeyboardHost.Child = _wifiKeyboard;
        }
        WifiPasswordPanel.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusNavigation.Focus(_wifiKeyboard.FirstKey));
    }

    private async void OnWifiConnectClick(object sender, RoutedEventArgs e) => await ConnectWithPasswordAsync();

    private async Task ConnectWithPasswordAsync()
    {
        if (_wifi is null || _wifiPendingSsid is null) return;
        WifiConnectButton.IsEnabled = false;
        try
        {
            WifiPasswordTitle.Text = $"Connecting to {_wifiPendingSsid}…";
            var (ok, _, message) = await _wifi.ConnectAsync(_wifiPendingSsid, WifiPasswordBox.Password);
            if (!ok)
            {
                WifiPasswordTitle.Text = $"{message} Password for {_wifiPendingSsid}";
                return;
            }
            WifiPasswordBox.Password = string.Empty;
            WifiPasswordPanel.Visibility = Visibility.Collapsed;
            _wifiPendingSsid = null;
            ShowWifiStatus(message);
            await ScanWifiAsync();
        }
        finally
        {
            WifiConnectButton.IsEnabled = true;
        }
    }

    private void OnWifiCancelClick(object sender, RoutedEventArgs e)
    {
        WifiPasswordBox.Password = string.Empty;
        WifiPasswordPanel.Visibility = Visibility.Collapsed;
        _wifiPendingSsid = null;
        FocusNavigation.Focus(WifiScanButton);
    }

    private void ShowWifiStatus(string text)
    {
        WifiStatusText.Text = text;
        WifiStatusText.Visibility = Visibility.Visible;
    }

    private static string Bars(int bars) => bars switch { >= 4 => "▂▄▆█", 3 => "▂▄▆_", 2 => "▂▄__", 1 => "▂___", _ => "____" };
}
