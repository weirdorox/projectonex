using Lumen.Core.Diagnostics;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Discovers GOG games from HKLM\SOFTWARE\WOW6432Node\GOG.com\Games (written by GOG installers
/// and GOG Galaxy). Launches the game executable directly - GOG games are DRM-free - unless the
/// user prefers launching through Galaxy (for cloud saves / achievements).
/// </summary>
public sealed class GogScanner : IGameScanner
{
    internal const string GamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";
    internal const string GalaxyKey = @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths";

    private readonly IRegistryReader _registry;
    private readonly bool _preferGalaxy;

    public GogScanner(IRegistryReader registry, bool preferGalaxy = false)
    {
        _registry = registry;
        _preferGalaxy = preferGalaxy;
    }

    public GameSource Source => GameSource.Gog;
    public string DisplayName => "GOG";

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var results = new List<GameEntry>();
            var galaxyDir = _registry.GetValue(RegistryRoot.LocalMachine, GalaxyKey, "client");
            var galaxyExe = string.IsNullOrWhiteSpace(galaxyDir) ? null : Path.Combine(galaxyDir, "GalaxyClient.exe");

            foreach (var id in _registry.GetSubKeyNames(RegistryRoot.LocalMachine, GamesKey))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var game = Read(id, galaxyExe);
                    if (game is not null) results.Add(game);
                }
                catch (Exception ex)
                {
                    Log.Warn($"GOG: failed to read game {id}", ex);
                }
            }

            Log.Info($"GOG: found {results.Count} installed games.");
            return results;
        }, cancellationToken);

    internal GameEntry? Read(string subKey, string? galaxyExe)
    {
        var key = $@"{GamesKey}\{subKey}";
        string? V(string name) => _registry.GetValue(RegistryRoot.LocalMachine, key, name);

        // DLC entries depend on a base game.
        if (!string.IsNullOrWhiteSpace(V("dependsOn"))) return null;

        var gameId = V("gameID") ?? subKey;
        var name = V("gameName") ?? V("GAMENAME");
        var path = V("path") ?? V("PATH");
        var exe = V("exe") ?? V("EXE");
        var launchParams = V("launchParam");
        var workingDir = V("workingDir");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exe)) return null;

        LaunchSpec launch;
        if (_preferGalaxy && galaxyExe is not null && File.Exists(galaxyExe))
        {
            launch = new LaunchSpec(LaunchKind.Executable, galaxyExe,
                $"/command=runGame /gameId={gameId} /path=\"{path}\"", Path.GetDirectoryName(galaxyExe));
        }
        else
        {
            launch = new LaunchSpec(LaunchKind.Executable, exe,
                string.IsNullOrWhiteSpace(launchParams) ? null : launchParams,
                string.IsNullOrWhiteSpace(workingDir) ? (path ?? Path.GetDirectoryName(exe)) : workingDir);
        }

        string? icon = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var ico = Path.Combine(path, $"goggame-{gameId}.ico");
            icon = File.Exists(ico) ? ico : exe;
        }

        return new GameEntry
        {
            Id = GameEntry.MakeId(GameSource.Gog, gameId),
            Source = GameSource.Gog,
            ExternalId = gameId,
            Title = name.Trim(),
            InstallPath = path,
            Launch = launch,
            Artwork = new ArtworkSet(Icon: icon),
        };
    }
}
