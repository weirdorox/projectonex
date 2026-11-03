using System.IO.Compression;
using System.Text;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Platform.Windows.Power;
using Lumen.Platform.Windows.Shell;

namespace Lumen.App.Services;

/// <summary>
/// Diagnostics page content and one-click log export. Every line says what Lumen actually detected;
/// features are reported as available / unavailable (with the reason) / locked by firmware.
/// </summary>
public static class DiagnosticsService
{
    public static async Task<string> BuildReportAsync(AppHost host, MainWindow shell)
    {
        var sb = new StringBuilder();
        void Line(string label, string value) => sb.AppendLine($"{label,-26} {value}");
        void Header(string title) { sb.AppendLine(); sb.AppendLine($"== {title} =="); }

        Line("Lumen", typeof(App).Assembly.GetName().Version?.ToString() ?? "?");
        Line("Windows", Environment.OSVersion.VersionString);
        Line("Safe mode", host.IsSafeMode ? "YES (hardware changes disabled)" : "no");
        Line("Data folder", host.Paths.Root);
        Line("Database schema", $"v{Lumen.Data.LumenDatabase.CurrentVersion}");

        HardwareServices? hw = null;
        try { hw = await host.Hardware.Value; }
        catch (Exception ex) { Line("Hardware", "initialisation failed: " + ex.Message); }

        if (hw is not null)
        {
            Header("Device");
            Line("Identity", hw.Identity.ToString());
            Line("Profile", hw.Profile.DisplayName);
            Header("Features (this device)");
            foreach (var f in Enum.GetValues<HardwareFeature>())
                Line(f.ToString(), hw.Supports(f) ? "available" : "unavailable - " + hw.Profile.ExplainUnsupported(f));
        }

        Header("TDP (Intel RAPL via Lumen helper)");
        Line("Helper shipped", PowerHelperSetup.IsHelperShipped ? "yes" : "no");
        Line("Helper enabled", PowerHelperSetup.IsInstalledCached() ? "yes (scheduled task)" : "no");
        Line("PawnIO library", PawnIo.FindLibrary() ?? "not found");
        var t = await shell.Tdp.RefreshAsync();
        if (!t.Available)
        {
            Line("Status", "unavailable - " + (t.UnavailableReason ?? "unknown"));
        }
        else
        {
            Line("Status", t.Locked ? "LOCKED by firmware (read-only)" : "available");
            Line("CPU", t.Cpu ?? "?");
            Line("Range", $"{t.MinWatts:0.#}-{t.MaxWatts:0.#} W ({t.RangeSource})");
            Line("Requested", t.RequestedWatts is { } r ? $"{r:0.#} W" : "none (firmware default)");
            Line("Read back PL1 / PL2", $"{t.Pl1Watts:0.#} / {t.Pl2Watts:0.#} W");
            Line("Firmware default PL1/2", $"{t.DefaultPl1Watts:0.#} / {t.DefaultPl2Watts:0.#} W");
            Line("Measured package power", t.PackageWatts is { } w ? $"{w:0.0} W" : "n/a");
            Line("Package temperature", t.PackageCelsius is { } c ? $"{c:0} °C" : "n/a");
            Line("Firmware override", t.FirmwareOverride ? "YES - the CPU did not keep the request" : "no");
            if (t.Message is { } m) Line("Note", m);
        }

        Header("FPS telemetry");
        var presentMon = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
        Line("PresentMon", File.Exists(presentMon) ? presentMon : "not included in this build");
        Line("Overlay mode", shell.Overlay.Mode.ToString());
        Line("Last reading", shell.Overlay.LatestFps?.Stats is { } fps ? $"{fps.Fps:0} FPS measured ({fps.Frames} frames)" : "none (only measured while the overlay is on and a game runs)");
        Line("FPS limiter", "not provided (would require injecting into games)");

        Header("Displays");
        try
        {
            foreach (var d in hw?.Displays?.GetActive() ?? [])
                Line(d.Name, $"{d.Width}x{d.Height}{(d.IsInternal ? " built-in" : " external")}{(d.IsPrimary ? " primary" : "")}, HDR {(d.HdrSupported ? d.HdrEnabled ? "on" : "off" : "not supported")}{(d.HdrForceDisabled ? " (blocked by Windows)" : "")}");
            Line("Mode", shell.DockDescription);
            if (hw?.Display?.GetCurrent() is { } mode) Line("Current mode", $"{mode.Width}x{mode.Height} @ {mode.RefreshHz} Hz");
        }
        catch (Exception ex) { Line("Displays", "error: " + ex.Message); }

        Header("Audio");
        try
        {
            var mic = hw?.Audio?.GetMicMuted();
            Line("Microphone", mic is null ? "none found" : mic.Value ? "muted" : "on");
            foreach (var o in hw?.Audio?.GetOutputs() ?? []) Line(o.IsDefault ? "Output (default)" : "Output", o.Name);
            Line("Output switching", "uses Windows' undocumented policy interface; falls back to Sound settings");
        }
        catch (Exception ex) { Line("Audio", "error: " + ex.Message); }

        Header("Battery");
        var b = host.Battery.Read();
        Line("Status", b.HasBattery ? $"{b.Percent}% {b.Source}{(b.IsCharging ? " charging" : "")}{(b.BatterySaverOn ? " saver" : "")}" : "no battery");
        if (hw?.BatteryTelemetry?.Read() is { } bt)
            Line("Telemetry", $"rate {bt.ChargeRateWatts?.ToString("0.0") ?? "?"} W, {bt.RemainingWh?.ToString("0.0") ?? "?"} / {bt.FullChargeWh?.ToString("0.0") ?? "?"} Wh (design {bt.DesignWh?.ToString("0.0") ?? "?"} Wh)");

        Header("Input & capture");
        Line("Controllers (XInput)", host.Gamepad.ConnectedCount.ToString());
        Line("OEM buttons", "not readable through XInput - map them in the manufacturer's tool");
        Line("Mouse mode", shell.MouseMode.Enabled ? "on" : "off");
        Line("Game Bar captures", GameBarCapture.IsEnabled() ? "enabled" : "turned off in Windows");
        Line("Captures folder", GameBarCapture.CapturesFolder);

        Header("Unsupported by design");
        Line("Fan control", "no documented interface on this device");
        Line("Universal game suspend", "not possible on Windows");
        Line("Exclusive-fullscreen overlay", "Windows does not allow normal windows over it");
        return sb.ToString();
    }

    /// <summary>Zips logs + a diagnostics report into Documents. Returns the zip path.</summary>
    public static async Task<string> ExportAsync(AppHost host, MainWindow shell)
    {
        var report = await BuildReportAsync(host, shell);
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, $"Lumen-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("diagnostics.txt");
            using (var w = new StreamWriter(entry.Open())) w.Write(report);

            if (Directory.Exists(host.Paths.Logs))
            {
                foreach (var file in Directory.EnumerateFiles(host.Paths.Logs, "*", SearchOption.TopDirectoryOnly)
                             .OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
                {
                    try
                    {
                        // Logs may be open for writing: share read/write.
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = zip.CreateEntry("logs/" + Path.GetFileName(file)).Open();
                        src.CopyTo(dst);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not add {file} to the export.", ex);
                    }
                }
            }
        });
        Log.Info($"Logs exported to {zipPath}.");
        return zipPath;
    }
}
