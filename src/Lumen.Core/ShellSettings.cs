using System.Text.Json;
using Lumen.Core.Library;

namespace Lumen.Core;

/// <summary>Typed view over the key/value settings store.</summary>
public sealed class ShellSettings
{
    private readonly ISettingsStore _store;

    public ShellSettings(ISettingsStore store) => _store = store;

    public IReadOnlyList<string> LibraryFolders
    {
        get
        {
            var raw = _store.Get("library.folders");
            if (string.IsNullOrWhiteSpace(raw)) return [];
            try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; }
            catch (JsonException) { return []; }
        }
        set => _store.Set("library.folders", JsonSerializer.Serialize(value.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public bool AllowOnlineArtwork
    {
        get => GetBool("library.onlineArtwork", true);
        set => SetBool("library.onlineArtwork", value);
    }

    public bool PreferGogGalaxy
    {
        get => GetBool("library.gogGalaxy", false);
        set => SetBool("library.gogGalaxy", value);
    }

    /// <summary>Start fullscreen (console mode). Off = windowed.</summary>
    public bool Fullscreen
    {
        get => GetBool("shell.fullscreen", true);
        set => SetBool("shell.fullscreen", value);
    }

    /// <summary>Minimize Lumen while a game launched from it is running.</summary>
    public bool MinimizeOnLaunch
    {
        get => GetBool("shell.minimizeOnLaunch", false);
        set => SetBool("shell.minimizeOnLaunch", value);
    }

    public bool FirstRunCompleted
    {
        get => GetBool("shell.firstRunCompleted", false);
        set => SetBool("shell.firstRunCompleted", value);
    }

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(_store.Get(key), out var v) ? v : fallback;

    private void SetBool(string key, bool value) => _store.Set(key, value ? "true" : "false");
}

/// <summary>In-memory settings (tests, safe mode fallback when the database is unusable).</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string? value) => _values[key] = value;
}
