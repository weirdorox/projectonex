using System.Text.Json;
using System.Text.Json.Serialization;
using Lumen.Core.Hardware;

namespace Lumen.Core.Power;

/// <summary>
/// One-line JSON protocol between Lumen (user) and Lumen.PowerHelper (elevated) over a named pipe
/// restricted to the signed-in user. The helper only accepts these commands; every value is validated
/// again on the elevated side.
/// </summary>
public sealed record PowerHelperRequest(string Command, double? Watts = null)
{
    public const string Status = "status";
    public const string Set = "set";
    public const string Restore = "restore";
    public const string Ping = "ping";
}

public static class PowerHelperProtocol
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PipeName(string userSid) => "Lumen.PowerHelper." + userSid;

    public static string Serialize(PowerHelperRequest r) => JsonSerializer.Serialize(r, Options);
    public static string Serialize(TdpStatus s) => JsonSerializer.Serialize(s, Options);

    public static PowerHelperRequest? ParseRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 512) return null;
        try { return JsonSerializer.Deserialize<PowerHelperRequest>(line, Options); }
        catch (JsonException) { return null; }
    }

    public static TdpStatus? ParseStatus(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize<TdpStatus>(line, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Elevated-side validation: known command, finite watts inside the detected range, whole watts.</summary>
    public static string? Validate(PowerHelperRequest? r, TdpRange? range)
    {
        if (r is null) return "Malformed request.";
        switch (r.Command)
        {
            case PowerHelperRequest.Status:
            case PowerHelperRequest.Restore:
            case PowerHelperRequest.Ping:
                return null;
            case PowerHelperRequest.Set:
                if (r.Watts is not { } w || double.IsNaN(w) || double.IsInfinity(w)) return "Missing wattage.";
                if (range is null) return "TDP range is unknown.";
                if (!range.Contains(w)) return $"{w:0.#} W is outside the supported range {range.MinWatts:0.#}-{range.MaxWatts:0.#} W.";
                return null;
            default:
                return $"Unknown command '{r.Command}'.";
        }
    }
}
