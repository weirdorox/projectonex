using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Lumen.App;

/// <summary>
/// Docked mode: an external display (dock, USB-C monitor, TV) is detected through the CCD API.
/// With "follow external display" on, fullscreen Lumen moves to it and back to the built-in panel on undock.
/// Windows' own projection mode (Win+P) decides what is mirrored/extended; Lumen never changes it.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _dockDebounce;
    private bool _dockKnown;

    internal DockState Dock { get; private set; } = DockState.Handheld;
    internal string DockDescription { get; private set; } = "Handheld";
    internal event EventHandler? DockChanged;

    /// <summary>Displays settle over a second or two after plugging in - evaluate once things are quiet.</summary>
    private void ScheduleDockCheck()
    {
        if (_dockDebounce is null)
        {
            _dockDebounce = DispatcherQueue.CreateTimer();
            _dockDebounce.IsRepeating = false;
            _dockDebounce.Interval = TimeSpan.FromMilliseconds(1500);
            _dockDebounce.Tick += async (_, _) => await EvaluateDockAsync();
        }
        _dockDebounce.Stop();
        _dockDebounce.Start();
    }

    private async Task EvaluateDockAsync()
    {
        try
        {
            var hw = await _host.Hardware.Value;
            if (hw.Displays is not { } topology) return;
            var displays = await Task.Run(topology.GetActive);
            var state = DockDetector.Evaluate(displays);
            var changed = state != Dock || !_dockKnown;
            var first = !_dockKnown;
            _dockKnown = true;
            Dock = state;
            DockDescription = DockDetector.Describe(state, displays);
            if (!changed) return;

            Log.Info($"Display state: {DockDescription} ({string.Join(", ", displays.Select(d => $"{d.Name}{(d.IsInternal ? " [internal]" : "")} {d.Width}x{d.Height}"))}).");
            DockChanged?.Invoke(this, EventArgs.Empty);
            if (!first) ShowToast(state == DockState.Handheld ? "Handheld mode" : DockDescription);

            if (_host.IsSafeMode || !_host.Settings.DockFollowExternal || !_wantFullscreen) return;
            if (first && state == DockState.Handheld) return;
            MoveToDisplay(state == DockState.Handheld ? displays.FirstOrDefault(d => d.IsInternal) : DockDetector.Target(displays));
        }
        catch (Exception ex)
        {
            Log.Warn("Dock detection failed.", ex);
        }
    }

    private void MoveToDisplay(DisplayInfo? target)
    {
        if (target is null) return;
        try
        {
            var areas = DisplayArea.FindAll();
            DisplayArea? match = null;
            for (var i = 0; i < areas.Count; i++)
            {
                var a = areas[i];
                if (a.OuterBounds.X == target.X && a.OuterBounds.Y == target.Y) match = a;
            }
            if (match is null) return;

            var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (current is not null && current.OuterBounds.X == match.OuterBounds.X && current.OuterBounds.Y == match.OuterBounds.Y) return;

            // A fullscreen window belongs to its monitor: go overlapped, move, then fullscreen again.
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.Move(new PointInt32(match.WorkArea.X + 40, match.WorkArea.Y + 40));
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            Log.Info($"Moved Lumen to '{target.Name}'.");
        }
        catch (Exception ex)
        {
            Log.Warn("Moving Lumen to the other display failed.", ex);
        }
    }
}
