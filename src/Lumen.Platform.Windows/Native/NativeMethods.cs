using System.Runtime.InteropServices;

namespace Lumen.Platform.Windows.Native;

/// <summary>Documented Win32 APIs only. Every signature here is from the public Windows SDK.</summary>
internal static class NativeMethods
{
    // ---------------- XInput (xinput1_4.dll ships with Windows 8+) ----------------
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_DEVICE_NOT_CONNECTED = 1167;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

    // ---------------- kernel32 ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;        // 8 charging, 128 no battery, 255 unknown
        public byte BatteryLifePercent; // 0-100, 255 unknown
        public byte SystemStatusFlag;   // 1 = battery saver on
        public int BatteryLifeTime;     // seconds, -1 unknown
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public readonly ulong Value => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    // ---------------- user32 ----------------
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int GWLP_WNDPROC = -4;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_DISPLAYCHANGE = 0x007E;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Display settings
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_TEST = 0x00000002;
    public const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;

        public static DEVMODE Create() => new()
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>(),
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    public static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    // ---------------- powrprof ----------------
    public const uint ACCESS_SCHEME = 16;
    public const uint ERROR_NO_MORE_ITEMS = 259;

    [DllImport("powrprof.dll")]
    public static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    // Suspend / resume and power setting notifications (callback flavour - no window needed)
    public const uint DEVICE_NOTIFY_CALLBACK = 2;
    public const uint PBT_APMSUSPEND = 0x0004;
    public const uint PBT_APMRESUMESUSPEND = 0x0007;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed.</summary>
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // first DWORD of data (sufficient for display state)
    }

    [DllImport("powrprof.dll")]
    public static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    public static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);
}
