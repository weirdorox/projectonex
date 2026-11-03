using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Power;

/// <summary>
/// Suspend/resume and display on/off notifications through the documented callback-based powrprof
/// API (no hidden window required). Events are raised on a system thread - marshal to the UI thread.
///
/// On Modern Standby devices (most handhelds) the display-off/on notification is the most reliable
/// signal; classic suspend/resume notifications are also handled for S3/hibernate.
/// </summary>
public sealed class PowerEventsMonitor : IDisposable
{
    private readonly DeviceNotifyCallbackRoutine _callback; // keep alive for the lifetime of the registration
    private IntPtr _recipient;
    private IntPtr _suspendHandle;
    private IntPtr _displayHandle;
    private bool _resumeRaised;

    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public event EventHandler<bool>? DisplayStateChanged; // true = on

    public PowerEventsMonitor()
    {
        _callback = OnNotify;
    }

    public void Start()
    {
        if (_recipient != IntPtr.Zero) return;
        var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
        {
            Callback = Marshal.GetFunctionPointerForDelegate(_callback),
            Context = IntPtr.Zero,
        };
        _recipient = Marshal.AllocHGlobal(Marshal.SizeOf<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>());
        Marshal.StructureToPtr(parameters, _recipient, false);

        var rc = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, _recipient, out _suspendHandle);
        if (rc != 0) Log.Warn($"Suspend/resume notifications unavailable (error {rc}).");

        var guid = GUID_CONSOLE_DISPLAY_STATE;
        rc = PowerSettingRegisterNotification(ref guid, DEVICE_NOTIFY_CALLBACK, _recipient, out _displayHandle);
        if (rc != 0) Log.Warn($"Display state notifications unavailable (error {rc}).");
    }

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            switch (type)
            {
                case PBT_APMSUSPEND:
                    _resumeRaised = false;
                    Log.Info("System suspending.");
                    Suspending?.Invoke(this, EventArgs.Empty);
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                case PBT_APMRESUMESUSPEND:
                    // Both may arrive for one resume; raise once.
                    if (_resumeRaised) break;
                    _resumeRaised = true;
                    Log.Info("System resumed.");
                    Resumed?.Invoke(this, EventArgs.Empty);
                    break;
                case PBT_POWERSETTINGCHANGE when setting != IntPtr.Zero:
                    var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(setting);
                    if (s.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                    {
                        var on = s.Data != 0; // 0 off, 1 on, 2 dimmed
                        Log.Info($"Display state: {s.Data}.");
                        DisplayStateChanged?.Invoke(this, on);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Power notification handler failed.", ex);
        }
        return 0;
    }

    public void Dispose()
    {
        if (_suspendHandle != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_suspendHandle);
        if (_displayHandle != IntPtr.Zero) PowerSettingUnregisterNotification(_displayHandle);
        _suspendHandle = _displayHandle = IntPtr.Zero;
        if (_recipient != IntPtr.Zero) Marshal.FreeHGlobal(_recipient);
        _recipient = IntPtr.Zero;
    }
}
