using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>A source of installed games (Steam, Epic, ...). Scanners must not launch anything.</summary>
public interface IGameScanner
{
    GameSource Source { get; }
    string DisplayName { get; }

    /// <summary>
    /// Returns the games currently installed from this source.
    /// Throw only for unexpected failures; "store not installed" is an empty result.
    /// </summary>
    Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken);
}

public enum RegistryRoot { LocalMachine, CurrentUser }

/// <summary>Read-only registry access, abstracted so scanners are testable off Windows.</summary>
public interface IRegistryReader
{
    /// <summary>Reads a string (REG_SZ / REG_EXPAND_SZ) or integer value as a string. Null if missing.</summary>
    string? GetValue(RegistryRoot root, string keyPath, string valueName);

    IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath);
}

/// <summary>Registry reader that has nothing in it (non-Windows, tests).</summary>
public sealed class EmptyRegistryReader : IRegistryReader
{
    public static EmptyRegistryReader Instance { get; } = new();
    public string? GetValue(RegistryRoot root, string keyPath, string valueName) => null;
    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath) => [];
}

/// <summary>Persistent storage for the library.</summary>
public interface IGameRepository
{
    Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default);
    Task<GameEntry?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(GameEntry game, CancellationToken ct = default);
    Task UpsertManyAsync(IEnumerable<GameEntry> games, CancellationToken ct = default);
    Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default);
    Task SetHiddenAsync(string id, bool hidden, CancellationToken ct = default);
    Task RecordLaunchAsync(string id, DateTimeOffset when, CancellationToken ct = default);
    Task AddPlaytimeAsync(string id, TimeSpan duration, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Simple key/value settings store.</summary>
public interface ISettingsStore
{
    string? Get(string key);
    void Set(string key, string? value);
}

public sealed record LaunchResult(bool Success, string? Error = null, int? ProcessId = null)
{
    public static LaunchResult Ok(int? pid = null) => new(true, null, pid);
    public static LaunchResult Fail(string error) => new(false, error);
}

public interface IGameLauncher
{
    /// <summary>Starts the game. For executables, returns the process id so play time can be tracked.</summary>
    LaunchResult Launch(LaunchSpec spec);
}
