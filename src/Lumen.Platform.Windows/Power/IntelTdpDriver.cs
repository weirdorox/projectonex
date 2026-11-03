using System.Text.Json;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Power;
using Microsoft.Win32;

namespace Lumen.Platform.Windows.Power;

/// <summary>
/// Intel package power limits (RAPL) through PawnIO's signed IntelMSR module. Runs only in the
/// elevated helper.
///
/// Safety rules:
/// - The range comes from the CPU (MSR 0x614) or the firmware's own boot-time limits; nothing above it
///   is ever written.
/// - The firmware lock bit is honoured (read-only when set) and never cleared.
/// - Only the PL1/PL2 power fields are changed; time windows/clamp bits are preserved.
/// - The boot-time value is saved and restored on request, on helper idle-exit, and by a reboot.
/// - If firmware (e.g. Intel DTT) keeps rewriting the limit, Lumen stops re-applying after 3 tries
///   and reports it instead of fighting the firmware.
/// </summary>
public sealed class IntelTdpDriver : IDisposable
{
    private const string Backend = "Intel RAPL (PawnIO)";
    private const int MaxReapplies = 3;

    private readonly PawnIo _io;
    private readonly RaplUnits _units;
    private readonly PackagePowerInfo _info;
    private readonly ulong _original;
    private readonly PackagePowerLimit _default;
    private readonly TdpRange _range;
    private readonly string _cpu;
    private double? _requested;
    private int _overrides;
    private bool _firmwareOverride;
    private ulong? _lastEnergy;
    private DateTime _lastEnergyAt;
    private double? _packageWatts;

    private IntelTdpDriver(PawnIo io, string dataDirectory, string cpu)
    {
        _io = io;
        _cpu = cpu;
        _units = IntelRapl.DecodeUnits(io.ReadMsr(IntelRapl.MsrRaplPowerUnit));
        _info = IntelRapl.DecodeInfo(io.ReadMsr(IntelRapl.MsrPkgPowerInfo), _units);
        _original = LoadOrCaptureOriginal(dataDirectory, io.ReadMsr(IntelRapl.MsrPkgPowerLimit));
        _default = IntelRapl.DecodeLimit(_original, _units);
        _range = IntelRapl.ComputeRange(_info, _default);
        Log.Info($"Intel RAPL: {cpu}; units {_units}; info {_info}; firmware default {_default}; range {_range}.");
    }

    public static IntelTdpDriver? TryCreate(string dataDirectory, out string? error)
    {
        string vendor = "", cpu = "Unknown CPU";
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            vendor = key?.GetValue("VendorIdentifier") as string ?? "";
            cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpu;
        }
        catch { /* ignore */ }

        if (!vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{cpu}: only Intel package power limits are implemented in this version (no AMD backend yet).";
            return null;
        }

        var io = PawnIo.TryOpen("IntelMSR.bin", dataDirectory, out error);
        if (io is null) return null;
        try
        {
            return new IntelTdpDriver(io, dataDirectory, cpu);
        }
        catch (Exception ex)
        {
            io.Dispose();
            error = $"Reading the CPU power registers failed: {ex.Message}";
            return null;
        }
    }

    public TdpStatus Status(string? message = null)
    {
        var current = IntelRapl.DecodeLimit(_io.ReadMsr(IntelRapl.MsrPkgPowerLimit), _units);
        double? celsius = null;
        try { celsius = IntelRapl.PackageCelsius(_io.ReadMsr(IntelRapl.MsrPackageThermStatus), _io.ReadMsr(IntelRapl.MsrTemperatureTarget)); }
        catch { /* optional */ }

        return new TdpStatus
        {
            Available = true,
            Backend = Backend,
            Cpu = _cpu,
            MinWatts = _range.MinWatts,
            MaxWatts = _range.MaxWatts,
            RangeSource = _range.Source,
            RequestedWatts = _requested,
            Pl1Watts = current.Pl1Watts,
            Pl2Watts = current.Pl2Watts,
            DefaultPl1Watts = _default.Pl1Watts,
            DefaultPl2Watts = _default.Pl2Watts,
            PackageWatts = _packageWatts is { } w ? Math.Round(w, 1) : null,
            PackageCelsius = celsius,
            Locked = current.Locked,
            FirmwareOverride = _firmwareOverride,
            Message = message ?? (current.Locked ? "The firmware has locked the power limits (read-only)." : null),
        };
    }

    public TdpRange Range => _range;

    public TdpStatus Set(double watts)
    {
        var raw = _io.ReadMsr(IntelRapl.MsrPkgPowerLimit);
        if (IntelRapl.DecodeLimit(raw, _units).Locked)
            return Status("The firmware has locked the power limits; they cannot be changed.");
        if (!_range.Contains(watts))
            return Status($"{watts:0.#} W is outside the supported range {_range.MinWatts:0.#}-{_range.MaxWatts:0.#} W.");

        // Handheld-style sustained limit: PL1 = PL2 = target (like SteamOS), never above the CPU maximum.
        var updated = IntelRapl.EncodeLimit(raw, watts, watts, _units);
        _io.WriteMsr(IntelRapl.MsrPkgPowerLimit, updated);
        var readBack = IntelRapl.DecodeLimit(_io.ReadMsr(IntelRapl.MsrPkgPowerLimit), _units);

        _requested = watts;
        _overrides = 0;
        _firmwareOverride = false;
        var ok = Math.Abs(readBack.Pl1Watts - watts) <= _units.PowerWatts * 2;
        Log.Info($"TDP set to {watts} W: read back PL1 {readBack.Pl1Watts} W / PL2 {readBack.Pl2Watts} W ({(ok ? "ok" : "mismatch")}).");
        return Status(ok
            ? $"Applied {watts:0.#} W."
            : $"The CPU reports {readBack.Pl1Watts:0.#} W after writing {watts:0.#} W - the firmware may be limiting it.");
    }

    public TdpStatus Restore()
    {
        var raw = _io.ReadMsr(IntelRapl.MsrPkgPowerLimit);
        _requested = null;
        _firmwareOverride = false;
        if (IntelRapl.DecodeLimit(raw, _units).Locked) return Status("Locked by firmware - nothing to restore.");
        if (raw != _original) _io.WriteMsr(IntelRapl.MsrPkgPowerLimit, _original);
        Log.Info("TDP restored to firmware defaults.");
        return Status("Firmware default limits restored.");
    }

    /// <summary>Called about once per second: measures package power and keeps the requested limit applied.</summary>
    public void Tick()
    {
        try
        {
            var energy = _io.ReadMsr(IntelRapl.MsrPkgEnergyStatus);
            var now = DateTime.UtcNow;
            if (_lastEnergy is { } last)
                _packageWatts = IntelRapl.PackageWatts(last, energy, now - _lastEnergyAt, _units);
            _lastEnergy = energy;
            _lastEnergyAt = now;

            if (_requested is not { } target || _firmwareOverride) return;
            var raw = _io.ReadMsr(IntelRapl.MsrPkgPowerLimit);
            var current = IntelRapl.DecodeLimit(raw, _units);
            if (Math.Abs(current.Pl1Watts - target) <= _units.PowerWatts * 2) return;

            if (++_overrides > MaxReapplies)
            {
                _firmwareOverride = true;
                Log.Warn($"Firmware keeps resetting PL1 (now {current.Pl1Watts} W, wanted {target} W); no longer re-applying.");
                return;
            }
            _io.WriteMsr(IntelRapl.MsrPkgPowerLimit, IntelRapl.EncodeLimit(raw, target, target, _units));
            Log.Info($"PL1 drifted to {current.Pl1Watts} W; re-applied {target} W ({_overrides}/{MaxReapplies}).");
        }
        catch (Exception ex)
        {
            Log.Warn("TDP tick failed.", ex);
        }
    }

    public bool HasActiveRequest => _requested is not null;

    private static ulong LoadOrCaptureOriginal(string dataDirectory, ulong current)
    {
        var file = Path.Combine(dataDirectory, "tdp-original.json");
        var boot = BootId();
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<IntelOriginalLimit>(File.ReadAllText(file)) is { } saved && saved.Boot == boot)
                return saved.Value; // helper restarted during this boot: keep the true firmware value
        }
        catch { /* re-capture */ }

        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(file, JsonSerializer.Serialize(new IntelOriginalLimit(boot, current)));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not persist the firmware power limit.", ex);
        }
        return current;
    }

    /// <summary>Boot time rounded to 5 minutes - identifies "this boot" (limits reset on reboot).</summary>
    private static string BootId()
    {
        var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var rounded = new DateTime(boot.Ticks - boot.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc);
        return rounded.ToString("O");
    }

    public void Dispose() => _io.Dispose();
}

internal sealed record IntelOriginalLimit(string Boot, ulong Value);
