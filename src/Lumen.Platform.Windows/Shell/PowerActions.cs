using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;

namespace Lumen.Platform.Windows.Shell;

/// <summary>
/// Sleep / hibernate / restart / shut down with documented APIs.
///
/// Sleep: on Modern Standby devices (most handhelds, "AoAc") SetSuspendState is not the right call -
/// it can hibernate instead - so Lumen turns the display off, which is exactly what the power button
/// does and enters standby. On classic S3 systems SetSuspendState(sleep) is used.
/// </summary>
public static class PowerActions
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IsPwrHibernateAllowed();

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities([Out] byte[] capabilities);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int OffsetSystemS3 = 5;   // SYSTEM_POWER_CAPABILITIES.SystemS3
    private const int OffsetAoAc = 20;      // SYSTEM_POWER_CAPABILITIES.AoAc (Modern Standby)

    private static readonly Lazy<(bool S3, bool ModernStandby)> Caps = new(() =>
    {
        try
        {
            var buffer = new byte[256];
            if (GetPwrCapabilities(buffer)) return (buffer[OffsetSystemS3] != 0, buffer[OffsetAoAc] != 0);
        }
        catch { /* fall through */ }
        return (false, true);
    });

    public static bool IsModernStandby => Caps.Value.ModernStandby;

    public static bool CanHibernate
    {
        get
        {
            try { return IsPwrHibernateAllowed(); }
            catch { return false; }
        }
    }

    public static void Sleep()
    {
        Log.Info($"Sleep requested (modern standby: {IsModernStandby}, S3: {Caps.Value.S3}).");
        if (!IsModernStandby && Caps.Value.S3 && SetSuspendState(false, false, false)) return;
        DisplayOff();
    }

    /// <summary>Same as pressing the power button on a Modern Standby device.</summary>
    public static void DisplayOff()
    {
        const uint WM_SYSCOMMAND = 0x0112;
        const int SC_MONITORPOWER = 0xF170;
        PostMessage(new IntPtr(0xFFFF), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
    }

    public static bool Hibernate()
    {
        Log.Info("Hibernate requested.");
        return CanHibernate && SetSuspendState(true, false, false);
    }

    public static bool Restart() => RunShutdown("/r /t 0");

    public static bool ShutDown() => RunShutdown("/s /t 0");

    public static bool OpenTaskManager() => Shell("taskmgr.exe");

    private static bool RunShutdown(string args)
    {
        Log.Info($"shutdown {args}");
        return Shell(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args, hidden: true);
    }

    private static bool Shell(string file, string args = "", bool hidden = false)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Starting {file} failed.", ex);
            return false;
        }
    }
}
