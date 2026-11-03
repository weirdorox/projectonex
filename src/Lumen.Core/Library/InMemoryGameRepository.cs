using System.Collections.Concurrent;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Non-persistent repository. Used as a fallback in safe mode when the database cannot be opened,
/// so the shell still starts and the user can reach the desktop and recovery options.
/// </summary>
public sealed class InMemoryGameRepository : IGameRepository
{
    private readonly ConcurrentDictionary<string, GameEntry> _games = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameEntry>>(_games.Values.ToList());

    public Task<GameEntry?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<GameEntry?>(_games.TryGetValue(id, out var g) ? g : null);

    public Task UpsertAsync(GameEntry game, CancellationToken ct = default)
    {
        _games[game.Id] = game;
        return Task.CompletedTask;
    }

    public Task UpsertManyAsync(IEnumerable<GameEntry> games, CancellationToken ct = default)
    {
        foreach (var g in games) _games[g.Id] = g;
        return Task.CompletedTask;
    }

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default) =>
        Update(id, g => g with { IsFavorite = favorite });

    public Task SetHiddenAsync(string id, bool hidden, CancellationToken ct = default) =>
        Update(id, g => g with { IsHidden = hidden });

    public Task RecordLaunchAsync(string id, DateTimeOffset when, CancellationToken ct = default) =>
        Update(id, g => g with { LastPlayed = when, PlayCount = g.PlayCount + 1 });

    public Task AddPlaytimeAsync(string id, TimeSpan duration, CancellationToken ct = default) =>
        Update(id, g => g with { PlaytimeSeconds = g.PlaytimeSeconds + (long)duration.TotalSeconds });

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _games.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    private Task Update(string id, Func<GameEntry, GameEntry> change)
    {
        if (_games.TryGetValue(id, out var g)) _games[id] = change(g);
        return Task.CompletedTask;
    }
}
