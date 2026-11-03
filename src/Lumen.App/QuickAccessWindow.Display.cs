using System.Diagnostics;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Performance;
using Lumen.Platform.Windows.Power;
using Lumen.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App;

/// <summary>Quick tab: resolution (with keep/revert), HDR, docked state, microphone mute and audio output.</summary>
public sealed partial class QuickAccessWindow
{
    private const int ResolutionConfirmSeconds = 15;
    private DispatcherQueueTimer? _resolutionTimer;
    private DisplayMode? _resolutionPrevious;
    private ResolutionOption? _resolutionPending;
    private int _resolutionCountdown;
    private DisplayInfo? _hdrTarget;

    private void RenderDisplayAudio()
    {
        RenderOverlayModes();
        RenderResolutions();
        RenderHdr();
        RenderAudio();
        DockText.Text = _main.Dock == DockState.Handheld ? string.Empty : $"Display: {_main.DockDescription}";
        DockText.Visibility = DockText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- performance overlay ----------------

    private void RenderOverlayModes()
    {
        OverlayPanel.Children.Clear();
        var current = _main.Overlay.Mode;
        foreach (var mode in Enum.GetValues<OverlayMode>())
        {
            var button = new Button
            {
                Content = mode.ToString(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 8),
            };
            if (mode == current) Highlight(button);
            var target = mode;
            button.Click += (_, _) =>
            {
                _main.Overlay.SetMode(target);
                RenderOverlayModes();
                FocusNavigation.Focus(OverlayPanel.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Content, target.ToString())) ?? (DependencyObject)OverlayPanel);
            };
            OverlayPanel.Children.Add(button);
        }
        OverlayNote.Text = !PowerHelperSetup.IsInstalledCached()
            ? "FPS and frame times need the Lumen helper (Settings › Performance). Without it the overlay shows system, power and battery data only."
            : "FPS is measured with Intel PresentMon (Windows event tracing - nothing is injected into games). Shown over borderless/windowed games, not exclusive fullscreen.";
    }

    // ---------------- resolution ----------------

    private void RenderResolutions()
    {
        ResolutionPanel.Children.Clear();
        var control = _hw?.Resolution;
        var current = _hw?.Display?.GetCurrent();
        IReadOnlyList<ResolutionOption> options = control is null ? [] : ResolutionChooser.Options(control.GetModes(), current);
        ResolutionSection.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var option in options)
        {
            var button = new Button
            {
                Content = option.IsNative ? $"{option.Label} ★" : option.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 8),
            };
            if (option.IsCurrent) Highlight(button);
            var target = option;
            button.Click += (_, _) => ChangeResolution(target, current);
            ResolutionPanel.Children.Add(button);
        }
    }

    private void ChangeResolution(ResolutionOption target, DisplayMode? previous)
    {
        if (_hw?.Resolution is not { } control || target.IsCurrent || _resolutionPending is not null) return;
        // Not persisted until confirmed: if nobody answers (black screen, Lumen crash), it reverts - at
        // the latest on the next sign-in.
        if (!control.SetResolution(target.Width, target.Height, null, persist: false))
        {
            _main.ShowToast($"{target.Label} was rejected by the display driver.");
            return;
        }
        _resolutionPrevious = previous;
        _resolutionPending = target;
        _resolutionCountdown = ResolutionConfirmSeconds;
        ResolutionConfirm.Visibility = Visibility.Visible;
        UpdateResolutionCountdown();
        if (_resolutionTimer is null)
        {
            _resolutionTimer = DispatcherQueue.CreateTimer();
            _resolutionTimer.Interval = TimeSpan.FromSeconds(1);
            _resolutionTimer.Tick += (_, _) =>
            {
                if (--_resolutionCountdown <= 0) RevertResolution();
                else UpdateResolutionCountdown();
            };
        }
        _resolutionTimer.Start();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusNavigation.Focus(ResolutionKeepButton));
    }

    private void UpdateResolutionCountdown() =>
        ResolutionConfirmText.Text = $"Keep {_resolutionPending?.Label}? Going back in {_resolutionCountdown} s.";

    private void OnResolutionKeepClick(object sender, RoutedEventArgs e)
    {
        _resolutionTimer?.Stop();
        if (_resolutionPending is { } p) _hw?.Resolution?.SetResolution(p.Width, p.Height, null, persist: true);
        Log.Info($"Resolution {_resolutionPending?.Label} kept.");
        _resolutionPending = null;
        ResolutionConfirm.Visibility = Visibility.Collapsed;
        RenderResolutions();
        RenderRefreshRates();
    }

    private void OnResolutionRevertClick(object sender, RoutedEventArgs e) => RevertResolution();

    private void RevertResolution()
    {
        _resolutionTimer?.Stop();
        if (_resolutionPrevious is { } prev)
            _hw?.Resolution?.SetResolution(prev.Width, prev.Height, prev.RefreshHz, persist: true);
        Log.Info("Resolution reverted.");
        _resolutionPending = null;
        ResolutionConfirm.Visibility = Visibility.Collapsed;
        RenderResolutions();
        RenderRefreshRates();
    }

    // ---------------- HDR ----------------

    private void RenderHdr()
    {
        IReadOnlyList<DisplayInfo> displays;
        try { displays = _hw?.Displays?.GetActive() ?? []; }
        catch (Exception ex) { Log.Warn("Reading displays failed.", ex); displays = []; }

        _hdrTarget = DockDetector.Target(displays) ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        var target = _hdrTarget;
        HdrSection.Visibility = target is { HdrSupported: true } ? Visibility.Visible : Visibility.Collapsed;
        if (target is null) return;
        HdrToggle.Header = displays.Count > 1 ? $"HDR · {target.Name}" : "HDR";
        HdrToggle.IsOn = target.HdrEnabled;
        HdrToggle.IsEnabled = !target.HdrForceDisabled;
        HdrNote.Text = target.HdrForceDisabled
            ? "Windows is blocking HDR right now (for example HDR is off on battery in Windows display settings)."
            : "Uses the Windows HDR switch. Games decide themselves whether they output HDR.";
    }

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _hdrTarget is null || _hw?.Displays is not { } topology) return;
        var want = HdrToggle.IsOn;
        var ok = topology.SetHdr(_hdrTarget, want);
        if (!ok) _main.ShowToast("Windows did not change HDR for this display.");
        _updating = true;
        try { RenderHdr(); }
        finally { _updating = false; }
    }

    // ---------------- audio ----------------

    private void RenderAudio()
    {
        var audio = _hw?.Audio;
        var mic = audio?.GetMicMuted();
        MicToggle.Visibility = mic is null ? Visibility.Collapsed : Visibility.Visible;
        MicToggle.IsOn = mic == true;

        OutputPanel.Children.Clear();
        IReadOnlyList<AudioEndpoint> outputs;
        try { outputs = audio?.GetOutputs() ?? []; }
        catch (Exception ex) { Log.Warn("Listing audio outputs failed.", ex); outputs = []; }
        OutputSection.Visibility = outputs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var output in outputs)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = output.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            if (output.IsDefault) Highlight(button);
            var id = output.Id;
            button.Click += (_, _) => SwitchOutput(id);
            OutputPanel.Children.Add(button);
        }
    }

    private void SwitchOutput(string id)
    {
        if (_hw?.Audio is not { } audio) return;
        if (audio.SetDefaultOutput(id))
        {
            RenderAudio();
            if (_hw.Volume is { } vol)
            {
                _updating = true;
                VolumeSlider.Value = (int)Math.Round(vol.GetLevel() * 100);
                _updating = false;
            }
            return;
        }
        _main.ShowToast("Windows did not switch the output - opening Sound settings.");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
            HideOverlay();
        }
        catch (Exception ex)
        {
            Log.Warn("Opening sound settings failed.", ex);
        }
    }

    private void OnMicToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || _hw?.Audio is not { } audio) return;
        if (!audio.SetMicMuted(MicToggle.IsOn))
        {
            _main.ShowToast("Windows did not change the microphone.");
            _updating = true;
            MicToggle.IsOn = audio.GetMicMuted() == true;
            _updating = false;
        }
    }

    // ---------------- capture & keyboard ----------------

    private async void OnScreenshotClick(object sender, RoutedEventArgs e) => await CaptureAsync(record: false);

    private async void OnRecordClick(object sender, RoutedEventArgs e) => await CaptureAsync(record: true);

    /// <summary>Close the panel first so the game (not Lumen) is in front when Game Bar captures.</summary>
    private async Task CaptureAsync(bool record)
    {
        if (!GameBarCapture.IsEnabled())
        {
            _main.ShowToast("Game Bar captures are turned off in Windows Settings › Gaming › Captures.");
            return;
        }
        HideOverlay();
        await Task.Delay(400);
        if (record) GameBarCapture.ToggleRecording();
        else GameBarCapture.Screenshot();
    }

    private async void OnKeyboardClick(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        await Task.Delay(250);
        if (!await Task.Run(TouchKeyboard.Toggle)) _main.ShowToast("Windows did not open a keyboard.");
    }

    private void OnCapturesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GameBarCapture.CapturesFolder;
            Directory.CreateDirectory(folder);
            using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            HideOverlay();
        }
        catch (Exception ex)
        {
            Log.Warn("Opening captures folder failed.", ex);
        }
    }
}
