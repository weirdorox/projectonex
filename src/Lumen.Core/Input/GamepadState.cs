namespace Lumen.Core.Input;

/// <summary>Button bits. Values match XInput's XINPUT_GAMEPAD_* so the Windows layer can cast directly.</summary>
[Flags]
public enum GamepadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Menu = 0x0010,          // Start / ≡
    View = 0x0020,          // Back / ⧉
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>Normalized controller snapshot. Sticks are -1..1 (Y up is positive), triggers 0..1.</summary>
public readonly record struct GamepadState(
    bool IsConnected,
    GamepadButtons Buttons,
    float LeftX, float LeftY,
    float RightX, float RightY,
    float LeftTrigger, float RightTrigger)
{
    public static GamepadState Disconnected { get; } = default;

    public bool IsDown(GamepadButtons b) => (Buttons & b) == b;

    /// <summary>
    /// Combines several controllers (e.g. detachable left/right halves or an external pad) into one
    /// logical controller: buttons are OR-ed, each axis takes the input with the largest magnitude.
    /// </summary>
    public static GamepadState Merge(IEnumerable<GamepadState> states)
    {
        var result = Disconnected;
        foreach (var s in states)
        {
            if (!s.IsConnected) continue;
            result = new GamepadState(
                true,
                result.Buttons | s.Buttons,
                Larger(result.LeftX, s.LeftX), Larger(result.LeftY, s.LeftY),
                Larger(result.RightX, s.RightX), Larger(result.RightY, s.RightY),
                MathF.Max(result.LeftTrigger, s.LeftTrigger), MathF.Max(result.RightTrigger, s.RightTrigger));
        }
        return result;
    }

    private static float Larger(float a, float b) => MathF.Abs(a) >= MathF.Abs(b) ? a : b;
}

/// <summary>A source of controller input (XInput, Windows.Gaming.Input, test fakes).</summary>
public interface IGamepadSource
{
    /// <summary>Reads all controllers and returns the merged logical state.</summary>
    GamepadState Read();

    int ConnectedCount { get; }

    /// <summary>Raised from the polling thread when a controller is connected (true) or lost (false).</summary>
    event EventHandler<ControllerConnectionEventArgs>? ConnectionChanged;

    /// <summary>Forces the next Read to re-check every slot (after resume from sleep).</summary>
    void Rescan();
}

public sealed class ControllerConnectionEventArgs(int slot, bool connected) : EventArgs
{
    public int Slot { get; } = slot;
    public bool Connected { get; } = connected;
}
