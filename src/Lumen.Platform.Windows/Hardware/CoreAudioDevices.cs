using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// Microphone mute (default capture endpoints, documented Core Audio API) and the output device list.
/// Changing the default output has no documented API: Lumen uses the IPolicyConfig interface that
/// Windows' own Sound settings use (as EarTrumpet / SoundSwitch do). If that ever fails, it returns
/// false and the UI opens Windows sound settings instead - nothing is faked.
/// </summary>
public sealed class CoreAudioDevices : IAudioDevices
{
    private const int eRender = 0, eCapture = 1;
    private const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
    private const int DEVICE_STATE_ACTIVE = 0x1;
    private const int CLSCTX_ALL = 0x17;
    private static Guid _context = Guid.Empty;
    private static PROPERTYKEY FriendlyNameKey = new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };

    public static CoreAudioDevices? TryCreate()
    {
        var d = new CoreAudioDevices();
        _ = d.GetOutputs();
        return d;
    }

    public bool? GetMicMuted()
    {
        try
        {
            return WithCaptureVolumes(volumes =>
            {
                if (volumes.Count == 0) return (bool?)null;
                Check(volumes[0].GetMute(out var muted));
                return muted;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Reading microphone mute failed.", ex);
            return null;
        }
    }

    public bool SetMicMuted(bool muted)
    {
        try
        {
            return WithCaptureVolumes(volumes =>
            {
                if (volumes.Count == 0) return false;
                foreach (var v in volumes) Check(v.SetMute(muted, ref _context));
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Setting microphone mute failed.", ex);
            return false;
        }
    }

    public IReadOnlyList<AudioEndpoint> GetOutputs()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var def) == 0)
            {
                try { def.GetId(out defaultId); } finally { Marshal.ReleaseComObject(def); }
            }

            Check(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection));
            try
            {
                Check(collection.GetCount(out var count));
                var list = new List<AudioEndpoint>();
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;
                    try
                    {
                        if (device.GetId(out var id) != 0 || id is null) continue;
                        list.Add(new AudioEndpoint(id, FriendlyName(device) ?? "Audio device", string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                return list;
            }
            finally { Marshal.ReleaseComObject(collection); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public bool SetDefaultOutput(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClientComObject();
            try
            {
                foreach (var role in new[] { eConsole, eMultimedia, eCommunications })
                    Check(policy.SetDefaultEndpoint(id, role));
            }
            finally { Marshal.ReleaseComObject(policy); }
            var ok = GetOutputs().Any(o => o.IsDefault && o.Id == id);
            Log.Info($"Default audio output changed: {ok}.");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("Switching audio output failed (no supported interface on this Windows build).", ex);
            return false;
        }
    }

    private static T WithCaptureVolumes<T>(Func<List<IAudioEndpointVolume>, T> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        var volumes = new List<IAudioEndpointVolume>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // The default console and communications microphones may differ (headset vs built-in).
            foreach (var role in new[] { eConsole, eCommunications })
            {
                if (enumerator.GetDefaultAudioEndpoint(eCapture, role, out var device) != 0) continue;
                try
                {
                    if (device.GetId(out var id) != 0 || id is null || !seen.Add(id)) continue;
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0)
                        volumes.Add((IAudioEndpointVolume)obj);
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return action(volumes);
        }
        finally
        {
            foreach (var v in volumes) Marshal.ReleaseComObject(v);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        try
        {
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                return value.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ---- COM declarations (mmdeviceapi.h / endpointvolume.h / propsys.h), vtable order ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid Fmtid; public uint Pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClientComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
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

    /// <summary>Undocumented (Windows 7+) interface used by Windows' own Sound settings.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
