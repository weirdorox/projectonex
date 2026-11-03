using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Microsoft.Win32;

namespace Lumen.Platform.Windows.Power;

/// <summary>
/// Thin wrapper over PawnIOLib.dll (https://pawnio.eu) - the signed, open-source I/O driver that
/// replaced WinRing0 and works with Memory Integrity enabled. Lumen does not ship or install the
/// driver: the user installs PawnIO, and Lumen loads the official signed module blob (IntelMSR.bin).
///
/// The PawnIO device only admits SYSTEM and Administrators, so this class is used exclusively by the
/// elevated Lumen.PowerHelper process.
/// </summary>
public sealed class PawnIo : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenFn(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadFn(IntPtr handle, byte[] blob, UIntPtr size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ExecuteFn(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returnSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseFn(IntPtr handle);

    private readonly IntPtr _library;
    private readonly ExecuteFn _execute;
    private readonly CloseFn _close;
    private IntPtr _handle;

    private PawnIo(IntPtr library, IntPtr handle, ExecuteFn execute, CloseFn close)
    {
        _library = library;
        _handle = handle;
        _execute = execute;
        _close = close;
    }

    public static IEnumerable<string> LibraryCandidates()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "PawnIO", "PawnIOLib.dll");
        string? installLocation = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            installLocation = key?.GetValue("InstallLocation") as string;
        }
        catch { /* ignore */ }
        if (!string.IsNullOrWhiteSpace(installLocation)) yield return Path.Combine(installLocation, "PawnIOLib.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "PawnIOLib.dll");
    }

    public static string? FindLibrary() => LibraryCandidates().FirstOrDefault(File.Exists);

    public static IEnumerable<string> ModuleCandidates(string moduleFile, string? dataDirectory)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "pawnio", moduleFile);
        if (dataDirectory is not null) yield return Path.Combine(dataDirectory, "pawnio", moduleFile);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "PawnIO", "modules", moduleFile);
    }

    /// <summary>Opens the driver and loads a module. Returns null with a human-readable reason on failure.</summary>
    public static PawnIo? TryOpen(string moduleFile, string? dataDirectory, out string? error)
    {
        error = null;
        var libPath = FindLibrary();
        if (libPath is null)
        {
            error = "PawnIO is not installed. Install it from https://pawnio.eu (signed, open source), then try again.";
            return null;
        }
        var modulePath = ModuleCandidates(moduleFile, dataDirectory).FirstOrDefault(File.Exists);
        if (modulePath is null)
        {
            error = $"The signed PawnIO module {moduleFile} was not found. Place it in {Path.Combine(dataDirectory ?? AppContext.BaseDirectory, "pawnio")} (from the official PawnIO.Modules release).";
            return null;
        }

        if (!NativeLibrary.TryLoad(libPath, out var lib))
        {
            error = $"Could not load {libPath}.";
            return null;
        }

        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenFn>(NativeLibrary.GetExport(lib, "pawnio_open"));
            var load = Marshal.GetDelegateForFunctionPointer<LoadFn>(NativeLibrary.GetExport(lib, "pawnio_load"));
            var execute = Marshal.GetDelegateForFunctionPointer<ExecuteFn>(NativeLibrary.GetExport(lib, "pawnio_execute"));
            var close = Marshal.GetDelegateForFunctionPointer<CloseFn>(NativeLibrary.GetExport(lib, "pawnio_close"));

            var hr = open(out var handle);
            if (hr < 0 || handle == IntPtr.Zero)
            {
                error = $"Could not open the PawnIO driver (0x{hr:X8}). It needs administrator rights and an installed, running driver.";
                NativeLibrary.Free(lib);
                return null;
            }

            var blob = File.ReadAllBytes(modulePath);
            hr = load(handle, blob, (UIntPtr)blob.Length);
            if (hr < 0)
            {
                close(handle);
                NativeLibrary.Free(lib);
                error = hr == unchecked((int)0x80070032) // HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)
                    ? "This CPU is not supported by the PawnIO module."
                    : $"PawnIO refused the module {moduleFile} (0x{hr:X8}). Use the official signed release.";
                return null;
            }

            Log.Info($"PawnIO opened ({libPath}), module {modulePath} loaded.");
            return new PawnIo(lib, handle, execute, close);
        }
        catch (Exception ex)
        {
            NativeLibrary.Free(lib);
            error = $"PawnIO error: {ex.Message}";
            return null;
        }
    }

    public ulong ReadMsr(uint msr)
    {
        var output = new ulong[1];
        var hr = _execute(_handle, "ioctl_read_msr", [msr], (UIntPtr)1, output, (UIntPtr)1, out _);
        if (hr < 0) throw new IOException($"MSR 0x{msr:X} read failed (0x{hr:X8}).");
        return output[0];
    }

    public void WriteMsr(uint msr, ulong value)
    {
        var hr = _execute(_handle, "ioctl_write_msr", [msr, value], (UIntPtr)2, [], UIntPtr.Zero, out _);
        if (hr < 0) throw new IOException($"MSR 0x{msr:X} write failed (0x{hr:X8}).");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            _close(_handle);
            _handle = IntPtr.Zero;
            NativeLibrary.Free(_library);
        }
    }
}
