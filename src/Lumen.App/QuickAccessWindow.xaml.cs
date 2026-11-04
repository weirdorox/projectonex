using System.Runtime.InteropServices;
using Lumen.App.Input;
using Lumen.App.Services;
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
    private const int PanelWidthEpx = 440;

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
            // (Compatibility mode only - in the default mode the panel never becomes active.)
            // Ignore the deactivation that can arrive right after showing (activation hand-over).
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsOpen && _tookFocus
                && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(600)) HideOverlay();
        };

        // Translucent dark panel (acrylic falls back to solid automatically, e.g. in battery saver).
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(); } catch { /* not supported */ }
        if (Root.Background is SolidColorBrush surface)
            Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD0, surface.Color.R, surface.Color.G, surface.Color.B));
        FocusHighlight.Attach(Root);

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

    /// <summary>
    /// Opens the panel over whatever is in front (game, desktop, another app) without touching the
    /// Lumen dashboard. By default the panel never takes focus: the game stays the active window
    /// (no minimise, no resolution change, no Alt+Tab) and Lumen drives the panel from the
    /// controller itself. <paramref name="takeFocus"/> is the compatibility mode.
    /// </summary>
    public async void ShowOverlay(bool takeFocus = false)
    {
        if (IsOpen) return;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            CaptureTarget(); // must run before anything changes the foreground
            _previousForeground = OtherForegroundWindow();
            _tookFocus = takeFocus;
            PositionOnRightEdge();
            SetNoActivate(!takeFocus);
            Root.Translation = new System.Numerics.Vector3((float)PanelWidthEpx, 0, 0);
            if (takeFocus)
            {
                AppWindow.Show();
                Activate();
                DesktopSwitcher.BringToForeground(_hwnd);
            }
            else
            {
                AppWindow.Show(false); // SW_SHOWNOACTIVATE
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            IsOpen = true;
            _shownAt = DateTime.UtcNow;
            SlideIn();
            UpdateHeader();
            SetSection(_targetGame is null ? 0 : GameSection, focus: false);
            RenderGameSection();
            _ = RenderSystemSectionAsync();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusNavigation.Focus(HomeButton));
            Log.Info($"Quick settings opened ({(takeFocus ? "took focus" : "game keeps focus")}) in {watch.ElapsedMilliseconds} ms; " +
                     $"dashboard in front: {DesktopSwitcher.IsForeground(_main.Hwnd)}.");

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
        // Only if we took focus: give it back to exactly what was in front before.
        if (_tookFocus && _previousForeground != IntPtr.Zero && IsWindow(_previousForeground) && IsWindowVisible(_previousForeground))
            SetForegroundWindow(_previousForeground);
        _tookFocus = false;
        Log.Info("Quick settings closed.");
    }

    /// <summary>
    /// Creates the window's content once at start-up (off-screen) so the first press is instant and
    /// later opens never need to activate anything.
    /// </summary>
    public void Prewarm()
    {
        try
        {
            var before = GetForegroundWindow();
            SetNoActivate(true);
            AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 10, 10));
            AppWindow.Show(false);
            AppWindow.Hide();
            if (before != IntPtr.Zero && GetForegroundWindow() != before) SetForegroundWindow(before);
        }
        catch (Exception ex)
        {
            Log.Warn("Quick settings prewarm failed.", ex);
        }
    }

    private IntPtr _previousForeground;
    private DateTime _shownAt;
    private bool _tookFocus;

    private IntPtr OtherForegroundWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId ? IntPtr.Zero : fg;
    }

    private void SetNoActivate(bool on)
    {
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST : ex & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private void SlideIn()
    {
        Root.TranslationTransition = null;
        Root.Translation = new System.Numerics.Vector3((float)PanelWidthEpx, 0, 0);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Root.TranslationTransition = Motion.Vector(180);
            Root.Translation = System.Numerics.Vector3.Zero;
        });
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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
            case NavAction.PreviousTab:
                SetSection(_section - 1);
                break;
            case NavAction.NextTab:
                SetSection(_section + 1);
                break;
        }
    }

    // ---------------- layout ----------------

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void PositionOnRightEdge()
    {
        // The display the game/app in front is on - not necessarily where the dashboard is.
        var area = _previousForeground != IntPtr.Zero && GetWindowRect(_previousForeground, out var r) && r.Right > r.Left
            ? DisplayArea.GetFromRect(new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), DisplayAreaFallback.Primary)
            : DisplayArea.GetFromWindowId(_main.AppWindow.Id, DisplayAreaFallback.Primary);
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
            BrightnessSection.Visibility = Visibility.Visible;
            BrightnessSlider.IsEnabled = brightness is not null;
            BrightnessNote.Visibility = brightness is null ? Visibility.Visible : Visibility.Collapsed;
            if (brightness is null) BrightnessValue.Text = "—";
            if (brightness is { } br)
            {
                BrightnessSlider.Value = Math.Max(BrightnessSlider.Minimum, br);
                BrightnessValue.Text = $"{br}%";
            }

            RenderRefreshRates();
            RenderDisplayAudio();
            RenderPowerPlans();

            PerfSection.Visibility = _hw.Monitor is null ? Visibility.Collapsed : Visibility.Visible;

            UnsupportedText.Text = _hw.Supports(HardwareFeature.FanControl)
                ? string.Empty
                : $"Fan control: not available on {_hw.Profile.DisplayName} (no documented interface). TDP: see the Power tab.";
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
            if (_section == 2) RenderBatterySection();
            if (_section == 1) RenderTdpLive();
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
        if (_updating || _hw?.Volume is not { } vol || VolumeValue is null) return;
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
        // Also raised while the XAML is still loading (Minimum clamps the value) - before the debounce exists.
        if (_updating || _brightnessDebounce is null || BrightnessValue is null) return;
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
