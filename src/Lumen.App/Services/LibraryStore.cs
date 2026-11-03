using Lumen.App.ViewModels;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Microsoft.UI.Dispatching;

namespace Lumen.App.Services;

/// <summary>
/// UI-thread view of the library: loads games from the repository, runs scans in the background,
/// caches tile view models and performs user actions (launch, favorite).
/// </summary>
public sealed class LibraryStore
{
    private readonly AppHost _host;
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<string, GameTileViewModel> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameEntry> _games = [];
    private bool _scanning;

    public LibraryStore(AppHost host, DispatcherQueue ui)
    {
        _host = host;
        _ui = ui;
        Steam = new SteamOnlineService(host);
    }

    /// <summary>Raised on the UI thread whenever the game list changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<GameEntry> Games => _games;
    public bool IsScanning => _scanning;

    public GameTileViewModel Tile(GameEntry game)
    {
        if (_tiles.TryGetValue(game.Id, out var vm))
        {
            vm.Update(game);
            return vm;
        }
        vm = new GameTileViewModel(game);
        _tiles[game.Id] = vm;
        return vm;
    }

    public IReadOnlyList<GameTileViewModel> Tiles(IEnumerable<GameEntry> games) => games.Select(Tile).ToList();

    public async Task LoadAsync()
    {
        try
        {
            var games = await _host.Games.GetAllAsync();
            _ui.TryEnqueue(() =>
            {
                _games = games;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Loading the library failed.", ex);
        }
    }

    /// <summary>Scans all sources in the background, then reloads.</summary>
    public SteamOnlineService Steam { get; }

    public async Task<ScanReport?> RescanAsync()
    {
        if (_scanning) return null;
        _scanning = true;
        try
        {
            if (!_host.IsSafeMode) await Steam.RefreshAsync(); // owned list, at most every 6 h
            var report = await Task.Run(() => _host.Library.ScanAsync());
            await LoadAsync();

            // Store artwork for games whose cover isn't in Steam's local cache (e.g. new releases
            // with hashed asset paths): look it up once, then rescan so tiles pick it up.
            if (!_host.IsSafeMode && await Steam.ResolveArtworkAsync(_host.Steam.MissingArtwork))
            {
                await Task.Run(() => _host.Library.ScanAsync());
                await LoadAsync();
            }
            return report;
        }
        catch (Exception ex)
        {
            Log.Error("Library scan failed.", ex);
            return null;
        }
        finally
        {
            _scanning = false;
        }
    }

    public async Task ToggleFavoriteAsync(GameEntry game)
    {
        await _host.Games.SetFavoriteAsync(game.Id, !game.IsFavorite);
        await LoadAsync();
    }

    public async Task SetHiddenAsync(GameEntry game, bool hidden)
    {
        await _host.Games.SetHiddenAsync(game.Id, hidden);
        await LoadAsync();
    }

    public async Task<LaunchResult> LaunchAsync(GameEntry game)
    {
        if (!game.IsInstalled)
        {
            // Owned Steam game: hand over to Steam's own installer.
            if (game.Source == GameSource.Steam && game.Launch.Target.StartsWith("steam://install/", StringComparison.Ordinal))
                return await Task.Run(() => _host.Launcher.Launch(game.Launch));
            return LaunchResult.Fail($"{game.Title} is not installed.");
        }

        var result = await Task.Run(() => _host.Launcher.Launch(game.Launch));
        if (!result.Success) return result;

        await _host.Games.RecordLaunchAsync(game.Id, DateTimeOffset.UtcNow);
        await LoadAsync();
        return result;
    }
}
