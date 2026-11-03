using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Power;
using Lumen.Platform.Windows.Power;
using Microsoft.UI.Dispatching;

namespace Lumen.App.Services;

/// <summary>
/// App-side TDP state. Wraps the helper client, caches the last *read* status for synchronous callers
/// (profile engine), and pings the helper while a non-default limit is active - if Lumen dies, the
/// helper notices the silence and restores the firmware limits by itself.
/// </summary>
public sealed class TdpService
{
    private readonly ITdpBackend? _backend;
    private readonly DispatcherQueueTimer _keepAlive;
    private readonly DispatcherQueue _ui;

    public TdpService(DispatcherQueue ui, bool safeMode)
    {
        _ui = ui;
        _backend = !safeMode && PowerHelperSetup.IsHelperShipped ? new PowerHelperClient() : null;
        _keepAlive = ui.CreateTimer();
        _keepAlive.Interval = TimeSpan.FromSeconds(30);
        _keepAlive.IsRepeating = true;
        _keepAlive.Tick += (_, _) => _ = RefreshAsync();
    }

    public event EventHandler<TdpStatus>? StatusChanged;

    public TdpStatus Status { get; private set; } = TdpStatus.Unavailable("Not checked yet.");

    public bool IsUsable => Status is { Available: true, Locked: false };

    public TdpRange? Range => Status is { Available: true, MinWatts: { } min, MaxWatts: { } max }
        ? new TdpRange(min, max, Status.RangeSource ?? "")
        : null;

    public async Task<TdpStatus> RefreshAsync()
    {
        if (_backend is null) return Update(TdpStatus.Unavailable("Real TDP control is not part of this installation."));
        if (!PowerHelperSetup.IsInstalled()) return Update(TdpStatus.Unavailable("Real TDP control is not enabled. Turn it on in Lumen Settings > Performance."));
        return Update(await _backend.GetStatusAsync());
    }

    public async Task<TdpStatus> SetAsync(double watts)
    {
        if (_backend is null) return Status;
        var s = Update(await _backend.SetAsync(watts));
        Log.Info($"TDP request {watts} W -> {s.Message ?? s.UnavailableReason}");
        return s;
    }

    public async Task<TdpStatus> RestoreAsync()
    {
        if (_backend is null) return Status;
        return Update(await _backend.RestoreDefaultsAsync());
    }

    private TdpStatus Update(TdpStatus s)
    {
        Status = s;
        void Publish()
        {
            if (s.RequestedWatts is not null) _keepAlive.Start();
            else _keepAlive.Stop();
            StatusChanged?.Invoke(this, s);
        }
        if (_ui.HasThreadAccess) Publish();
        else _ui.TryEnqueue(Publish);
        return s;
    }
}
