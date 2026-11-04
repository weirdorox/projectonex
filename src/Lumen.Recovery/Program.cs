using System.Diagnostics;
using Lumen.Core;
using Lumen.Core.Diagnostics;
using Lumen.Platform.Windows.Shell;

namespace Lumen.Recovery;

/// <summary>
/// Lumen Recovery - restores normal Windows operation.
///
///   Lumen.Recovery.exe                     interactive menu
///   Lumen.Recovery.exe --restore           close Lumen, remove autostart, safe mode next start, ensure desktop
///   Lumen.Recovery.exe --disable-autologon turn off automatic sign-in (asks for administrator rights)
///   Lumen.Recovery.exe --remove-power-helper remove the elevated TDP helper task (administrator)
///   Lumen.Recovery.exe --rollback-update   reinstall the previous Lumen version after a bad update
///   Lumen.Recovery.exe --all               all of the above
///   add --quiet to skip the final "press a key"
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var paths = AppPaths.ForCurrentUser();
        Log.Initialize(paths.Logs);
        Log.Info($"Lumen Recovery started: {string.Join(' ', args)} (elevated: {RecoveryActions.IsElevated})");

        var a = args.Select(x => x.ToLowerInvariant()).ToHashSet();
        var quiet = a.Contains("--quiet");
        try
        {
            if (a.Contains("--all"))
            {
                Restore(paths);
                DisableAutoLogon(args);
                RemovePowerHelper();
            }
            else if (a.Contains("--rollback-update"))
            {
                var ok = RollBackUpdate(paths);
                Pause(quiet);
                return ok ? 0 : 2;
            }
            else if (a.Contains("--remove-power-helper"))
            {
                var ok = RemovePowerHelper();
                Pause(quiet);
                return ok ? 0 : 2;
            }
            else if (a.Contains("--restore"))
            {
                Restore(paths);
            }
            else if (a.Contains("--disable-autologon"))
            {
                var ok = DisableAutoLogon(args);
                Pause(quiet);
                return ok ? 0 : 2;
            }
            else
            {
                return Menu(paths, args);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Recovery failed.", ex);
            Console.WriteLine($"Error: {ex.Message}");
            Pause(quiet);
            return 1;
        }

        Pause(quiet);
        return 0;
    }

    private static int Menu(AppPaths paths, string[] args)
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("=====================================");
            Console.WriteLine("  Lumen Recovery");
            Console.WriteLine("=====================================");
            var autoSignIn = AutoSignIn.Read();
            Console.WriteLine($"  Automatic sign-in: {(autoSignIn.Enabled ? "ON for " + autoSignIn.UserName : "off")}");
            Console.WriteLine($"  Running elevated:  {(RecoveryActions.IsElevated ? "yes" : "no")}");
            Console.WriteLine();
            Console.WriteLine("  1  Restore normal Windows startup");
            Console.WriteLine("     (close Lumen, stop it starting at sign-in, safe mode next time, show desktop)");
            Console.WriteLine("  2  Turn off automatic sign-in (administrator)");
            Console.WriteLine("  3  Both");
            Console.WriteLine("  4  Open Lumen log folder");
            Console.WriteLine("  5  Remove the Lumen power helper / TDP control (administrator)");
            Console.WriteLine("  Q  Quit");
            Console.WriteLine();
            Console.Write("Choose: ");
            var key = Console.ReadKey().KeyChar;
            Console.WriteLine();
            switch (char.ToLowerInvariant(key))
            {
                case '1': Restore(paths); Pause(false); break;
                case '2': DisableAutoLogon(args); Pause(false); break;
                case '3': Restore(paths); DisableAutoLogon(args); Pause(false); break;
                case '4':
                    using (Process.Start(new ProcessStartInfo(paths.Logs) { UseShellExecute = true })) { }
                    break;
                case '5': RemovePowerHelper(); Pause(false); break;
                case 'q': return 0;
            }
        }
    }

    private static void Restore(AppPaths paths)
    {
        Step("Closing Lumen", () => $"{RecoveryActions.StopShell()} process(es) closed");
        Step("Removing 'start at sign-in'", () => { RecoveryActions.RemoveAutostart(); return "done"; });
        Step("Requesting safe mode on next Lumen start", () => { RecoveryActions.RequestSafeModeOnNextStart(paths); return "done"; });
        Step("Making sure the Windows desktop is running", () => { RecoveryActions.RestoreDesktop(); return "done"; });
        Console.WriteLine();
        Console.WriteLine("If Lumen was installed from an MSIX package, also check Settings > Apps > Startup.");
    }

    private static bool DisableAutoLogon(string[] args)
    {
        if (!RecoveryActions.IsElevated)
        {
            Console.WriteLine("Administrator rights are needed - asking Windows for permission...");
            try
            {
                var exe = Environment.ProcessPath!;
                using var p = Process.Start(new ProcessStartInfo(exe, "--disable-autologon --quiet")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit();
                var done = p?.ExitCode == 0;
                Console.WriteLine(done ? "Automatic sign-in is now off." : "The elevated step did not complete.");
                return done;
            }
            catch (Exception ex)
            {
                // ERROR_CANCELLED (1223) when the UAC prompt is declined.
                Console.WriteLine($"Not changed: {ex.Message}");
                return false;
            }
        }

        var (ok, message) = RecoveryActions.DisableAutoSignIn();
        Console.WriteLine(ok ? $"Automatic sign-in is now off. {message}" : $"Not changed: {message}");
        return ok;
    }

    /// <summary>
    /// Removes the "Lumen\PowerHelper" scheduled task. TDP limits themselves return to the firmware
    /// values on the next restart (and the helper restores them when Lumen stops).
    /// </summary>
    private static bool RemovePowerHelper()
    {
        if (!RecoveryActions.IsElevated)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--remove-power-helper --quiet")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                p?.WaitForExit();
                var done = p?.ExitCode == 0;
                Console.WriteLine(done ? "Power helper removed." : "The power helper was not removed.");
                return done;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Not changed: {ex.Message}");
                return false;
            }
        }
        var rc = Lumen.Platform.Windows.Power.PowerHelperSetup.UninstallElevated();
        Console.WriteLine(rc == 0 ? "Power helper removed. TDP returns to firmware limits after a restart." : "No power helper task was found.");
        return true;
    }

    private static void Step(string name, Func<string> action)
    {
        Console.Write($"  {name}... ");
        try
        {
            Console.WriteLine(action());
        }
        catch (Exception ex)
        {
            Log.Warn($"Recovery step '{name}' failed.", ex);
            Console.WriteLine($"failed ({ex.Message})");
        }
    }

    /// <summary>Reinstalls the previous Lumen version with its verified installer (after a bad update).</summary>
    private static bool RollBackUpdate(Lumen.Core.AppPaths paths)
    {
        var journalPath = Lumen.Core.Updates.UpdateJournal.JournalPath(paths.Root);
        var journal = Lumen.Core.Updates.UpdateJournal.Load(journalPath);
        if (journal.VerifiedPreviousInstaller() is not { } installer)
        {
            Console.WriteLine("No verified previous Lumen installer is available - reinstall Lumen from GitHub instead.");
            return false;
        }
        Console.WriteLine($"Rolling back to Lumen {journal.FromVersion} …");
        journal.Phase = Lumen.Core.Updates.UpdatePhase.Installing;
        (journal.FromVersion, journal.ToVersion) = (journal.ToVersion, journal.FromVersion);
        journal.InstallerPath = installer;
        journal.PreviousInstallerPath = null;
        journal.PreviousInstallerSha256 = null;
        journal.StartsSinceInstall = 0;
        journal.Save(journalPath);
        using var _ = Process.Start(new ProcessStartInfo(installer,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP- /LUMENUPDATE=1 /LOG=\"{Path.Combine(paths.Root, "updates", "rollback.log")}\"")
        { UseShellExecute = true });
        Log.Info($"Rollback started with {installer}.");
        return true;
    }

    private static void Pause(bool quiet)
    {
        if (quiet || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey(intercept: true);
    }
}
