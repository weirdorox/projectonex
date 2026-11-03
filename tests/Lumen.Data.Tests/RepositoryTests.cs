using Lumen.Core;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lumen.Data.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumen-data-" + Guid.NewGuid().ToString("N"));
    private readonly LumenDatabase _db;

    public RepositoryTests()
    {
        _db = new LumenDatabase(Path.Combine(_dir, "test.db"));
        _db.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static GameEntry Game(string id, string title = "Game") => new()
    {
        Id = GameEntry.MakeId(GameSource.Steam, id),
        Source = GameSource.Steam,
        ExternalId = id,
        Title = title,
        Launch = new LaunchSpec(LaunchKind.Uri, $"steam://rungameid/{id}", null, null),
        InstallPath = @"C:\Games\" + id,
        Artwork = new ArtworkSet(Cover: "cover.jpg", Hero: null, Logo: "logo.png", Icon: null),
        AddedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    [Fact]
    public void Migrate_is_idempotent()
    {
        _db.Migrate();
        _db.Migrate();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal((long)LumenDatabase.CurrentVersion, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Upsert_and_read_round_trip()
    {
        var repo = new SqliteGameRepository(_db);
        var g = Game("620", "Portal 2") with { IsFavorite = true, LastPlayed = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(2)) };
        await repo.UpsertAsync(g);

        var read = await repo.GetAsync(g.Id);
        Assert.NotNull(read);
        Assert.Equal(g.Title, read!.Title);
        Assert.Equal(g.Launch, read.Launch);
        Assert.Equal(g.Artwork, read.Artwork);
        Assert.True(read.IsFavorite);
        Assert.Equal(g.LastPlayed!.Value.UtcDateTime, read.LastPlayed!.Value.UtcDateTime);
        Assert.Equal(g.AddedAt, read.AddedAt);
    }

    [Fact]
    public async Task Upsert_updates_existing_rows_without_duplicates()
    {
        var repo = new SqliteGameRepository(_db);
        await repo.UpsertManyAsync([Game("1", "A"), Game("2", "B")]);
        await repo.UpsertManyAsync([Game("1", "A2")]);
        var all = await repo.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("A2", all.Single(x => x.ExternalId == "1").Title);
    }

    [Fact]
    public async Task Launch_and_playtime_tracking()
    {
        var repo = new SqliteGameRepository(_db);
        var g = Game("1");
        await repo.UpsertAsync(g);
        var when = DateTimeOffset.UtcNow;
        await repo.RecordLaunchAsync(g.Id, when);
        await repo.RecordLaunchAsync(g.Id, when);
        await repo.AddPlaytimeAsync(g.Id, TimeSpan.FromMinutes(90));
        await repo.SetFavoriteAsync(g.Id, true);
        await repo.SetHiddenAsync(g.Id, true);

        var read = (await repo.GetAsync(g.Id))!;
        Assert.Equal(2, read.PlayCount);
        Assert.Equal(5400, read.PlaytimeSeconds);
        Assert.True(read.IsFavorite);
        Assert.True(read.IsHidden);
        Assert.NotNull(read.LastPlayed);

        await repo.DeleteAsync(g.Id);
        Assert.Null(await repo.GetAsync(g.Id));
    }

    [Fact]
    public void Settings_persist_across_instances()
    {
        var s1 = new SqliteSettingsStore(_db);
        s1.Set("a", "1");
        s1.Set("b", null);
        var s2 = new SqliteSettingsStore(_db);
        Assert.Equal("1", s2.Get("a"));
        Assert.Null(s2.Get("b"));
        Assert.Null(s2.Get("missing"));

        var typed = new ShellSettings(s2) { Fullscreen = false };
        Assert.False(new ShellSettings(new SqliteSettingsStore(_db)).Fullscreen);
    }

    [Fact]
    public async Task Library_service_works_against_sqlite()
    {
        var repo = new SqliteGameRepository(_db);
        await repo.UpsertAsync(Game("1") with { IsFavorite = true, PlayCount = 4 });
        var svc = new LibraryService(repo, [new FixedScanner([Game("1", "Renamed"), Game("2")])]);
        var report = await svc.ScanAsync();
        Assert.Equal(1, report.Added);
        var g1 = (await repo.GetAsync("steam:1"))!;
        Assert.Equal("Renamed", g1.Title);
        Assert.True(g1.IsFavorite);
        Assert.Equal(4, g1.PlayCount);
    }

    private sealed class FixedScanner(IReadOnlyList<GameEntry> games) : IGameScanner
    {
        public GameSource Source => GameSource.Steam;
        public string DisplayName => "Fixed";
        public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(games);
    }
}
