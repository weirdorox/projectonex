using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Power;
using Lumen.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App;

/// <summary>Power (Smart Power + real TDP), Battery and System power actions of the quick-access menu.</summary>
public sealed partial class QuickAccessWindow
{
    private DispatcherQueueTimer? _tdpDebounce;
    private bool _powerUpdating;
    private string? _armedPowerAction;

    // ======================= Power tab =======================

    private async Task RenderPowerSectionAsync()
    {
        _powerUpdating = true;
        try
        {
            var settings = App.Host.Settings;
            var onBattery = _main.SmartPower.OnBattery;
            SmartAutoToggle.IsOn = settings.SmartPowerAuto;
            SmartSlider.Value = onBattery ? settings.SmartPowerBattery : settings.SmartPowerAc;

            var status = await _main.Tdp.RefreshAsync();
            RenderTdp(status);
            await RenderSmartPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Rendering the power section failed.", ex);
        }
        finally
        {
            _powerUpdating = false;
        }
    }

    private void RenderTdp(TdpStatus status)
    {
        var usable = status is { Available: true, Locked: false, MinWatts: not null, MaxWatts: not null };
        TdpControls.Visibility = status.Available ? Visibility.Visible : Visibility.Collapsed;
        TdpSetupButton.Visibility = !status.Available && !App.Host.IsSafeMode ? Visibility.Visible : Visibility.Collapsed;

        if (!status.Available)
        {
            TdpStatusText.Text = status.UnavailableReason ?? "Not available.";
            return;
        }

        TdpStatusText.Text = $"{status.Cpu} · {status.Backend}" + (status.Locked ? " · locked by firmware (read-only)" : "");
        TdpRangeText.Text = $"{status.MinWatts:0.#}–{status.MaxWatts:0.#} W";
        TdpSlider.IsEnabled = usable;
        TdpRestoreButton.IsEnabled = usable && status.RequestedWatts is not null;
        if (usable)
        {
            var wasUpdating = _powerUpdating;
            _powerUpdating = true;
            TdpSlider.Minimum = status.MinWatts!.Value;
            TdpSlider.Maximum = status.MaxWatts!.Value;
            TdpSlider.Value = status.RequestedWatts ?? status.Pl1Watts ?? status.MinWatts.Value;
            _powerUpdating = wasUpdating;
        }
        TdpValueText.Text = status.RequestedWatts is { } r ? $"{r:0} W" : $"Default ({status.DefaultPl1Watts:0.#} W)";

        // Presets: disabled with the reason when outside the CPU's range - never silently clamped.
        TdpPresetPanel.Children.Clear();
        foreach (var p in TdpPresets.Resolve(usable ? new TdpRange(status.MinWatts!.Value, status.MaxWatts!.Value, status.RangeSource ?? "") : null))
        {
            var b = new Button
            {
                Content = $"{p.Preset.Name}\n{p.Preset.TargetWatts:0} W",
                IsEnabled = p.Available,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 13,
                Padding = new Thickness(6, 2, 6, 2),
            };
            ToolTipService.SetToolTip(b, p.Reason ?? $"Set {p.Preset.TargetWatts:0} W");
            if (status.RequestedWatts is { } req && Math.Abs(req - p.Preset.TargetWatts) < 0.5)
            {
                b.Background = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
                b.Foreground = (Brush)Application.Current.Resources["LumenAccentBrush"];
            }
            var watts = p.Preset.TargetWatts;
            b.Click += async (_, _) => await ApplyTdpAsync(watts);
            TdpPresetPanel.Children.Add(b);
        }
        var unavailable = TdpPresets.Resolve(usable ? new TdpRange(status.MinWatts!.Value, status.MaxWatts!.Value, "") : null)
            .Where(p => !p.Available).Select(p => $"{p.Preset.Name}: {p.Reason}").ToList();

        RenderTdpLive(status, unavailable);
    }

    private void RenderTdpLive() => RenderTdpLive(_main.Tdp.Status, null);

    private void RenderTdpLive(TdpStatus status, IReadOnlyList<string>? notes)
    {
        if (!status.Available) return;
        var parts = new List<string>();
        if (status.Pl1Watts is { } pl1) parts.Add($"Limit read back: PL1 {pl1:0.#} W / PL2 {status.Pl2Watts:0.#} W");
        if (status.PackageWatts is { } pkg) parts.Add($"Package power now: {pkg:0.#} W");
        if (status.PackageCelsius is { } t) parts.Add($"CPU package: {t:0} °C");
        if (status.FirmwareOverride) parts.Add("The firmware keeps resetting the limit - Lumen stopped re-applying it.");
        if (!string.IsNullOrEmpty(status.Message)) parts.Add(status.Message);
        if (notes is { Count: > 0 }) parts.AddRange(notes);
        TdpLiveText.Text = string.Join("\n", parts);
    }

    private void OnTdpSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_powerUpdating) return;
        TdpValueText.Text = $"{e.NewValue:0} W";
        _tdpDebounce ??= CreateDebounce(TimeSpan.FromMilliseconds(450), () => _ = ApplyTdpAsync(Math.Round(TdpSlider.Value)));
        _tdpDebounce.Stop();
        _tdpDebounce.Start();
    }

    private async Task ApplyTdpAsync(double watts)
    {
        TdpLiveText.Text = $"Applying {watts:0} W…";
        var status = await _main.Tdp.SetAsync(watts);
        _powerUpdating = true;
        RenderTdp(status);
        _powerUpdating = false;
        await RenderSmartPreviewAsync();
    }

    private async void OnTdpRestoreClick(object sender, RoutedEventArgs e)
    {
        var status = await _main.Tdp.RestoreAsync();
        _powerUpdating = true;
        RenderTdp(status);
        _powerUpdating = false;
    }

    private void OnTdpSetupClick(object sender, RoutedEventArgs e)
    {
        _main.ReturnToShell();
        _main.NavigateTab(MainWindow.SettingsTabIndex);
    }

    // ---------------- Smart Power ----------------

    private async void OnSmartChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_powerUpdating) return;
        await RenderSmartPreviewAsync();
    }

    private async Task RenderSmartPreviewAsync()
    {
        try
        {
            var position = (int)SmartSlider.Value;
            var plan = await _main.SmartPower.PreviewAsync(position);
            var (_, telemetry, avg) = _main.SmartPower.SampleBattery(_hw);
            var parts = new List<string> { $"{plan.Label} ({position}%)" };
            parts.Add(plan.TdpWatts is { } w ? $"TDP {w:0} W" : "TDP unchanged (not available)");
            if (plan.RefreshHz is { } hz) parts.Add($"{hz} Hz");
            if (plan.PowerPlanId is { } id)
                parts.Add(_hw?.PowerPlans?.GetPlans().FirstOrDefault(p => p.Id == id)?.Name ?? "power plan");
            parts.Add($"suggested in-game FPS limit: {plan.FpsTarget}");

            var runtime = SmartPower.EstimateRuntime(telemetry?.RemainingWh, avg, _main.Tdp.Status.PackageWatts, plan.TdpWatts);
            var estimate = runtime is { } rt
                ? $"\nEstimated battery life at this setting: about {(int)rt.TotalHours} h {rt.Minutes:00} min (from the current {avg:0.#} W draw)."
                : "\nBattery life estimate appears while running on battery.";
            SmartPreviewText.Text = string.Join(" · ", parts) + estimate;
        }
        catch (Exception ex)
        {
            Log.Warn("Smart Power preview failed.", ex);
        }
    }

    private async void OnSmartApplyClick(object sender, RoutedEventArgs e)
    {
        SmartApplyButton.IsEnabled = false;
        try
        {
            var result = await _main.SmartPower.ApplyAsync((int)SmartSlider.Value);
            var text = result.Applied.Count > 0 ? "Applied: " + string.Join(", ", result.Applied) : "Nothing could be applied.";
            if (result.Skipped.Count > 0) text += "\nSkipped: " + string.Join("; ", result.Skipped);
            SmartPreviewText.Text = text;
            _powerUpdating = true;
            RenderTdp(_main.Tdp.Status);
            _powerUpdating = false;
        }
        catch (Exception ex)
        {
            Log.Error("Smart Power apply failed.", ex);
            SmartPreviewText.Text = "Smart Power could not be applied (see log).";
        }
        finally
        {
            SmartApplyButton.IsEnabled = true;
        }
    }

    private void OnSmartAutoToggled(object sender, RoutedEventArgs e)
    {
        if (!_powerUpdating) App.Host.Settings.SmartPowerAuto = SmartAutoToggle.IsOn;
    }

    // ======================= Battery tab =======================

    private void RenderBatterySection()
    {
        var (status, telemetry, avg) = _main.SmartPower.SampleBattery(_hw);
        if (!status.HasBattery)
        {
            BatteryBigText.Text = "No battery";
            BatteryStateText.Text = "Running on external power.";
            BatteryDetailText.Text = string.Empty;
            return;
        }

        BatteryBigText.Text = status.Percent is { } pct ? $"{pct}%" : "–";
        BatteryStateText.Text = status.IsCharging
            ? telemetry?.ChargeWatts is { } cw ? $"Charging at {cw:0.#} W" : "Charging"
            : status.Source == PowerSource.AC ? "Plugged in"
            : avg is { } d ? $"On battery · using {d:0.#} W" : "On battery";

        var lines = new List<string>();
        var runtime = SmartPower.EstimateRuntime(telemetry?.RemainingWh, avg);
        if (runtime is { } rt && status.Source == PowerSource.Battery)
            lines.Add($"About {(int)rt.TotalHours} h {rt.Minutes:00} min left at the current draw (measured).");
        else if (status.Remaining is { } windowsEstimate)
            lines.Add($"Windows estimate: {(int)windowsEstimate.TotalHours} h {windowsEstimate.Minutes:00} min left.");
        if (telemetry?.RemainingWh is { } rem && telemetry.FullChargeWh is { } full)
            lines.Add($"Capacity: {rem:0.#} of {full:0.#} Wh");
        if (telemetry?.HealthPercent is { } health)
            lines.Add($"Battery health: {health:0.#}% of design capacity ({telemetry.DesignWh:0.#} Wh)");
        if (status.BatterySaverOn) lines.Add("Battery saver is on.");
        if (telemetry is null) lines.Add("Detailed battery telemetry is not available on this device.");
        BatteryDetailText.Text = string.Join("\n", lines);
    }

    // ======================= System: power actions =======================

    /// <summary>Destructive actions need a second press within a few seconds.</summary>
    private bool Confirm(string action, Button button, string label)
    {
        if (_armedPowerAction == action) return true;
        _armedPowerAction = action;
        PowerConfirmText.Text = $"Press {label} again to confirm.";
        FocusNavigation.Focus(button);
        var reset = CreateDebounce(TimeSpan.FromSeconds(4), () =>
        {
            if (_armedPowerAction == action)
            {
                _armedPowerAction = null;
                PowerConfirmText.Text = string.Empty;
            }
        });
        reset.Start();
        return false;
    }

    private void OnSleepClick(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        PowerActions.Sleep();
    }

    private void OnHibernateClick(object sender, RoutedEventArgs e)
    {
        if (!PowerActions.CanHibernate)
        {
            PowerConfirmText.Text = "Hibernation is turned off in Windows on this device.";
            return;
        }
        if (!Confirm("hibernate", HibernateButton, "Hibernate")) return;
        HideOverlay();
        if (!PowerActions.Hibernate()) _main.ShowToast("Windows did not hibernate.");
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("restart", RestartButton, "Restart")) return;
        _ = RestoreTdpThen(() => PowerActions.Restart());
    }

    private void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("shutdown", ShutdownButton, "Shut down")) return;
        _ = RestoreTdpThen(() => PowerActions.ShutDown());
    }

    private async Task RestoreTdpThen(Func<bool> action)
    {
        // Firmware limits reset on reboot anyway; restoring first keeps the helper's state consistent.
        if (_main.Tdp.Status.RequestedWatts is not null) await _main.Tdp.RestoreAsync();
        App.Host.Guard.MarkCleanExit();
        if (!action()) _main.ShowToast("Windows refused the request.");
    }

    private void OnRestartLumenClick(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        _main.RestartShell();
    }

    private void OnTaskManagerClick(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        PowerActions.OpenTaskManager();
    }

    private DispatcherQueueTimer CreateDebounce(TimeSpan delay, Action action)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = delay;
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Warn("Overlay timer failed.", ex); }
        };
        return t;
    }
}
