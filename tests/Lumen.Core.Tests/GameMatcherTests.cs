using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public class GameMatcherTests
{
    private static GameEntry Game(string id, string? installPath, LaunchSpec? launch = null, bool installed = true) =>
        TestGames.Make(GameSource.Steam, id) with
        {
            InstallPath = installPath,
            Launch = launch ?? new LaunchSpec(LaunchKind.Uri, $"steam://rungameid/{id}"),
            IsInstalled = installed,
        };

    private static readonly GameEntry[] Library =
    [
        Game("1", @"C:\Steam\steamapps\common\Hades"),
        Game("2", @"C:\Steam\steamapps\common\Hades II"),
        Game("3", @"D:\Games\Doom"),
        Game("4", @"D:\Games\Doom\Mods\SigilEdition"),
        Game("5", null, new LaunchSpec(LaunchKind.Executable, @"E:\Portable\celeste\Celeste.exe")),
        Game("6", @"F:\Old\Removed", installed: false),
        Game("7", @"C:\"),
    ];

    [Theory]
    [InlineData(@"C:\Steam\steamapps\common\Hades\x64\Hades.exe", "1")]
    [InlineData(@"c:/steam/steamapps/common/hades ii/Ship/Hades2.exe", "2")] // case + separators, not a prefix collision
    [InlineData(@"D:\Games\Doom\doom.exe", "3")]
    [InlineData(@"D:\Games\Doom\Mods\SigilEdition\launcher.exe", "4")]  // longest prefix wins
    [InlineData(@"E:\Portable\celeste\Celeste.exe", "5")]               // exact exe match
    public void Matches_expected_game(string processPath, string expectedId) =>
        Assert.Equal(expectedId, GameMatcher.Match(processPath, Library)?.ExternalId);

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"F:\Old\Removed\game.exe")]   // uninstalled games are ignored
    [InlineData(@"D:\Games\DoomEternal\x.exe")] // "Doom" must match on a folder boundary
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_match_non_games(string? processPath) =>
        Assert.Null(GameMatcher.Match(processPath, Library));
}
