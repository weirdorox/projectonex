using Lumen.Core.Models;
using Lumen.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace Lumen.Data.Tests;

public sealed class ProfileRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumen-prof-" + Guid.NewGuid().ToString("N"));

    public ProfileRepositoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private LumenDatabase NewDb()
    {
        var db = new LumenDatabase(Path.Combine(_dir, "t.db"));
        db.Migrate();
        return db;
    }

    [Fact]
    public async Task Save_get_update_delete_round_trip()
    {
        var repo = new SqliteProfileRepository(NewDb());
        var plan = Guid.NewGuid();
        await repo.SaveAsync(new PerformanceProfile { GameId = "steam:1", RefreshHz = 90, PowerPlanId = plan, BrightnessPercent = 55 });

        var p = await repo.GetAsync("steam:1");
        Assert.NotNull(p);
        Assert.Equal(90, p!.RefreshHz);
        Assert.Equal(plan, p.PowerPlanId);
        Assert.Equal(55, p.BrightnessPercent);
        Assert.Null(p.TdpWatts);

        await repo.SaveAsync(p with { RefreshHz = 60, PowerPlanId = null });
        p = await repo.GetAsync("steam:1");
        Assert.Equal(60, p!.RefreshHz);
        Assert.Null(p.PowerPlanId);
        Assert.Single(await repo.GetAllAsync());

        await repo.SaveAsync(new PerformanceProfile { GameId = "steam:1" }); // empty => delete
        Assert.Null(await repo.GetAsync("steam:1"));

        await repo.SaveAsync(new PerformanceProfile { GameId = "steam:2", RefreshHz = 120 });
        await repo.DeleteAsync("steam:2");
        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task V1_database_upgrades_in_place_and_keeps_games()
    {
        var path = Path.Combine(_dir, "old.db");
        // Build a v1 database by hand.
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE games (id TEXT PRIMARY KEY, source INTEGER NOT NULL, external_id TEXT NOT NULL, title TEXT NOT NULL,
                    launch_kind INTEGER NOT NULL, launch_target TEXT NOT NULL, launch_args TEXT NULL, launch_workdir TEXT NULL,
                    install_path TEXT NULL, art_cover TEXT NULL, art_hero TEXT NULL, art_logo TEXT NULL, art_icon TEXT NULL,
                    is_installed INTEGER NOT NULL DEFAULT 1, is_favorite INTEGER NOT NULL DEFAULT 0, is_hidden INTEGER NOT NULL DEFAULT 0,
                    last_played TEXT NULL, play_count INTEGER NOT NULL DEFAULT 0, playtime_seconds INTEGER NOT NULL DEFAULT 0,
                    custom_cover TEXT NULL, added_at TEXT NOT NULL);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NULL);
                INSERT INTO games (id, source, external_id, title, launch_kind, launch_target, is_favorite, added_at)
                VALUES ('steam:620', 1, '620', 'Portal 2', 0, 'steam://rungameid/620', 1, '2026-01-01T00:00:00.0000000+00:00');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new LumenDatabase(path);
        db.Migrate();

        var game = await new SqliteGameRepository(db).GetAsync("steam:620");
        Assert.NotNull(game);
        Assert.True(game!.IsFavorite);
        Assert.Equal(GameSource.Steam, game.Source);

        var profiles = new SqliteProfileRepository(db);
        await profiles.SaveAsync(new PerformanceProfile { GameId = "steam:620", RefreshHz = 60 });
        Assert.Equal(60, (await profiles.GetAsync("steam:620"))!.RefreshHz);
    }
}

public sealed class ActivityRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumen-act-" + Guid.NewGuid().ToString("N"));
    private readonly LumenDatabase _db;

    public ActivityRepositoryTests()
    {
        _db = new LumenDatabase(Path.Combine(_dir, "a.db"));
        _db.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Collections_and_membership()
    {
        var repo = new SqliteCollectionRepository(_db);
        var a = Lumen.Core.Library.GameCollection.Create("RPGs", 1);
        var b = Lumen.Core.Library.GameCollection.Create("Arcade", 0);
        await repo.SaveAsync(a);
        await repo.SaveAsync(b);
        await repo.SaveAsync(a with { Name = "Role-playing" });
        Assert.Equal(new[] { "Arcade", "Role-playing" }, (await repo.GetAllAsync()).Select(c => c.Name).ToArray());

        await repo.SetMembershipAsync(a.Id, "steam:1", true);
        await repo.SetMembershipAsync(a.Id, "steam:1", true); // idempotent
        await repo.SetMembershipAsync(b.Id, "steam:1", true);
        Assert.Single(await repo.GetGameIdsAsync(a.Id));
        Assert.Equal(2, (await repo.GetCollectionIdsForGameAsync("steam:1")).Count);

        await repo.DeleteAsync(a.Id);
        Assert.Equal(new[] { b.Id }, (await repo.GetCollectionIdsForGameAsync("steam:1")).ToArray());
    }

    [Fact]
    public async Task Play_sessions_round_trip_newest_first()
    {
        var repo = new SqlitePlaySessionRepository(_db);
        var t = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
        await repo.AddAsync(new Lumen.Core.Activity.PlaySession("steam:1", t, t.AddHours(1), true, 58.5, 14.2, 21, FpsMeasured: true));
        await repo.AddAsync(new Lumen.Core.Activity.PlaySession("steam:1", t.AddDays(1), t.AddDays(1).AddMinutes(30), false));
        await repo.AddAsync(new Lumen.Core.Activity.PlaySession("steam:2", t, t.AddMinutes(5), false));

        var list = await repo.GetForGameAsync("steam:1");
        Assert.Equal(2, list.Count);
        Assert.Equal(t.AddDays(1), list[0].StartedAt);
        var first = list[1];
        Assert.True(first.OnBattery && first.FpsMeasured);
        Assert.Equal(58.5, first.AvgFps);
        Assert.Equal(14.2, first.AvgPackageWatts);
        Assert.Equal(21, first.BatteryPercentUsed);
        Assert.Equal(TimeSpan.FromHours(1), first.Duration);
        Assert.Equal(3, (await repo.GetRecentAsync()).Count);
    }
}
