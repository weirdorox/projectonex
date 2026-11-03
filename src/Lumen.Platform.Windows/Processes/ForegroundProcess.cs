using System.Diagnostics;
using Lumen.Core.Diagnostics;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Processes;

public sealed record ProcessInfo(int ProcessId, string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// Identifies processes with documented, low-privilege APIs (PROCESS_QUERY_LIMITED_INFORMATION works
/// even for most elevated processes). Cheap enough to call once per second.
/// </summary>
public static class ForegroundProcess
{
    /// <summary>The process owning the foreground window, or null.</summary>
    public static ProcessInfo? Current()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : Describe((int)pid);
    }

    public static ProcessInfo? Describe(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? new ProcessInfo(processId, new string(buffer, 0, (int)size))
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool IsAlive(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception)
        {
            // Access denied for HasExited on some protected processes: it exists.
            return true;
        }
    }
}

public enum CloseResult { Closed, StillRunning, AccessDenied, NotRunning }

/// <summary>Closing games politely first; force only on an explicit second request.</summary>
public static class ProcessCloser
{
    public static async Task<CloseResult> RequestCloseAsync(int processId, TimeSpan wait)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            if (!p.CloseMainWindow()) Log.Info($"Process {processId} has no main window to close.");
            using var cts = new CancellationTokenSource(wait);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return CloseResult.StillRunning; }
            return CloseResult.Closed;
        }
        catch (ArgumentException) { return CloseResult.NotRunning; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn($"Close request for {processId} failed.", ex);
            return CloseResult.AccessDenied;
        }
    }

    public static CloseResult ForceClose(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            Log.Warn($"Force-closed process {processId}.");
            return CloseResult.Closed;
        }
        catch (ArgumentException) { return CloseResult.NotRunning; }
        catch (Exception ex)
        {
            Log.Warn($"Force close of {processId} failed.", ex);
            return CloseResult.AccessDenied;
        }
    }
}
