using System.Runtime.InteropServices;
using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Master volume of the default render endpoint via the documented Core Audio COM API
/// (IMMDeviceEnumerator / IAudioEndpointVolume). The default device is resolved on every call so
/// switching to headphones or Bluetooth is picked up automatically.
/// </summary>
public sealed class CoreAudioVolumeControl : IVolumeControl
{
    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const int CLSCTX_ALL = 0x17;
    private static Guid _context = Guid.Empty;

    public static CoreAudioVolumeControl? TryCreate()
    {
        try
        {
            var c = new CoreAudioVolumeControl();
            _ = c.GetLevel();
            return c;
        }
        catch
        {
            return null; // no audio endpoint
        }
    }

    public float GetLevel()
    {
        var v = Endpoint();
        try
        {
            Check(v.GetMasterVolumeLevelScalar(out var level));
            return level;
        }
        finally { Marshal.ReleaseComObject(v); }
    }

    public void SetLevel(float level)
    {
        var v = Endpoint();
        try { Check(v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _context)); }
        finally { Marshal.ReleaseComObject(v); }
    }

    public bool GetMuted()
    {
        var v = Endpoint();
        try
        {
            Check(v.GetMute(out var muted));
            return muted;
        }
        finally { Marshal.ReleaseComObject(v); }
    }

    public void SetMuted(bool muted)
    {
        var v = Endpoint();
        try { Check(v.SetMute(muted, ref _context)); }
        finally { Marshal.ReleaseComObject(v); }
    }

    private static IAudioEndpointVolume Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device));
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj));
                return (IAudioEndpointVolume)obj;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ---- COM declarations (mmdeviceapi.h / endpointvolume.h). Methods are listed in vtable order. ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
