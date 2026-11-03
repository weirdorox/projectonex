using System.Management;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Internal panel brightness via the documented WMI classes WmiMonitorBrightness /
/// WmiMonitorBrightnessMethods (root\WMI). Works on laptop-style integrated panels without admin
/// rights; external monitors (DDC/CI) are not supported by this provider.
/// </summary>
public sealed class WmiBrightnessControl : IBrightnessControl
{
    private const string Scope = @"root\WMI";

    private WmiBrightnessControl() { }

    /// <summary>Returns null if no WMI-controllable panel exists.</summary>
    public static WmiBrightnessControl? TryCreate()
    {
        var c = new WmiBrightnessControl();
        return c.GetBrightness() is null ? null : c;
    }

    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using (o)
                {
                    return Convert.ToInt32(o["CurrentBrightness"]);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Brightness read failed: {ex.Message}");
        }
        return null;
    }

    public bool SetBrightness(int percent)
    {
        try
        {
            var value = (byte)Math.Clamp(percent, 0, 100);
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    o.InvokeMethod("WmiSetBrightness", [(uint)1, value]);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Brightness change failed.", ex);
        }
        return false;
    }
}
