using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public class VdfParserTests
{
    [Fact]
    public void Parses_modern_libraryfolders()
    {
        var vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                    "apps"
                    {
                        "228980"		"123"
                    }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;
        var root = VdfParser.Parse(vdf);
        var lib = root["libraryfolders"]!;
        Assert.Equal(2, lib.Children.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", lib["0"]!.GetString("path"));
        Assert.Equal(@"D:\SteamLibrary", lib["1"]!.GetString("path"));
        Assert.Equal("", lib["0"]!.GetString("label"));
        Assert.Equal("123", lib["0"]!["apps"]!.GetString("228980"));
    }

    [Fact]
    public void Handles_comments_escapes_conditionals_and_case()
    {
        var vdf = """
            // comment
            "Root" {
                "Quote" "say \"hi\""   // trailing comment
                "Cond"  "1" [$WIN32]
                Unquoted value
            }
            """;
        var root = VdfParser.Parse(vdf)["root"]!;
        Assert.Equal("say \"hi\"", root.GetString("quote"));
        Assert.Equal("1", root.GetString("cond"));
        Assert.Equal("value", root.GetString("unquoted"));
    }

    [Fact]
    public void Truncated_input_does_not_throw()
    {
        var root = VdfParser.Parse("\"AppState\" { \"appid\" \"10\" \"name\"");
        Assert.Equal("10", root["AppState"]!.GetString("appid"));
    }
}

public class SteamScannerTests
{
    private static string Manifest(string appId, string name, int flags = 4, string installDir = "Game", long lastPlayed = 0) => $$"""
        "AppState"
        {
            "appid"		"{{appId}}"
            "name"		"{{name}}"
            "StateFlags"		"{{flags}}"
            "installdir"		"{{installDir}}"
            "LastPlayed"		"{{lastPlayed}}"
        }
        """;

    [Fact]
    public async Task Finds_installed_games_across_libraries_and_skips_tools()
    {
        using var steam = new TempDir();
        using var lib2 = new TempDir();
        steam.Write("steamapps/libraryfolders.vdf", $$"""
            "libraryfolders" { "0" { "path" "{{steam.Path.Replace("\\", "\\\\")}}" } "1" { "path" "{{lib2.Path.Replace("\\", "\\\\")}}" } }
            """);
        steam.Write("steamapps/appmanifest_620.acf", Manifest("620", "Portal 2", lastPlayed: 1700000000));
        steam.Write("steamapps/appmanifest_228980.acf", Manifest("228980", "Steamworks Common Redistributables"));
        lib2.Write("steamapps/appmanifest_1145360.acf", Manifest("1145360", "Hades", installDir: "Hades"));
        lib2.Write("steamapps/appmanifest_999.acf", Manifest("999", "Downloading", flags: 1026));

        var scanner = new SteamScanner(EmptyRegistryReader.Instance, steam.Path, allowOnlineArtwork: false);
        var games = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(new[] { "Hades", "Portal 2" }, games.Select(g => g.Title).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        var portal = games.Single(g => g.ExternalId == "620");
        Assert.Equal("steam:620", portal.Id);
        Assert.Equal(LaunchKind.Uri, portal.Launch.Kind);
        Assert.Equal("steam://rungameid/620", portal.Launch.Target);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), portal.LastPlayed);
        Assert.EndsWith(Path.Combine("steamapps", "common", "Game"), portal.InstallPath);
    }

    [Fact]
    public async Task Missing_steam_returns_empty()
    {
        var scanner = new SteamScanner(EmptyRegistryReader.Instance);
        Assert.Null(scanner.LocateSteam());
        Assert.Empty(await scanner.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public void Finds_flat_and_nested_artwork()
    {
        using var steam = new TempDir();
        var flat = steam.Write("appcache/librarycache/10_library_600x900.jpg");
        var nested = steam.Write("appcache/librarycache/20/abc123/library_600x900.jpg");
        var hero = steam.Write("appcache/librarycache/20/library_hero.jpg");

        Assert.Equal(flat, SteamScanner.FindArtwork(steam.Path, "10", allowOnline: false).Cover);
        var art20 = SteamScanner.FindArtwork(steam.Path, "20", allowOnline: false);
        Assert.Equal(nested, art20.Cover);
        Assert.Equal(hero, art20.Hero);

        Assert.Null(SteamScanner.FindArtwork(steam.Path, "30", allowOnline: false).Cover);
        Assert.StartsWith("https://", SteamScanner.FindArtwork(steam.Path, "30", allowOnline: true).Cover);
    }

    [Fact]
    public void Legacy_libraryfolders_format_is_supported()
    {
        using var steam = new TempDir();
        steam.Write("steamapps/libraryfolders.vdf", """
            "LibraryFolders" { "TimeNextStatsReport" "1234" "ContentStatsID" "-1" "1" "E:/Games/Steam" }
            """);
        var folders = SteamScanner.GetLibraryFolders(steam.Path);
        Assert.Equal(2, folders.Count);
        Assert.Contains(folders, f => f.Contains("Games"));
    }
}

public class EpicScannerTests
{
    [Fact]
    public void Parses_game_manifest()
    {
        var json = """
            {
              "FormatVersion": 0,
              "bIsIncompleteInstall": false,
              "DisplayName": "Alan Wake 2",
              "InstallLocation": "C:\\Program Files\\Epic Games\\AlanWake2",
              "LaunchExecutable": "AlanWake2.exe",
              "AppName": "dc9d2e595d0e4650b35d659f90d41059",
              "CatalogNamespace": "2bd4a2ae4f6e4e10bd4b2f3a0b5d3c3c",
              "CatalogItemId": "4e36e9a77bb84ea8a6e4d1b7e0f7e6a1",
              "MainGameAppName": "dc9d2e595d0e4650b35d659f90d41059",
              "AppCategories": ["public", "games", "applications"]
            }
            """;
        var g = EpicScanner.ParseManifest(json)!;
        Assert.Equal("Alan Wake 2", g.Title);
        Assert.Equal("epic:dc9d2e595d0e4650b35d659f90d41059", g.Id);
        Assert.StartsWith("com.epicgames.launcher://apps/2bd4a2ae4f6e4e10bd4b2f3a0b5d3c3c%3A4e36e9a77bb84ea8a6e4d1b7e0f7e6a1%3Adc9d2e595d0e4650b35d659f90d41059", g.Launch.Target);
        Assert.Contains("action=launch", g.Launch.Target);
    }

    [Theory]
    [InlineData("""{ "DisplayName": "X", "AppName": "a", "bIsIncompleteInstall": true }""")]
    [InlineData("""{ "DisplayName": "DLC", "AppName": "dlc", "MainGameAppName": "base" }""")]
    [InlineData("""{ "DisplayName": "Unreal Engine", "AppName": "UE_5", "AppCategories": ["engines"] }""")]
    public void Skips_non_games(string json) => Assert.Null(EpicScanner.ParseManifest(json));

    [Fact]
    public async Task Scans_manifest_directory_and_ignores_bad_files()
    {
        using var dir = new TempDir();
        dir.Write("a.item", """{ "DisplayName": "Game A", "AppName": "a", "CatalogNamespace": "n", "CatalogItemId": "i" }""");
        dir.Write("broken.item", "{ not json");
        var games = await new EpicScanner(EmptyRegistryReader.Instance, dir.Path).ScanAsync(CancellationToken.None);
        Assert.Single(games);
    }
}

public class GogScannerTests
{
    [Fact]
    public async Task Reads_games_and_skips_dlc()
    {
        var reg = new FakeRegistry()
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1207658924", "gameID", "1207658924")
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1207658924", "gameName", "Fallout")
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1207658924", "path", @"C:\GOG Games\Fallout")
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1207658924", "exe", @"C:\GOG Games\Fallout\falloutw.exe")
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1", "gameName", "Some DLC")
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1", "exe", @"C:\x.exe")
            .Set(RegistryRoot.LocalMachine, GogScanner.GamesKey + @"\1", "dependsOn", "1207658924");

        var games = await new GogScanner(reg).ScanAsync(CancellationToken.None);
        var g = Assert.Single(games);
        Assert.Equal("Fallout", g.Title);
        Assert.Equal("gog:1207658924", g.Id);
        Assert.Equal(LaunchKind.Executable, g.Launch.Kind);
        Assert.Equal(@"C:\GOG Games\Fallout\falloutw.exe", g.Launch.Target);
        Assert.Equal(@"C:\GOG Games\Fallout", g.Launch.WorkingDirectory);
    }
}

public class FolderScannerTests
{
    [Fact]
    public void Picks_main_executable_and_ignores_installers()
    {
        using var dir = new TempDir();
        dir.WriteBytes("Celeste/unins000.exe", 5000);
        dir.WriteBytes("Celeste/Celeste.exe", 100);
        dir.WriteBytes("Celeste/tools/big.exe", 10000);
        Assert.EndsWith("Celeste.exe", ExecutableHeuristics.PickMainExecutable(Path.Combine(dir.Path, "Celeste")));
    }

    [Fact]
    public void Falls_back_to_shallowest_then_largest()
    {
        using var dir = new TempDir();
        dir.WriteBytes("Foo/bin/small.exe", 10);
        dir.WriteBytes("Foo/bin/large.exe", 1000);
        dir.WriteBytes("Foo/vc_redist.x64.exe", 99999);
        Assert.EndsWith("large.exe", ExecutableHeuristics.PickMainExecutable(Path.Combine(dir.Path, "Foo")));
    }

    [Fact]
    public async Task Each_subfolder_with_an_exe_becomes_a_game()
    {
        using var dir = new TempDir();
        dir.WriteBytes("my_cool-game/game.exe", 10);
        dir.Write("my_cool-game/cover.jpg");
        dir.Write("EmptyFolder/readme.txt");
        var games = await new FolderScanner(() => [dir.Path, Path.Combine(dir.Path, "does-not-exist")]).ScanAsync(CancellationToken.None);
        var g = Assert.Single(games);
        Assert.Equal("My Cool-game", g.Title);
        Assert.Equal(GameSource.Folder, g.Source);
        Assert.EndsWith("cover.jpg", g.Artwork.Cover);
    }
}
