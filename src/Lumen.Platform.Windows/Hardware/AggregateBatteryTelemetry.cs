using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Battery wattage, capacity and health from the documented Windows.Devices.Power aggregate battery
/// report (sums all batteries; no permissions needed).
/// </summary>
public sealed class AggregateBatteryTelemetry : IBatteryTelemetry
{
    public static AggregateBatteryTelemetry? TryCreate()
    {
        var t = new AggregateBatteryTelemetry();
        var r = t.Read();
        return r is null || (r.FullChargeWh is null && r.RemainingWh is null) ? null : t;
    }

    public BatteryTelemetry? Read()
    {
        try
        {
            var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            static double? Wh(int? mwh) => mwh is > 0 ? mwh / 1000.0 : null;
            return new BatteryTelemetry(
                ChargeRateWatts: report.ChargeRateInMilliwatts is { } mw ? mw / 1000.0 : null,
                RemainingWh: Wh(report.RemainingCapacityInMilliwattHours),
                FullChargeWh: Wh(report.FullChargeCapacityInMilliwattHours),
                DesignWh: Wh(report.DesignCapacityInMilliwattHours));
        }
        catch
        {
            return null;
        }
    }
}
