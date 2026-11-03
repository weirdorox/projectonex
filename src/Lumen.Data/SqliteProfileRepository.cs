using System.Globalization;
using Lumen.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace Lumen.Data;

/// <summary>Per-game performance profiles (schema v2). Saving an empty profile deletes it.</summary>
public sealed class SqliteProfileRepository : IProfileRepository
{
    private readonly LumenDatabase _db;

    public SqliteProfileRepository(LumenDatabase db) => _db = db;

    private const string Columns = "game_id, refresh_hz, power_plan, brightness, tdp_watts, updated_at";

    public Task<PerformanceProfile?> GetAsync(string gameId, CancellationToken ct = default) =>
        Task.Run<PerformanceProfile?>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM game_profiles WHERE game_id = $id";
            cmd.Parameters.AddWithValue("$id", gameId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }, ct);

    public Task<IReadOnlyList<PerformanceProfile>> GetAllAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PerformanceProfile>>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM game_profiles";
            using var r = cmd.ExecuteReader();
            var list = new List<PerformanceProfile>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }, ct);

    public Task SaveAsync(PerformanceProfile profile, CancellationToken ct = default)
    {
        if (profile.IsEmpty) return DeleteAsync(profile.GameId, ct);
        return Task.Run(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO game_profiles (game_id, refresh_hz, power_plan, brightness, tdp_watts, updated_at)
                VALUES ($id, $hz, $plan, $br, $tdp, $at)
                ON CONFLICT(game_id) DO UPDATE SET
                    refresh_hz = excluded.refresh_hz, power_plan = excluded.power_plan,
                    brightness = excluded.brightness, tdp_watts = excluded.tdp_watts,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", profile.GameId);
            cmd.Parameters.AddWithValue("$hz", (object?)profile.RefreshHz ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$plan", (object?)profile.PowerPlanId?.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$br", (object?)profile.BrightnessPercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tdp", (object?)profile.TdpWatts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", profile.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task DeleteAsync(string gameId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM game_profiles WHERE game_id = $id";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.ExecuteNonQuery();
        }, ct);

    private static PerformanceProfile Map(SqliteDataReader r) => new()
    {
        GameId = r.GetString(0),
        RefreshHz = r.IsDBNull(1) ? null : r.GetInt32(1),
        PowerPlanId = r.IsDBNull(2) || !Guid.TryParse(r.GetString(2), out var g) ? null : g,
        BrightnessPercent = r.IsDBNull(3) ? null : r.GetInt32(3),
        TdpWatts = r.IsDBNull(4) ? null : r.GetInt32(4),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
