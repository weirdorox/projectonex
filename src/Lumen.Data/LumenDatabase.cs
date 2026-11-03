using Lumen.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Lumen.Data;

/// <summary>
/// Owns the SQLite file and its schema. Migrations are forward-only and idempotent;
/// each runs in a transaction and bumps <c>PRAGMA user_version</c>.
/// </summary>
public sealed class LumenDatabase
{
    private readonly string _connectionString;

    public LumenDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public const int CurrentVersion = 2;

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Creates or upgrades the schema. Safe to call on every start.</summary>
    public void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        var version = GetUserVersion(c);
        if (version > CurrentVersion)
        {
            // Downgrade (older Lumen on newer DB): don't touch it.
            Log.Warn($"Database schema v{version} is newer than this build (v{CurrentVersion}); opening read/write without migration.");
            return;
        }

        for (var v = version + 1; v <= CurrentVersion; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v - 1] + $"\nPRAGMA user_version = {v};";
            cmd.ExecuteNonQuery();
            tx.Commit();
            Log.Info($"Database migrated to v{v}.");
        }
    }

    private static int GetUserVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE IF NOT EXISTS games (
            id               TEXT PRIMARY KEY,
            source           INTEGER NOT NULL,
            external_id      TEXT NOT NULL,
            title            TEXT NOT NULL,
            launch_kind      INTEGER NOT NULL,
            launch_target    TEXT NOT NULL,
            launch_args      TEXT NULL,
            launch_workdir   TEXT NULL,
            install_path     TEXT NULL,
            art_cover        TEXT NULL,
            art_hero         TEXT NULL,
            art_logo         TEXT NULL,
            art_icon         TEXT NULL,
            is_installed     INTEGER NOT NULL DEFAULT 1,
            is_favorite      INTEGER NOT NULL DEFAULT 0,
            is_hidden        INTEGER NOT NULL DEFAULT 0,
            last_played      TEXT NULL,
            play_count       INTEGER NOT NULL DEFAULT 0,
            playtime_seconds INTEGER NOT NULL DEFAULT 0,
            custom_cover     TEXT NULL,
            added_at         TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_games_last_played ON games(last_played);
        CREATE INDEX IF NOT EXISTS ix_games_source ON games(source);
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NULL
        );
        """,
        // v2: per-game performance profiles
        """
        CREATE TABLE IF NOT EXISTS game_profiles (
            game_id     TEXT PRIMARY KEY,
            refresh_hz  INTEGER NULL,
            power_plan  TEXT NULL,
            brightness  INTEGER NULL,
            tdp_watts   INTEGER NULL,
            updated_at  TEXT NOT NULL
        );
        """,
    ];
}
