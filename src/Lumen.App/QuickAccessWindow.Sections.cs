using System.Diagnostics;
using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Lumen.Platform.Windows.Connectivity;
using Lumen.Platform.Windows.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App;

/// <summary>
/// Quick-access sections: Quick (hardware), Game (whatever is in front, from any store) and System
/// (radios, Lumen options, Windows settings that open over Lumen instead of via the desktop).
/// </summary>
public sealed partial class QuickAccessWindow
{
    private const int SectionCount = 5;
    internal const int GameSection = 3;
    private int _section;
    private ProcessInfo? _target;
    private GameEntry? _targetGame;
    private RadioControl? _radios;
    private bool _radiosProbed;

    /// <summary>The game the panel acts on, when one was recognised.</summary>
    internal GameEntry? TargetGame => _targetGame;
    internal ProcessInfo? TargetProcess => _target;

    /// <summary>
    /// Remembers what was in the foreground *before* the panel opened - that is the game the user
    /// wants to act on. Falls back to a game Lumen knows is still running (Feature 3 watcher).
    /// </summary>
    private void CaptureTarget()
    {
        var fg = ForegroundProcess.Current();
        if (fg is not null && fg.ProcessId != Environment.ProcessId)
        {
            _target = fg;
            _targetGame = GameMatcher.Match(fg.Path, _main.Library.Games);
        }
        else if (_main.RunningGame is { } running)
        {
            _target = running.Process;
            _targetGame = running.Game;
        }
        else
        {
            _target = null;
            _targetGame = null;
        }
    }

    private void SetSection(int index, bool focus = true)
    {
        _section = (index % SectionCount + SectionCount) % SectionCount;
        StackPanel[] panels = [QuickPanel, PowerPanel, BatteryPanel, GamePanel, SystemPanel];
        for (var i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == _section ? Visibility.Visible : Visibility.Collapsed;
        if (_section == 1) _ = RenderPowerSectionAsync();
        if (_section == 2) RenderBatterySection();

        var accent = (Brush)Application.Current.Resources["LumenAccentBrush"];
        var soft = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
        var dim = (Brush)Application.Current.Resources["LumenTextDimBrush"];
        var surface = (Brush)Application.Current.Resources["LumenSurfaceHighBrush"];
        Button[] tabs = [QuickTab, PowerTab, BatteryTab, GameTab, SystemTab];
        for (var i = 0; i < tabs.Length; i++)
        {
            tabs[i].Foreground = i == _section ? accent : dim;
            tabs[i].Background = i == _section ? soft : surface;
        }

        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        if (!focus) return;
        var panel = panels[_section];
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (FocusManager.FindFirstFocusableElement(panel) is { } first) FocusNavigation.Focus(first);
            else FocusNavigation.Focus(tabs[_section]);
        });
    }

    private void OnSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var i)) SetSection(i);
    }

    // ---------------- Game ----------------

    private void RenderGameSection()
    {
        ForceCloseButton.Visibility = Visibility.Collapsed;
        GameStatusText.Text = string.Empty;

        if (_targetGame is { } game)
        {
            var tile = _main.Library.Tile(game);
            GameTitle.Text = game.Title;
            GameSubtitle.Text = $"{tile.SourceLabel} · running";
            GameArt.Source = GameTileViewModel.CreateBitmap(ArtworkService.Instance?.Cache.TryGetLocal(game.EffectiveCover), 200);
            CloseGameButton.Visibility = _target is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            GameTitle.Text = _target is null ? "No game in front" : _target.Name;
            GameSubtitle.Text = _target is null
                ? "Open quick access while a game is running to manage it here."
                : "Not a game in your Lumen library.";
            GameArt.Source = null;
            CloseGameButton.Visibility = Visibility.Collapsed;
        }
        RenderProfile();
    }

    /// <summary>Filled in by the per-game profile feature.</summary>
    partial void RenderProfile();

    private async void OnCloseGameClick(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;
        CloseGameButton.IsEnabled = false;
        GameStatusText.Text = "Asking the game to close…";
        try
        {
            var result = await ProcessCloser.RequestCloseAsync(_target.ProcessId, TimeSpan.FromSeconds(6));
            switch (result)
            {
                case CloseResult.Closed:
                case CloseResult.NotRunning:
                    GameStatusText.Text = "Closed.";
                    _main.ReturnToShell();
                    break;
                case CloseResult.StillRunning:
                    GameStatusText.Text = "The game is still running (it may be showing a save prompt). Use Force close only if it is stuck.";
                    ForceCloseButton.Visibility = Visibility.Visible;
                    FocusNavigation.Focus(ForceCloseButton);
                    break;
                default:
                    GameStatusText.Text = "Windows did not allow Lumen to close this game (it may run as administrator).";
                    break;
            }
        }
        finally
        {
            CloseGameButton.IsEnabled = true;
        }
    }

    private void OnForceCloseClick(object sender, RoutedEventArgs e)
    {
        if (_target is null) return;
        var result = ProcessCloser.ForceClose(_target.ProcessId);
        GameStatusText.Text = result is CloseResult.Closed or CloseResult.NotRunning
            ? "Force closed."
            : "Windows did not allow Lumen to close this game.";
        if (result is CloseResult.Closed or CloseResult.NotRunning) _main.ReturnToShell();
    }

    // ---------------- System ----------------

    private async Task RenderSystemSectionAsync()
    {
        _updating = true;
        OverlayFullscreenToggle.IsOn = App.Host.Settings.Fullscreen;
        OverlayMinimizeToggle.IsOn = App.Host.Settings.MinimizeOnLaunch;
        MouseModeToggle.IsOn = _main.MouseMode.Enabled;
        RenderUpdateStatus();
        MouseModeToggle.IsEnabled = _main.RunningGame is null;
        _updating = false;

        if (!_radiosProbed)
        {
            _radiosProbed = true;
            _radios = await RadioControl.TryCreateAsync();
        }

        _updating = true;
        try
        {
            WifiToggle.Visibility = _radios?.Has(RadioType.WiFi) == true ? Visibility.Visible : Visibility.Collapsed;
            BluetoothToggle.Visibility = _radios?.Has(RadioType.Bluetooth) == true ? Visibility.Visible : Visibility.Collapsed;
            WifiToggle.IsOn = _radios?.IsOn(RadioType.WiFi) == true;
            BluetoothToggle.IsOn = _radios?.IsOn(RadioType.Bluetooth) == true;
            RadiosUnavailableText.Visibility = _radios is null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updating = false;
        }
    }

    private async void OnWifiToggled(object sender, RoutedEventArgs e) => await SetRadioAsync(RadioType.WiFi, WifiToggle);
    private async void OnBluetoothToggled(object sender, RoutedEventArgs e) => await SetRadioAsync(RadioType.Bluetooth, BluetoothToggle);

    private async Task SetRadioAsync(RadioType type, ToggleSwitch toggle)
    {
        if (_updating || _radios is null) return;
        var ok = await _radios.SetAsync(type, toggle.IsOn);
        if (ok) return;
        _updating = true;
        toggle.IsOn = _radios.IsOn(type) == true; // revert to the real state
        _updating = false;
        _main.ShowToast($"Windows did not allow changing {type}.");
    }

    private void OnOverlayFullscreenToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        App.Host.Settings.Fullscreen = OverlayFullscreenToggle.IsOn;
        _main.SetFullscreen(OverlayFullscreenToggle.IsOn);
    }

    private void OnMouseModeToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _main.MouseMode.SetEnabled(MouseModeToggle.IsOn, "Quick Access");
    }

    private void OnOverlayMinimizeToggled(object sender, RoutedEventArgs e)
    {
        if (!_updating) App.Host.Settings.MinimizeOnLaunch = OverlayMinimizeToggle.IsOn;
    }

    private void OnLumenSettingsClick(object sender, RoutedEventArgs e)
    {
        _main.ReturnToShell();
        _main.NavigateTab(MainWindow.SettingsTabIndex);
    }

    private void OnWindowsSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string uri }) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            HideOverlay(); // Settings opens on top of Lumen/the game; the desktop is never shown
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening {uri} failed.", ex);
        }
    }
}
