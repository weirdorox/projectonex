using System.Diagnostics;
using System.Text;

namespace Lumen.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error, Fatal }

/// <summary>
/// Minimal, dependency-free, thread-safe file logger. One file per day, old files pruned.
/// Logging must never throw: every failure inside the logger is swallowed.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static int _retainDays = 7;
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Optional in-memory hook (tests, diagnostics page).</summary>
    public static event Action<LogLevel, string>? Written;

    public static string? LogDirectory => _directory;

    public static void Initialize(string directory, int retainDays = 7)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            _directory = directory;
            _retainDays = Math.Max(1, retainDays);
            Prune();
        }
        catch
        {
            _directory = null; // fall back to Debug output only
        }
    }

    public static string CurrentFile =>
        _directory is null ? string.Empty : Path.Combine(_directory, $"lumen-{DateTime.Now:yyyyMMdd}.log");

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
    public static void Fatal(string message, Exception? ex = null) => Write(LogLevel.Fatal, message, ex);

    /// <summary>Startup phase marker with elapsed time since process start (for startup profiling).</summary>
    public static void Phase(string name) => Write(LogLevel.Info, $"[startup +{Uptime.ElapsedMilliseconds} ms] {name}");

    public static void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinimumLevel) return;
        try
        {
            var sb = new StringBuilder(128);
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
              .Append('(').Append(Environment.CurrentManagedThreadId).Append(") ")
              .Append(message);
            if (ex is not null) sb.AppendLine().Append(ex);
            var line = sb.ToString();

            System.Diagnostics.Debug.WriteLine(line);
            Written?.Invoke(level, line);

            if (_directory is null) return;
            lock (Gate)
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // never throw from logging
        }
    }

    private static void Prune()
    {
        if (_directory is null) return;
        var cutoff = DateTime.Now.AddDays(-_retainDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "lumen-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch { /* ignore */ }
        }
    }
}
