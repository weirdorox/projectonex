using System.Diagnostics;
using System.Text.Json;
using Lumen.Core.Activity;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Lumen.Core.Profiles;
using Lumen.Platform.Windows.Processes;
using Microsoft.UI.Dispatching;

namespace Lumen.App.Services;

/// <summary>
/// Watches which library game is in front (1 Hz, 0.33 Hz on low battery, paused while the display
/// is off), runs <see cref="GameSessionPolicy"/> and drives the <see cref="ProfileEngine"/>.
///
/// Works for games from every store - recognition is by process path, not by how it was launched.
/// The pre-game settings are persisted while a session is active, so even a crash or power loss
/// mid-game is repaired on the next start.
/// </summary>
public sealed class GameSessionService
{
    private readonly AppHost _host;
    private readonly MainWindow _shell;
    private readonly DispatcherQueueTimer _timer;
    private readonly GameSessionPolicy _policy = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, ProcessInfo> _seen = new(StringComparer.OrdinalIgnoreCase);
    private ProfileEngine? _engine;
    private HardwareProfileAdapter? _adapter;
    private bool _busy;
    private SessionRecorder? _recorder;
    private int _ticks;

    /// <summary>Collects what a play session record needs. Only real readings - nothing is estimated.</summary>
    private sealed class SessionRecorder(string gameId, DateTimeOffset startedAt, BatteryStatus battery)
    {
        public string GameId { get; } = gameId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int? StartPercent { get; } = battery.HasBattery ? battery.Percent : null;
        public bool EverOnAc { get; set; } = battery.Source != PowerSource.Battery;
        public double WattsSum { get; set; }
        public int WattsSamples { get; set; }
    }

    public GameSessionService(AppHost host, MainWindow shell, DispatcherQueue ui)
    {
        _host = host;
        _shell = shell;
        _timer = ui.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => _ = TickAsync();
    }

    /// <summary>Raised on the UI thread when a session starts/ends or the active profile changes.</summary>
    public event EventHandler? Changed;

    public ProfileEngine? Engine => _engine;
    public HardwareProfileAdapter? Hardware => _adapter;
    public string? ActiveGameId => _policy.ActiveGameId;
    public ProfileApplyReport LastReport { get; private set; } = ProfileApplyReport.None;

    public async Task StartAsync()
    {
        if (_host.IsSafeMode) return; // safe mode never changes hardware settings
        await EnsureEngineAsync();
        RecoverPendingBaseline();
        _timer.Start();
    }

    public void SetLowPower(bool lowPower) =>
        _timer.Interval = lowPower ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(1);

    public void Pause() => _timer.Stop();

    /// <summary>After wake: re-assert the running game's profile (drivers may reset display modes) and resume watching.</summary>
    public ProfileApplyReport Resume()
    {
        if (_host.IsSafeMode) return ProfileApplyReport.None;
        var report = _engine?.Reapply() ?? ProfileApplyReport.None;
        if (report.AnyApplied) Log.Info("Profile re-applied after resume.");
        _timer.Start();
        return report;
    }

    // ---------------- profiles API (UI) ----------------

    public Task<PerformanceProfile?> GetProfileAsync(string gameId) => _host.Profiles.GetAsync(gameId);

    public async Task<ProfileApplyReport> SaveProfileAsync(PerformanceProfile profile)
    {
        await _host.Profiles.SaveAsync(profile with { UpdatedAt = DateTimeOffset.UtcNow });
        var report = ProfileApplyReport.None;
        if (_engine is not null && string.Equals(_engine.SessionGameId, profile.GameId, StringComparison.OrdinalIgnoreCase))
        {
            var engine = _engine;
            report = await Task.Run(() => engine.UpdateProfile(profile.IsEmpty ? null : profile)); // may cross to the TDP helper
            LastReport = report;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return report;
    }

    /// <summary>"Save current settings for this game" from the overlay.</summary>
    public async Task<PerformanceProfile?> SaveCurrentAsync(string gameId)
    {
        if (await EnsureEngineAsync() is not { } adapter) return null;
        var now = adapter.Capture();
        var profile = new PerformanceProfile
        {
            GameId = gameId,
            RefreshHz = adapter.Supports(ProfileField.RefreshRate) ? now.RefreshHz : null,
            PowerPlanId = adapter.Supports(ProfileField.PowerPlan) ? now.PowerPlanId : null,
            BrightnessPercent = adapter.Supports(ProfileField.Brightness) ? now.BrightnessPercent : null,
        };
        await SaveProfileAsync(profile);
        return profile;
    }

    public Task<ProfileApplyReport> DeleteProfileAsync(string gameId) =>
        SaveProfileAsync(new PerformanceProfile { GameId = gameId });

    public async Task<HardwareProfileAdapter?> EnsureEngineAsync()
    {
        if (_adapter is not null) return _adapter;
        try
        {
            var hw = await _host.Hardware.Value;
            await _shell.Tdp.RefreshAsync();
            _adapter = new HardwareProfileAdapter(hw, _shell.Tdp);
            _engine = new ProfileEngine(_adapter);
        }
        catch (Exception ex)
        {
            Log.Error("Profile engine unavailable.", ex);
        }
        return _adapter;
    }

    // ---------------- watching ----------------

    private async Task TickAsync()
    {
        if (_busy || _engine is null) return;
        _busy = true;
        try
        {
            string? foregroundId = null;
            var fg = ForegroundProcess.Current();
            if (fg is not null && fg.ProcessId != Environment.ProcessId)
            {
                var game = GameMatcher.Match(fg.Path, _shell.Library.Games);
                if (game is not null)
                {
                    foregroundId = game.Id;
                    _seen[game.Id] = fg;
                }
            }

            if (_recorder is not null && ++_ticks % 15 == 0) await SampleAsync(_recorder);

            var active = _shell.RunningGame;
            var alive = active is null || ForegroundProcess.IsAlive(active.Process.ProcessId);

            foreach (var e in _policy.Observe(foregroundId, alive, _clock.Elapsed))
            {
                if (e.Kind == SessionEventKind.Ended) await EndAsync();
                else await StartSessionAsync(e.GameId);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Game session tick failed.", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task StartSessionAsync(string gameId)
    {
        var game = _shell.Library.Games.FirstOrDefault(g => string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase));
        if (game is null || !_seen.TryGetValue(gameId, out var process)) return;

        var profile = await _host.Profiles.GetAsync(gameId);
        var report = await Task.Run(() => _engine!.BeginSession(gameId, profile));
        LastReport = report;
        PersistBaseline(_engine!.Baseline);
        _shell.RunningGame = new RunningGame(game, process, DateTimeOffset.UtcNow);
        _recorder = new SessionRecorder(game.Id, DateTimeOffset.UtcNow, _host.Battery.Read());
        _ticks = 0;
        Log.Info($"Game session started: {game.Title} (pid {process.ProcessId}), profile: {(profile is null ? "none" : string.Join(", ", report.Fields.Select(f => $"{f.Key}={f.Value}")))}.");

        if (report.AnyApplied) _shell.ShowToast($"{game.Title}: performance profile applied");
        if (report.Failed.Any()) _shell.ShowToast($"{game.Title}: some profile settings were rejected ({string.Join(", ", report.Failed)})");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task EndAsync()
    {
        var title = _shell.RunningGame?.Game.Title;
        await Task.Run(() => _engine!.EndSession());
        PersistBaseline(null);
        _shell.RunningGame = null;
        _seen.Clear();
        await RecordAsync();
        Log.Info($"Game session ended: {title}.");
        _ = _shell.Library.LoadAsync(); // play stats / last played
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- play history ----------------

    private async Task SampleAsync(SessionRecorder r)
    {
        try
        {
            if (_host.Battery.Read().Source != PowerSource.Battery) r.EverOnAc = true;
            // Measured CPU package power only when the TDP helper is already in use (never started just for stats).
            if (_shell.Tdp.Status.Available)
            {
                var status = await _shell.Tdp.RefreshAsync();
                if (status.PackageWatts is { } w and > 0)
                {
                    r.WattsSum += w;
                    r.WattsSamples++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Session sampling failed.", ex);
        }
    }

    private async Task RecordAsync()
    {
        var r = _recorder;
        _recorder = null;
        if (r is null) return;
        try
        {
            var end = DateTimeOffset.UtcNow;
            var battery = _host.Battery.Read();
            if (battery.Source != PowerSource.Battery) r.EverOnAc = true;
            double? used = !r.EverOnAc && r.StartPercent is { } a && battery.Percent is { } b && a >= b ? a - b : null;
            var session = new PlaySession(
                r.GameId, r.StartedAt, end,
                OnBattery: !r.EverOnAc,
                AvgFps: null, // FPS only when measured by the telemetry overlay
                AvgPackageWatts: r.WattsSamples > 0 ? Math.Round(r.WattsSum / r.WattsSamples, 1) : null,
                BatteryPercentUsed: used,
                FpsMeasured: false);
            await _host.History.AddAsync(session);
            if (session.Duration >= PlayStatsCalculator.MinimumSession)
                await _host.Games.AddPlaytimeAsync(r.GameId, session.Duration);
            Log.Info($"Play session recorded: {session.Duration.TotalMinutes:0.#} min, battery {(session.OnBattery ? "yes" : "no")}, watts {session.AvgPackageWatts?.ToString() ?? "n/a"}.");
        }
        catch (Exception ex)
        {
            Log.Error("Recording play session failed.", ex);
        }
    }

    // ---------------- crash safety ----------------

    private void PersistBaseline(HardwareSnapshot? baseline)
    {
        try
        {
            _host.Settings.PendingProfileBaseline = baseline is null ? null : JsonSerializer.Serialize(baseline);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not persist profile baseline.", ex);
        }
    }

    /// <summary>A session was active when Lumen last stopped (crash, power loss): put the user's settings back.</summary>
    private void RecoverPendingBaseline()
    {
        var json = _host.Settings.PendingProfileBaseline;
        if (string.IsNullOrEmpty(json) || _adapter is null) return;
        try
        {
            var baseline = JsonSerializer.Deserialize<HardwareSnapshot>(json);
            if (baseline is not null)
            {
                var engine = new ProfileEngine(_adapter);
                // Re-open a session against the stored baseline and end it: restores every stored field.
                engine.BeginSession("__recovery", new PerformanceProfile
                {
                    GameId = "__recovery",
                    RefreshHz = baseline.RefreshHz,
                    PowerPlanId = baseline.PowerPlanId,
                    BrightnessPercent = baseline.BrightnessPercent,
                }, baseline);
                engine.EndSession();
                Log.Warn("Restored settings left over from an interrupted game session.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not restore the interrupted session's settings.", ex);
        }
        PersistBaseline(null);
    }
}
