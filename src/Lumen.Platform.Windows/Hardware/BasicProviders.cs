using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Library;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>Reads SMBIOS identity from the registry copy Windows keeps (no WMI, no admin).</summary>
public static class SystemIdentityReader
{
    public static DeviceIdentity Read(IRegistryReader registry)
    {
        const string key = @"HARDWARE\DESCRIPTION\System\BIOS";
        string V(string name) => registry.GetValue(RegistryRoot.LocalMachine, key, name)?.Trim() ?? string.Empty;
        return new DeviceIdentity(
            V("SystemManufacturer"),
            V("SystemProductName"),
            V("BaseBoardProduct"),
            V("SystemFamily"),
            V("BIOSVersion"));
    }
}

/// <summary>GetSystemPowerStatus - documented, cheap, no permissions needed.</summary>
public sealed class Win32BatteryInfo : IBatteryInfo
{
    public BatteryStatus Read()
    {
        if (!GetSystemPowerStatus(out var s)) return BatteryStatus.Unknown;

        var noBattery = s.BatteryFlag == 128 || s.BatteryFlag == 255;
        var source = s.ACLineStatus switch
        {
            0 => PowerSource.Battery,
            1 => PowerSource.AC,
            _ => PowerSource.Unknown,
        };
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        TimeSpan? remaining = s.BatteryLifeTime >= 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;

        return new BatteryStatus(
            HasBattery: !noBattery,
            Percent: noBattery ? null : percent,
            Source: source,
            IsCharging: (s.BatteryFlag & 8) != 0,
            BatterySaverOn: s.SystemStatusFlag == 1,
            Remaining: remaining);
    }
}

/// <summary>Refresh rate of the primary display via EnumDisplaySettings / ChangeDisplaySettingsEx.</summary>
public sealed class DisplayModeControl : IDisplayModeControl
{
    public DisplayMode? GetCurrent()
    {
        var dm = DEVMODE.Create();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
    }

    public IReadOnlyList<int> GetSupportedRefreshRates()
    {
        var current = DEVMODE.Create();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref current)) return [];

        var rates = new SortedSet<int>();
        var dm = DEVMODE.Create();
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmPelsWidth == current.dmPelsWidth && dm.dmPelsHeight == current.dmPelsHeight &&
                dm.dmBitsPerPel == current.dmBitsPerPel && dm.dmDisplayFrequency > 1)
            {
                rates.Add((int)dm.dmDisplayFrequency);
            }
            dm = DEVMODE.Create();
        }
        return rates.ToList();
    }

    public bool SetRefreshRate(int hz)
    {
        var dm = DEVMODE.Create();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        dm.dmDisplayFrequency = (uint)hz;
        dm.dmFields = DM_DISPLAYFREQUENCY;

        var test = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL)
        {
            Log.Warn($"Refresh rate {hz} Hz rejected by driver (test result {test}).");
            return false;
        }
        var rc = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        Log.Info($"Set refresh rate to {hz} Hz: result {rc}.");
        return rc == DISP_CHANGE_SUCCESSFUL;
    }
}

/// <summary>Classic power plans via the documented powrprof API. Does not touch the Windows 11 "power mode" overlay.</summary>
public sealed class PowerPlanControl : IPowerPlanControl
{
    public IReadOnlyList<PowerPlan> GetPlans()
    {
        var plans = new List<PowerPlan>();
        var size = (uint)Marshal.SizeOf<Guid>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            for (uint i = 0; ; i++)
            {
                var bs = size;
                var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buffer, ref bs);
                if (rc == ERROR_NO_MORE_ITEMS || rc != 0) break;
                var id = Marshal.PtrToStructure<Guid>(buffer);
                plans.Add(new PowerPlan(id, ReadName(id) ?? id.ToString()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return plans;
    }

    public Guid? GetActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public bool SetActivePlan(Guid id)
    {
        var rc = PowerSetActiveScheme(IntPtr.Zero, ref id);
        Log.Info($"Set power plan {id}: result {rc}.");
        return rc == 0;
    }

    private static string? ReadName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
            return null;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
