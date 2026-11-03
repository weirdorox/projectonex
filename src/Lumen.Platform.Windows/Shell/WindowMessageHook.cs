using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Shell;

/// <summary>
/// Subclasses a top-level window (SetWindowLongPtr/CallWindowProc) to receive messages WinUI does not
/// surface: global hotkeys, session end (sign-out/shutdown) and display changes.
/// </summary>
public sealed class WindowMessageHook : IDisposable
{
    public const int ToggleHotkeyId = 0x4C01; // arbitrary, app-unique

    private readonly IntPtr _hwnd;
    private readonly WndProc _proc; // keep delegate alive
    private IntPtr _previous;
    private bool _hotkeyRegistered;

    public event Action<int>? HotkeyPressed;
    public event Action? SessionEnding;
    public event Action? DisplayChanged;

    public WindowMessageHook(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WindowProc;
        _previous = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
        if (_previous == IntPtr.Zero)
            Log.Warn($"Window subclassing failed (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Registers Ctrl+Alt+Home (configurable later) as a global "show Lumen" hotkey.</summary>
    public bool RegisterToggleHotkey(uint modifiers = MOD_CONTROL | MOD_ALT, uint virtualKey = 0x24 /* VK_HOME */)
    {
        _hotkeyRegistered = RegisterHotKey(_hwnd, ToggleHotkeyId, modifiers | MOD_NOREPEAT, virtualKey);
        if (!_hotkeyRegistered)
            Log.Warn($"Global hotkey registration failed (error {Marshal.GetLastWin32Error()}) - probably used by another app.");
        return _hotkeyRegistered;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_HOTKEY:
                    HotkeyPressed?.Invoke(wParam.ToInt32());
                    break;
                case WM_ENDSESSION when wParam != IntPtr.Zero: // session really is ending
                    SessionEnding?.Invoke();
                    break;
                case WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Window message handler failed for 0x{msg:X}.", ex);
        }
        return CallWindowProc(_previous, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hotkeyRegistered) UnregisterHotKey(_hwnd, ToggleHotkeyId);
        _hotkeyRegistered = false;
        if (_previous != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previous);
            _previous = IntPtr.Zero;
        }
    }
}
