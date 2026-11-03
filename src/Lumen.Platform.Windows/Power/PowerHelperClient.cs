using System.IO.Pipes;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Performance;
using Lumen.Core.Power;

namespace Lumen.Platform.Windows.Power;

/// <summary>
/// User-side TDP backend: talks to the elevated helper, starting it through its scheduled task when
/// needed. Every result is the helper's live reading - nothing is cached or assumed.
/// </summary>
public sealed class PowerHelperClient : ITdpBackend
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static PowerHelperClient? _shared;

    /// <summary>One client per process: the pipe serves one connection at a time.</summary>
    public static PowerHelperClient Shared => _shared ??= new PowerHelperClient();

    public PowerHelperClient()
    {
        _pipeName = PowerHelperProtocol.PipeName(PowerHelperSetup.CurrentUserSid);
    }

    public string Name => "Intel RAPL via Lumen power helper";

    public Task<TdpStatus> GetStatusAsync(CancellationToken ct = default) => SendAsync(new PowerHelperRequest(PowerHelperRequest.Status), ct);
    public Task<TdpStatus> SetAsync(double watts, CancellationToken ct = default) => SendAsync(new PowerHelperRequest(PowerHelperRequest.Set, Math.Round(watts)), ct);
    public Task<TdpStatus> RestoreDefaultsAsync(CancellationToken ct = default) => SendAsync(new PowerHelperRequest(PowerHelperRequest.Restore), ct);

    /// <summary>
    /// Real frame telemetry (PresentMon/ETW, run by the elevated helper). Never starts the helper on its
    /// own unless the user enabled it; returns an explained "unavailable" reading otherwise.
    /// </summary>
    public async Task<FpsReading> GetFpsAsync(int processId, CancellationToken ct = default)
    {
        if (!PowerHelperSetup.IsInstalledCached())
            return FpsReading.Unavailable("FPS needs the Lumen helper (Settings › Performance)");
        return await SendAsync(new PowerHelperRequest(PowerHelperRequest.Fps, ProcessId: processId),
            PowerHelperProtocol.ParseFps, FpsReading.Unavailable, ct).ConfigureAwait(false);
    }

    public Task StopFpsAsync(CancellationToken ct = default) =>
        PowerHelperSetup.IsInstalledCached()
            ? SendAsync(new PowerHelperRequest(PowerHelperRequest.FpsStop), PowerHelperProtocol.ParseFps, FpsReading.Unavailable, ct, startIfNeeded: false)
            : Task.CompletedTask;

    private Task<TdpStatus> SendAsync(PowerHelperRequest request, CancellationToken ct) =>
        SendAsync(request, PowerHelperProtocol.ParseStatus, reason => TdpStatus.Unavailable(reason), ct);

    private async Task<T> SendAsync<T>(PowerHelperRequest request, Func<string?, T?> parse, Func<string, T> unavailable,
        CancellationToken ct, bool startIfNeeded = true) where T : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = parse(await TryExchangeAsync(request, TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false));
            if (response is not null) return response;
            if (!startIfNeeded) return unavailable("The Lumen power helper is not running.");

            if (!PowerHelperSetup.IsInstalled())
                return unavailable("Real TDP control is not enabled. Turn it on in Lumen Settings > Performance.");
            if (!PowerHelperSetup.Start())
                return unavailable("The Lumen power helper could not be started.");

            for (var i = 0; i < 10; i++) // helper start-up incl. driver open: ~1-3 s
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                response = parse(await TryExchangeAsync(request, TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false));
                if (response is not null) return response;
            }
            return unavailable("The Lumen power helper did not respond.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> TryExchangeAsync(PowerHelperRequest request, TimeSpan connectTimeout, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(PowerHelperProtocol.Serialize(request)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return line;
        }
        catch (TimeoutException) { return null; }
        catch (IOException ex)
        {
            Log.Debug($"Power helper pipe: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("Power helper pipe access denied.", ex);
            return null;
        }
    }
}
