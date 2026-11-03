using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Lumen.App.Views;

/// <summary>Settings › Display &amp; sound: brightness and HDR right here; everything else in Quick settings.</summary>
public sealed partial class SettingsPage
{
    private HardwareServices? _displayHw;
    private DisplayInfo? _settingsHdrTarget;
    private bool _displayLoading = true;

    private async Task RenderDisplayAsync()
    {
        _displayLoading = true;
        try
        {
            _displayHw ??= await Host.Hardware.Value;
            var brightness = await Task.Run(() => _displayHw.Brightness?.GetBrightness());
            SettingsBrightnessSlider.IsEnabled = brightness is not null;
            if (brightness is { } b) SettingsBrightnessSlider.Value = Math.Max(5, b);
            SettingsBrightnessNote.Text = brightness is null
                ? "Brightness can't be changed on this display. For a monitor, turn on DDC/CI in its own on-screen menu (many TVs don't support it)."
                : string.Empty;

            var displays = await Task.Run(() => _displayHw.Displays?.GetActive() ?? []);
            _settingsHdrTarget = DockDetector.Target(displays) ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            var t = _settingsHdrTarget;
            SettingsHdrToggle.IsOn = t?.HdrEnabled == true;
            SettingsHdrToggle.IsEnabled = t is { HdrSupported: true, HdrForceDisabled: false };
            SettingsHdrNote.Text = t is null ? "No display information available."
                : !t.HdrSupported ? $"{t.Name} does not report HDR support to Windows (check the monitor's HDR setting and the cable)."
                : t.HdrForceDisabled ? "Windows is blocking HDR right now (for example HDR is off on battery in Windows display settings)."
                : $"Display: {t.Name}";
        }
        catch (Exception ex)
        {
            Log.Warn("Display settings failed to load.", ex);
        }
        finally
        {
            _displayLoading = false;
        }
    }

    private void OnSettingsBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_displayLoading || _displayHw?.Brightness is not { } control) return;
        var value = (int)e.NewValue;
        _ = Task.Run(() => control.SetBrightness(value));
    }

    private async void OnSettingsHdrToggled(object sender, RoutedEventArgs e)
    {
        if (_displayLoading || _settingsHdrTarget is null || _displayHw?.Displays is not { } topology) return;
        var want = SettingsHdrToggle.IsOn;
        var target = _settingsHdrTarget;
        var ok = await Task.Run(() => topology.SetHdr(target, want));
        if (!ok) Shell.ShowToast("Windows did not change HDR for this display.");
        await RenderDisplayAsync();
    }

    private void OnOpenQuickSettingsClick(object sender, RoutedEventArgs e) => Shell.ToggleQuickAccess();
}
