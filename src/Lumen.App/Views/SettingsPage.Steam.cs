using Lumen.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace Lumen.App.Views;

/// <summary>Settings › Steam account: sign-in, API key, owned-games list.</summary>
public sealed partial class SettingsPage
{
    private Services.SteamOnlineService Steam => Shell.Library.Steam;

    private void RenderSteam(string? message = null)
    {
        var s = Steam;
        var who = s.PersonaName ?? (s.SteamId is { } id ? $"Steam ID {id}" : null);
        SteamStatusText.Text = message ?? (!s.IsSignedIn
            ? "Not signed in."
            : !s.HasApiKey
                ? $"Signed in as {who}. Next: get your API key (step 2)."
                : $"Signed in as {who} · {s.OwnedCount} games owned" +
                  (s.OwnedFetchedAt is { } at ? $" · updated {at.LocalDateTime:g}" : ""));
        SteamKeyButton.IsEnabled = s.IsSignedIn;
        SteamRefreshButton.IsEnabled = s.IsSignedIn && s.HasApiKey;
        SteamSignOutButton.IsEnabled = s.IsSignedIn || s.HasApiKey;
        SteamOwnedToggle.IsOn = Host.Settings.SteamShowOwned;
    }

    private void OnSteamSignInClick(object sender, RoutedEventArgs e) => OpenSteamWindow(SteamSignInWindow.Mode.SignIn);

    private void OnSteamKeyClick(object sender, RoutedEventArgs e) => OpenSteamWindow(SteamSignInWindow.Mode.ApiKey);

    private void OpenSteamWindow(SteamSignInWindow.Mode mode)
    {
        var window = new SteamSignInWindow(Steam, mode, Path.Combine(Host.Paths.Root, "WebView2"));
        window.Finished += error => DispatcherQueue.TryEnqueue(async () =>
        {
            if (error is null)
            {
                RenderSteam(mode == SteamSignInWindow.Mode.SignIn ? "Signed in. Loading your library…" : "Key saved. Loading your library…");
                await Shell.Library.RescanAsync();
                RenderSteam();
            }
            else
            {
                RenderSteam(error == "Closed." ? null : error);
            }
            Shell.ReturnToShell();
        });
        window.Activate();
    }

    private async void OnSteamSaveKeyClick(object sender, RoutedEventArgs e)
    {
        var error = await Steam.SetApiKeyAsync(SteamKeyBox.Text ?? "");
        SteamKeyBox.Text = string.Empty;
        if (error is null) await Shell.Library.RescanAsync();
        RenderSteam(error);
    }

    private async void OnSteamRefreshClick(object sender, RoutedEventArgs e)
    {
        SteamRefreshButton.IsEnabled = false;
        RenderSteam("Loading your Steam library…");
        var error = await Steam.RefreshAsync(force: true);
        if (error is null) await Shell.Library.RescanAsync();
        RenderSteam(error);
    }

    private async void OnSteamSignOutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Steam.SignOut();
            await Shell.Library.RescanAsync(); // owned-only entries become "not installed" history, never deleted
        }
        catch (Exception ex)
        {
            Log.Warn("Steam sign-out failed.", ex);
        }
        RenderSteam();
    }

    private async void OnSteamOwnedToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.SteamShowOwned = SteamOwnedToggle.IsOn;
        await Shell.Library.RescanAsync();
    }
}
