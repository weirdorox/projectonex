using System.Diagnostics;
using Lumen.Core.Diagnostics;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Shell;

/// <summary>Helpers that keep the normal Windows desktop reachable at all times.</summary>
public static class DesktopSwitcher
{
    /// <summary>
    /// Lumen never replaces explorer.exe, so explorer is normally running. If it crashed or was
    /// killed, start it again so the taskbar and desktop come back.
    /// </summary>
    public static void EnsureExplorerRunning()
    {
        try
        {
            var session = Process.GetCurrentProcess().SessionId;
            var running = Process.GetProcessesByName("explorer").Any(p =>
            {
                using (p) return p.SessionId == session;
            });
            if (running) return;

            Log.Warn("explorer.exe was not running in this session - starting it.");
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(windir, "explorer.exe")) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not ensure explorer.exe is running.", ex);
        }
    }

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>
    /// Brings a window to the foreground even when another app (a game) owns it. Windows restricts
    /// SetForegroundWindow for background processes; temporarily attaching to the foreground
    /// thread's input queue is the documented way to satisfy those rules.
    /// </summary>
    public static void BringToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            var fg = GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            var current = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != current)
            {
                AttachThreadInput(current, fgThread, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(current, fgThread, false);
                }
            }
            else
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("BringToForeground failed.", ex);
        }
    }

    public static void Minimize(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_MINIMIZE);
    }
}
