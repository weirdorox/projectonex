using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public class LibraryServiceTests
{
    [Fact]
    public async Task New_games_are_added()
    {
        var repo = new FakeGameRepository();
        var svc = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [TestGames.Make(GameSource.Steam, "1", "A")])]);
        var report = await svc.ScanAsync();
        Assert.Equal(1, report.Added);
        Assert.True(repo.Games["steam:1"].IsInstalled);
    }

    [Fact]
    public async Task Rescan_preserves_user_data_and_updates_discovery_data()
    {
        var repo = new FakeGameRepository();
        var played = DateTimeOffset.UtcNow.AddDays(-1);
        await repo.UpsertAsync(TestGames.Make(GameSource.Steam, "1", "Old Title") with
        {
            IsFavorite = true, PlayCount = 7, PlaytimeSeconds = 3600, LastPlayed = played, CustomCover = "c.png",
        });

        var svc = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [TestGames.Make(GameSource.Steam, "1", "New Title")])]);
        var report = await svc.ScanAsync();

        var g = repo.Games["steam:1"];
        Assert.Equal(1, report.Updated);
        Assert.Equal("New Title", g.Title);
        Assert.True(g.IsFavorite);
        Assert.Equal(7, g.PlayCount);
        Assert.Equal(3600, g.PlaytimeSeconds);
        Assert.Equal(played, g.LastPlayed);
        Assert.Equal("c.png", g.EffectiveCover);
    }

    [Fact]
    public async Task Uninstalled_games_are_marked_not_deleted()
    {
        var repo = new FakeGameRepository();
        await repo.UpsertAsync(TestGames.Make(GameSource.Steam, "1") with { IsFavorite = true });
        var svc = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [])]);

        var report = await svc.ScanAsync();

        Assert.Equal(1, report.MarkedMissing);
        Assert.False(repo.Games["steam:1"].IsInstalled);
        Assert.True(repo.Games["steam:1"].IsFavorite);
    }

    [Fact]
    public async Task Failing_scanner_does_not_touch_its_games_or_block_others()
    {
        var repo = new FakeGameRepository();
        await repo.UpsertAsync(TestGames.Make(GameSource.Epic, "e"));
        var svc = new LibraryService(repo,
        [
            new StubScanner(GameSource.Epic, () => throw new IOException("boom")),
            new StubScanner(GameSource.Gog, () => [TestGames.Make(GameSource.Gog, "g")]),
        ]);

        var report = await svc.ScanAsync();

        Assert.True(repo.Games["epic:e"].IsInstalled);
        Assert.True(repo.Games.ContainsKey("gog:g"));
        Assert.Contains(GameSource.Epic, report.Failures.Keys);
    }

    [Fact]
    public async Task Manual_entries_survive_scans()
    {
        var repo = new FakeGameRepository();
        await repo.UpsertAsync(TestGames.Make(GameSource.Manual, "m"));
        var svc = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [])]);
        await svc.ScanAsync();
        Assert.True(repo.Games["manual:m"].IsInstalled);
    }

    [Fact]
    public async Task Reinstalled_game_becomes_installed_again()
    {
        var repo = new FakeGameRepository();
        await repo.UpsertAsync(TestGames.Make(GameSource.Steam, "1") with { IsInstalled = false, PlayCount = 3 });
        var svc = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [TestGames.Make(GameSource.Steam, "1")])]);
        await svc.ScanAsync();
        Assert.True(repo.Games["steam:1"].IsInstalled);
        Assert.Equal(3, repo.Games["steam:1"].PlayCount);
    }

    [Fact]
    public void Shelves_filter_hidden_and_uninstalled_and_sort()
    {
        var now = DateTimeOffset.UtcNow;
        var games = new[]
        {
            TestGames.Make(GameSource.Steam, "1", "The Witcher 3") with { IsFavorite = true, LastPlayed = now.AddHours(-2) },
            TestGames.Make(GameSource.Steam, "2", "Alan Wake") with { IsFavorite = true, LastPlayed = now },
            TestGames.Make(GameSource.Steam, "3", "Hidden") with { IsHidden = true, IsFavorite = true, LastPlayed = now },
            TestGames.Make(GameSource.Steam, "4", "Gone") with { IsInstalled = false, LastPlayed = now },
            TestGames.Make(GameSource.Steam, "5", "Balatro"),
        };

        Assert.Equal(new[] { "Alan Wake", "The Witcher 3" }, LibraryService.RecentlyPlayed(games).Select(g => g.Title).ToArray());
        Assert.Equal(new[] { "Alan Wake", "The Witcher 3" }, LibraryService.Favorites(games).Select(g => g.Title).ToArray());
        // "The Witcher 3" sorts under W
        Assert.Equal(new[] { "Alan Wake", "Balatro", "The Witcher 3" }, LibraryService.AllVisible(games).Select(g => g.Title).ToArray());
    }

    [Fact]
    public void MakeId_is_stable_and_lowercase()
    {
        Assert.Equal("epic:abcdef", GameEntry.MakeId(GameSource.Epic, " AbCdEf "));
    }
}
