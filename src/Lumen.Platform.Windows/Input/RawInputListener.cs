using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;

namespace Lumen.Platform.Windows.Input;

public sealed record RawDevice(IntPtr Handle, string Name, int VendorId, int ProductId, int UsagePage, int Usage, bool IsKeyboard);

public sealed record RawKeyEvent(RawDevice? Device, int VirtualKey, int ScanCode, bool Extended, bool Down);

public sealed record RawHidEvent(RawDevice Device, byte[] Report);

/// <summary>
/// System-wide, read-only input listener using Windows Raw Input (RIDEV_INPUTSINK) on a hidden
/// message-only window: keyboard events (OEM buttons often send key combinations) and HID reports
/// from vendor/consumer/gamepad collections. Not a hook - nothing is intercepted or blocked; every
/// other app still receives the same input. Also hosts RegisterHotKey registrations (WM_HOTKEY),
/// which are the documented way to *own* a keyboard shortcut.
/// </summary>
public sealed class RawInputListener : IDisposable
{
    private const uint WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_HOTKEY = 0x0312;
    private const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000, RIDEV_REMOVE = 0x1;
    private const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProc _proc;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<IntPtr, RawDevice> _devices = new();
    private readonly HashSet<(int Page, int Usage)> _hidRegistered = new();
    private byte[] _buffer = new byte[1024];
    private bool _keyboardRegistered;

    public RawInputListener()
    {
        _proc = WindowProc;
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "LumenRawInput_" + Environment.ProcessId,
        };
        if (RegisterClassEx(ref cls) == 0) Log.Warn($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "Lumen input", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
    }

    public event Action<RawKeyEvent>? Key;
    public event Action<RawHidEvent>? Hid;
    public event Action<int>? Hotkey;
    public event Action? DevicesChanged;

    // ---------------- registration ----------------

    public bool ListenKeyboard()
    {
        _keyboardRegistered = Register(1, 6, RIDEV_INPUTSINK | RIDEV_DEVNOTIFY);
        return _keyboardRegistered;
    }

    /// <summary>Starts receiving reports from HID collections with these (page, usage) pairs.</summary>
    public int ListenHid(IEnumerable<(int Page, int Usage)> collections)
    {
        var count = 0;
        foreach (var c in collections.Distinct())
        {
            if (_hidRegistered.Contains(c)) { count++; continue; }
            if (Register((ushort)c.Page, (ushort)c.Usage, RIDEV_INPUTSINK | RIDEV_DEVNOTIFY))
            {
                _hidRegistered.Add(c);
                count++;
            }
        }
        return count;
    }

    public void StopHid(Func<(int Page, int Usage), bool> which)
    {
        foreach (var c in _hidRegistered.Where(which).ToList())
        {
            if (Register((ushort)c.Page, (ushort)c.Usage, RIDEV_REMOVE, IntPtr.Zero)) _hidRegistered.Remove(c);
        }
    }

    /// <summary>Re-applies registrations (after sleep/resume or device changes).</summary>
    public void Renew()
    {
        if (_keyboardRegistered) ListenKeyboard();
        foreach (var c in _hidRegistered.ToList()) Register((ushort)c.Page, (ushort)c.Usage, RIDEV_INPUTSINK | RIDEV_DEVNOTIFY);
        lock (_devices) _devices.Clear();
    }

    /// <summary>HID collections that may carry extra buttons (vendor, consumer, system, gamepad). No touch/mouse/sensors.</summary>
    public IReadOnlyList<RawDevice> EnumerateHid()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return [];
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return [];
        return list.Where(d => d.dwType == RIM_TYPEHID).Select(d => Describe(d.hDevice)).OfType<RawDevice>()
            .Where(d => IsInteresting(d.UsagePage, d.Usage)).ToList();
    }

    public static bool IsInteresting(int page, int usage) =>
        page >= 0xFF00 || page == 0x0C || (page == 0x01 && usage is 0x04 or 0x05 or 0x80);

    // ---------------- hotkeys ----------------

    public bool RegisterHotkey(int id, int modifiers, int vk) => RegisterHotKey(_hwnd, id, (uint)modifiers | MOD_NOREPEAT, (uint)vk);

    public void UnregisterHotkey(int id) => UnregisterHotKey(_hwnd, id);

    /// <summary>True if another app already owns this shortcut (registration attempt, immediately undone).</summary>
    public bool IsReserved(int modifiers, int vk)
    {
        const int probeId = 0x7FF0;
        if (!RegisterHotKey(_hwnd, probeId, (uint)modifiers | MOD_NOREPEAT, (uint)vk)) return true;
        UnregisterHotKey(_hwnd, probeId);
        return false;
    }

    // ---------------- messages ----------------

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_INPUT:
                    OnInput(lParam);
                    break;
                case WM_INPUT_DEVICE_CHANGE:
                    lock (_devices) _devices.Remove(lParam);
                    DevicesChanged?.Invoke();
                    break;
                case WM_HOTKEY:
                    Hotkey?.Invoke(wParam.ToInt32());
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Raw input handler failed (0x{msg:X}).", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnInput(IntPtr hRawInput)
    {
        const uint headerSize = 24; // RAWINPUTHEADER on x64
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        if (_buffer.Length < size) _buffer = new byte[size * 2];
        var handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, handle.AddrOfPinnedObject(), ref size, headerSize) == uint.MaxValue) return;
        }
        finally
        {
            handle.Free();
        }

        var type = BitConverter.ToUInt32(_buffer, 0);
        var device = (IntPtr)BitConverter.ToInt64(_buffer, 8);
        var info = Describe(device);
        if (type == RIM_TYPEKEYBOARD)
        {
            var make = BitConverter.ToUInt16(_buffer, 24);
            var flags = BitConverter.ToUInt16(_buffer, 26);
            var vk = BitConverter.ToUInt16(_buffer, 30);
            if (vk is 0 or 0xFF) return; // fake keys (e.g. Pause sequences)
            Key?.Invoke(new RawKeyEvent(info, vk, make, (flags & 2) != 0, (flags & 1) == 0));
        }
        else if (type == RIM_TYPEHID && info is not null)
        {
            var reportSize = (int)BitConverter.ToUInt32(_buffer, 24);
            var reportCount = (int)BitConverter.ToUInt32(_buffer, 28);
            for (var i = 0; i < reportCount && reportSize > 0; i++)
            {
                var offset = 32 + i * reportSize;
                if (offset + reportSize > size) break;
                Hid?.Invoke(new RawHidEvent(info, _buffer.AsSpan(offset, reportSize).ToArray()));
            }
        }
    }

    private RawDevice? Describe(IntPtr device)
    {
        if (device == IntPtr.Zero) return null;
        lock (_devices)
        {
            if (_devices.TryGetValue(device, out var known)) return known;
        }
        var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
        var size = info.cbSize;
        if (GetRawInputDeviceInfo(device, RIDI_DEVICEINFO, ref info, ref size) == uint.MaxValue) return null;

        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        var name = "";
        if (chars > 0)
        {
            var mem = Marshal.AllocHGlobal((int)chars * 2);
            try
            {
                if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, mem, ref chars) != uint.MaxValue) name = Marshal.PtrToStringUni(mem) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        var d = info.dwType == RIM_TYPEHID
            ? new RawDevice(device, name, (int)info.VendorId, (int)info.ProductId, info.UsagePage, info.Usage, false)
            : new RawDevice(device, name, VidPid(name, "VID_"), VidPid(name, "PID_"), 1, 6, true);
        lock (_devices) _devices[device] = d;
        return d;
    }

    private static int VidPid(string name, string tag)
    {
        var i = name.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 8 <= name.Length && int.TryParse(name.AsSpan(i + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private bool Register(ushort page, ushort usage, uint flags) => Register(page, usage, flags, _hwnd);

    private static bool Register(ushort page, ushort usage, uint flags, IntPtr target)
    {
        var device = new[] { new RAWINPUTDEVICE { usUsagePage = page, usUsage = usage, dwFlags = flags, hwndTarget = target } };
        var ok = RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!ok) Log.Warn($"Raw input registration {page:X4}/{usage:X2} failed ({Marshal.GetLastWin32Error()}).");
        return ok;
    }

    public void Dispose()
    {
        foreach (var c in _hidRegistered) Register((ushort)c.Page, (ushort)c.Usage, RIDEV_REMOVE, IntPtr.Zero);
        if (_keyboardRegistered) Register(1, 6, RIDEV_REMOVE, IntPtr.Zero);
        _hidRegistered.Clear();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    // ---------------- interop ----------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public uint VendorId;
        [FieldOffset(12)] public uint ProductId;
        [FieldOffset(16)] public uint VersionNumber;
        [FieldOffset(20)] public ushort UsagePage;
        [FieldOffset(22)] public ushort Usage;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RID_DEVICE_INFO data, ref uint size);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
