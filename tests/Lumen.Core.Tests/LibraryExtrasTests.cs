using Lumen.Core.Activity;
using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public class GameSearchTests
{
    private static readonly GameEntry[] Games =
    [
        TestGames.Make(GameSource.Steam, "1", "The Witcher 3: Wild Hunt"),
        TestGames.Make(GameSource.Steam, "2", "Red Dead Redemption 2"),
        TestGames.Make(GameSource.Steam, "3", "Pokémon Legends"),
        TestGames.Make(GameSource.Steam, "4", "Hades") with { IsHidden = true },
        TestGames.Make(GameSource.Steam, "5", "Hades II"),
        TestGames.Make(GameSource.Steam, "6", "Shadow of Doom") with { IsInstalled = false },
        TestGames.Make(GameSource.Epic, "7", "Celeste"),
    ];

    private static string[] Titles(string q, bool hidden = false) =>
        GameSearch.Search(Games, q, hidden).Select(g => g.Title).ToArray();

    [Fact]
    public void Prefix_and_word_matches_rank_first()
    {
        Assert.Equal("Hades II", Titles("hades")[0]);
        Assert.Equal("The Witcher 3: Wild Hunt", Titles("witch")[0]);
    }

    [Fact]
    public void Acronyms_accents_and_punctuation()
    {
        Assert.Equal("Red Dead Redemption 2", Titles("rdr")[0]);
        Assert.Equal("Pokémon Legends", Titles("pokemon")[0]);
        Assert.Equal("The Witcher 3: Wild Hunt", Titles("witcher 3 wild")[0]);
    }

    [Fact]
    public void Subsequence_is_a_last_resort()
    {
        Assert.Equal(new[] { "Celeste" }, Titles("clst"));
        Assert.Empty(Titles("zzz"));
    }

    [Fact]
    public void Hidden_and_uninstalled_rules()
    {
        Assert.DoesNotContain("Hades", Titles("hades"));
        Assert.Contains("Hades", Titles("hades", hidden: true));
        Assert.Empty(Titles("doom"));
        Assert.Equal(5, GameSearch.Search(Games, "").Count); // empty query = all visible, sorted
    }

    [Fact]
    public void Normalize_collapses_everything()
    {
        Assert.Equal("the witcher 3 wild hunt", GameSearch.Normalize("  The Witcher 3:  Wild-Hunt! "));
    }
}

public class CollectionTests
{
    [Fact]
    public void Names_are_cleaned_and_required()
    {
        Assert.Equal("Couch co-op", GameCollection.Clean("  Couch   co-op "));
        Assert.Equal(40, GameCollection.Clean(new string('x', 80)).Length);
        Assert.Throws<ArgumentException>(() => GameCollection.Clean("   "));
    }

    [Fact]
    public async Task Membership_round_trip()
    {
        var repo = new InMemoryCollectionRepository();
        var c = GameCollection.Create("Short sessions");
        await repo.SaveAsync(c);
        await repo.SetMembershipAsync(c.Id, "steam:1", true);
        await repo.SetMembershipAsync(c.Id, "steam:2", true);
        await repo.SetMembershipAsync(c.Id, "steam:2", false);
        Assert.Equal(new[] { "steam:1" }, (await repo.GetGameIdsAsync(c.Id)).ToArray());
        Assert.Equal(new[] { c.Id }, (await repo.GetCollectionIdsForGameAsync("steam:1")).ToArray());
        await repo.DeleteAsync(c.Id);
        Assert.Empty(await repo.GetCollectionIdsForGameAsync("steam:1"));
    }
}

public class PlayStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

    private static PlaySession S(int daysAgo, int minutes, bool battery = false, double? fps = null, double? watts = null, double? pct = null) =>
        new("g", Now.AddDays(-daysAgo).AddHours(-2), Now.AddDays(-daysAgo).AddHours(-2).AddMinutes(minutes),
            battery, fps, watts, pct, FpsMeasured: fps is not null);

    [Fact]
    public void Aggregates_totals_and_ignores_blips()
    {
        var stats = PlayStatsCalculator.Compute([S(0, 60), S(1, 30), S(10, 90), S(0, 0)], Now, TimeZoneInfo.Utc);
        Assert.Equal(3, stats.Sessions);
        Assert.Equal(TimeSpan.FromMinutes(180), stats.Total);
        Assert.Equal(TimeSpan.FromMinutes(60), stats.Average);
        Assert.Equal(TimeSpan.FromMinutes(90), stats.Longest);
        Assert.Equal(TimeSpan.FromMinutes(90), stats.Last7Days);
        Assert.Equal(14, stats.Last14Days.Count);
        Assert.Equal(TimeSpan.FromMinutes(60), stats.Last14Days[^1].Played);
        Assert.Equal(TimeSpan.FromMinutes(90), stats.Last14Days[3].Played);
    }

    [Fact]
    public void Only_measured_fps_is_averaged_and_weighted_by_time()
    {
        var stats = PlayStatsCalculator.Compute([S(0, 60, fps: 60), S(1, 120, fps: 30), S(2, 60)], Now, TimeZoneInfo.Utc);
        Assert.Equal(40, stats.AvgFps); // (60*1 + 30*2) / 3
    }

    [Fact]
    public void Battery_drain_per_hour()
    {
        var stats = PlayStatsCalculator.Compute([S(0, 60, battery: true, pct: 25), S(1, 120, battery: true, pct: 40), S(2, 60, pct: 99)], Now, TimeZoneInfo.Utc);
        Assert.Equal(21.7, stats.AvgBatteryPercentPerHour);
        Assert.Null(PlayStatsCalculator.Compute([], Now, TimeZoneInfo.Utc).AvgFps);
    }
}

public class StoreScannerTests
{
    [Fact]
    public async Task Ubisoft_installs_with_titles_from_uninstall_entries()
    {
        var reg = new FakeRegistry()
            .Set(RegistryRoot.LocalMachine, UbisoftScanner.InstallsKey + @"\635", "InstallDir", "C:/Ubisoft/Assassin's Creed Origins/")
            .Set(RegistryRoot.LocalMachine, UbisoftScanner.UninstallKey + @"\Uplay Install 635", "DisplayName", "Assassin's Creed Origins")
            .Set(RegistryRoot.LocalMachine, UbisoftScanner.InstallsKey + @"\4311", "InstallDir", "C:/Ubisoft/far_cry_6/")
            .Set(RegistryRoot.LocalMachine, UbisoftScanner.InstallsKey + @"\notanid", "InstallDir", "C:/x");

        var games = (await new UbisoftScanner(reg).ScanAsync(CancellationToken.None)).OrderBy(g => g.ExternalId).ToList();
        Assert.Equal(2, games.Count);
        Assert.Equal("ubisoft:4311", games[0].Id);
        Assert.Equal("Far Cry 6", games[0].Title); // folder name fallback
        Assert.Equal("uplay://launch/635/0", games[1].Launch.Target);
        Assert.Equal("Assassin's Creed Origins", games[1].Title);
    }

    [Fact]
    public async Task EA_requires_installer_folder_and_publisher()
    {
        using var dir = new TempDir();
        var game = dir.WriteBytes("FIFA/FIFA.exe", 100);
        dir.Write("FIFA/__Installer/installerdata.xml", "<x/>");
        dir.WriteBytes("Tool/tool.exe", 100);
        const string key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        var reg = new FakeRegistry()
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-1}", "Publisher", "Electronic Arts")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-1}", "DisplayName", "EA SPORTS FC")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-1}", "InstallLocation", Path.Combine(dir.Path, "FIFA"))
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-1}", "DisplayIcon", game + ",0")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-2}", "Publisher", "Electronic Arts")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-2}", "DisplayName", "Some EA Tool")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-2}", "InstallLocation", Path.Combine(dir.Path, "Tool"))
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-3}", "Publisher", "Electronic Arts")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-3}", "DisplayName", "EA app")
            .Set(RegistryRoot.LocalMachine, key + @"\{EA-3}", "InstallLocation", Path.Combine(dir.Path, "FIFA"))
            .Set(RegistryRoot.LocalMachine, key + @"\Other", "Publisher", "Valve")
            .Set(RegistryRoot.LocalMachine, key + @"\Other", "DisplayName", "Steam");

        var games = await UninstallEntryScanner.Ea(reg).ScanAsync(CancellationToken.None);
        var g = Assert.Single(games);
        Assert.Equal("EA SPORTS FC", g.Title);
        Assert.Equal(GameSource.EA, g.Source);
        Assert.Equal(Path.GetFullPath(game), g.Launch.Target);
    }

    [Fact]
    public async Task BattleNet_skips_the_launcher_itself()
    {
        using var dir = new TempDir();
        dir.WriteBytes("Overwatch/Overwatch.exe", 100);
        dir.WriteBytes("BNet/Battle.net.exe", 100);
        const string key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        var reg = new FakeRegistry()
            .Set(RegistryRoot.LocalMachine, key + @"\Overwatch", "Publisher", "Blizzard Entertainment")
            .Set(RegistryRoot.LocalMachine, key + @"\Overwatch", "DisplayName", "Overwatch")
            .Set(RegistryRoot.LocalMachine, key + @"\Overwatch", "InstallLocation", Path.Combine(dir.Path, "Overwatch"))
            .Set(RegistryRoot.LocalMachine, key + @"\Battle.net", "Publisher", "Blizzard Entertainment")
            .Set(RegistryRoot.LocalMachine, key + @"\Battle.net", "DisplayName", "Battle.net")
            .Set(RegistryRoot.LocalMachine, key + @"\Battle.net", "InstallLocation", Path.Combine(dir.Path, "BNet"));

        var g = Assert.Single(await UninstallEntryScanner.BattleNet(reg).ScanAsync(CancellationToken.None));
        Assert.Equal("battlenet:overwatch", g.Id);
        Assert.EndsWith("Overwatch.exe", g.Launch.Target);
    }
}
