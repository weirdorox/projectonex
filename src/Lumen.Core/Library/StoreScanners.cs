using Lumen.Core.Diagnostics;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\{id}\InstallDir (written by the
/// launcher for every installed game). Titles come from the matching "Uplay Install {id}" uninstall
/// entry. Launching through uplay://launch/{id}/0 keeps Ubisoft's DRM and cloud saves working.
/// </summary>
public sealed class UbisoftScanner : IGameScanner
{
    internal const string InstallsKey = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
    internal const string UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IRegistryReader _registry;
    public UbisoftScanner(IRegistryReader registry) => _registry = registry;

    public GameSource Source => GameSource.Ubisoft;
    public string DisplayName => "Ubisoft Connect";

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var results = new List<GameEntry>();
            foreach (var id in _registry.GetSubKeyNames(RegistryRoot.LocalMachine, InstallsKey))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!id.All(char.IsDigit)) continue;
                var dir = _registry.GetValue(RegistryRoot.LocalMachine, $@"{InstallsKey}\{id}", "InstallDir");
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var title = _registry.GetValue(RegistryRoot.LocalMachine, $@"{UninstallKey}\Uplay Install {id}", "DisplayName")
                            ?? ExecutableHeuristics.PrettifyTitle(Path.GetFileName(dir.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar)));
                results.Add(new GameEntry
                {
                    Id = GameEntry.MakeId(GameSource.Ubisoft, id),
                    Source = GameSource.Ubisoft,
                    ExternalId = id,
                    Title = title.Trim(),
                    InstallPath = dir.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar),
                    Launch = new LaunchSpec(LaunchKind.Uri, $"uplay://launch/{id}/0"),
                });
            }
            Log.Info($"Ubisoft: found {results.Count} installed games.");
            return results;
        }, cancellationToken);
}

/// <summary>
/// EA app and Battle.net games, from their Windows uninstall entries (the only stable, documented
/// local record either launcher writes). Matched by publisher; EA games additionally need the
/// "__Installer" folder EA's installer creates. They are launched through their executable - both
/// launchers intercept that and start the game through their client (sign-in/DRM still apply).
/// Product-specific deep links are not used because their IDs are not stored reliably on disk.
/// </summary>
public sealed class UninstallEntryScanner : IGameScanner
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private readonly IRegistryReader _registry;
    private readonly GameSource _source;
    private readonly Func<string, bool> _publisherMatches;
    private readonly Func<string, string, bool> _isGame; // (installDir, keyName)
    private readonly HashSet<string> _excludedNames;

    private UninstallEntryScanner(IRegistryReader registry, GameSource source, string displayName,
        Func<string, bool> publisherMatches, Func<string, string, bool> isGame, IEnumerable<string> excluded)
    {
        _registry = registry;
        _source = source;
        DisplayName = displayName;
        _publisherMatches = publisherMatches;
        _isGame = isGame;
        _excludedNames = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
    }

    public static UninstallEntryScanner Ea(IRegistryReader registry) => new(
        registry, GameSource.EA, "EA app",
        p => p.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase),
        (dir, _) => Directory.Exists(Path.Combine(dir, "__Installer")),
        ["EA app", "EA", "Origin", "EA Desktop"]);

    public static UninstallEntryScanner BattleNet(IRegistryReader registry) => new(
        registry, GameSource.BattleNet, "Battle.net",
        p => p.Contains("Blizzard", StringComparison.OrdinalIgnoreCase),
        (_, key) => !key.Equals("Battle.net", StringComparison.OrdinalIgnoreCase),
        ["Battle.net", "Blizzard Battle.net App"]);

    public GameSource Source => _source;
    public string DisplayName { get; }

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var results = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in UninstallKeys)
            foreach (var key in _registry.GetSubKeyNames(RegistryRoot.LocalMachine, root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var game = Read($@"{root}\{key}", key);
                    if (game is not null) results.TryAdd(game.Id, game);
                }
                catch (Exception ex)
                {
                    Log.Debug($"{DisplayName}: skipped {key}: {ex.Message}");
                }
            }
            Log.Info($"{DisplayName}: found {results.Count} installed games.");
            return results.Values.ToList();
        }, cancellationToken);

    internal GameEntry? Read(string keyPath, string keyName)
    {
        string? V(string name) => _registry.GetValue(RegistryRoot.LocalMachine, keyPath, name);
        var publisher = V("Publisher");
        var name = V("DisplayName");
        var dir = V("InstallLocation")?.Trim().Trim('"');
        if (publisher is null || !_publisherMatches(publisher) || string.IsNullOrWhiteSpace(name)) return null;
        if (_excludedNames.Contains(name.Trim()) || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        if (!_isGame(dir, keyName)) return null;

        var exe = ExeFromDisplayIcon(V("DisplayIcon"), dir) ?? ExecutableHeuristics.PickMainExecutable(dir);
        if (exe is null) return null;

        return new GameEntry
        {
            Id = GameEntry.MakeId(_source, keyName),
            Source = _source,
            ExternalId = keyName,
            Title = name.Trim(),
            InstallPath = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Launch = new LaunchSpec(LaunchKind.Executable, exe, null, Path.GetDirectoryName(exe)),
        };
    }

    /// <summary>DisplayIcon is often "C:\path\game.exe,0" - use it when it is an exe inside the install folder.</summary>
    internal static string? ExeFromDisplayIcon(string? displayIcon, string installDir)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 0 && !path[(comma + 1)..].Contains('\\')) path = path[..comma].Trim().Trim('"');
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        if (ExecutableHeuristics.IsExcluded(path)) return null;
        var full = Path.GetFullPath(path);
        var dir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
