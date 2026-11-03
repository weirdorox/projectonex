using System.Globalization;

namespace Lumen.Core.Performance;

/// <summary>One frame as reported by PresentMon (all times in milliseconds). Null = not reported / not displayed.</summary>
public readonly record struct FrameSample(
    double FrameMs,
    double? DisplayedMs,
    double? GpuBusyMs,
    double? CpuBusyMs,
    double? LatencyMs,
    string? PresentMode,
    string? Runtime);

/// <summary>
/// Header-driven parser for PresentMon CSV (1.x, 2.x and current column names). Unknown columns are
/// ignored, so new PresentMon versions keep working as long as the frame-time column exists.
/// </summary>
public sealed class PresentMonCsv
{
    private static readonly string[] FrameCols = ["MsBetweenPresents", "FrameTime", "MsBetweenAppStart"];
    private static readonly string[] DisplayCols = ["MsBetweenDisplayChange", "DisplayedTime"];
    private static readonly string[] GpuCols = ["MsGPUBusy", "GPUBusy", "MsGPUActive"];
    private static readonly string[] CpuCols = ["MsCPUBusy", "CPUBusy"];
    private static readonly string[] LatencyCols = ["DisplayLatency", "MsUntilDisplayed"];

    private int _frame = -1, _display = -1, _gpu = -1, _cpu = -1, _latency = -1, _mode = -1, _runtime = -1, _process = -1;

    public bool HasHeader => _frame >= 0;

    /// <summary>Feeds one line of PresentMon output; returns a sample for data rows, null otherwise.</summary>
    public FrameSample? Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var cells = line.Split(',');
        if (line.StartsWith("Application,", StringComparison.Ordinal))
        {
            ReadHeader(cells);
            return null;
        }
        if (!HasHeader || cells.Length <= _frame) return null;

        var frame = Number(cells, _frame);
        if (frame is not { } ms || ms <= 0 || ms > 5000) return null; // first frame / pauses
        return new FrameSample(
            ms,
            Number(cells, _display) is { } d and > 0 ? d : null,
            Number(cells, _gpu),
            Number(cells, _cpu),
            Number(cells, _latency),
            Cell(cells, _mode),
            Cell(cells, _runtime));
    }

    /// <summary>Process id of a data row (PresentMon may report several swap chains).</summary>
    public int? ProcessId(string line)
    {
        var cells = line.Split(',');
        return Cell(cells, _process) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    private void ReadHeader(string[] cells)
    {
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                var i = Array.FindIndex(cells, c => string.Equals(c.Trim(), n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }
        _frame = Find(FrameCols);
        _display = Find(DisplayCols);
        _gpu = Find(GpuCols);
        _cpu = Find(CpuCols);
        _latency = Find(LatencyCols);
        _mode = Find("PresentMode");
        _runtime = Find("PresentRuntime", "Runtime");
        _process = Find("ProcessID");
    }

    private static string? Cell(string[] cells, int i) => i >= 0 && i < cells.Length && cells[i].Length > 0 ? cells[i].Trim() : null;

    private static double? Number(string[] cells, int i) =>
        Cell(cells, i) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}

/// <summary>Summary of the last few seconds of measured frames. Every number comes from real frames.</summary>
public sealed record FrameStats(
    double Fps,
    double OnePercentLowFps,
    double? DisplayedFps,
    double AvgFrameMs,
    double P99FrameMs,
    double? AvgGpuBusyMs,
    double? AvgCpuBusyMs,
    double? AvgLatencyMs,
    string? PresentMode,
    string? Runtime,
    int Frames,
    IReadOnlyList<double> RecentFrameMs);

/// <summary>Rolling window over measured frames (by accumulated frame time, not wall clock).</summary>
public sealed class FrameWindow
{
    private readonly Queue<FrameSample> _frames = new();
    private readonly double _windowMs;
    private readonly int _graphPoints;
    private double _sumMs;

    public FrameWindow(TimeSpan window, int graphPoints = 120)
    {
        _windowMs = window.TotalMilliseconds;
        _graphPoints = graphPoints;
    }

    public int Count => _frames.Count;

    public void Add(FrameSample s)
    {
        _frames.Enqueue(s);
        _sumMs += s.FrameMs;
        while (_sumMs > _windowMs && _frames.Count > 1) _sumMs -= _frames.Dequeue().FrameMs;
    }

    public void Clear()
    {
        _frames.Clear();
        _sumMs = 0;
    }

    /// <summary>Null until at least <paramref name="minFrames"/> frames were measured.</summary>
    public FrameStats? Summarize(int minFrames = 10)
    {
        if (_frames.Count < minFrames || _sumMs <= 0) return null;
        var list = _frames.ToList();
        var times = list.Select(f => f.FrameMs).OrderBy(t => t).ToList();
        var p99 = Percentile(times, 0.99);
        var displayed = list.Where(f => f.DisplayedMs is not null).ToList();
        double? displayedFps = displayed.Count >= minFrames / 2
            ? displayed.Count * 1000.0 / list.Sum(f => f.FrameMs)
            : null;
        return new FrameStats(
            Fps: Math.Round(list.Count * 1000.0 / _sumMs, 1),
            OnePercentLowFps: Math.Round(1000.0 / p99, 1),
            DisplayedFps: displayedFps is { } dfps ? Math.Round(dfps, 1) : null,
            AvgFrameMs: Math.Round(_sumMs / list.Count, 2),
            P99FrameMs: Math.Round(p99, 2),
            AvgGpuBusyMs: Avg(list.Select(f => f.GpuBusyMs)),
            AvgCpuBusyMs: Avg(list.Select(f => f.CpuBusyMs)),
            AvgLatencyMs: Avg(list.Select(f => f.LatencyMs)),
            PresentMode: list.LastOrDefault(f => f.PresentMode is not null).PresentMode,
            Runtime: list.LastOrDefault(f => f.Runtime is not null).Runtime,
            Frames: list.Count,
            RecentFrameMs: list.Skip(Math.Max(0, list.Count - _graphPoints)).Select(f => Math.Round(f.FrameMs, 2)).ToList());
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var v = values.Where(x => x is not null).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 2);
    }

    /// <summary>Nearest-rank percentile on a sorted list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}

/// <summary>What the helper reports for an FPS request. <see cref="Stats"/> is null when nothing was measured.</summary>
public sealed record FpsReading(bool Available, string? Reason, int? ProcessId, FrameStats? Stats, string Source = "PresentMon (ETW)")
{
    public static FpsReading Unavailable(string reason) => new(false, reason, null, null);
}
