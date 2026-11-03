using System.Globalization;
using Lumen.Core.Diagnostics;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Discovers installed Steam games from libraryfolders.vdf and appmanifest_*.acf files.
/// Launching goes through the Steam client (steam://rungameid/) so DRM, overlay and cloud saves work.
/// </summary>
public sealed class SteamScanner : IGameScanner
{
    private static readonly HashSet<string> ExcludedAppIds =
    [
        "228980",   // Steamworks Common Redistributables
        "1070560",  // Steam Linux Runtime
        "1391110",  // Steam Linux Runtime - Soldier
        "1628350",  // Steam Linux Runtime - Sniper
        "1493710",  // Proton Experimental
        "1826330",  // Proton EasyAntiCheat Runtime
    ];

    private static readonly string[] ExcludedNamePrefixes =
    [
        "Steamworks Common", "Proton ", "Steam Linux Runtime", "Steamworks SDK",
    ];

    private readonly IRegistryReader _registry;
    private readonly string? _steamPathOverride;
    private readonly bool _allowOnlineArtwork;

    public SteamScanner(IRegistryReader registry, string? steamPathOverride = null, bool allowOnlineArtwork = true)
    {
        _registry = registry;
        _steamPathOverride = steamPathOverride;
        _allowOnlineArtwork = allowOnlineArtwork;
    }

    public GameSource Source => GameSource.Steam;
    public string DisplayName => "Steam";

    public string? LocateSteam()
    {
        if (!string.IsNullOrWhiteSpace(_steamPathOverride)) return _steamPathOverride;

        var candidates = new[]
        {
            _registry.GetValue(RegistryRoot.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            _registry.GetValue(RegistryRoot.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            _registry.GetValue(RegistryRoot.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var normalized = Path.GetFullPath(c.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(normalized)) return normalized;
        }
        return null;
    }

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() => Scan(cancellationToken), cancellationToken);

    private List<GameEntry> Scan(CancellationToken ct)
    {
        var results = new List<GameEntry>();
        var steam = LocateSteam();
        if (steam is null)
        {
            Log.Info("Steam: client not found, skipping.");
            return results;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in GetLibraryFolders(steam))
        {
            ct.ThrowIfCancellationRequested();
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); }
            catch (Exception ex) { Log.Warn($"Steam: cannot read {steamapps}", ex); continue; }

            foreach (var manifest in manifests)
            {
                try
                {
                    var game = ParseManifest(steam, steamapps, File.ReadAllText(manifest));
                    if (game is not null && seen.Add(game.ExternalId)) results.Add(game);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Steam: failed to parse {manifest}", ex);
                }
            }
        }

        Log.Info($"Steam: found {results.Count} installed games.");
        return results;
    }

    internal static IReadOnlyList<string> GetLibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };
        var candidates = new[]
        {
            Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamPath, "config", "libraryfolders.vdf"),
        };

        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            VdfNode root;
            try { root = VdfParser.Parse(File.ReadAllText(file)); }
            catch (Exception ex) { Log.Warn($"Steam: cannot parse {file}", ex); continue; }

            var lib = root["libraryfolders"];
            if (lib is null) continue;

            foreach (var (key, node) in lib.Children)
            {
                string? path = node.IsObject
                    ? node.GetString("path")
                    : int.TryParse(key, out _) ? node.Value : null; // legacy format: "1" "D:\\SteamLibrary"

                if (string.IsNullOrWhiteSpace(path)) continue;
                path = path.Replace('/', Path.DirectorySeparatorChar);
                if (!folders.Any(f => string.Equals(f.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                    folders.Add(path);
            }
            break; // first readable file wins
        }
        return folders;
    }

    internal GameEntry? ParseManifest(string steamPath, string steamappsDir, string text)
    {
        var state = VdfParser.Parse(text)["AppState"];
        if (state is null) return null;

        var appId = state.GetString("appid");
        var name = state.GetString("name");
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name)) return null;
        if (ExcludedAppIds.Contains(appId)) return null;
        if (ExcludedNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return null;

        // StateFlags bit 4 = fully installed. Updating games keep bit 4 plus others.
        if (int.TryParse(state.GetString("StateFlags"), out var flags) && (flags & 4) == 0) return null;

        var installDir = state.GetString("installdir");
        var installPath = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(steamappsDir, "common", installDir);

        DateTimeOffset? lastPlayed = null;
        if (long.TryParse(state.GetString("LastPlayed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) && unix > 0)
            lastPlayed = DateTimeOffset.FromUnixTimeSeconds(unix);

        return new GameEntry
        {
            Id = GameEntry.MakeId(GameSource.Steam, appId),
            Source = GameSource.Steam,
            ExternalId = appId,
            Title = name.Trim(),
            InstallPath = installPath,
            Launch = new LaunchSpec(LaunchKind.Uri, $"steam://rungameid/{appId}"),
            Artwork = FindArtwork(steamPath, appId, _allowOnlineArtwork),
            LastPlayed = lastPlayed,
        };
    }

    /// <summary>
    /// Steam caches artwork in appcache/librarycache. Older clients use flat files
    /// ("{appid}_library_600x900.jpg"); newer clients use per-app folders, sometimes with hashed
    /// sub-folders. Falls back to the public Steam CDN if allowed.
    /// </summary>
    internal static ArtworkSet FindArtwork(string steamPath, string appId, bool allowOnline)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");

        string? Find(string flatName, string nestedName)
        {
            var flat = Path.Combine(cache, $"{appId}_{flatName}");
            if (File.Exists(flat)) return flat;
            var dir = Path.Combine(cache, appId);
            if (Directory.Exists(dir))
            {
                try
                {
                    return Directory.EnumerateFiles(dir, nestedName, SearchOption.AllDirectories).FirstOrDefault();
                }
                catch { /* ignore */ }
            }
            return null;
        }

        var cover = Find("library_600x900.jpg", "library_600x900.jpg");
        var hero = Find("library_hero.jpg", "library_hero.jpg");
        var logo = Find("logo.png", "logo.png");
        var header = Find("header.jpg", "header.jpg");

        if (allowOnline)
        {
            const string cdn = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";
            cover ??= $"{cdn}/{appId}/library_600x900.jpg";
            hero ??= $"{cdn}/{appId}/library_hero.jpg";
        }

        return new ArtworkSet(Cover: cover, Hero: hero ?? header, Logo: logo, Icon: header);
    }
}
