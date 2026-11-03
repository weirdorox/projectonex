using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Power;
using Lumen.Platform.Windows.Power;

namespace Lumen.PowerHelper;

/// <summary>
/// Lumen.PowerHelper --user-sid S-1-5-21-... --data "C:\Users\me\AppData\Local\Lumen"
///
/// Serves one-line JSON requests (status / set / restore / ping) on a named pipe that only the given
/// user, SYSTEM and Administrators can open. Exits - restoring firmware limits first - when Lumen has
/// not contacted it for <see cref="IdleTimeout"/>.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static DateTime _lastContact = DateTime.UtcNow;

    private static int Main(string[] args)
    {
        var sid = Arg(args, "--user-sid");
        var data = Arg(args, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");
        Log.Initialize(Path.Combine(data, "logs"));
        Log.Info($"PowerHelper starting (elevated: {IsElevated()}, user {sid}).");

        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            Log.Error("PowerHelper needs --user-sid.");
            return 2;
        }

        using var single = new Mutex(true, "Local\\Lumen.PowerHelper." + sid, out var first);
        if (!first) return 0; // already running

        IntelTdpDriver? driver = null;
        string? unavailable = null;
        if (!IsElevated()) unavailable = "The Lumen power helper is not running with administrator rights. Re-enable real TDP control in Lumen Settings.";
        else driver = IntelTdpDriver.TryCreate(data, out unavailable);
        if (driver is null) Log.Warn($"TDP unavailable: {unavailable}");

        using var cts = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                lock (Gate)
                {
                    driver?.Tick();
                    if (DateTime.UtcNow - _lastContact > IdleTimeout)
                    {
                        if (driver?.HasActiveRequest == true)
                        {
                            Log.Warn("Lumen stopped talking to the helper - restoring firmware TDP limits.");
                            driver.Restore();
                        }
                        cts.Cancel();
                    }
                }
            }
        });

        try
        {
            ServeAsync(PowerHelperProtocol.PipeName(sid), new SecurityIdentifier(sid), driver, unavailable, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("PowerHelper failed.", ex);
            lock (Gate) driver?.Restore();
            return 1;
        }
        finally
        {
            driver?.Dispose();
            Log.Info("PowerHelper exiting.");
        }
        return 0;
    }

    private static async Task ServeAsync(string pipeName, SecurityIdentifier user, IntelTdpDriver? driver, string? unavailable, CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        while (!ct.IsCancellationRequested)
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 4096, security);
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var response = Handle(line, driver, unavailable);
                await writer.WriteLineAsync(PowerHelperProtocol.Serialize(response)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Log.Debug($"Pipe client error: {ex.Message}");
            }
        }
    }

    private static TdpStatus Handle(string? line, IntelTdpDriver? driver, string? unavailable)
    {
        lock (Gate)
        {
            _lastContact = DateTime.UtcNow;
            if (driver is null) return TdpStatus.Unavailable(unavailable ?? "TDP control is not available.", "Intel RAPL (PawnIO)");

            var request = PowerHelperProtocol.ParseRequest(line);
            var error = PowerHelperProtocol.Validate(request, driver.Range);
            if (error is not null) return driver.Status(error);

            try
            {
                return request!.Command switch
                {
                    PowerHelperRequest.Set => driver.Set(request.Watts!.Value),
                    PowerHelperRequest.Restore => driver.Restore(),
                    _ => driver.Status(),
                };
            }
            catch (Exception ex)
            {
                Log.Error("TDP request failed.", ex);
                return driver.Status($"The CPU rejected the request: {ex.Message}");
            }
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
