using Lumen.App.Services;
using Microsoft.UI.Xaml;

namespace Lumen.App;

/// <summary>System tab: update status and actions - without opening the dashboard.</summary>
public sealed partial class QuickAccessWindow
{
    private UpdateService? _updatesHooked;

    private void RenderUpdateStatus()
    {
        var u = _main.Updates;
        if (u is null)
        {
            QsUpdateText.Text = "Updates are checked shortly after Lumen starts.";
            QsUpdateCheckButton.IsEnabled = QsUpdateActionButton.IsEnabled = false;
            return;
        }
        if (_updatesHooked != u)
        {
            _updatesHooked = u;
            u.Changed += (_, _) => { if (IsOpen) RenderUpdateStatus(); };
        }
        QsUpdateText.Text = u.Status switch
        {
            UpdateStatus.Idle => $"Lumen {u.Current}.",
            UpdateStatus.Available => $"Update available: Lumen {u.Latest?.Version}.",
            UpdateStatus.Downloading => $"Downloading update… {u.Progress:P0}",
            UpdateStatus.Ready => _main.RunningGame is null ? $"Ready to install Lumen {u.Latest?.Version}." : "Update ready - it installs after you close the game.",
            UpdateStatus.Failed => "Update failed: " + u.Message,
            _ => u.Message,
        };
        QsUpdateProgress.Visibility = u.Status == UpdateStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
        QsUpdateProgress.Value = u.Progress;
        QsUpdateCheckButton.IsEnabled = u.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing);
        (QsUpdateActionButton.Content, QsUpdateActionButton.IsEnabled) = u.Status switch
        {
            UpdateStatus.Available => ((object)"Download", true),
            UpdateStatus.Downloading => ("Pause", true),
            UpdateStatus.Ready => ("Install & restart", _main.RunningGame is null),
            _ => ("Download", false),
        };
    }

    private async void OnQsUpdateCheckClick(object sender, RoutedEventArgs e)
    {
        if (_main.Updates is { } u) await u.CheckAsync();
    }

    private async void OnQsUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_main.Updates is not { } u) return;
        switch (u.Status)
        {
            case UpdateStatus.Available:
                await u.DownloadAsync();
                break;
            case UpdateStatus.Downloading:
                u.CancelDownload();
                break;
            case UpdateStatus.Ready:
                HideOverlay();
                await u.InstallAndRestartAsync();
                break;
        }
    }
}
