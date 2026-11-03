namespace Lumen.Core.Library;

/// <summary>A user-made collection ("Couch co-op", "Short sessions"...). Favorites remain a built-in flag.</summary>
public sealed record GameCollection(string Id, string Name, int SortOrder, DateTimeOffset CreatedAt)
{
    public static GameCollection Create(string name, int sortOrder = 0) =>
        new(Guid.NewGuid().ToString("N"), Clean(name), sortOrder, DateTimeOffset.UtcNow);

    public static string Clean(string name)
    {
        var trimmed = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (trimmed.Length == 0) throw new ArgumentException("A collection needs a name.", nameof(name));
        return trimmed.Length > 40 ? trimmed[..40] : trimmed;
    }
}

public interface ICollectionRepository
{
    Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(GameCollection collection, CancellationToken ct = default);
    Task DeleteAsync(string collectionId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGameIdsAsync(string collectionId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCollectionIdsForGameAsync(string gameId, CancellationToken ct = default);
    Task SetMembershipAsync(string collectionId, string gameId, bool member, CancellationToken ct = default);
}

public sealed class InMemoryCollectionRepository : ICollectionRepository
{
    private readonly Dictionary<string, GameCollection> _collections = new();
    private readonly HashSet<(string Collection, string Game)> _members = new();
    private readonly object _gate = new();

    public Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<GameCollection>>(_collections.Values.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList());
    }

    public Task SaveAsync(GameCollection collection, CancellationToken ct = default)
    {
        lock (_gate) _collections[collection.Id] = collection;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string collectionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _collections.Remove(collectionId);
            _members.RemoveWhere(m => m.Collection == collectionId);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetGameIdsAsync(string collectionId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_members.Where(m => m.Collection == collectionId).Select(m => m.Game).ToList());
    }

    public Task<IReadOnlyList<string>> GetCollectionIdsForGameAsync(string gameId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_members.Where(m => m.Game == gameId).Select(m => m.Collection).ToList());
    }

    public Task SetMembershipAsync(string collectionId, string gameId, bool member, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (member) _members.Add((collectionId, gameId));
            else _members.Remove((collectionId, gameId));
        }
        return Task.CompletedTask;
    }
}
