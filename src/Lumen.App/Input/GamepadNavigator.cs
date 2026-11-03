using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Input;
using Lumen.Core.Power;
using Microsoft.UI.Dispatching;

namespace Lumen.App.Input;

/// <summary>
/// Polls the controller on the UI thread and routes semantic actions.
///
/// - While a Lumen window is in the foreground: polled at the profile's foreground rate (60 Hz) and
///   every action is delivered to <see cref="ActionReceived"/>.
/// - While a game or the desktop is in the foreground: polled slowly and only the global chords
///   (quick access, emergency desktop) are acted on. Everything else is ignored so Lumen never
///   reacts to in-game input.
/// - Paused entirely while the display is off.
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private readonly IGamepadSource _source;
    private readonly Func<bool> _isShellForeground;
    private readonly DispatcherQueueTimer _timer;
    private readonly InputMapper _mapper = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ShellPerformanceProfile _profile = PowerPolicy.PluggedIn;
    private bool _wasForeground;
    private bool _paused;

    public GamepadNavigator(DispatcherQueue dispatcher, IGamepadSource source, Func<bool> isShellForeground)
    {
        _source = source;
        _isShellForeground = isShellForeground;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = _profile.ForegroundInputInterval;
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Delivered only while Lumen is in the foreground.</summary>
    public event Action<NavAction>? ActionReceived;

    /// <summary>View + Menu - works everywhere, including in games.</summary>
    public event Action? QuickAccessRequested;

    /// <summary>View + Menu + LB + RB held for 3 seconds - works everywhere.</summary>
    public event Action? EmergencyDesktopRequested;

    /// <summary>Global controller shortcuts (screenshot, overlay) - raised whether or not Lumen is in front.</summary>
    public event Action<NavAction>? ShortcutRequested;

    /// <summary>Raw state after each poll (desktop mouse mode). Raised in the foreground and background.</summary>
    public event Action<GamepadState>? StateRead;

    private bool _fast;

    /// <summary>Poll at the foreground rate even in the background (mouse mode needs smooth movement).</summary>
    public bool FastPolling
    {
        get => _fast;
        set
        {
            _fast = value;
            UpdateInterval(_wasForeground);
        }
    }

    public void Start() => _timer.Start();

    public void SetProfile(ShellPerformanceProfile profile)
    {
        if (profile == _profile) return;
        _profile = profile;
        Log.Info($"Input polling profile: {profile.Name} ({profile.ForegroundInputHz}/{profile.BackgroundInputHz} Hz).");
        UpdateInterval(_wasForeground);
    }

    /// <summary>Display off / suspend: stop polling entirely.</summary>
    public void Pause()
    {
        _paused = true;
        _timer.Stop();
    }

    /// <summary>Display on / resume: re-detect controllers and ignore buttons held during the transition.</summary>
    public void Resume()
    {
        _source.Rescan();
        _mapper.Reset();
        if (_paused)
        {
            _paused = false;
            _timer.Start();
        }
    }

    private void Poll()
    {
        GamepadState state;
        try
        {
            state = _source.Read();
        }
        catch (Exception ex)
        {
            Log.Error("Controller read failed.", ex);
            return;
        }

        var foreground = _isShellForeground();
        if (foreground != _wasForeground)
        {
            _wasForeground = foreground;
            _mapper.Reset(); // buttons held while switching windows must not fire
            UpdateInterval(foreground);
        }

        try { StateRead?.Invoke(state); }
        catch (Exception ex) { Log.Error("Controller state handler failed.", ex); }

        var actions = _mapper.Update(state, _clock.Elapsed);
        foreach (var action in actions)
        {
            try
            {
                switch (action)
                {
                    case NavAction.QuickAccess:
                        QuickAccessRequested?.Invoke();
                        break;
                    case NavAction.EmergencyDesktop:
                        EmergencyDesktopRequested?.Invoke();
                        break;
                    case NavAction.Screenshot:
                    case NavAction.CycleOverlay:
                        ShortcutRequested?.Invoke(action);
                        break;
                    default:
                        if (foreground) ActionReceived?.Invoke(action);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Handler for {action} failed.", ex);
            }
        }
    }

    private void UpdateInterval(bool foreground) =>
        _timer.Interval = foreground || _fast ? _profile.ForegroundInputInterval : _profile.BackgroundInputInterval;

    public void Dispose() => _timer.Stop();
}
