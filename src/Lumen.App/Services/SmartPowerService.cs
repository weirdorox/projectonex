using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Power;

namespace Lumen.App.Services;

public sealed record SmartPowerResult(SmartPowerPlan Plan, IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

/// <summary>
/// Applies a Smart Power slider position through the real providers (TDP helper, display mode,
/// power plans) and reports exactly what was applied and what was skipped and why.
/// Battery telemetry for the runtime estimate is averaged here.
/// </summary>
public sealed class SmartPowerService
{
    private readonly AppHost _host;
    private readonly TdpService _tdp;
    private readonly DischargeAverager _discharge = new();

    public SmartPowerService(AppHost host, TdpService tdp)
    {
        _host = host;
        _tdp = tdp;
    }

    public async Task<SmartPowerCapabilities> GetCapabilitiesAsync()
    {
        var hw = await _host.Hardware.Value;
        return new SmartPowerCapabilities(
            _tdp.IsUsable ? _tdp.Range : null,
            hw.Display?.GetSupportedRefreshRates() ?? [],
            hw.PowerPlans?.GetPlans() ?? []);
    }

    public bool OnBattery => _host.Battery.Read().Source == PowerSource.Battery;

    public async Task<SmartPowerPlan> PreviewAsync(int position) =>
        SmartPower.Compute(position, await GetCapabilitiesAsync(), OnBattery);

    public async Task<SmartPowerResult> ApplyAsync(int position)
    {
        var hw = await _host.Hardware.Value;
        var plan = SmartPower.Compute(position, await GetCapabilitiesAsync(), OnBattery);
        var applied = new List<string>();
        var skipped = new List<string>();

        if (plan.TdpWatts is { } w)
        {
            var s = await _tdp.SetAsync(w);
            if (s.Available && s.RequestedWatts == w) applied.Add($"TDP {w:0} W");
            else skipped.Add($"TDP: {s.Message ?? s.UnavailableReason}");
        }
        else skipped.Add("TDP: " + (_tdp.Status.UnavailableReason ?? (_tdp.Status.Locked ? "locked by firmware" : "not available")));

        if (plan.RefreshHz is { } hz && hw.Display is { } display)
        {
            var current = display.GetCurrent()?.RefreshHz;
            if (current == hz || await Task.Run(() => display.SetRefreshRate(hz))) applied.Add($"{hz} Hz");
            else skipped.Add($"{hz} Hz rejected by the display driver");
        }

        if (plan.PowerPlanId is { } id && hw.PowerPlans is { } plans)
        {
            var name = plans.GetPlans().FirstOrDefault(p => p.Id == id)?.Name ?? "power plan";
            if (plans.GetActivePlan() == id || plans.SetActivePlan(id)) applied.Add(name);
            else skipped.Add($"{name} could not be activated");
        }

        if (OnBattery) _host.Settings.SmartPowerBattery = position;
        else _host.Settings.SmartPowerAc = position;

        Log.Info($"Smart Power {position}% ({plan.Label}): applied [{string.Join(", ", applied)}], skipped [{string.Join(", ", skipped)}].");
        return new SmartPowerResult(plan, applied, skipped);
    }

    /// <summary>Samples battery telemetry (call ~every few seconds while battery info is visible).</summary>
    public (BatteryStatus Status, BatteryTelemetry? Telemetry, double? AvgDischargeWatts) SampleBattery(HardwareServices? hw)
    {
        var status = _host.Battery.Read();
        var telemetry = hw?.BatteryTelemetry?.Read();
        var avg = _discharge.Add(telemetry?.DischargeWatts);
        return (status, telemetry, avg);
    }
}
