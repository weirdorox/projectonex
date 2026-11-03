using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Lumen.Core;
using Lumen.Core.Diagnostics;
using Microsoft.Win32;

namespace Lumen.Platform.Windows.Shell;

/// <summary>
/// Everything needed to return the device to normal Windows operation. Used by the recovery tool and
/// by Lumen's own "Restore normal Windows" command. Each step is independent and best-effort.
/// </summary>
public static class RecoveryActions
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Stops every Lumen shell process in this session (not the caller).</summary>
    public static int StopShell()
    {
        var count = 0;
        var self = Environment.ProcessId;
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("Lumen"))
        {
            using (p)
            {
                if (p.Id == self || p.SessionId != session) continue;
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: false);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not stop Lumen process {p.Id}.", ex);
                }
            }
        }
        return count;
    }

    /// <summary>Removes the per-user autostart entry. (An MSIX startup task is turned off in Settings > Apps > Startup.)</summary>
    public static void RemoveAutostart() => RunKeyStartupRegistration.RemoveStatic();

    /// <summary>Tells the next Lumen start to come up in safe mode (windowed, recovery screen).</summary>
    public static void RequestSafeModeOnNextStart(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.RecoveryFlag, $"Requested {DateTimeOffset.Now:O}");
    }

    public static void RestoreDesktop() => DesktopSwitcher.EnsureExplorerRunning();

    /// <summary>
    /// Turns automatic sign-in OFF. Requires administrator rights. Removes:
    ///  - AutoAdminLogon (set to "0"),
    ///  - a plaintext DefaultPassword registry value if one exists,
    ///  - the DefaultPassword LSA secret written by Sysinternals Autologon.
    /// The user name is left in place so the sign-in screen pre-selects the account.
    /// </summary>
    public static (bool Success, string Message) DisableAutoSignIn()
    {
        if (!IsElevated) return (false, "Administrator rights are required to turn off automatic sign-in.");
        var messages = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var winlogon = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: true);
            if (winlogon is not null)
            {
                winlogon.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                winlogon.DeleteValue("DefaultPassword", throwOnMissingValue: false);
                winlogon.DeleteValue("AutoLogonCount", throwOnMissingValue: false);
                messages.Add("AutoAdminLogon set to 0.");
            }

            var lsa = Lsa.DeleteSecret("DefaultPassword");
            messages.Add(lsa ? "Stored auto sign-in password removed." : "No stored auto sign-in password found.");
            Log.Info("Automatic sign-in disabled by recovery.");
            return (true, string.Join(' ', messages));
        }
        catch (Exception ex)
        {
            Log.Error("Disabling automatic sign-in failed.", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>Minimal LSA private-data access (advapi32, documented). Used only to DELETE the secret.</summary>
    private static class Lsa
    {
        private const uint POLICY_CREATE_SECRET = 0x00000020;
        private const uint STATUS_OBJECT_NAME_NOT_FOUND = 0xC0000034;

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [DllImport("advapi32.dll")]
        private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr policy);

        [DllImport("advapi32.dll")]
        private static extern uint LsaStorePrivateData(IntPtr policy, ref LSA_UNICODE_STRING keyName, IntPtr privateData);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policy);

        [DllImport("advapi32.dll")]
        private static extern int LsaNtStatusToWinError(uint status);

        /// <summary>Returns true if a secret was deleted, false if none existed. Throws on other errors.</summary>
        public static bool DeleteSecret(string name)
        {
            var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
            var status = LsaOpenPolicy(IntPtr.Zero, ref attrs, POLICY_CREATE_SECRET, out var policy);
            if (status != 0) throw new System.ComponentModel.Win32Exception(LsaNtStatusToWinError(status));

            var buffer = Marshal.StringToHGlobalUni(name);
            try
            {
                var key = new LSA_UNICODE_STRING
                {
                    Buffer = buffer,
                    Length = (ushort)(name.Length * 2),
                    MaximumLength = (ushort)((name.Length + 1) * 2),
                };
                status = LsaStorePrivateData(policy, ref key, IntPtr.Zero); // null data = delete
                if (status == STATUS_OBJECT_NAME_NOT_FOUND) return false;
                if (status != 0) throw new System.ComponentModel.Win32Exception(LsaNtStatusToWinError(status));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                LsaClose(policy);
            }
        }
    }
}
