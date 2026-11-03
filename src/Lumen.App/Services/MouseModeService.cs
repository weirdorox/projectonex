using System.Diagnostics;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Input;
using Lumen.Platform.Windows.Shell;

namespace Lumen.App.Services;

/// <summary>
/// Desktop mouse mode (explicit opt-in from Quick Access): the controller drives the mouse while the
/// Windows desktop or another app is in front. It never runs while a game session is active (games
/// get the controller untouched) and it is off again after every restart.
/// </summary>
public sealed class MouseModeService
{
    private readonly MainWindow _shell;
    private readonly GamepadNavigator _navigator;
    private readonly MouseEmulator _emulator = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last;
    private bool _active;

    public MouseModeService(MainWindow shell, GamepadNavigator navigator)
    {
        _shell = shell;
        _navigator = navigator;
        _navigator.StateRead += OnState;
    }

    public bool Enabled { get; private set; }

    public event EventHandler? Changed;

    public void SetEnabled(bool enabled, string? reason = null)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        _navigator.FastPolling = enabled;
        if (!enabled) Stop();
        Log.Info($"Desktop mouse mode {(enabled ? "on" : "off")}{(reason is null ? "" : $" ({reason})")}.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnState(GamepadState state)
    {
        var now = _clock.Elapsed;
        var dt = now - _last;
        _last = now;

        if (!Enabled) return;
        if (_shell.RunningGame is not null)
        {
            SetEnabled(false, "a game started");
            _shell.ShowToast("Mouse mode turned off - a game is running");
            return;
        }

        // Inside Lumen (or its Quick Access panel) the controller navigates normally.
        var shouldRun = !_shell.IsShellForegroundForOverlay;
        if (!shouldRun)
        {
            Stop();
            return;
        }
        if (!_active)
        {
            _active = true;
            _emulator.Reset();
        }

        try
        {
            MouseInjector.Send(_emulator.Update(state, dt, _shell.InputScale));
        }
        catch (Exception ex)
        {
            Log.Warn("Mouse injection failed.", ex);
        }
    }

    private void Stop()
    {
        if (!_active) return;
        _active = false;
        try { MouseInjector.Send(_emulator.Release()); }
        catch (Exception ex) { Log.Warn("Releasing mouse buttons failed.", ex); }
    }
}
