using Lumen.App.ViewModels;
using Lumen.Core.Diagnostics;

namespace Lumen.App.Services;

/// <summary>User actions on games, shared by the home and library pages.</summary>
public static class GameActions
{
    private static string? _lastLaunchId;
    private static DateTime _lastLaunchAt;

    public static async void Launch(GameTileViewModel tile)
    {
        var shell = App.MainShell;
        if (shell is null) return;

        // Debounce double taps / repeated A presses.
        if (_lastLaunchId == tile.Id && DateTime.UtcNow - _lastLaunchAt < TimeSpan.FromSeconds(4)) return;
        _lastLaunchId = tile.Id;
        _lastLaunchAt = DateTime.UtcNow;

        try
        {
            shell.ShowToast($"Starting {tile.Title}…");
            var result = await shell.Library.LaunchAsync(tile.Game);
            if (!result.Success)
            {
                _lastLaunchId = null;
                shell.ShowToast($"Couldn't start {tile.Title}: {result.Error}");
                return;
            }

            shell.Host.Gamepad.Pulse();
            if (shell.Host.Settings.MinimizeOnLaunch) shell.SwitchToDesktop();
        }
        catch (Exception ex)
        {
            _lastLaunchId = null;
            Log.Error($"Launching {tile.Title} failed.", ex);
            shell.ShowToast($"Couldn't start {tile.Title}.");
        }
    }

    public static async void ToggleFavorite(GameTileViewModel tile)
    {
        var shell = App.MainShell;
        if (shell is null) return;
        var wasFavorite = tile.Game.IsFavorite;
        try
        {
            await shell.Library.ToggleFavoriteAsync(tile.Game);
            shell.ShowToast(wasFavorite ? $"Removed {tile.Title} from favorites" : $"Added {tile.Title} to favorites");
        }
        catch (Exception ex)
        {
            Log.Error("Toggling favorite failed.", ex);
        }
    }
}
