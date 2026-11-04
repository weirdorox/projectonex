using System.Diagnostics;
using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Input;
using Lumen.Platform.Windows.Power;
using Lumen.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Views;

/// <summary>Console-mode setup (explicit and reversible), library sources, device capabilities, recovery.</summary>
public sealed partial class SettingsPage : Page, IShellPage
{
    private bool _loading;
    private bool _restoreArmed;
    private IReadOnlyList<ButtonHint>? _hints;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private static MainWindow Shell => App.MainShell!;
    private static AppHost Host => App.Host;

    public IReadOnlyList<ButtonHint> Hints => _hints ??=
    [
        ButtonHint.Create("A", "Select"),
        ButtonHint.Create("B", "Home"),
        ButtonHint.Create("R", "Scroll"),
    ];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            // Visiting settings counts as having seen the setup prompt.
            Host.Settings.FirstRunCompleted = true;

            FullscreenToggle.IsOn = Host.Settings.Fullscreen;
            MinimizeToggle.IsOn = Host.Settings.MinimizeOnLaunch;
            DockFollowToggle.IsOn = Host.Settings.DockFollowExternal;
            RenderPersonalization();
            RenderSteam();
            RenderButtons();
            RenderUpdates();
            _ = RenderDisplayAsync();
            OnlineArtToggle.IsOn = Host.Settings.AllowOnlineArtwork;
            GalaxyToggle.IsOn = Host.Settings.PreferGogGalaxy;
            RenderFolders();
            RenderAutoSignIn();
            AboutText.Text = $"Lumen {typeof(App).Assembly.GetName().Version} · data in {Host.Paths.Root} · {(PackageInfo.IsPackaged ? "MSIX" : "unpackaged")}";

            var state = await Host.Startup.GetStateAsync();
            StartupToggle.IsOn = state == StartupState.Enabled;
            StartupStatusText.Text = DescribeStartup(state);
        }
        catch (Exception ex)
        {
            Log.Error("Settings load failed.", ex);
        }
        finally
        {
            _loading = false;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
        await RenderTdpSetupAsync();
        await RenderDeviceAsync();
    }

    public void FocusDefault() => FocusNavigation.Focus(StartupToggle);

    public bool HandleAction(NavAction action)
    {
        switch (action)
        {
            case NavAction.ScrollDown:
            case NavAction.ScrollUp:
                FocusNavigation.Scroll(Scroller, action == NavAction.ScrollDown);
                return true;
            default:
                return false;
        }
    }

    // ================= Console mode =================

    private static string DescribeStartup(StartupState state) => state switch
    {
        StartupState.Enabled => $"Lumen starts automatically after sign-in ({Host.Startup.Mechanism}).",
        StartupState.DisabledByUser => "Turned off in Task Manager / Settings > Apps > Startup. Turn it back on there.",
        StartupState.DisabledByPolicy => "Blocked by a system policy.",
        _ => "Lumen does not start automatically. Windows starts normally to the desktop.",
    };

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            StartupState state;
            if (StartupToggle.IsOn)
            {
                state = await Host.Startup.EnableAsync();
            }
            else
            {
                await Host.Startup.DisableAsync();
                state = await Host.Startup.GetStateAsync();
            }
            StartupStatusText.Text = DescribeStartup(state);
            _loading = true;
            StartupToggle.IsOn = state == StartupState.Enabled;
            _loading = false;
        }
        catch (Exception ex)
        {
            Log.Error("Changing autostart failed.", ex);
            StartupStatusText.Text = $"Could not change the startup setting: {ex.Message}";
        }
    }

    private void OnFullscreenToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.Fullscreen = FullscreenToggle.IsOn;
        Shell.SetFullscreen(FullscreenToggle.IsOn);
    }

    private void OnDockFollowToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.DockFollowExternal = DockFollowToggle.IsOn;
    }

    private void OnMinimizeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.MinimizeOnLaunch = MinimizeToggle.IsOn;
    }

    private void RenderAutoSignIn()
    {
        var s = AutoSignIn.Read();
        AutoSignInStatusText.Text = s.Enabled
            ? $"Automatic sign-in is ON for {(string.IsNullOrEmpty(s.Domain) ? "" : s.Domain + "\\")}{s.UserName}."
            : "Automatic sign-in is off. Windows asks for your PIN/password at startup (Lumen still starts after you sign in).";
        if (s.PlaintextPasswordPresent)
            AutoSignInStatusText.Text += " Warning: a plaintext password is stored in the registry - use Autologon or turn auto sign-in off to remove it.";
        HelloOnlyText.Visibility = s.WindowsHelloOnlyEnabled ? Visibility.Visible : Visibility.Collapsed;
        DisableAutoSignInButton.Visibility = s.Enabled || s.PlaintextPasswordPresent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutologonClick(object sender, RoutedEventArgs e) => AutoSignIn.OpenAutologonDownload();
    private void OnNetplwizClick(object sender, RoutedEventArgs e) => AutoSignIn.OpenUserAccountsDialog();
    private void OnSignInOptionsClick(object sender, RoutedEventArgs e) => AutoSignIn.OpenSignInOptions();

    private void OnDisableAutoSignInClick(object sender, RoutedEventArgs e)
    {
        // Needs administrator rights: run the recovery tool elevated for just this step.
        if (!RunRecoveryTool("--disable-autologon", elevated: true))
            TroubleshootText.Text = "Could not start Lumen Recovery. Use Sysinternals Autologon → Disable, or netplwiz.";
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RenderAutoSignIn);
    }

    // ================= Performance (real TDP) =================

    private async Task RenderTdpSetupAsync()
    {
        var shipped = PowerHelperSetup.IsHelperShipped;
        var pawnIo = PawnIo.FindLibrary() is not null;
        var module = PawnIo.ModuleCandidates("IntelMSR.bin", Host.Paths.Root).Any(File.Exists);
        var installed = shipped && await Task.Run(PowerHelperSetup.IsInstalled);

        var lines = new List<string>
        {
            $"PawnIO driver: {(pawnIo ? "installed" : "not installed")}",
            $"IntelMSR module: {(module ? "found" : "missing")}",
            $"Lumen power helper: {(!shipped ? "not part of this build" : installed ? "enabled" : "not enabled")}",
        };
        if (installed)
        {
            var status = await Shell.Tdp.RefreshAsync();
            lines.Add(status.Available
                ? $"TDP control works: {status.Cpu}, {status.MinWatts:0.#}–{status.MaxWatts:0.#} W{(status.Locked ? " (locked by firmware)" : "")}."
                : $"Not working yet: {status.UnavailableReason}");
        }
        TdpSetupStatusText.Text = string.Join("\n", lines);
        TdpEnableButton.Visibility = shipped && !installed ? Visibility.Visible : Visibility.Collapsed;
        TdpDisableButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGetPawnIoClick(object sender, RoutedEventArgs e)
    {
        try { using var _ = Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Opening pawnio.eu failed.", ex); }
    }

    private void OnModuleFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Host.Paths.Root, "pawnio");
            Directory.CreateDirectory(dir);
            using var _ = Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Opening the module folder failed.", ex); }
    }

    private async void OnTdpEnableClick(object sender, RoutedEventArgs e)
    {
        TdpSetupStatusText.Text = "Waiting for the administrator prompt…";
        var error = await Task.Run(() => PowerHelperSetup.Install(Host.Paths.Root));
        if (error is not null) TroubleshootText.Text = error;
        await RenderTdpSetupAsync();
    }

    private async void OnTdpDisableClick(object sender, RoutedEventArgs e)
    {
        if (Shell.Tdp.Status.RequestedWatts is not null) await Shell.Tdp.RestoreAsync();
        var error = await Task.Run(PowerHelperSetup.Uninstall);
        if (error is not null) TroubleshootText.Text = error;
        await RenderTdpSetupAsync();
    }

    // ================= Library =================

    private void RenderFolders()
    {
        FoldersPanel.Children.Clear();
        var folders = Host.Settings.LibraryFolders;
        if (folders.Count == 0)
        {
            FoldersPanel.Children.Add(new TextBlock
            {
                Text = "No folders added.",
                Style = (Style)Application.Current.Resources["LumenCaptionText"],
            });
            return;
        }

        foreach (var folder in folders)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = folder,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["LumenTextBrush"],
            });
            var remove = new Button { Content = "Remove" };
            var captured = folder;
            remove.Click += (_, _) =>
            {
                Host.Settings.LibraryFolders = Host.Settings.LibraryFolders
                    .Where(f => !string.Equals(f, captured, StringComparison.OrdinalIgnoreCase)).ToList();
                RenderFolders();
                FocusNavigation.Focus(ScanButton);
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            FoldersPanel.Children.Add(row);
        }
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Shell.Hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            Host.Settings.LibraryFolders = Host.Settings.LibraryFolders.Append(folder.Path).ToList();
            RenderFolders();
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Folder picker failed.", ex);
            ScanResultText.Text = "The folder picker could not be opened.";
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        ScanResultText.Text = "Scanning…";
        var report = await Shell.Library.RescanAsync();
        if (report is null)
        {
            ScanResultText.Text = "A scan is already running.";
            return;
        }
        var failed = report.Failures.Count == 0 ? "" : $" Failed: {string.Join(", ", report.Failures.Keys)} (see log).";
        ScanResultText.Text = $"{report.Total} games found · {report.Added} new · {report.MarkedMissing} no longer installed.{failed}";
    }

    private void OnOnlineArtToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) Host.Settings.AllowOnlineArtwork = OnlineArtToggle.IsOn;
    }

    private void OnGalaxyToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) Host.Settings.PreferGogGalaxy = GalaxyToggle.IsOn;
    }

    // ================= Device =================

    private async Task RenderDeviceAsync()
    {
        try
        {
            ProfileText.Text = "Detecting hardware…";
            var hw = await Host.Hardware.Value;
            DeviceText.Text = string.IsNullOrWhiteSpace(hw.Identity.Manufacturer)
                ? "Unknown device"
                : $"{hw.Identity.Manufacturer} {hw.Identity.ProductName}\nBoard: {hw.Identity.BaseboardProduct} · Family: {hw.Identity.SystemFamily} · BIOS: {hw.Identity.BiosVersion}";

            var traits = hw.Profile.Traits;
            var traitText = traits.NativeWidth is { } w ? $" · panel {w}×{traits.NativeHeight} @ {traits.MaxRefreshHz} Hz" : "";
            ProfileText.Text = $"Profile: {hw.Profile.DisplayName}{traitText}";

            FeaturesPanel.Children.Clear();
            foreach (var f in Enum.GetValues<HardwareFeature>())
            {
                var supported = hw.Supports(f);
                FeaturesPanel.Children.Add(new TextBlock
                {
                    Text = supported ? $"✓  {f}" : $"–  {f}: {hw.Profile.ExplainUnsupported(f)}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources[supported ? "LumenGoodBrush" : "LumenTextDimBrush"],
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Device info failed.", ex);
            ProfileText.Text = "Hardware information is unavailable.";
        }
    }

    // ================= Troubleshooting =================

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Host.Paths.Logs) { UseShellExecute = true });
            Shell.SwitchToDesktop();
        }
        catch (Exception ex)
        {
            Log.Error("Opening logs failed.", ex);
        }
    }

    /// <summary>Two-step (press twice) so an accidental "A" never changes startup behaviour.</summary>
    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!_restoreArmed)
        {
            _restoreArmed = true;
            RestoreButton.Content = "Press again to confirm";
            TroubleshootText.Text = "This turns off 'Start Lumen when I sign in' and fullscreen mode. Automatic sign-in is left as is (use the button above to turn it off).";
            return;
        }

        _restoreArmed = false;
        RestoreButton.Content = "Restore normal Windows startup";
        try
        {
            await Host.Startup.DisableAsync();
            RecoveryActions.RemoveAutostart();
            Host.Settings.Fullscreen = false;
            Shell.SetFullscreen(false);
            _loading = true;
            StartupToggle.IsOn = false;
            FullscreenToggle.IsOn = false;
            _loading = false;
            StartupStatusText.Text = DescribeStartup(await Host.Startup.GetStateAsync());
            TroubleshootText.Text = "Done. Windows will start normally to the desktop. You can still open Lumen from the Start menu.";
            Log.Info("User restored normal Windows startup from settings.");
        }
        catch (Exception ex)
        {
            Log.Error("Restore failed.", ex);
            TroubleshootText.Text = $"Restore failed: {ex.Message}. Run Lumen Recovery from the Start menu.";
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Shell.ExitShell();

    private static bool RunRecoveryTool(string args, bool elevated)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Lumen.Recovery.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Recovery tool did not start (UAC declined?).", ex);
            return false;
        }
    }
}
