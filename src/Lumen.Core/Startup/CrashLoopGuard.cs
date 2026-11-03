using System.Text.Json;
using Lumen.Core.Diagnostics;

namespace Lumen.Core.Startup;

public enum StartupDecision
{
    /// <summary>Start normally (fullscreen console mode if configured).</summary>
    Normal,
    /// <summary>Repeated crashes detected or recovery requested: start windowed with the recovery screen.</summary>
    SafeMode,
}

/// <summary>
/// Detects crash loops so that an auto-starting fullscreen shell can never lock the user out.
///
/// On every start the guard records that a session is active. If the previous session never
/// reported a clean exit it is counted as a crash. When <see cref="Threshold"/> crashes happen
/// within <see cref="Window"/>, Lumen starts in safe mode. Once the app has run for
/// <see cref="StableAfter"/> the crash history is cleared.
/// </summary>
public sealed class CrashLoopGuard
{
    private readonly string _stateFile;
    private CrashLoopState _state = new();

    public CrashLoopGuard(string stateFile) => _stateFile = stateFile;

    public int Threshold { get; init; } = 3;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    public int RecentCrashCount => _state.RecentCrashes.Count;

    public StartupDecision OnStartup(DateTimeOffset now, bool recoveryRequested = false)
    {
        _state = Load();

        if (_state.SessionActive && _state.StartedAt is { } prevStart)
        {
            _state.RecentCrashes.Add(prevStart);
            Log.Warn($"Previous session (started {prevStart:O}) did not exit cleanly.");
        }

        _state.RecentCrashes.RemoveAll(t => now - t > Window);

        var decision = recoveryRequested || _state.RecentCrashes.Count >= Threshold
            ? StartupDecision.SafeMode
            : StartupDecision.Normal;

        _state.SessionActive = true;
        _state.StartedAt = now;
        Save();

        if (decision == StartupDecision.SafeMode)
            Log.Warn($"Starting in SAFE MODE (recent crashes: {_state.RecentCrashes.Count}, recovery requested: {recoveryRequested}).");
        return decision;
    }

    /// <summary>Call once the app has been running fine for a while.</summary>
    public void MarkStable()
    {
        if (_state.RecentCrashes.Count == 0) return;
        _state.RecentCrashes.Clear();
        Save();
        Log.Info("Session stable; crash history cleared.");
    }

    /// <summary>Call on normal exit, sign-out and shutdown.</summary>
    public void MarkCleanExit()
    {
        _state.SessionActive = false;
        Save();
    }

    private CrashLoopState Load()
    {
        try
        {
            if (File.Exists(_stateFile))
                return JsonSerializer.Deserialize<CrashLoopState>(File.ReadAllText(_stateFile)) ?? new CrashLoopState();
        }
        catch (Exception ex)
        {
            Log.Warn("Startup state unreadable; starting fresh.", ex);
        }
        return new CrashLoopState();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save startup state.", ex);
        }
    }

}

internal sealed class CrashLoopState
{
    public bool SessionActive { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public List<DateTimeOffset> RecentCrashes { get; set; } = [];
}
