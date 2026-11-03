namespace Lumen.Core.Profiles;

public enum SessionEventKind { Started, Ended }

public sealed record SessionEvent(SessionEventKind Kind, string GameId);

/// <summary>
/// Decides when a play session starts and ends from periodic observations, with hysteresis:
/// - a game must be in the foreground for <see cref="ActivationDelay"/> before its session starts
///   (ignores brief flashes, e.g. a launcher that immediately hands over);
/// - a session ends only when the game's process is gone - Alt+Tab, the quick-access panel or the
///   Lumen home screen do NOT end it;
/// - a different game held in the foreground ends the old session and starts a new one.
/// Pure logic: the caller supplies the observations and a monotonic clock.
/// </summary>
public sealed class GameSessionPolicy
{
    private string? _candidate;
    private TimeSpan _candidateSince;

    public TimeSpan ActivationDelay { get; init; } = TimeSpan.FromSeconds(2);

    public string? ActiveGameId { get; private set; }

    /// <param name="foregroundGameId">Library game owning the foreground window, or null.</param>
    /// <param name="activeGameAlive">Whether the active session's process still exists.</param>
    public IReadOnlyList<SessionEvent> Observe(string? foregroundGameId, bool activeGameAlive, TimeSpan now)
    {
        var events = new List<SessionEvent>(2);

        if (ActiveGameId is not null && !activeGameAlive)
        {
            events.Add(new SessionEvent(SessionEventKind.Ended, ActiveGameId));
            ActiveGameId = null;
        }

        if (foregroundGameId is null || string.Equals(foregroundGameId, ActiveGameId, StringComparison.OrdinalIgnoreCase))
        {
            _candidate = null;
            return events;
        }

        if (!string.Equals(_candidate, foregroundGameId, StringComparison.OrdinalIgnoreCase))
        {
            _candidate = foregroundGameId;
            _candidateSince = now;
            return events;
        }

        if (now - _candidateSince < ActivationDelay) return events;

        if (ActiveGameId is not null) events.Add(new SessionEvent(SessionEventKind.Ended, ActiveGameId));
        ActiveGameId = foregroundGameId;
        _candidate = null;
        events.Add(new SessionEvent(SessionEventKind.Started, foregroundGameId));
        return events;
    }

    /// <summary>Forget everything (e.g. the watcher was restarted).</summary>
    public void Reset()
    {
        ActiveGameId = null;
        _candidate = null;
    }
}
