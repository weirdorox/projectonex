using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relative, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relative, int size)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
    }
}

public sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(RegistryRoot root, string key, string name, string value)
    {
        var k = $"{root}\\{key}";
        if (!_keys.TryGetValue(k, out var values))
            _keys[k] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values[name] = value;
        return this;
    }

    public string? GetValue(RegistryRoot root, string keyPath, string valueName) =>
        _keys.TryGetValue($"{root}\\{keyPath}", out var v) && v.TryGetValue(valueName, out var s) ? s : null;

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath)
    {
        var prefix = $"{root}\\{keyPath}\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class FakeGameRepository : IGameRepository
{
    public Dictionary<string, GameEntry> Games { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameEntry>>(Games.Values.ToList());

    public Task<GameEntry?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<GameEntry?>(Games.TryGetValue(id, out var g) ? g : null);

    public Task UpsertAsync(GameEntry game, CancellationToken ct = default)
    {
        Games[game.Id] = game;
        return Task.CompletedTask;
    }

    public Task UpsertManyAsync(IEnumerable<GameEntry> games, CancellationToken ct = default)
    {
        foreach (var g in games) Games[g.Id] = g;
        return Task.CompletedTask;
    }

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
    {
        Games[id] = Games[id] with { IsFavorite = favorite };
        return Task.CompletedTask;
    }

    public Task SetHiddenAsync(string id, bool hidden, CancellationToken ct = default)
    {
        Games[id] = Games[id] with { IsHidden = hidden };
        return Task.CompletedTask;
    }

    public Task RecordLaunchAsync(string id, DateTimeOffset when, CancellationToken ct = default)
    {
        Games[id] = Games[id] with { LastPlayed = when, PlayCount = Games[id].PlayCount + 1 };
        return Task.CompletedTask;
    }

    public Task AddPlaytimeAsync(string id, TimeSpan duration, CancellationToken ct = default)
    {
        Games[id] = Games[id] with { PlaytimeSeconds = Games[id].PlaytimeSeconds + (long)duration.TotalSeconds };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        Games.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed class StubScanner(GameSource source, Func<IReadOnlyList<GameEntry>> scan) : IGameScanner
{
    public GameSource Source => source;
    public string DisplayName => source.ToString();
    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(scan());
}

public static class TestGames
{
    public static GameEntry Make(GameSource source, string id, string title = "Game") => new()
    {
        Id = GameEntry.MakeId(source, id),
        Source = source,
        ExternalId = id,
        Title = title,
        Launch = new LaunchSpec(LaunchKind.Uri, $"test://{id}"),
    };
}
