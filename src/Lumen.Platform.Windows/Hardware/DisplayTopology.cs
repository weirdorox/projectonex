using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Active displays, internal/external detection and the Windows HDR switch through the documented
/// CCD API (QueryDisplayConfig / DisplayConfigGetDeviceInfo / DisplayConfigSetDeviceInfo).
/// Windows 11 24H2+ uses the "HDR state" requests; older builds the "advanced colour" ones.
/// </summary>
public sealed class DisplayTopology : IDisplayTopology
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint MODE_INFO_TYPE_SOURCE = 1;

    private const uint GET_TARGET_NAME = 2;
    private const uint GET_ADVANCED_COLOR_INFO = 9;
    private const uint SET_ADVANCED_COLOR_STATE = 10;
    private const uint GET_ADVANCED_COLOR_INFO_2 = 15;
    private const uint SET_HDR_STATE = 16;

    private readonly Dictionary<string, (LUID Adapter, uint TargetId)> _targets = new();
    private readonly object _gate = new();

    public static DisplayTopology? TryCreate()
    {
        var t = new DisplayTopology();
        return t.GetActive().Count > 0 ? t : null;
    }

    public IReadOnlyList<DisplayInfo> GetActive()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return [];
        var paths = new PATH_INFO[pathCount];
        var modes = new MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return [];

        var result = new List<DisplayInfo>();
        lock (_gate)
        {
            _targets.Clear();
            for (var i = 0; i < pathCount; i++)
            {
                var p = paths[i];
                var tech = p.Target.OutputTechnology;
                var isInternal = tech is 0x80000000u or 4u /* LVDS */ or 11u /* DP embedded */ or 13u /* UDI embedded */;

                int width = 0, height = 0, x = 0, y = 0;
                var primary = false;
                if (p.Source.ModeInfoIdx < modeCount && modes[p.Source.ModeInfoIdx].InfoType == MODE_INFO_TYPE_SOURCE)
                {
                    var m = modes[p.Source.ModeInfoIdx];
                    width = (int)m.SourceWidth;
                    height = (int)m.SourceHeight;
                    primary = m.PositionX == 0 && m.PositionY == 0;
                    x = m.PositionX;
                    y = m.PositionY;
                }

                var name = TargetName(p.Target.AdapterId, p.Target.Id) ?? (isInternal ? "Built-in display" : "External display");
                if (isInternal && name.Length == 0) name = "Built-in display";
                var (supported, enabled, forced) = ReadHdr(p.Target.AdapterId, p.Target.Id);
                var id = $"{p.Target.AdapterId.Low:x8}{p.Target.AdapterId.High:x8}:{p.Target.Id}";
                _targets[id] = (p.Target.AdapterId, p.Target.Id);
                result.Add(new DisplayInfo(id, name, isInternal, primary, width, height, supported, enabled, forced, x, y));
            }
        }
        return result;
    }

    public bool SetHdr(DisplayInfo display, bool enable)
    {
        (LUID Adapter, uint TargetId) target;
        lock (_gate)
        {
            if (!_targets.TryGetValue(display.Id, out target)) return false;
        }

        // 24H2+: SET_HDR_STATE. Older: SET_ADVANCED_COLOR_STATE.
        var hdr = new SET_STATE
        {
            Header = Header(SET_HDR_STATE, Marshal.SizeOf<SET_STATE>(), target.Adapter, target.TargetId),
            Value = enable ? 1u : 0u,
        };
        var rc = DisplayConfigSetDeviceInfo(ref hdr);
        if (rc != 0)
        {
            var legacy = hdr with { Header = Header(SET_ADVANCED_COLOR_STATE, Marshal.SizeOf<SET_STATE>(), target.Adapter, target.TargetId) };
            rc = DisplayConfigSetDeviceInfo(ref legacy);
        }
        Log.Info($"HDR {(enable ? "on" : "off")} for '{display.Name}': result {rc}.");
        if (rc != 0) return false;

        var (_, nowEnabled, _) = ReadHdr(target.Adapter, target.TargetId);
        return nowEnabled == enable;
    }

    private static (bool Supported, bool Enabled, bool ForceDisabled) ReadHdr(LUID adapter, uint id)
    {
        var info2 = new COLOR_INFO_2 { Header = Header(GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf<COLOR_INFO_2>(), adapter, id) };
        if (DisplayConfigGetDeviceInfo(ref info2) == 0)
        {
            var v = info2.Value;
            return ((v & 0x10) != 0, (v & 0x20) != 0, (v & 0x08) != 0);
        }

        var info = new COLOR_INFO { Header = Header(GET_ADVANCED_COLOR_INFO, Marshal.SizeOf<COLOR_INFO>(), adapter, id) };
        if (DisplayConfigGetDeviceInfo(ref info) == 0)
        {
            var v = info.Value;
            return ((v & 0x1) != 0, (v & 0x2) != 0, (v & 0x8) != 0);
        }
        return (false, false, false);
    }

    private static string? TargetName(LUID adapter, uint id)
    {
        var name = new TARGET_NAME { Header = Header(GET_TARGET_NAME, Marshal.SizeOf<TARGET_NAME>(), adapter, id) };
        return DisplayConfigGetDeviceInfo(ref name) == 0 ? name.FriendlyName?.Trim() : null;
    }

    private static DEVICE_INFO_HEADER Header(uint type, int size, LUID adapter, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

    // ---- wingdi.h CCD structures ----

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE Source; public PATH_TARGET Target; public uint Flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct MODE_INFO
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public LUID AdapterId;
        [FieldOffset(16)] public uint SourceWidth;
        [FieldOffset(20)] public uint SourceHeight;
        [FieldOffset(24)] public uint PixelFormat;
        [FieldOffset(28)] public int PositionX;
        [FieldOffset(32)] public int PositionY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint Type; public uint Size; public LUID AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COLOR_INFO { public DEVICE_INFO_HEADER Header; public uint Value; public uint ColorEncoding; public uint BitsPerColorChannel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COLOR_INFO_2 { public DEVICE_INFO_HEADER Header; public uint Value; public uint ColorEncoding; public uint BitsPerColorChannel; public uint ActiveColorMode; }

    [StructLayout(LayoutKind.Sequential)]
    private record struct SET_STATE { public DEVICE_INFO_HEADER Header; public uint Value; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_NAME
    {
        public DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO_2 info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SET_STATE info);
}
