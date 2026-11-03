namespace Lumen.Core.Input;

/// <summary>
/// How often to probe empty controller slots. Right after wake / display-on / unlock, Bluetooth and
/// detachable controllers reconnect within a few seconds, so Lumen probes every 250 ms for a short
/// burst (feels instant) and then drops back to the cheap 2 s cadence.
/// </summary>
public sealed class ReconnectPolicy
{
    public TimeSpan FastInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan NormalInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BurstLength { get; init; } = TimeSpan.FromSeconds(15);

    private TimeSpan? _burstStart;

    /// <summary>Start a fast-probe burst (call on resume, display on, session unlock).</summary>
    public void Boost(TimeSpan now) => _burstStart = now;

    public bool InBurst(TimeSpan now) => _burstStart is { } s && now - s < BurstLength;

    public TimeSpan ProbeInterval(TimeSpan now) => InBurst(now) ? FastInterval : NormalInterval;
}
