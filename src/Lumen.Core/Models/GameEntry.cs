namespace Lumen.Core.Models;

/// <summary>Where a game was discovered.</summary>
public enum GameSource
{
    Manual = 0,
    Steam = 1,
    Epic = 2,
    Gog = 3,
    Xbox = 4,
    Folder = 5,
}

/// <summary>How a game is started.</summary>
public enum LaunchKind
{
    /// <summary>Protocol URI handled by a store client, e.g. steam://rungameid/123.</summary>
    Uri = 0,
    /// <summary>A Win32 executable started directly.</summary>
    Executable = 1,
    /// <summary>A packaged (MSIX/GDK) app identified by its AppUserModelID.</summary>
    PackagedApp = 2,
}

public sealed record LaunchSpec(
    LaunchKind Kind,
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null);

/// <summary>Paths or URIs to artwork. Any may be null; the UI falls back to a generated tile.</summary>
public sealed record ArtworkSet(
    string? Cover = null,   // portrait ~2:3 (e.g. Steam library_600x900)
    string? Hero = null,    // wide background
    string? Logo = null,
    string? Icon = null)
{
    public static ArtworkSet Empty { get; } = new();
}

/// <summary>
/// A game as known to the library. Discovery data (title, paths, launch) is refreshed by scans;
/// user data (favorite, hidden, play stats, custom art) is preserved across scans.
/// </summary>
public sealed record GameEntry
{
    /// <summary>Stable identifier: "{source}:{externalId}" in lower case.</summary>
    public required string Id { get; init; }
    public required GameSource Source { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public required LaunchSpec Launch { get; init; }

    public string? InstallPath { get; init; }
    public ArtworkSet Artwork { get; init; } = ArtworkSet.Empty;
    public bool IsInstalled { get; init; } = true;

    // ---- user data ----
    public bool IsFavorite { get; init; }
    public bool IsHidden { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
    public int PlayCount { get; init; }
    public long PlaytimeSeconds { get; init; }
    public string? CustomCover { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Custom art wins over discovered art.</summary>
    public string? EffectiveCover => CustomCover ?? Artwork.Cover;

    public string SortTitle => SortKey(Title);

    public static string MakeId(GameSource source, string externalId) =>
        $"{source.ToString().ToLowerInvariant()}:{externalId.Trim().ToLowerInvariant()}";

    internal static string SortKey(string title)
    {
        var t = title.Trim();
        foreach (var prefix in new[] { "The ", "A ", "An " })
        {
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && t.Length > prefix.Length)
                return t[prefix.Length..].ToUpperInvariant();
        }
        return t.ToUpperInvariant();
    }
}
