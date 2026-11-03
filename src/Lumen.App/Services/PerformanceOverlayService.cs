using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Performance;
using Lumen.Platform.Windows.Power;
using Microsoft.UI.Dispatching;

namespace Lumen.App.Services;

/// <summary>
/// Drives the on-screen performance overlay: real frame telemetry from the elevated helper
/// (PresentMon/ETW) for the running game, plus system, power and battery readings Lumen already has.
/// Nothing runs while the overlay is off.
/// </summary>
public sealed class PerformanceOverlayService
{
    private readonly AppHost _host;
    private readonly MainWindow _shell;
    private readonly DispatcherQueueTimer _timer;
    private OsdWindow? _window;
    private bool _busy;
    private int _ticks;
    private SystemSnapshot? _system;

    public PerformanceOverlayService(AppHost host, MainWindow shell, DispatcherQueue ui)
    {
        _host = host;
        _shell = shell;
        _timer = ui.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => _ = TickAsync();
    }

    public OverlayMode Mode { get; private set; } = OverlayMode.Off;

    /// <summary>Latest real FPS reading (null when not measured). Used for play-history statistics.</summary>
    public FpsReading? LatestFps { get; private set; }

    public event EventHandler? ModeChanged;

    public void Start()
    {
        if (_host.IsSafeMode) return;
        SetMode(_host.Settings.OverlayMode, persist: false);
    }

    public void Cycle() => SetMode(OverlayContent.Next(Mode));

    public void SetMode(OverlayMode mode, bool persist = true)
    {
        if (persist) _host.Settings.OverlayMode = mode;
        var wasOff = Mode == OverlayMode.Off;
        Mode = mode;
        Log.Info($"Performance overlay: {mode}.");
        if (mode == OverlayMode.Off)
        {
            _timer.Stop();
            _window?.HideOsd();
            LatestFps = null;
            _ = PowerHelperClient.Shared.StopFpsAsync();
        }
        else
        {
            _window ??= new OsdWindow(_host.Settings.OverlayClickThrough);
            if (wasOff) _ = TickAsync();
            _timer.Start();
        }
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hide while the shell itself is in front (the overlay is for games); show again otherwise.</summary>
    public void SetShellVisible(bool shellInFront)
    {
        if (Mode == OverlayMode.Off || _window is null) return;
        if (shellInFront) _window.HideOsd();
    }

    private async Task TickAsync()
    {
        if (_busy || Mode == OverlayMode.Off) return;
        _busy = true;
        try
        {
            _ticks++;
            var hw = _host.Hardware.IsValueCreated && _host.Hardware.Value.IsCompletedSuccessfully ? _host.Hardware.Value.Result : null;
            var game = _shell.RunningGame;

            FpsReading fps;
            if (game is null) fps = FpsReading.Unavailable("No game running");
            else if (!PowerHelperSetup.IsHelperShipped) fps = FpsReading.Unavailable("FPS telemetry is not part of this build");
            else fps = await PowerHelperClient.Shared.GetFpsAsync(game.Process.ProcessId);
            LatestFps = fps.Stats is null ? null : fps;

            if (Mode >= OverlayMode.Detailed && hw?.Monitor is { } monitor && _ticks % 2 == 0)
                _system = await Task.Run(monitor.Sample);
            if (Mode >= OverlayMode.Detailed && _shell.Tdp.Status.Available && _ticks % 4 == 0)
                await _shell.Tdp.RefreshAsync();

            var inputs = new OverlayInputs(
                fps,
                _system,
                _shell.Tdp.Status,
                _host.Battery.Read(),
                Mode >= OverlayMode.Detailed ? hw?.BatteryTelemetry?.Read() : null);

            var shellInFront = _shell.IsShellForegroundForOverlay;
            if (Mode == OverlayMode.Off) return;
            if (shellInFront) _window?.HideOsd();
            else _window?.Render(OverlayContent.Lines(Mode, inputs), OverlayContent.ShowsGraph(Mode) ? fps.Stats?.RecentFrameMs : null);
        }
        catch (Exception ex)
        {
            Log.Warn("Overlay update failed.", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _window?.CloseForGood();
        _window = null;
    }
}
