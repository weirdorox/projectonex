using Lumen.Core;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Library;
using Lumen.Core.Profiles;
using Lumen.Core.Startup;
using Lumen.Data;
using Lumen.Platform.Windows;
using Lumen.Platform.Windows.Hardware;
using Lumen.Platform.Windows.Input;
using Lumen.Platform.Windows.Launch;
using Lumen.Platform.Windows.Library;
using Lumen.Platform.Windows.Power;
using Lumen.Platform.Windows.Shell;

namespace Lumen.App;

/// <summary>
/// Composition root. Every service is created here, in dependency order, with failures contained:
/// if the database is unusable the shell still starts (in safe mode) so the user can always reach
/// the desktop and the recovery options.
/// </summary>
public sealed class AppHost
{
    private AppHost() { }

    public required StartupOptions Options { get; init; }
    public required AppPaths Paths { get; init; }
    public required CrashLoopGuard Guard { get; init; }
    public required StartupDecision Decision { get; init; }
    public required string? SafeModeReason { get; init; }
    public required ShellSettings Settings { get; init; }
    public required IGameRepository Games { get; init; }
    public required IProfileRepository Profiles { get; init; }
    public required ICollectionRepository Collections { get; init; }
    public required SteamScanner Steam { get; init; }
    public required SteamWebCache SteamWeb { get; init; }
    public required Lumen.Core.Activity.IPlaySessionRepository History { get; init; }
    public required LibraryService Library { get; init; }
    public required GameLauncher Launcher { get; init; }
    public required PlaySessionTracker Sessions { get; init; }
    public required XInputGamepadSource Gamepad { get; init; }
    public required IStartupRegistration Startup { get; init; }
    public required PowerEventsMonitor Power { get; init; }
    public required Win32BatteryInfo Battery { get; init; }

    /// <summary>Hardware probing (WMI, performance counters) is slow-ish, so it runs off the startup path.</summary>
    public required Lazy<Task<HardwareServices>> Hardware { get; init; }

    public bool IsSafeMode => Decision == StartupDecision.SafeMode;

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Lumen.exe");

    public static AppHost Create(StartupOptions options)
    {
        var paths = AppPaths.ForCurrentUser();
        try { paths.EnsureCreated(); } catch { /* logged below if it matters */ }
        Log.Initialize(paths.Logs);
        Log.Info($"==== Lumen {typeof(AppHost).Assembly.GetName().Version} starting (args: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}) ====");
        Log.Info($"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}, packaged={PackageInfo.IsPackaged}");
        Log.Phase("logging initialised");

        // Recovery flag written by the recovery tool => safe mode once.
        var recoveryRequested = options.SafeMode;
        string? reason = options.SafeMode ? "Safe mode was requested on the command line." : null;
        if (File.Exists(paths.RecoveryFlag))
        {
            recoveryRequested = true;
            reason = "The recovery tool asked Lumen to start in safe mode.";
            try { File.Delete(paths.RecoveryFlag); } catch { /* ignore */ }
        }

        var guard = new CrashLoopGuard(paths.StartupState);
        var decision = guard.OnStartup(DateTimeOffset.UtcNow, recoveryRequested);
        if (decision == StartupDecision.SafeMode && reason is null)
            reason = $"Lumen did not shut down cleanly {guard.RecentCrashCount} times in a row.";
        Log.Phase($"crash guard: {decision}");

        // Data
        IGameRepository games;
        IProfileRepository profiles;
        ICollectionRepository collections;
        Lumen.Core.Activity.IPlaySessionRepository history;
        ISettingsStore settingsStore;
        try
        {
            var db = new LumenDatabase(paths.Database);
            db.Migrate();
            games = new SqliteGameRepository(db);
            profiles = new SqliteProfileRepository(db);
            collections = new SqliteCollectionRepository(db);
            history = new SqlitePlaySessionRepository(db);
            settingsStore = new SqliteSettingsStore(db);
        }
        catch (Exception ex)
        {
            Log.Error("Database unavailable - continuing in safe mode with an in-memory library.", ex);
            games = new InMemoryGameRepository();
            profiles = new InMemoryProfileRepository();
            collections = new InMemoryCollectionRepository();
            history = new Lumen.Core.Activity.InMemoryPlaySessionRepository();
            settingsStore = new InMemorySettingsStore();
            decision = StartupDecision.SafeMode;
            reason ??= $"The library database could not be opened ({ex.Message}).";
        }
        var settings = new ShellSettings(settingsStore);
        Log.Phase("database ready");

        var registry = WindowsRegistryReader.Instance;
        var steamWeb = new SteamWebCache(Path.Combine(paths.Root, "steam-web.json"));
        var steamScanner = new SteamScanner(registry, allowOnlineArtwork: settings.AllowOnlineArtwork,
            web: steamWeb, includeOwned: () => settings.SteamShowOwned && steamWeb.SteamId is not null);
        IGameScanner[] scanners =
        [
            steamScanner,
            new EpicScanner(registry),
            new GogScanner(registry, settings.PreferGogGalaxy),
            new XboxScanner(),
            new UbisoftScanner(registry),
            UninstallEntryScanner.Ea(registry),
            UninstallEntryScanner.BattleNet(registry),
            new FolderScanner(() => settings.LibraryFolders),
        ];

        var host = new AppHost
        {
            Options = options,
            Paths = paths,
            Guard = guard,
            Decision = decision,
            SafeModeReason = reason,
            Settings = settings,
            Games = games,
            Profiles = profiles,
            Collections = collections,
            History = history,
            Library = new LibraryService(games, scanners),
            Steam = steamScanner,
            SteamWeb = steamWeb,
            Launcher = new GameLauncher(),
            Sessions = new PlaySessionTracker(),
            Gamepad = new XInputGamepadSource(),
            Startup = StartupRegistrationFactory.Create(ExecutablePath),
            Power = new PowerEventsMonitor(),
            Battery = new Win32BatteryInfo(),
            Hardware = new Lazy<Task<HardwareServices>>(() => Task.Run(() => WindowsHardwareFactory.Create())),
        };

        return host;
    }
}
