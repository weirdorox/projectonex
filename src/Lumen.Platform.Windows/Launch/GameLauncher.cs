using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Platform.Windows.Launch;

/// <summary>
/// Starts games. Store games go through their client's protocol handler (DRM, overlays and cloud
/// saves keep working); executables are started via the shell so UAC manifests are honoured;
/// packaged apps are activated through IApplicationActivationManager (documented COM API), which
/// also returns the process id.
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    public LaunchResult Launch(LaunchSpec spec)
    {
        try
        {
            return spec.Kind switch
            {
                LaunchKind.Uri => LaunchUri(spec.Target),
                LaunchKind.Executable => LaunchExecutable(spec),
                LaunchKind.PackagedApp => LaunchPackaged(spec.Target, spec.Arguments),
                _ => LaunchResult.Fail($"Unknown launch kind {spec.Kind}"),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return LaunchResult.Fail("Launch was cancelled at the administrator prompt.");
        }
        catch (Exception ex)
        {
            Log.Error($"Launch failed for {spec.Kind} '{spec.Target}'", ex);
            return LaunchResult.Fail(ex.Message);
        }
    }

    private static LaunchResult LaunchUri(string uri)
    {
        Log.Info($"Launching URI {uri}");
        using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return LaunchResult.Ok();
    }

    private static LaunchResult LaunchExecutable(LaunchSpec spec)
    {
        if (!File.Exists(spec.Target))
            return LaunchResult.Fail($"Executable not found: {spec.Target}");

        var psi = new ProcessStartInfo(spec.Target)
        {
            UseShellExecute = true,
            Arguments = spec.Arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                ? Path.GetDirectoryName(spec.Target) ?? string.Empty
                : spec.WorkingDirectory,
        };
        Log.Info($"Launching executable {spec.Target} {psi.Arguments}");
        using var p = Process.Start(psi);
        return LaunchResult.Ok(p?.Id);
    }

    private static LaunchResult LaunchPackaged(string appUserModelId, string? arguments)
    {
        Log.Info($"Activating packaged app {appUserModelId}");
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = manager.ActivateApplication(appUserModelId, arguments ?? string.Empty, ActivateOptions.None, out var pid);
            if (hr >= 0) return LaunchResult.Ok((int)pid);
            Log.Warn($"ActivateApplication returned 0x{hr:X8}; falling back to shell:AppsFolder.");
        }
        catch (Exception ex)
        {
            Log.Warn("ActivateApplication failed; falling back to shell:AppsFolder.", ex);
        }

        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appUserModelId}") { UseShellExecute = true });
        return LaunchResult.Ok();
    }

    // ---- IApplicationActivationManager (shobjidl_core.h) ----
    [Flags]
    private enum ActivateOptions
    {
        None = 0x0,
        DesignMode = 0x1,
        NoErrorUI = 0x2,
        NoSplashScreen = 0x4,
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, ActivateOptions options, out uint processId);

        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
}

/// <summary>
/// Measures play time for games whose process Lumen started directly. Store-launched games
/// (Steam/Epic URIs) hand off to the client, so no reliable process is known; those only get
/// "last played" and launch count. Sessions shorter than <see cref="MinimumSession"/> are ignored
/// (launcher stubs that exit immediately).
/// </summary>
public sealed class PlaySessionTracker
{
    public static readonly TimeSpan MinimumSession = TimeSpan.FromSeconds(60);

    public event Action<string, TimeSpan>? SessionEnded;

    public void Track(string gameId, int processId)
    {
        try
        {
            var p = Process.GetProcessById(processId);
            var started = DateTimeOffset.UtcNow;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                var duration = DateTimeOffset.UtcNow - started;
                Log.Info($"Game {gameId} exited after {duration:hh\\:mm\\:ss}.");
                if (duration >= MinimumSession) SessionEnded?.Invoke(gameId, duration);
                p.Dispose();
            };
        }
        catch (Exception ex)
        {
            // Elevated games deny SYNCHRONIZE access to a non-elevated shell - expected.
            Log.Info($"Cannot track process {processId} for {gameId}: {ex.Message}");
        }
    }
}
