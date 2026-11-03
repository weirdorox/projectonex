using System.Globalization;
using Lumen.Core.Activity;
using Lumen.Core.Library;
using Microsoft.Data.Sqlite;

namespace Lumen.Data;

internal static class Db
{
    public static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset Time(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public static object Value(object? v) => v ?? DBNull.Value;
}

public sealed class SqliteCollectionRepository : ICollectionRepository
{
    private readonly LumenDatabase _db;
    public SqliteCollectionRepository(LumenDatabase db) => _db = db;

    public Task<IReadOnlyList<GameCollection>> GetAllAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GameCollection>>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, name, sort_order, created_at FROM collections ORDER BY sort_order, name";
            using var r = cmd.ExecuteReader();
            var list = new List<GameCollection>();
            while (r.Read()) list.Add(new GameCollection(r.GetString(0), r.GetString(1), r.GetInt32(2), Db.Time(r.GetString(3))));
            return list;
        }, ct);

    public Task SaveAsync(GameCollection collection, CancellationToken ct = default) =>
        Exec("""
             INSERT INTO collections (id, name, sort_order, created_at) VALUES ($a, $b, $c, $d)
             ON CONFLICT(id) DO UPDATE SET name = excluded.name, sort_order = excluded.sort_order
             """, ct, collection.Id, collection.Name, collection.SortOrder, Db.Time(collection.CreatedAt));

    public Task DeleteAsync(string collectionId, CancellationToken ct = default) =>
        Exec("DELETE FROM collection_games WHERE collection_id = $a; DELETE FROM collections WHERE id = $a;", ct, collectionId);

    public Task<IReadOnlyList<string>> GetGameIdsAsync(string collectionId, CancellationToken ct = default) =>
        Strings("SELECT game_id FROM collection_games WHERE collection_id = $a", collectionId, ct);

    public Task<IReadOnlyList<string>> GetCollectionIdsForGameAsync(string gameId, CancellationToken ct = default) =>
        Strings("SELECT collection_id FROM collection_games WHERE game_id = $a", gameId, ct);

    public Task SetMembershipAsync(string collectionId, string gameId, bool member, CancellationToken ct = default) =>
        member
            ? Exec("INSERT OR IGNORE INTO collection_games (collection_id, game_id) VALUES ($a, $b)", ct, collectionId, gameId)
            : Exec("DELETE FROM collection_games WHERE collection_id = $a AND game_id = $b", ct, collectionId, gameId);

    private Task<IReadOnlyList<string>> Strings(string sql, string arg, CancellationToken ct) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$a", arg);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }, ct);

    private Task Exec(string sql, CancellationToken ct, params object?[] args) =>
        Task.Run(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (char)('a' + i), Db.Value(args[i]));
            cmd.ExecuteNonQuery();
        }, ct);
}

public sealed class SqlitePlaySessionRepository : IPlaySessionRepository
{
    private readonly LumenDatabase _db;
    public SqlitePlaySessionRepository(LumenDatabase db) => _db = db;

    private const string Columns = "game_id, started_at, ended_at, on_battery, avg_fps, fps_measured, avg_package_w, battery_used_pct";

    public Task AddAsync(PlaySession s, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO play_sessions ({Columns}) VALUES ($a, $b, $c, $d, $e, $f, $g, $h)";
            object?[] values = [s.GameId, Db.Time(s.StartedAt), Db.Time(s.EndedAt), s.OnBattery ? 1 : 0, s.AvgFps, s.FpsMeasured ? 1 : 0, s.AvgPackageWatts, s.BatteryPercentUsed];
            for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$" + (char)('a' + i), Db.Value(values[i]));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<PlaySession>> GetForGameAsync(string gameId, int max = 200, CancellationToken ct = default) =>
        Query($"SELECT {Columns} FROM play_sessions WHERE game_id = $id ORDER BY started_at DESC LIMIT $max", gameId, max, ct);

    public Task<IReadOnlyList<PlaySession>> GetRecentAsync(int max = 200, CancellationToken ct = default) =>
        Query($"SELECT {Columns} FROM play_sessions ORDER BY started_at DESC LIMIT $max", null, max, ct);

    private Task<IReadOnlyList<PlaySession>> Query(string sql, string? gameId, int max, CancellationToken ct) =>
        Task.Run<IReadOnlyList<PlaySession>>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            if (gameId is not null) cmd.Parameters.AddWithValue("$id", gameId);
            cmd.Parameters.AddWithValue("$max", max);
            using var r = cmd.ExecuteReader();
            var list = new List<PlaySession>();
            while (r.Read())
            {
                list.Add(new PlaySession(
                    r.GetString(0), Db.Time(r.GetString(1)), Db.Time(r.GetString(2)), r.GetInt32(3) != 0,
                    r.IsDBNull(4) ? null : r.GetDouble(4),
                    r.IsDBNull(6) ? null : r.GetDouble(6),
                    r.IsDBNull(7) ? null : r.GetDouble(7),
                    r.GetInt32(5) != 0));
            }
            return list;
        }, ct);
}
