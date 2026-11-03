using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using static Lumen.Platform.Windows.Native.NativeMethods;

namespace Lumen.Platform.Windows.Hardware;

/// <summary>
/// CPU, GPU, RAM and ACPI thermal-zone readings using documented, non-privileged sources:
/// - CPU: GetSystemTimes deltas
/// - RAM: GlobalMemoryStatusEx
/// - GPU: "GPU Engine" performance counters (3D engines, summed per adapter, clamped) - the same
///   source Task Manager uses
/// - Thermal: "Thermal Zone Information" counters (ACPI zone, approximate; NOT CPU die temperature)
///
/// CPU/GPU die temperatures are intentionally not reported: Windows has no documented API for them
/// and the common workaround (kernel driver such as WinRing0) is flagged as vulnerable.
/// Sample only while the overlay is visible to keep the shell's own footprint low.
/// </summary>
public sealed class WindowsSystemMonitor : ISystemMonitor
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private List<PerformanceCounter> _gpuCounters = [];
    private List<PerformanceCounter> _thermalCounters = [];
    private DateTime _gpuRefreshedAt = DateTime.MinValue;
    private bool _gpuAvailable = true;
    private bool _thermalAvailable = true;

    public SystemSnapshot Sample()
    {
        var cpu = SampleCpu();
        long? used = null, total = null;
        var mem = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            total = (long)mem.ullTotalPhys;
            used = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
        }

        return new SystemSnapshot(
            DateTimeOffset.Now,
            cpu,
            SampleGpu(),
            used,
            total,
            SampleThermal(),
            CpuCelsius: null,
            GpuCelsius: null);
    }

    private double? SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return null;
        ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
        double? result = null;
        if (_prevKernel != 0)
        {
            var dIdle = idle - _prevIdle;
            var dTotal = (kernel - _prevKernel) + (user - _prevUser); // kernel time includes idle
            if (dTotal > 0) result = Math.Clamp(100.0 * (1.0 - (double)dIdle / dTotal), 0, 100);
        }
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        return result;
    }

    private double? SampleGpu()
    {
        if (!_gpuAvailable) return null;
        try
        {
            // Engine instances appear/disappear with processes; refresh the list periodically.
            if (DateTime.UtcNow - _gpuRefreshedAt > TimeSpan.FromSeconds(10))
            {
                foreach (var c in _gpuCounters) c.Dispose();
                var category = new PerformanceCounterCategory("GPU Engine");
                _gpuCounters = category.GetInstanceNames()
                    .Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                    .ToList();
                foreach (var c in _gpuCounters) _ = c.NextValue(); // prime
                _gpuRefreshedAt = DateTime.UtcNow;
                return null;
            }

            double sum = 0;
            foreach (var c in _gpuCounters)
            {
                try { sum += c.NextValue(); }
                catch (InvalidOperationException) { /* instance went away */ }
            }
            return Math.Clamp(sum, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Info($"GPU counters unavailable: {ex.Message}");
            _gpuAvailable = false;
            return null;
        }
    }

    private double? SampleThermal()
    {
        if (!_thermalAvailable) return null;
        try
        {
            if (_thermalCounters.Count == 0)
            {
                if (!PerformanceCounterCategory.Exists("Thermal Zone Information"))
                {
                    _thermalAvailable = false;
                    return null;
                }
                var category = new PerformanceCounterCategory("Thermal Zone Information");
                _thermalCounters = category.GetInstanceNames()
                    .Select(n => new PerformanceCounter("Thermal Zone Information", "Temperature", n, readOnly: true))
                    .ToList();
                if (_thermalCounters.Count == 0) { _thermalAvailable = false; return null; }
            }

            double? max = null;
            foreach (var c in _thermalCounters)
            {
                var kelvin = c.NextValue();
                if (kelvin <= 0) continue;
                var celsius = kelvin - 273.15;
                if (celsius is > 0.0 and < 130.0) max = max is null ? celsius : Math.Max(max.Value, celsius);
            }
            return max is null ? null : Math.Round(max.Value, 1);
        }
        catch (Exception ex)
        {
            Log.Info($"Thermal zone counters unavailable: {ex.Message}");
            _thermalAvailable = false;
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var c in _gpuCounters) c.Dispose();
        foreach (var c in _thermalCounters) c.Dispose();
        _gpuCounters.Clear();
        _thermalCounters.Clear();
    }
}
