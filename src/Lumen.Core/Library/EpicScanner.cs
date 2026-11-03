using System.Text.Json;
using Lumen.Core.Diagnostics;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Discovers Epic Games Store installs from the launcher's *.item manifests
/// (%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests). Launches through the Epic launcher URI
/// so ownership checks and cloud saves work.
/// </summary>
public sealed class EpicScanner : IGameScanner
{
    private readonly IRegistryReader _registry;
    private readonly string? _manifestDirOverride;

    public EpicScanner(IRegistryReader registry, string? manifestDirOverride = null)
    {
        _registry = registry;
        _manifestDirOverride = manifestDirOverride;
    }

    public GameSource Source => GameSource.Epic;
    public string DisplayName => "Epic Games";

    public string? LocateManifests()
    {
        if (!string.IsNullOrWhiteSpace(_manifestDirOverride)) return _manifestDirOverride;

        var dataPath = _registry.GetValue(RegistryRoot.LocalMachine, @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath");
        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            var dir = Path.Combine(dataPath, "Manifests");
            if (Directory.Exists(dir)) return dir;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            var fallback = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (Directory.Exists(fallback)) return fallback;
        }
        return null;
    }

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var results = new List<GameEntry>();
            var dir = LocateManifests();
            if (dir is null)
            {
                Log.Info("Epic: launcher manifests not found, skipping.");
                return results;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var game = ParseManifest(File.ReadAllText(file));
                    if (game is not null) results.Add(game);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Epic: failed to parse {file}", ex);
                }
            }

            Log.Info($"Epic: found {results.Count} installed games.");
            return results;
        }, cancellationToken);

    internal static GameEntry? ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = doc.RootElement;

        string? Str(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        bool Bool(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True;

        if (Bool("bIsIncompleteInstall")) return null;

        var appName = Str("AppName");
        var mainGame = Str("MainGameAppName");
        // DLC / add-ons have their own manifest pointing at the base game.
        if (!string.IsNullOrEmpty(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase))
            return null;

        if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            var categories = cats.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList();
            if (categories.Count > 0 && !categories.Contains("games", StringComparer.OrdinalIgnoreCase))
                return null; // e.g. Unreal Engine, tools
        }

        var title = Str("DisplayName");
        var ns = Str("CatalogNamespace");
        var itemId = Str("CatalogItemId");
        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(title)) return null;

        var installLocation = Str("InstallLocation");
        var exe = Str("LaunchExecutable");

        LaunchSpec launch;
        if (!string.IsNullOrWhiteSpace(ns) && !string.IsNullOrWhiteSpace(itemId))
        {
            launch = new LaunchSpec(LaunchKind.Uri,
                $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(itemId)}%3A{Uri.EscapeDataString(appName)}?action=launch&silent=true");
        }
        else
        {
            launch = new LaunchSpec(LaunchKind.Uri,
                $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true");
        }

        string? icon = null;
        if (!string.IsNullOrWhiteSpace(installLocation) && !string.IsNullOrWhiteSpace(exe))
            icon = Path.Combine(installLocation, exe);

        return new GameEntry
        {
            Id = GameEntry.MakeId(GameSource.Epic, appName),
            Source = GameSource.Epic,
            ExternalId = appName,
            Title = title.Trim(),
            InstallPath = installLocation,
            Launch = launch,
            Artwork = new ArtworkSet(Icon: icon),
        };
    }
}
