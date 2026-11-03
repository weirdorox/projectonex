using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Performance;

namespace Lumen.PowerHelper;

/// <summary>
/// Runs Intel's open-source PresentMon (ETW consumer - reads present events Windows already emits;
/// nothing is injected into the game) for one process and keeps a rolling frame window.
/// Stops by itself when Lumen stops asking (overlay off) or the game exits.
/// </summary>
internal sealed class FpsCapture : IDisposable
{
    private const string SessionName = "LumenFps";
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(6);
    private readonly object _gate = new();
    private readonly FrameWindow _window = new(TimeSpan.FromSeconds(2));
    private PresentMonCsv _csv = new();
    private Process? _process;
    private int _pid;
    private DateTime _startedAt;
    private DateTime _lastRead;
    private DateTime _lastFrame;
    private string? _error;
    private bool? _trusted;
    private string? _trustProblem;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");

    public FpsReading Read(int pid)
    {
        lock (_gate)
        {
            _lastRead = DateTime.UtcNow;
            if (!File.Exists(ExePath))
                return FpsReading.Unavailable("PresentMon is not included in this build");
            _trusted ??= SignatureCheck.IsSignedBy(ExePath, "Intel Corporation", out _trustProblem);
            if (_trusted == false)
            {
                Log.Warn($"Refusing to run PresentMon: {_trustProblem}.");
                return FpsReading.Unavailable($"PresentMon refused: {_trustProblem}");
            }
            if (!IsAlive(pid)) return FpsReading.Unavailable("Game process has exited");

            if (_pid != pid || _process is null || _process.HasExited)
            {
                if (_pid == pid && _error is not null && DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(10))
                    return new FpsReading(false, _error, pid, null);
                Start(pid);
            }

            var stats = _window.Summarize();
            string? reason = null;
            if (stats is null)
            {
                reason = DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(4)
                    ? "Measuring…"
                    : "No frames from this game yet (paused, loading or minimized)";
            }
            else if (DateTime.UtcNow - _lastFrame > TimeSpan.FromSeconds(2))
            {
                stats = null; // stale - never show old numbers as current
                reason = "No new frames";
            }
            return new FpsReading(_error is null, _error ?? reason, pid, stats);
        }
    }

    /// <summary>Called every second by the helper loop.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_process is not null && DateTime.UtcNow - _lastRead > IdleStop) StopLocked("idle");
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked("requested");
    }

    private void Start(int pid)
    {
        StopLocked("switching process");
        _pid = pid;
        _csv = new PresentMonCsv();
        _window.Clear();
        _startedAt = DateTime.UtcNow;
        _error = null;
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            };
            foreach (var a in new[]
                     {
                         "--process_id", pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--output_stdout", "--no_console_stats", "--no_track_input",
                         "--session_name", SessionName, "--stop_existing_session", "--terminate_on_proc_exit",
                     })
                psi.ArgumentList.Add(a);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(process, e.Data);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log.Debug($"PresentMon: {e.Data}");
            };
            process.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    if (_process == process && process.ExitCode != 0 && _window.Count == 0)
                        _error = $"PresentMon stopped (code {process.ExitCode})";
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Log.Info($"FPS capture started for pid {pid}.");
        }
        catch (Exception ex)
        {
            _error = "PresentMon could not be started";
            Log.Error("Starting PresentMon failed.", ex);
        }
    }

    private void OnLine(Process source, string? line)
    {
        if (line is null) return;
        lock (_gate)
        {
            if (source != _process) return;
            if (!line.StartsWith("Application,", StringComparison.Ordinal) && _csv.HasHeader && _csv.ProcessId(line) is { } p && p != _pid) return;
            if (_csv.Feed(line) is { } sample)
            {
                _window.Add(sample);
                _lastFrame = DateTime.UtcNow;
            }
        }
    }

    private void StopLocked(string why)
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
            // Make sure the ETW session is gone even though PresentMon was killed.
            using var cleanup = Process.Start(new ProcessStartInfo(ExePath, $"--terminate_existing_session --session_name {SessionName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            cleanup?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping PresentMon failed.", ex);
        }
        _window.Clear();
        Log.Info($"FPS capture stopped ({why}).");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
