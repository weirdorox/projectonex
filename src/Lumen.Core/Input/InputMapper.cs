namespace Lumen.Core.Input;

/// <summary>Semantic UI actions produced from raw controller input.</summary>
public enum NavAction
{
    Up, Down, Left, Right,
    Accept,         // A
    Back,           // B
    Secondary,      // X  (details / context)
    Tertiary,       // Y  (favorite)
    Menu,           // ≡  (released without chord)
    View,           // ⧉  (released without chord)
    PreviousTab,    // LB
    NextTab,        // RB
    PreviousSection,// LT
    NextSection,    // RT
    ScrollUp,       // right stick
    ScrollDown,
    QuickAccess,    // View + Menu together
    EmergencyDesktop, // View + Menu + LB + RB held
}

public sealed record InputMapperOptions
{
    public TimeSpan InitialRepeatDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan RepeatInterval { get; init; } = TimeSpan.FromMilliseconds(110);
    public TimeSpan FastRepeatAfter { get; init; } = TimeSpan.FromMilliseconds(1300);
    public TimeSpan FastRepeatInterval { get; init; } = TimeSpan.FromMilliseconds(55);
    public float StickPressThreshold { get; init; } = 0.55f;
    public float StickReleaseThreshold { get; init; } = 0.35f;
    public float TriggerPressThreshold { get; init; } = 0.55f;
    public float TriggerReleaseThreshold { get; init; } = 0.30f;
    public TimeSpan EmergencyHold { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Pure, deterministic translation from controller snapshots to <see cref="NavAction"/>s:
/// edge detection, auto-repeat with acceleration, stick hysteresis, trigger thresholds and chords.
/// The caller supplies a monotonic clock value so the class is fully unit-testable.
/// </summary>
public sealed class InputMapper
{
    private readonly InputMapperOptions _o;
    private GamepadState _prev;
    private bool _primed;

    private readonly Repeater _nav;
    private readonly Repeater _scroll;
    private NavAction? _stickDir;
    private NavAction? _rightStickDir;

    private bool _ltDown, _rtDown;
    private bool _chordUsed;
    private TimeSpan? _emergencyStart;
    private bool _emergencyFired;

    public InputMapper(InputMapperOptions? options = null)
    {
        _o = options ?? new InputMapperOptions();
        _nav = new Repeater(_o);
        _scroll = new Repeater(_o);
    }

    /// <summary>
    /// Forgets all state. The next update only records what is held, so buttons held across a
    /// focus change, sleep/resume or controller reconnect do not fire phantom actions.
    /// </summary>
    public void Reset()
    {
        _primed = false;
        _nav.Clear();
        _scroll.Clear();
        _stickDir = _rightStickDir = null;
        _ltDown = _rtDown = false;
        _chordUsed = false;
        _emergencyStart = null;
        _emergencyFired = false;
    }

    public IReadOnlyList<NavAction> Update(GamepadState s, TimeSpan now)
    {
        var actions = new List<NavAction>(2);

        if (!s.IsConnected)
        {
            if (_primed) Reset();
            _prev = s;
            return actions;
        }

        if (!_primed)
        {
            _primed = true;
            _prev = s;
            _ltDown = s.LeftTrigger >= _o.TriggerPressThreshold;
            _rtDown = s.RightTrigger >= _o.TriggerPressThreshold;
            _chordUsed = s.IsDown(GamepadButtons.View) || s.IsDown(GamepadButtons.Menu);
            return actions;
        }

        bool Pressed(GamepadButtons b) => s.IsDown(b) && !_prev.IsDown(b);
        bool Released(GamepadButtons b) => !s.IsDown(b) && _prev.IsDown(b);

        var viewHeld = s.IsDown(GamepadButtons.View);
        var menuHeld = s.IsDown(GamepadButtons.Menu);

        // ---- chords: View + Menu ----
        if (viewHeld && menuHeld && (Pressed(GamepadButtons.View) || Pressed(GamepadButtons.Menu)) && !_chordUsed)
        {
            actions.Add(NavAction.QuickAccess);
            _chordUsed = true;
        }

        var emergencyCombo = GamepadButtons.View | GamepadButtons.Menu | GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder;
        if (s.IsDown(emergencyCombo))
        {
            _emergencyStart ??= now;
            if (!_emergencyFired && now - _emergencyStart.Value >= _o.EmergencyHold)
            {
                actions.Add(NavAction.EmergencyDesktop);
                _emergencyFired = true;
            }
        }
        else
        {
            _emergencyStart = null;
            _emergencyFired = false;
        }

        if (Released(GamepadButtons.View) && !_chordUsed) actions.Add(NavAction.View);
        if (Released(GamepadButtons.Menu) && !_chordUsed) actions.Add(NavAction.Menu);
        if (!viewHeld && !menuHeld) _chordUsed = false;

        var modifierHeld = viewHeld || menuHeld;

        // ---- face buttons and shoulders (press edge) ----
        if (!modifierHeld)
        {
            if (Pressed(GamepadButtons.A)) actions.Add(NavAction.Accept);
            if (Pressed(GamepadButtons.B)) actions.Add(NavAction.Back);
            if (Pressed(GamepadButtons.X)) actions.Add(NavAction.Secondary);
            if (Pressed(GamepadButtons.Y)) actions.Add(NavAction.Tertiary);
            if (Pressed(GamepadButtons.LeftShoulder)) actions.Add(NavAction.PreviousTab);
            if (Pressed(GamepadButtons.RightShoulder)) actions.Add(NavAction.NextTab);
        }

        // ---- triggers with hysteresis ----
        if (!_ltDown && s.LeftTrigger >= _o.TriggerPressThreshold) { _ltDown = true; if (!modifierHeld) actions.Add(NavAction.PreviousSection); }
        else if (_ltDown && s.LeftTrigger <= _o.TriggerReleaseThreshold) _ltDown = false;
        if (!_rtDown && s.RightTrigger >= _o.TriggerPressThreshold) { _rtDown = true; if (!modifierHeld) actions.Add(NavAction.NextSection); }
        else if (_rtDown && s.RightTrigger <= _o.TriggerReleaseThreshold) _rtDown = false;

        // ---- directional navigation: D-pad wins over the left stick ----
        var dir = DPadDirection(s.Buttons);
        _stickDir = StickDirection(s.LeftX, s.LeftY, _stickDir);
        dir ??= _stickDir;
        if (_nav.Update(modifierHeld ? null : dir, now) is { } navAction) actions.Add(navAction);

        // ---- right stick: vertical scrolling ----
        _rightStickDir = StickDirection(0, s.RightY, _rightStickDir);
        NavAction? scroll = _rightStickDir switch
        {
            NavAction.Up => NavAction.ScrollUp,
            NavAction.Down => NavAction.ScrollDown,
            _ => null,
        };
        if (_scroll.Update(scroll, now) is { } scrollAction) actions.Add(scrollAction);

        _prev = s;
        return actions;
    }

    private static NavAction? DPadDirection(GamepadButtons b)
    {
        if ((b & GamepadButtons.DPadUp) != 0) return NavAction.Up;
        if ((b & GamepadButtons.DPadDown) != 0) return NavAction.Down;
        if ((b & GamepadButtons.DPadLeft) != 0) return NavAction.Left;
        if ((b & GamepadButtons.DPadRight) != 0) return NavAction.Right;
        return null;
    }

    /// <summary>Dominant-axis direction with hysteresis: an active direction holds until its axis drops below the release threshold.</summary>
    internal NavAction? StickDirection(float x, float y, NavAction? current)
    {
        if (current is { } c)
        {
            var axis = c switch
            {
                NavAction.Up => y,
                NavAction.Down => -y,
                NavAction.Left => -x,
                NavAction.Right => x,
                _ => 0f,
            };
            if (axis > _o.StickReleaseThreshold) return c;
        }

        var ax = MathF.Abs(x);
        var ay = MathF.Abs(y);
        if (MathF.Max(ax, ay) < _o.StickPressThreshold) return null;
        if (ay >= ax) return y > 0 ? NavAction.Up : NavAction.Down;
        return x > 0 ? NavAction.Right : NavAction.Left;
    }

    /// <summary>Auto-repeat: fire on press, then after an initial delay, then faster after a while.</summary>
    private sealed class Repeater(InputMapperOptions o)
    {
        private NavAction? _active;
        private TimeSpan _pressedAt;
        private TimeSpan _nextAt;

        public void Clear() => _active = null;

        public NavAction? Update(NavAction? dir, TimeSpan now)
        {
            if (dir is null)
            {
                _active = null;
                return null;
            }
            if (dir != _active)
            {
                _active = dir;
                _pressedAt = now;
                _nextAt = now + o.InitialRepeatDelay;
                return dir;
            }
            if (now >= _nextAt)
            {
                var interval = now - _pressedAt >= o.FastRepeatAfter ? o.FastRepeatInterval : o.RepeatInterval;
                _nextAt = now + interval;
                return dir;
            }
            return null;
        }
    }
}
