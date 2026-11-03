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

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        try { using var _ = Process.Start(new ProcessStartInfo(App.Host.Paths.Logs) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Opening logs failed.", ex); }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e) => Shell.NavigateTab(0);
}
