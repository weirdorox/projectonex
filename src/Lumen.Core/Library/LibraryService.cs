using Lumen.Core.Diagnostics;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

public sealed record ScanReport(
    int Total,
    int Added,
    int Updated,
    int MarkedMissing,
    IReadOnlyDictionary<GameSource, string> Failures);

/// <summary>
/// Orchestrates scanners and merges results into the repository. Rules:
/// - discovery fields (title, paths, launch, artwork) come from the latest scan;
/// - user fields (favorite, hidden, play stats, custom cover) are always preserved;
/// - games that disappear are marked not-installed, never deleted (history survives reinstall);
/// - if a scanner fails, its existing games are left untouched;
/// - manual entries are never touched by scans.
/// </summary>
public sealed class LibraryService
{
    private readonly IGameRepository _repository;
    private readonly IReadOnlyList<IGameScanner> _scanners;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public LibraryService(IGameRepository repository, IEnumerable<IGameScanner> scanners)
    {
        _repository = repository;
        _scanners = scanners.ToList();
    }

    public event EventHandler? LibraryChanged;

    public IReadOnlyList<IGameScanner> Scanners => _scanners;

    public async Task<ScanReport> ScanAsync(CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = (await _repository.GetAllAsync(ct).ConfigureAwait(false))
                .ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

            var failures = new Dictionary<GameSource, string>();
            var discovered = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            var succeededSources = new HashSet<GameSource>();

            var tasks = _scanners.Select(async s =>
            {
                try
                {
                    var games = await s.ScanAsync(ct).ConfigureAwait(false);
                    return (Scanner: s, Games: games, Error: (string?)null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"Scanner {s.DisplayName} failed", ex);
                    return (Scanner: s, Games: (IReadOnlyList<GameEntry>)[], Error: ex.Message);
                }
            }).ToList();

            foreach (var (scanner, games, error) in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                if (error is not null)
                {
                    failures[scanner.Source] = error;
                    continue;
                }
                succeededSources.Add(scanner.Source);
                foreach (var g in games) discovered.TryAdd(g.Id, g);
            }

            int added = 0, updated = 0, missing = 0;
            var toWrite = new List<GameEntry>();

            foreach (var found in discovered.Values)
            {
                if (existing.TryGetValue(found.Id, out var old))
                {
                    var merged = Merge(old, found);
                    if (merged != old) { toWrite.Add(merged); updated++; }
                }
                else
                {
                    toWrite.Add(found with { AddedAt = DateTimeOffset.UtcNow });
                    added++;
                }
            }

            foreach (var old in existing.Values)
            {
                if (old.Source == GameSource.Manual) continue;
                if (!succeededSources.Contains(old.Source)) continue;
                if (discovered.ContainsKey(old.Id)) continue;
                if (!old.IsInstalled) continue;
                toWrite.Add(old with { IsInstalled = false });
                missing++;
            }

            if (toWrite.Count > 0)
                await _repository.UpsertManyAsync(toWrite, ct).ConfigureAwait(false);

            var report = new ScanReport(discovered.Count, added, updated, missing, failures);
            Log.Info($"Library scan: {report.Total} found, {added} added, {updated} updated, {missing} missing, {failures.Count} failed sources.");
            if (toWrite.Count > 0) LibraryChanged?.Invoke(this, EventArgs.Empty);
            return report;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Discovery data from <paramref name="found"/>, user data from <paramref name="old"/>.</summary>
    public static GameEntry Merge(GameEntry old, GameEntry found)
    {
        // Prefer the newer "last played" (store clients may know about play sessions outside Lumen).
        var lastPlayed = (old.LastPlayed, found.LastPlayed) switch
        {
            (null, var f) => f,
            (var o, null) => o,
            var (o, f) => o > f ? o : f,
        };

        return old with
        {
            Title = found.Title,
            Launch = found.Launch,
            InstallPath = found.InstallPath,
            Artwork = found.Artwork,
            IsInstalled = found.IsInstalled, // owned-but-not-installed entries report false
            LastPlayed = lastPlayed,
        };
    }

    public static IReadOnlyList<GameEntry> RecentlyPlayed(IEnumerable<GameEntry> games, int count = 12) =>
        games.Where(g => g.IsInstalled && !g.IsHidden && g.LastPlayed is not null)
             .OrderByDescending(g => g.LastPlayed)
             .Take(count)
             .ToList();

    public static IReadOnlyList<GameEntry> Favorites(IEnumerable<GameEntry> games) =>
        games.Where(g => g.IsInstalled && !g.IsHidden && g.IsFavorite)
             .OrderBy(g => g.SortTitle, StringComparer.Ordinal)
             .ToList();

    public static IReadOnlyList<GameEntry> AllVisible(IEnumerable<GameEntry> games) =>
        games.Where(g => g.IsInstalled && !g.IsHidden)
             .OrderBy(g => g.SortTitle, StringComparer.Ordinal)
             .ToList();
}
