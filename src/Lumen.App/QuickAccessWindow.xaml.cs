using System.Runtime.InteropServices;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Input;
using Lumen.Core.Power;
using Lumen.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Lumen.App;

/// <summary>
/// The quick-access side panel: a separate, always-on-top, borderless window docked to the right
/// edge so it can appear over the shell, the desktop and borderless/windowed games.
///
/// Limitations (by design of Windows, documented in docs/HARDWARE.md):
/// - It cannot draw over games in *exclusive* fullscreen; those games are minimised by Windows when
///   the panel takes focus, or the panel appears behind them.
/// - Games keep receiving controller input while the panel is open (XInput is not focus-exclusive).
/// </summary>
public sealed partial class QuickAccessWindow : Window
{
    private const int PanelWidthEpx = 420;

    private readonly MainWindow _main;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _statsTimer;
    private readonly DispatcherQueueTimer _brightnessDebounce;
    private HardwareServices? _hw;
    private bool _updating;
    private bool _closeForGood;
    private bool _sampling;

    public QuickAccessWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        AppWindow.Closing += (_, e) =>
        {
            if (_closeForGood) return;
            e.Cancel = true;
            HideOverlay();
        };
        Activated += (_, e) =>
        {
            // Tapping outside (into a game or the desktop) dismisses the panel.
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsOpen) HideOverlay();
        };

        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.IsRepeating = true;
        _statsTimer.Tick += (_, _) => _ = SampleAsync();

        _brightnessDebounce = DispatcherQueue.CreateTimer();
        _brightnessDebounce.IsRepeating = false;
        _brightnessDebounce.Interval = TimeSpan.FromMilliseconds(150);
        _brightnessDebounce.Tick += (_, _) =>
        {
            var value = (int)BrightnessSlider.Value;
            _ = Task.Run(() => _hw?.Brightness?.SetBrightness(value));
        };
    }

    public IntPtr Hwnd => _hwnd;
    public bool IsOpen { get; private set; }

    public async void ShowOverlay()
    {
        try
        {
            PositionOnRightEdge();
            AppWindow.Show();
            Activate();
            DesktopSwitcher.BringToForeground(_hwnd);
            IsOpen = true;
            UpdateHeader();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusNavigation.Focus(HomeButton));

            _hw ??= await App.Host.Hardware.Value;
            RefreshControls();
            var profile = PowerPolicy.Choose(App.Host.Battery.Read());
            _statsTimer.Interval = profile.MonitorInterval;
            _statsTimer.Start();
            _ = SampleAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Quick access failed to open.", ex);
        }
    }

    public void HideOverlay()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _statsTimer.Stop();
        AppWindow.Hide();
    }

    public void CloseForGood()
    {
        _closeForGood = true;
        _statsTimer.Stop();
        try { Close(); } catch { /* already closed */ }
    }

    public void HandleAction(NavAction action)
    {
        var root = Content.XamlRoot;
        switch (action)
        {
            case NavAction.Back:
            case NavAction.Menu:
            case NavAction.View:
                HideOverlay();
                break;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                FocusNavigation.Move(root, action);
                break;
            case NavAction.Accept:
                FocusNavigation.InvokeFocused(root);
                break;
            case NavAction.ScrollDown:
            case NavAction.ScrollUp:
                FocusNavigation.Scroll(Scroller, action == NavAction.ScrollDown);
                break;
        }
    }

    // ---------------- layout ----------------

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void PositionOnRightEdge()
    {
        var area = DisplayArea.GetFromWindowId(_main.AppWindow.Id, DisplayAreaFallback.Primary);
        var bounds = area.OuterBounds;
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var width = Math.Min((int)(PanelWidthEpx * scale), bounds.Width);
        AppWindow.MoveAndResize(new RectInt32(bounds.X + bounds.Width - width, bounds.Y, width, bounds.Height));
    }

    // ---------------- content ----------------

    private void UpdateHeader()
    {
        ClockText.Text = DateTime.Now.ToString("t");
        var b = App.Host.Battery.Read();
        BatteryText.Text = !b.HasBattery || b.Percent is null
            ? "No battery"
            : $"Battery {b.Percent}%{(b.IsCharging ? " · charging" : b.Source == PowerSource.AC ? " · plugged in" : "")}" +
              (b.Remaining is { } r && b.Source == PowerSource.Battery ? $" · about {(int)r.TotalHours} h {r.Minutes} min left" : "") +
              (b.BatterySaverOn ? " · battery saver" : "");
    }

    private void RefreshControls()
    {
        if (_hw is null) return;
        _updating = true;
        try
        {
            // Volume
            VolumeSection.Visibility = _hw.Volume is null ? Visibility.Collapsed : Visibility.Visible;
            if (_hw.Volume is { } vol)
            {
                var level = (int)Math.Round(vol.GetLevel() * 100);
                VolumeSlider.Value = level;
                VolumeValue.Text = $"{level}%";
                UpdateMuteIcon(vol.GetMuted());
            }

            // Brightness
            var brightness = _hw.Brightness?.GetBrightness();
            BrightnessSection.Visibility = brightness is null ? Visibility.Collapsed : Visibility.Visible;
            if (brightness is { } br)
            {
                BrightnessSlider.Value = Math.Max(BrightnessSlider.Minimum, br);
                BrightnessValue.Text = $"{br}%";
            }

            RenderRefreshRates();
            RenderPowerPlans();

            PerfSection.Visibility = _hw.Monitor is null ? Visibility.Collapsed : Visibility.Visible;

            var unsupported = new[] { HardwareFeature.TdpControl, HardwareFeature.FanControl }
                .Where(f => !_hw.Supports(f))
                .Select(f => f == HardwareFeature.TdpControl ? "TDP" : "Fan control");
            UnsupportedText.Text =
                $"{string.Join(" and ", unsupported)} are not controlled by Lumen on {_hw.Profile.DisplayName}: there is no documented interface for them. Use the manufacturer's tool if you need them.";
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing quick access controls failed.", ex);
        }
        finally
        {
            _updating = false;
        }
    }

    private void RenderRefreshRates()
    {
        RefreshPanel.Children.Clear();
        var display = _hw?.Display;
        var rates = display?.GetSupportedRefreshRates() ?? [];
        RefreshSection.Visibility = rates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var current = display?.GetCurrent()?.RefreshHz;
        foreach (var hz in rates)
        {
            var button = new Button
            {
                Content = $"{hz} Hz",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 8),
            };
            if (hz == current) Highlight(button);
            var target = hz;
            button.Click += (_, _) =>
            {
                if (display!.SetRefreshRate(target)) RenderRefreshRates();
                else _main.ShowToast($"{target} Hz was rejected by the display driver.");
            };
            RefreshPanel.Children.Add(button);
        }
    }

    private void RenderPowerPlans()
    {
        PlansPanel.Children.Clear();
        var control = _hw?.PowerPlans;
        var plans = control?.GetPlans() ?? [];
        PowerSection.Visibility = control is null ? Visibility.Collapsed : Visibility.Visible;
        var active = control?.GetActivePlan();
        foreach (var plan in plans)
        {
            var button = new Button { Content = plan.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (plan.Id == active) Highlight(button);
            var id = plan.Id;
            button.Click += (_, _) =>
            {
                control!.SetActivePlan(id);
                RenderPowerPlans();
            };
            PlansPanel.Children.Add(button);
        }
        if (plans.Count <= 1)
        {
            PlansPanel.Children.Add(new TextBlock
            {
                Text = "Windows 11 uses one plan with a power mode slider; change the mode in Windows power settings.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["LumenCaptionText"],
            });
        }
    }

    private static void Highlight(Button b)
    {
        b.Background = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
        b.Foreground = (Brush)Application.Current.Resources["LumenAccentBrush"];
    }

    private async Task SampleAsync()
    {
        var monitor = _hw?.Monitor;
        if (monitor is null || _sampling || !IsOpen) return;
        _sampling = true;
        try
        {
            var s = await Task.Run(() => monitor.Sample());
            UpdateHeader();
            SetBar(CpuBar, CpuText, s.CpuPercent);
            SetBar(GpuBar, GpuText, s.GpuPercent);
            SetBar(RamBar, RamText, s.MemoryPercent);
            if (s.MemoryUsedBytes is { } used && s.MemoryTotalBytes is { } total)
                RamText.Text = $"{used / 1073741824.0:0.0}/{total / 1073741824.0:0} GB";
            TempText.Text = s.ThermalZoneCelsius is { } t
                ? $"{t:0} °C (ACPI thermal zone - approximate)"
                : "Not available (no documented sensor API)";
        }
        catch (Exception ex)
        {
            Log.Warn("Performance sample failed.", ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    private static void SetBar(ProgressBar bar, TextBlock text, double? value)
    {
        bar.Value = value ?? 0;
        bar.Opacity = value is null ? 0.3 : 1;
        text.Text = value is { } v ? $"{v:0}%" : "–";
    }

    // ---------------- handlers ----------------

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _hw?.Volume is not { } vol) return;
        try
        {
            vol.SetLevel((float)(e.NewValue / 100.0));
            if (e.NewValue > 0 && vol.GetMuted()) { vol.SetMuted(false); UpdateMuteIcon(false); }
            VolumeValue.Text = $"{(int)e.NewValue}%";
        }
        catch (Exception ex)
        {
            Log.Warn("Setting volume failed.", ex);
        }
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_hw?.Volume is not { } vol) return;
        try
        {
            var muted = !vol.GetMuted();
            vol.SetMuted(muted);
            UpdateMuteIcon(muted);
        }
        catch (Exception ex)
        {
            Log.Warn("Toggling mute failed.", ex);
        }
    }

    private void UpdateMuteIcon(bool muted) => MuteIcon.Glyph = muted ? "" : "";

    private void OnBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        BrightnessValue.Text = $"{(int)e.NewValue}%";
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start();
    }

    private void OnPowerSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true });
            _main.SwitchToDesktop();
        }
        catch (Exception ex)
        {
            Log.Warn("Opening power settings failed.", ex);
        }
    }

    private void OnHomeClick(object sender, RoutedEventArgs e) => _main.ReturnToShell();

    private void OnDesktopClick(object sender, RoutedEventArgs e) => _main.SwitchToDesktop();

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideOverlay();
}
