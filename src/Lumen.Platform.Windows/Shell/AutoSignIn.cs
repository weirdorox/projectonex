using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Microsoft.Win32;

namespace Lumen.Platform.Windows.Shell;

public sealed record AutoSignInStatus(
    bool Enabled,
    string? UserName,
    string? Domain,
    bool PlaintextPasswordPresent,  // a plaintext DefaultPassword value exists in Winlogon (insecure)
    bool WindowsHelloOnlyEnabled);  // "Only allow Windows Hello sign-in" is on - hides the netplwiz checkbox

/// <summary>
/// Inspects - but never silently changes - automatic sign-in.
///
/// Lumen deliberately does NOT write passwords. The supported, reversible ways to enable automatic
/// sign-in are Microsoft's own tools, launched explicitly by the user:
///  1. Sysinternals Autologon (stores the password as an encrypted LSA secret), or
///  2. netplwiz ("Users must enter a user name and password" checkbox).
/// Disabling is done by those same tools or by the Lumen recovery tool.
/// </summary>
public static class AutoSignIn
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string PasswordLessKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

    public const string AutologonDownloadUrl = "https://learn.microsoft.com/sysinternals/downloads/autologon";

    public static AutoSignInStatus Read()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var winlogon = hklm.OpenSubKey(WinlogonKey);
            var enabled = (winlogon?.GetValue("AutoAdminLogon") as string)?.Trim() == "1";
            var user = winlogon?.GetValue("DefaultUserName") as string;
            var domain = winlogon?.GetValue("DefaultDomainName") as string;
            // Only the *presence* of the value name is checked; the value is never read.
            var plaintext = winlogon?.GetValueNames().Contains("DefaultPassword", StringComparer.OrdinalIgnoreCase) == true;

            using var pwless = hklm.OpenSubKey(PasswordLessKey);
            var helloOnly = pwless?.GetValue("DevicePasswordLessBuildVersion") is int v && v == 2;

            return new AutoSignInStatus(enabled, user, domain, plaintext, helloOnly);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read auto sign-in status.", ex);
            return new AutoSignInStatus(false, null, null, false, false);
        }
    }

    /// <summary>Opens the classic User Accounts dialog (UAC prompt). The user makes the change there.</summary>
    public static bool OpenUserAccountsDialog() => Shell("netplwiz");

    /// <summary>Settings > Accounts > Sign-in options (for the "Windows Hello only" toggle).</summary>
    public static bool OpenSignInOptions() => Shell("ms-settings:signinoptions");

    /// <summary>Opens Microsoft's download page for Sysinternals Autologon.</summary>
    public static bool OpenAutologonDownload() => Shell(AutologonDownloadUrl);

    private static bool Shell(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}.", ex);
            return false;
        }
    }
}
