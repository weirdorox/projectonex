using Lumen.Core.Diagnostics;
using Lumen.Core.Startup;
using Microsoft.Win32;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Shell;

public enum StartupState
{
    Disabled,
    Enabled,
    /// <summary>Registered, but the user turned it off in Task Manager / Settings > Apps > Startup.</summary>
    DisabledByUser,
    /// <summary>Blocked by policy.</summary>
    DisabledByPolicy,
}

/// <summary>
/// "Start Lumen when I sign in". Never touches the Winlogon Shell value or replaces explorer.exe;
/// it registers a normal per-user startup entry that can be removed from Task Manager, Settings,
/// the Lumen recovery tool, or by uninstalling.
/// </summary>
public interface IStartupRegistration
{
    string Mechanism { get; }
    Task<StartupState> GetStateAsync();
    Task<StartupState> EnableAsync();
    Task DisableAsync();
}

public static class StartupRegistrationFactory
{
    public static IStartupRegistration Create(string executablePath) =>
        PackageInfo.IsPackaged ? new PackagedStartupTask() : new RunKeyStartupRegistration(executablePath);
}

/// <summary>Unpackaged: HKCU\Software\Microsoft\Windows\CurrentVersion\Run. No admin rights needed.</summary>
public sealed class RunKeyStartupRegistration : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "Lumen";

    private readonly string _exe;

    public RunKeyStartupRegistration(string executablePath) => _exe = executablePath;

    public string Mechanism => @"Per-user Run key (HKCU\...\CurrentVersion\Run)";

    public string Command => $"\"{_exe}\" {StartupOptions.AutoStartSwitch}";

    public Task<StartupState> GetStateAsync()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string) return Task.FromResult(StartupState.Disabled);

        // Task Manager writes a 12-byte blob; first byte 0x02/0x06 = enabled, odd = disabled.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } blob && (blob[0] & 1) == 1)
            return Task.FromResult(StartupState.DisabledByUser);

        return Task.FromResult(StartupState.Enabled);
    }

    public Task<StartupState> EnableAsync()
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        run.SetValue(ValueName, Command, RegistryValueKind.String);
        Log.Info($"Autostart enabled: {Command}");
        return GetStateAsync();
    }

    public Task DisableAsync()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info("Autostart disabled.");
        return Task.CompletedTask;
    }

    /// <summary>Used by the recovery tool - no instance needed.</summary>
    public static void RemoveStatic()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>MSIX: windows.startupTask extension declared in Package.appxmanifest (TaskId "LumenStartup").</summary>
public sealed class PackagedStartupTask : IStartupRegistration
{
    public const string TaskId = "LumenStartup";

    public string Mechanism => "MSIX startup task";

    public async Task<StartupState> GetStateAsync() => Map((await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId)).State);

    public async Task<StartupState> EnableAsync()
    {
        var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        var state = await task.RequestEnableAsync();
        Log.Info($"Startup task enable requested: {state}");
        return Map(state);
    }

    public async Task DisableAsync()
    {
        var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
        task.Disable();
    }

    private static StartupState Map(global::Windows.ApplicationModel.StartupTaskState s) => s switch
    {
        global::Windows.ApplicationModel.StartupTaskState.Enabled => StartupState.Enabled,
        global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => StartupState.Enabled,
        global::Windows.ApplicationModel.StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        global::Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
        _ => StartupState.Disabled,
    };
}

public static class PackageInfo
{
    private static readonly Lazy<bool> Packaged = new(() =>
    {
        try
        {
            var length = 0;
            var rc = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return rc != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>True when running with MSIX package identity.</summary>
    public static bool IsPackaged => Packaged.Value;
}
