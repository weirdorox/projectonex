using System.Globalization;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lumen.Data;

public sealed class SqliteGameRepository : IGameRepository
{
    private readonly LumenDatabase _db;

    public SqliteGameRepository(LumenDatabase db) => _db = db;

    private const string SelectColumns =
        "id, source, external_id, title, launch_kind, launch_target, launch_args, launch_workdir, install_path, " +
        "art_cover, art_hero, art_logo, art_icon, is_installed, is_favorite, is_hidden, last_played, play_count, " +
        "playtime_seconds, custom_cover, added_at";

    public Task<IReadOnlyList<GameEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM games";
            using var r = cmd.ExecuteReader();
            var list = new List<GameEntry>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }, ct);

    public Task<GameEntry?> GetAsync(string id, CancellationToken ct = default) =>
        Task.Run<GameEntry?>(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM games WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }, ct);

    public Task UpsertAsync(GameEntry game, CancellationToken ct = default) => UpsertManyAsync([game], ct);

    public Task UpsertManyAsync(IEnumerable<GameEntry> games, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO games (id, source, external_id, title, launch_kind, launch_target, launch_args, launch_workdir,
                                   install_path, art_cover, art_hero, art_logo, art_icon, is_installed, is_favorite, is_hidden,
                                   last_played, play_count, playtime_seconds, custom_cover, added_at)
                VALUES ($id, $source, $external_id, $title, $launch_kind, $launch_target, $launch_args, $launch_workdir,
                        $install_path, $art_cover, $art_hero, $art_logo, $art_icon, $is_installed, $is_favorite, $is_hidden,
                        $last_played, $play_count, $playtime_seconds, $custom_cover, $added_at)
                ON CONFLICT(id) DO UPDATE SET
                    source = excluded.source, external_id = excluded.external_id, title = excluded.title,
                    launch_kind = excluded.launch_kind, launch_target = excluded.launch_target,
                    launch_args = excluded.launch_args, launch_workdir = excluded.launch_workdir,
                    install_path = excluded.install_path, art_cover = excluded.art_cover, art_hero = excluded.art_hero,
                    art_logo = excluded.art_logo, art_icon = excluded.art_icon, is_installed = excluded.is_installed,
                    is_favorite = excluded.is_favorite, is_hidden = excluded.is_hidden, last_played = excluded.last_played,
                    play_count = excluded.play_count, playtime_seconds = excluded.playtime_seconds,
                    custom_cover = excluded.custom_cover;
                """;

            string[] names =
            [
                "$id", "$source", "$external_id", "$title", "$launch_kind", "$launch_target", "$launch_args", "$launch_workdir",
                "$install_path", "$art_cover", "$art_hero", "$art_logo", "$art_icon", "$is_installed", "$is_favorite", "$is_hidden",
                "$last_played", "$play_count", "$playtime_seconds", "$custom_cover", "$added_at",
            ];
            foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, null));

            foreach (var g in games)
            {
                ct.ThrowIfCancellationRequested();
                object?[] values =
                [
                    g.Id, (int)g.Source, g.ExternalId, g.Title, (int)g.Launch.Kind, g.Launch.Target, g.Launch.Arguments,
                    g.Launch.WorkingDirectory, g.InstallPath, g.Artwork.Cover, g.Artwork.Hero, g.Artwork.Logo, g.Artwork.Icon,
                    g.IsInstalled ? 1 : 0, g.IsFavorite ? 1 : 0, g.IsHidden ? 1 : 0, ToDb(g.LastPlayed), g.PlayCount,
                    g.PlaytimeSeconds, g.CustomCover, ToDb(g.AddedAt),
                ];
                for (var i = 0; i < values.Length; i++)
                    cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }, ct);

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default) =>
        Exec("UPDATE games SET is_favorite = $v WHERE id = $id", id, favorite ? 1 : 0, ct);

    public Task SetHiddenAsync(string id, bool hidden, CancellationToken ct = default) =>
        Exec("UPDATE games SET is_hidden = $v WHERE id = $id", id, hidden ? 1 : 0, ct);

    public Task RecordLaunchAsync(string id, DateTimeOffset when, CancellationToken ct = default) =>
        Exec("UPDATE games SET last_played = $v, play_count = play_count + 1 WHERE id = $id", id, ToDb(when), ct);

    public Task AddPlaytimeAsync(string id, TimeSpan duration, CancellationToken ct = default) =>
        Exec("UPDATE games SET playtime_seconds = playtime_seconds + $v WHERE id = $id", id, (long)Math.Max(0, duration.TotalSeconds), ct);

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM games WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }, ct);

    private Task Exec(string sql, string id, object? value, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }, ct);

    private static string? ToDb(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? FromDb(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static GameEntry Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Source = (GameSource)r.GetInt32(1),
        ExternalId = r.GetString(2),
        Title = r.GetString(3),
        Launch = new LaunchSpec((LaunchKind)r.GetInt32(4), r.GetString(5), Str(r, 6), Str(r, 7)),
        InstallPath = Str(r, 8),
        Artwork = new ArtworkSet(Str(r, 9), Str(r, 10), Str(r, 11), Str(r, 12)),
        IsInstalled = r.GetInt32(13) != 0,
        IsFavorite = r.GetInt32(14) != 0,
        IsHidden = r.GetInt32(15) != 0,
        LastPlayed = FromDb(r, 16),
        PlayCount = r.GetInt32(17),
        PlaytimeSeconds = r.GetInt64(18),
        CustomCover = Str(r, 19),
        AddedAt = FromDb(r, 20) ?? DateTimeOffset.UtcNow,
    };
}
