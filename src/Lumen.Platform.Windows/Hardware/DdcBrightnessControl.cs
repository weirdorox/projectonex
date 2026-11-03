using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Brightness of external monitors/TVs over DDC/CI with the documented Monitor Configuration API
/// (dxva2: GetMonitorBrightness / SetMonitorBrightness). Many monitors support it; some need DDC/CI
/// enabled in their own on-screen menu, and many TVs do not support it at all.
/// </summary>
public sealed class DdcBrightnessControl : IBrightnessControl, IDisposable
{
    private readonly object _gate = new();
    private List<PHYSICAL_MONITOR> _monitors = [];

    private DdcBrightnessControl() { }

    public static DdcBrightnessControl? TryCreate()
    {
        var c = new DdcBrightnessControl();
        c.Refresh();
        if (c.GetBrightness() is not null) return c;
        c.Dispose();
        return null;
    }

    public int? GetBrightness()
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                foreach (var m in _monitors)
                {
                    if (GetMonitorBrightness(m.Handle, out var min, out var cur, out var max) && max > min)
                        return (int)Math.Round((cur - min) * 100.0 / (max - min));
                }
                Refresh(); // handles go stale after display changes
            }
        }
        return null;
    }

    public bool SetBrightness(int percent)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var any = false;
                foreach (var m in _monitors)
                {
                    if (!GetMonitorBrightness(m.Handle, out var min, out _, out var max) || max <= min) continue;
                    var value = (uint)Math.Round(min + Math.Clamp(percent, 0, 100) / 100.0 * (max - min));
                    any |= SetMonitorBrightness(m.Handle, value);
                }
                if (any) return true;
                Refresh();
            }
        }
        Log.Debug("DDC/CI brightness: no monitor accepted the change.");
        return false;
    }

    private void Refresh()
    {
        Release();
        var found = new List<PHYSICAL_MONITOR>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) && count > 0)
            {
                var array = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, array)) found.AddRange(array);
            }
            return true;
        }, IntPtr.Zero);
        _monitors = found;
    }

    private void Release()
    {
        if (_monitors.Count > 0) DestroyPhysicalMonitors((uint)_monitors.Count, _monitors.ToArray());
        _monitors = [];
    }

    public void Dispose()
    {
        lock (_gate) Release();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);
}

/// <summary>Built-in panel (WMI) and external monitors (DDC/CI) behind one brightness control.</summary>
public sealed class CombinedBrightnessControl(IBrightnessControl? panel, DdcBrightnessControl? external) : IBrightnessControl
{
    public static IBrightnessControl? TryCreate()
    {
        var panel = WmiBrightnessControl.TryCreate();
        DdcBrightnessControl? ddc = null;
        try { ddc = DdcBrightnessControl.TryCreate(); }
        catch (Exception ex) { Log.Warn("DDC/CI probe failed.", ex); }
        Log.Info($"Brightness: built-in panel {(panel is null ? "no" : "yes")}, external DDC/CI {(ddc is null ? "no" : "yes")}.");
        if (panel is null && ddc is null) return null;
        return new CombinedBrightnessControl(panel, ddc);
    }

    public int? GetBrightness() => panel?.GetBrightness() ?? external?.GetBrightness();

    public bool SetBrightness(int percent)
    {
        var ok = panel?.SetBrightness(percent) ?? false;
        ok |= external?.SetBrightness(percent) ?? false;
        return ok;
    }
}
