using System.Diagnostics;
using Lumen.App.Services;
using Lumen.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace Lumen.App.Views;

public sealed partial class SettingsPage
{
    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        DiagnosticsButton.IsEnabled = false;
        try
        {
            DiagnosticsText.Visibility = Visibility.Visible;
            DiagnosticsText.Text = "Checking…";
            DiagnosticsText.Text = await DiagnosticsService.BuildReportAsync(Host, Shell);
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostics failed.", ex);
            DiagnosticsText.Text = "Diagnostics failed: " + ex.Message;
        }
        finally
        {
            DiagnosticsButton.IsEnabled = true;
        }
    }

    private async void OnExportLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var zip = await DiagnosticsService.ExportAsync(Host, Shell);
            Shell.ShowToast($"Saved {Path.GetFileName(zip)} to Documents");
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Log export failed.", ex);
            Shell.ShowToast("Could not export the logs.");
        }
    }
}
