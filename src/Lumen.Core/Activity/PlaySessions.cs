namespace Lumen.Core.Activity;

/// <summary>One recorded play session (from Lumen's foreground-game detection, any store).</summary>
public sealed record PlaySession(
    string GameId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool OnBattery,
    double? AvgFps = null,
    double? AvgPackageWatts = null,
    double? BatteryPercentUsed = null,
    bool FpsMeasured = false)
{
    public TimeSpan Duration => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;
}

public interface IPlaySessionRepository
{
    Task AddAsync(PlaySession session, CancellationToken ct = default);
    Task<IReadOnlyList<PlaySession>> GetForGameAsync(string gameId, int max = 200, CancellationToken ct = default);
    Task<IReadOnlyList<PlaySession>> GetRecentAsync(int max = 200, CancellationToken ct = default);
}

public sealed class InMemoryPlaySessionRepository : IPlaySessionRepository
{
    private readonly List<PlaySession> _sessions = [];
    private readonly object _gate = new();

    public Task AddAsync(PlaySession session, CancellationToken ct = default)
    {
        lock (_gate) _sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlaySession>> GetForGameAsync(string gameId, int max = 200, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PlaySession>>(_sessions.Where(s => s.GameId == gameId).OrderByDescending(s => s.StartedAt).Take(max).ToList());
    }

    public Task<IReadOnlyList<PlaySession>> GetRecentAsync(int max = 200, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PlaySession>>(_sessions.OrderByDescending(s => s.StartedAt).Take(max).ToList());
    }
}

public sealed record DailyPlaytime(DateOnly Day, TimeSpan Played);

public sealed record PlayStats(
    TimeSpan Total,
    int Sessions,
    TimeSpan Average,
    TimeSpan Longest,
    TimeSpan Last7Days,
    IReadOnlyList<DailyPlaytime> Last14Days,
    double? AvgFps,
    double? AvgPackageWatts,
    double? AvgBatteryPercentPerHour);

/// <summary>Aggregates sessions for the details page. Sessions under a minute are ignored (launcher blips).</summary>
public static class PlayStatsCalculator
{
    public static readonly TimeSpan MinimumSession = TimeSpan.FromMinutes(1);

    public static PlayStats Compute(IEnumerable<PlaySession> sessions, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var list = sessions.Where(s => s.Duration >= MinimumSession).ToList();
        var total = TimeSpan.FromTicks(list.Sum(s => s.Duration.Ticks));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        var byDay = list
            .GroupBy(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.StartedAt, zone).DateTime))
            .ToDictionary(g => g.Key, g => TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)));
        var last14 = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(i - 13))
            .Select(d => new DailyPlaytime(d, byDay.TryGetValue(d, out var t) ? t : TimeSpan.Zero))
            .ToList();
        var last7 = TimeSpan.FromTicks(last14.Skip(7).Sum(d => d.Played.Ticks));

        double? Weighted(Func<PlaySession, double?> value)
        {
            var with = list.Where(s => value(s) is not null).ToList();
            var hours = with.Sum(s => s.Duration.TotalHours);
            return hours <= 0 ? null : Math.Round(with.Sum(s => value(s)!.Value * s.Duration.TotalHours) / hours, 1);
        }

        var batterySessions = list.Where(s => s.OnBattery && s.BatteryPercentUsed is > 0).ToList();
        var batteryHours = batterySessions.Sum(s => s.Duration.TotalHours);
        double? perHour = batteryHours <= 0 ? null : Math.Round(batterySessions.Sum(s => s.BatteryPercentUsed!.Value) / batteryHours, 1);

        return new PlayStats(
            total,
            list.Count,
            list.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(total.Ticks / list.Count),
            list.Count == 0 ? TimeSpan.Zero : list.Max(s => s.Duration),
            last7,
            last14,
            Weighted(s => s.FpsMeasured ? s.AvgFps : null),
            Weighted(s => s.AvgPackageWatts),
            perHour);
    }
}
