using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Lumen.Core.Diagnostics;

namespace Lumen.Platform.Windows.Power;

/// <summary>
/// Creates/removes the "Lumen\PowerHelper" scheduled task. The task runs Lumen.PowerHelper with the
/// user's highest privileges *on demand only* (no trigger), so Lumen can start it without a UAC prompt
/// every time. Creating and deleting it each require one explicit UAC confirmation. Nothing else on the
/// system is changed.
/// </summary>
public static class PowerHelperSetup
{
    public const string TaskName = @"Lumen\PowerHelper";

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Lumen.PowerHelper.exe");

    public static string CurrentUserSid
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? throw new InvalidOperationException("No user SID.");
        }
    }

    public static bool IsHelperShipped => File.Exists(HelperPath);

    public static bool IsInstalled() => RunSchtasks($"/Query /TN \"{TaskName}\"", elevated: false) == 0;

    private static (bool Value, DateTime At)? _installedCache;

    /// <summary><see cref="IsInstalled"/> cached for 30 s (it starts schtasks.exe) - for frequently polled UI/telemetry.</summary>
    public static bool IsInstalledCached()
    {
        if (_installedCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Value;
        var v = IsInstalled();
        _installedCache = (v, DateTime.UtcNow);
        return v;
    }

    public static void InvalidateCache() => _installedCache = null;

    /// <summary>Starts the helper through the task (no UAC). Returns false if the task does not exist.</summary>
    public static bool Start() => RunSchtasks($"/Run /TN \"{TaskName}\"", elevated: false) == 0;

    /// <summary>Registers the task. Shows one UAC prompt. Returns null on success or an error message.</summary>
    public static string? Install(string dataDirectory)
    {
        InvalidateCache();
        if (!IsHelperShipped) return "Lumen.PowerHelper.exe is missing from the installation.";
        var sid = CurrentUserSid;
        var xml = BuildTaskXml(HelperPath, sid, dataDirectory);
        var file = Path.Combine(Path.GetTempPath(), $"lumen-powerhelper-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var rc = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", elevated: true);
            return rc switch
            {
                0 => null,
                1223 => "Cancelled at the administrator prompt.",
                _ => $"Windows Task Scheduler returned error {rc}.",
            };
        }
        finally
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
    }

    /// <summary>Removes the task. Shows one UAC prompt.</summary>
    public static string? Uninstall()
    {
        InvalidateCache();
        var rc = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevated: true);
        return rc is 0 ? null : rc == 1223 ? "Cancelled at the administrator prompt." : $"Task Scheduler error {rc}.";
    }

    /// <summary>Removal from an already elevated process (recovery tool).</summary>
    public static int UninstallElevated() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevated: false);

    public static string BuildTaskXml(string helperPath, string userSid, string dataDirectory)
    {
        string E(string s) => SecurityElement.Escape(s) ?? s;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Lumen power helper: real TDP control through the PawnIO driver. Started on demand by Lumen; remove it in Lumen Settings or with Lumen Recovery.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{E(userSid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{E(helperPath)}</Command>
                  <Arguments>--user-sid {E(userSid)} --data "{E(dataDirectory)}"</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(string arguments, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = elevated,
                Verb = elevated ? "runas" : string.Empty,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!elevated)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }
            using var p = Process.Start(psi);
            if (p is null) return -1;
            if (!elevated)
            {
                _ = p.StandardOutput.ReadToEnd();
                _ = p.StandardError.ReadToEnd();
            }
            p.WaitForExit(30000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
        catch (Exception ex)
        {
            Log.Warn($"schtasks {arguments} failed.", ex);
            return -1;
        }
    }
}
