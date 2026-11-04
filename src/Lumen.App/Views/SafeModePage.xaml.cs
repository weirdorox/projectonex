using System.Diagnostics;
using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Input;
using Lumen.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App.Views;

/// <summary>Shown after repeated crashes or when the recovery tool requested it.</summary>
public sealed partial class SafeModePage : Page, IShellPage
{
    private IReadOnlyList<ButtonHint>? _hints;

    public SafeModePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ReasonText.Text = App.Host.SafeModeReason ?? "Safe mode was requested.";
            // A crash right after an update: offer the verified previous version.
            var journal = Lumen.Core.Updates.UpdateJournal.Load(Lumen.Core.Updates.UpdateJournal.JournalPath(App.Host.Paths.Root));
            if (journal.Phase is Lumen.Core.Updates.UpdatePhase.Installed or Lumen.Core.Updates.UpdatePhase.Installing && journal.PreviousInstallerPath is not null)
            {
                RollbackButton.Visibility = Visibility.Visible;
                RollbackText.Text = $"Roll back the update (back to Lumen {journal.FromVersion})";
            }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
        };
    }

    private static MainWindow Shell => App.MainShell!;

    public IReadOnlyList<ButtonHint> Hints => _hints ??= [ButtonHint.Create("A", "Select")];

    public void FocusDefault() => FocusNavigation.Focus(DesktopButton);

    public bool HandleAction(NavAction action) => false;

    private void OnDesktopClick(object sender, RoutedEventArgs e) => Shell.SwitchToDesktop();

    private async void OnDisableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Host.Startup.DisableAsync();
            RecoveryActions.RemoveAutostart();
            ResultText.Text = "Lumen will no longer start automatically. You can turn it back on in Settings.";
        }
        catch (Exception ex)
        {
            Log.Error("Disabling autostart from safe mode failed.", ex);
            ResultText.Text = $"That didn't work: {ex.Message}";
        }
    }

    private void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Lumen.Recovery.exe"), "--rollback-update --quiet") { UseShellExecute = true });
            ResultText.Text = "Rolling back - Lumen closes and the previous version starts.";
        }
        catch (Exception ex)
        {
            ResultText.Text = "Could not start the rollback: " + ex.Message;
        }
    }

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        try { using var _ = Process.Start(new ProcessStartInfo(App.Host.Paths.Logs) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Opening logs failed.", ex); }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e) => Shell.NavigateTab(0);
}
