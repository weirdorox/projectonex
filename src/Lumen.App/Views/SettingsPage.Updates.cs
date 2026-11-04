using Lumen.App.Services;
using Lumen.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Views;

/// <summary>Settings › System › Updates.</summary>
public sealed partial class SettingsPage
{
    private UpdateService? _updatesHooked;
    private UpdateStatus _lastUpdateAction = UpdateStatus.Checking;

    private void RenderUpdates()
    {
        var u = Shell.Updates;
        if (u is not null && _updatesHooked != u)
        {
            _updatesHooked = u;
            u.Changed += (_, _) => RenderUpdates();
        }
        var channel = Host.Settings.UpdateChannel;
        Pill(ChannelStableButton, channel == UpdateChannel.Stable);
        Pill(ChannelBetaButton, channel == UpdateChannel.Beta);

        if (u is null)
        {
            UpdateVersionText.Text = $"Installed: Lumen {UpdateService.CurrentVersion()}";
            UpdateStatusText.Text = "The updater starts a few seconds after Lumen.";
            foreach (var b in new[] { UpdateCheckButton, UpdateDownloadButton, UpdateInstallButton, UpdateNotesButton, UpdateRetryButton, UpdateRollbackButton }) b.IsEnabled = false;
            return;
        }

        var latest = u.Latest;
        var last = Host.Settings.LastUpdateCheck;
        UpdateVersionText.Text =
            $"Installed: Lumen {u.Current}\n" +
            $"Latest: {(latest is null ? (u.Status == UpdateStatus.UpToDate ? u.Current.ToString() : "unknown") : latest.Version + (latest.Prerelease ? " (beta)" : ""))}" +
            (latest?.PublishedAt is { } at ? $" · released {at.LocalDateTime:d}" : "") +
            (u.Manifest is { } m ? $" · {UpdateService.Size(m.Size)}" : "") + "\n" +
            $"Channel: {(Host.Settings.UpdateChannel == UpdateChannel.Beta ? "Beta (pre-releases)" : "Stable")} · Last check: {(last is { } l ? l.LocalDateTime.ToString("g") : "never")}";

        UpdateStatusText.Text = u.Message.Length > 0 ? u.Message : "Not checked yet.";
        UpdateStatusText.Foreground = (Brush)Application.Current.Resources[u.Status == UpdateStatus.Failed ? "LumenWarnBrush" : "LumenTextBrush"];
        UpdateProgress.Visibility = u.Status is UpdateStatus.Downloading or UpdateStatus.Installing ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = u.Status == UpdateStatus.Installing;
        UpdateProgress.Value = u.Progress;

        var busy = u.Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing;
        UpdateCheckButton.IsEnabled = !busy;
        UpdateDownloadButton.IsEnabled = u.Status == UpdateStatus.Available || u.Status == UpdateStatus.Downloading;
        UpdateDownloadButton.Content = u.Status == UpdateStatus.Downloading ? "Pause download" : "Download update";
        UpdateInstallButton.IsEnabled = u.Status == UpdateStatus.Ready;
        UpdateInstallButton.Content = Shell.RunningGame is null ? "Install and restart" : "Install (close the game first)";
        UpdateNotesButton.IsEnabled = latest is not null;
        UpdateRetryButton.Visibility = u.Status == UpdateStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        UpdateRetryButton.IsEnabled = true;
        UpdateRollbackButton.Visibility = u.CanRollBack ? Visibility.Visible : Visibility.Collapsed;
        UpdateRollbackButton.Content = u.PreviousVersion is { } prev ? $"Roll back to {prev}" : "Roll back";
        UpdateRollbackButton.IsEnabled = !busy;
        if (latest is not null) UpdateNotesText.Text = string.IsNullOrWhiteSpace(latest.Notes) ? "No release notes." : latest.Notes.Trim();
    }

    private static void Pill(Microsoft.UI.Xaml.Controls.Button b, bool selected)
    {
        b.Background = (Brush)Application.Current.Resources[selected ? "LumenAccentSoftBrush" : "LumenSurfaceBrush"];
        b.Foreground = (Brush)Application.Current.Resources[selected ? "LumenAccentBrush" : "LumenTextDimBrush"];
    }

    private async void OnChannelStableClick(object sender, RoutedEventArgs e) => await SetChannelAsync(UpdateChannel.Stable);

    private async void OnChannelBetaClick(object sender, RoutedEventArgs e) => await SetChannelAsync(UpdateChannel.Beta);

    private async Task SetChannelAsync(UpdateChannel channel)
    {
        Host.Settings.UpdateChannel = channel;
        RenderUpdates();
        if (Shell.Updates is { } u) await u.CheckAsync();
    }

    private async void OnUpdateCheckClick(object sender, RoutedEventArgs e)
    {
        _lastUpdateAction = UpdateStatus.Checking;
        if (Shell.Updates is { } u) await u.CheckAsync();
    }

    private async void OnUpdateDownloadClick(object sender, RoutedEventArgs e)
    {
        if (Shell.Updates is not { } u) return;
        if (u.Status == UpdateStatus.Downloading)
        {
            u.CancelDownload();
            return;
        }
        _lastUpdateAction = UpdateStatus.Downloading;
        await u.DownloadAsync();
    }

    private async void OnUpdateInstallClick(object sender, RoutedEventArgs e)
    {
        _lastUpdateAction = UpdateStatus.Installing;
        if (Shell.Updates is { } u) await u.InstallAndRestartAsync();
    }

    private void OnUpdateNotesClick(object sender, RoutedEventArgs e) =>
        UpdateNotesText.Visibility = UpdateNotesText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async void OnUpdateRetryClick(object sender, RoutedEventArgs e)
    {
        if (Shell.Updates is not { } u) return;
        switch (_lastUpdateAction)
        {
            case UpdateStatus.Downloading when u.Manifest is not null:
                await u.DownloadAsync();
                break;
            case UpdateStatus.Installing when u.Manifest is not null:
                await u.CheckAsync(); // re-verifies the downloaded file
                if (u.Status == UpdateStatus.Ready) await u.InstallAndRestartAsync();
                break;
            default:
                await u.CheckAsync();
                break;
        }
    }

    private void OnUpdateRollbackClick(object sender, RoutedEventArgs e)
    {
        if (Shell.Updates?.RollBack() is { } error) Shell.ShowToast(error);
    }
}
