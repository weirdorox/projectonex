namespace Lumen.Core.Input;

/// <summary>What to inject this frame. Moves are whole pixels; fractions carry over.</summary>
public readonly record struct MouseOutput(int Dx, int Dy, int Wheel, bool? Left, bool? Right)
{
    public bool IsEmpty => Dx == 0 && Dy == 0 && Wheel == 0 && Left is null && Right is null;
}

/// <summary>
/// Controller-as-mouse for the desktop (pure, tested): left stick moves with a deadzone and a
/// quadratic curve, right stick scrolls, A = left button, B = right button, RT held = precision.
/// Face buttons are ignored while View/Menu are held (those are Lumen shortcuts).
/// </summary>
public sealed class MouseEmulator
{
    public float Deadzone { get; init; } = 0.15f;
    public double MaxPixelsPerSecond { get; init; } = 1600;
    public double WheelNotchesPerSecond { get; init; } = 12;

    private double _fx, _fy, _fw;
    private bool _left, _right;
    private bool _primed;

    public void Reset()
    {
        _fx = _fy = _fw = 0;
        _left = _right = false;
        _primed = false;
    }

    /// <summary><paramref name="dt"/> = time since the previous update (clamped to 100 ms).</summary>
    public MouseOutput Update(GamepadState s, TimeSpan dt, double scale = 1.0)
    {
        if (!s.IsConnected)
        {
            var release = Release();
            Reset();
            return release;
        }

        var seconds = Math.Clamp(dt.TotalSeconds, 0, 0.1);
        var precision = s.RightTrigger > 0.5f ? 0.3 : 1.0;
        var speed = MaxPixelsPerSecond * scale * precision * seconds;

        _fx += Curve(s.LeftX) * speed;
        _fy += -Curve(s.LeftY) * speed; // stick up = screen up
        _fw += Curve(s.RightY) * WheelNotchesPerSecond * 120 * seconds;

        var dx = (int)Math.Truncate(_fx);
        var dy = (int)Math.Truncate(_fy);
        var wheel = (int)(Math.Truncate(_fw / 120) * 120);
        _fx -= dx;
        _fy -= dy;
        _fw -= wheel;

        bool? left = null, right = null;
        var modifier = s.IsDown(GamepadButtons.View) || s.IsDown(GamepadButtons.Menu);
        var a = !modifier && s.IsDown(GamepadButtons.A);
        var b = !modifier && s.IsDown(GamepadButtons.B);
        if (!_primed)
        {
            // Buttons held when mouse mode starts (e.g. the A that switched it on) must not click.
            _primed = true;
            _left = a;
            _right = b;
        }
        else
        {
            if (a != _left) left = _left = a;
            if (b != _right) right = _right = b;
        }

        return new MouseOutput(dx, dy, wheel, left, right);
    }

    /// <summary>Releases any held buttons (when mouse mode stops).</summary>
    public MouseOutput Release()
    {
        var output = new MouseOutput(0, 0, 0, _left ? false : null, _right ? false : null);
        _left = _right = false;
        return output;
    }

    private double Curve(float v)
    {
        var a = Math.Abs(v);
        if (a < Deadzone) return 0;
        var n = Math.Min(1, (a - Deadzone) / (1 - Deadzone));
        return Math.Sign(v) * n * n;
    }
}
