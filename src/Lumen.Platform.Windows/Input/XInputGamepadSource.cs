using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Input;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Input;

/// <summary>
/// XInput backend. Polls the four XInput slots and merges every connected controller into one
/// logical controller, so detachable halves, the built-in pad and an external pad all work.
///
/// Calling XInputGetState on an empty slot is comparatively slow, so disconnected slots are only
/// probed every <see cref="ReprobeInterval"/> (or immediately after <see cref="Rescan"/>).
///
/// Note: the Guide/Xbox button is not exposed by the public XInput API and is deliberately not used.
/// </summary>
public sealed class XInputGamepadSource : IGamepadSource
{
    private const int SlotCount = 4;
    private readonly ReconnectPolicy _reconnect = new();

    private readonly bool[] _connected = new bool[SlotCount];
    private readonly GamepadState[] _states = new GamepadState[SlotCount];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastProbe = TimeSpan.MinValue;
    private bool _available = true;

    public event EventHandler<ControllerConnectionEventArgs>? ConnectionChanged;

    public int ConnectedCount => _connected.Count(c => c);

    /// <summary>Probe every slot now, then fast for a short burst (after wake/unlock).</summary>
    public void Rescan()
    {
        _lastProbe = TimeSpan.MinValue;
        _reconnect.Boost(_clock.Elapsed);
    }

    public GamepadState Read()
    {
        if (!_available) return GamepadState.Disconnected;

        var now = _clock.Elapsed;
        var probeAll = _lastProbe == TimeSpan.MinValue || now - _lastProbe >= _reconnect.ProbeInterval(now);
        if (probeAll) _lastProbe = now;

        for (uint slot = 0; slot < SlotCount; slot++)
        {
            if (!_connected[slot] && !probeAll) continue;

            uint rc;
            XINPUT_STATE raw;
            try
            {
                rc = XInputGetState(slot, out raw);
            }
            catch (DllNotFoundException ex)
            {
                Log.Error("XInput is not available on this system.", ex);
                _available = false;
                return GamepadState.Disconnected;
            }

            var isConnected = rc == ERROR_SUCCESS;
            if (isConnected != _connected[slot])
            {
                _connected[slot] = isConnected;
                Log.Info($"Controller slot {slot} {(isConnected ? "connected" : "disconnected")}.");
                ConnectionChanged?.Invoke(this, new ControllerConnectionEventArgs((int)slot, isConnected));
            }

            _states[slot] = isConnected ? Convert(raw.Gamepad) : GamepadState.Disconnected;
        }

        return GamepadState.Merge(_states);
    }

    private static GamepadState Convert(XINPUT_GAMEPAD g) => new(
        true,
        (GamepadButtons)g.wButtons,
        Axis(g.sThumbLX), Axis(g.sThumbLY),
        Axis(g.sThumbRX), Axis(g.sThumbRY),
        g.bLeftTrigger / 255f, g.bRightTrigger / 255f);

    private static float Axis(short v) => Math.Clamp(v / 32767f, -1f, 1f);

    /// <summary>Short haptic tick for confirmations. Fails silently on controllers without motors.</summary>
    public void Pulse(ushort strength = 18000, int milliseconds = 60)
    {
        for (uint slot = 0; slot < SlotCount; slot++)
        {
            if (!_connected[slot]) continue;
            var on = new XINPUT_VIBRATION { wLeftMotorSpeed = strength, wRightMotorSpeed = strength };
            var off = new XINPUT_VIBRATION();
            var s = slot;
            try
            {
                XInputSetState(s, ref on);
                _ = Task.Delay(milliseconds).ContinueWith(_ =>
                {
                    try { XInputSetState(s, ref off); } catch { /* ignore */ }
                }, TaskScheduler.Default);
            }
            catch { /* ignore */ }
        }
    }
}
