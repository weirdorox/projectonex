using Lumen.Core.Library;
using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public class SteamWebTests
{
    private const string ItemsJson = """
        {"response":{"store_items":[
          {"item_type":0,"id":2420110,"success":1,"visible":true,"name":"Phantom Blade Zero","appid":2420110,
           "assets":{"asset_url_format":"steam/apps/2420110/0123456789abcdef0123456789abcdef01234567/${FILENAME}?t=1",
             "main_capsule":"capsule_616x353.jpg","header":"header.jpg","library_capsule":"library_capsule.jpg",
             "library_capsule_2x":"library_capsule_2x.jpg","library_hero":"library_hero.jpg",
             "community_icon":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}},
          {"item_type":0,"id":5,"success":15}
        ]}}
        """;

    [Fact]
    public void Store_items_resolve_hashed_asset_urls()
    {
        var items = SteamStoreApi.ParseItems(ItemsJson);
        var a = items["2420110"]!;
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/2420110/0123456789abcdef0123456789abcdef01234567/library_capsule_2x.jpg?t=1", a.Capsule);
        Assert.EndsWith("/library_hero.jpg?t=1", a.Hero);
        Assert.EndsWith("/header.jpg?t=1", a.Header);
        Assert.Null(a.Logo);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/2420110/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", a.Icon);
        Assert.True(items.ContainsKey("5"));
        Assert.Null(items["5"]);
    }

    [Fact]
    public void Absolute_or_traversing_formats_are_refused()
    {
        var json = ItemsJson.Replace("steam/apps/2420110/0123456789abcdef0123456789abcdef01234567/${FILENAME}?t=1", "https://evil.example/${FILENAME}");
        Assert.Null(SteamStoreApi.ParseItems(json)["2420110"]!.Capsule);
    }

    [Fact]
    public void Get_items_url_contains_only_numeric_ids()
    {
        var url = SteamStoreApi.GetItemsUrl(["620", "abc", "1145360"]);
        var input = Uri.UnescapeDataString(url[(url.IndexOf("input_json=", StringComparison.Ordinal) + 11)..]);
        Assert.Contains("{\"appid\":620}", input);
        Assert.Contains("{\"appid\":1145360}", input);
        Assert.DoesNotContain("abc", input);
        Assert.Contains("\"include_assets\":true", input);
    }

    [Fact]
    public void Owned_games_and_key_format()
    {
        var owned = SteamStoreApi.ParseOwnedGames("""
            {"response":{"game_count":2,"games":[
              {"appid":620,"name":"Portal 2","playtime_forever":600,"rtime_last_played":1700000000},
              {"appid":10,"name":"Counter-Strike","playtime_forever":0,"rtime_last_played":0}]}}
            """)!;
        Assert.Equal(2, owned.Count);
        Assert.Equal(new SteamOwnedGame("620", "Portal 2", 600, DateTimeOffset.FromUnixTimeSeconds(1700000000)), owned[0]);
        Assert.Null(owned[1].LastPlayed);
        Assert.Null(SteamStoreApi.ParseOwnedGames("""{"response":{}}""")); // private profile
        Assert.True(SteamStoreApi.IsValidApiKey("0123456789ABCDEF0123456789abcdef"));
        Assert.False(SteamStoreApi.IsValidApiKey("short"));
        Assert.Contains("steamid=76561198000000000", SteamStoreApi.OwnedGamesUrl("K", "76561198000000000"));
    }

    [Fact]
    public void OpenId_flow()
    {
        var login = SteamOpenId.LoginUrl();
        Assert.StartsWith("https://steamcommunity.com/openid/login?", login);
        Assert.Contains("openid.mode=checkid_setup", login);

        var back = SteamOpenId.ReturnTo + "?openid.ns=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0&openid.mode=id_res" +
                   "&openid.claimed_id=https%3A%2F%2Fsteamcommunity.com%2Fopenid%2Fid%2F76561198000000000" +
                   "&openid.return_to=" + Uri.EscapeDataString(SteamOpenId.ReturnTo) + "&openid.sig=abc%2B%3D";
        Assert.True(SteamOpenId.IsReturn(back));
        var q = SteamOpenId.Query(back);
        Assert.Equal("76561198000000000", SteamOpenId.SteamId(q));
        var body = SteamOpenId.VerificationBody(q);
        Assert.Contains("openid.mode=check_authentication", body);
        Assert.Contains("openid.sig=abc%2B%3D", body);
        Assert.True(SteamOpenId.IsVerified("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n"));
        Assert.False(SteamOpenId.IsVerified("is_valid:false"));

        Assert.Null(SteamOpenId.SteamId(SteamOpenId.Query(back.Replace("id_res", "cancel"))));
        Assert.Null(SteamOpenId.SteamId(SteamOpenId.Query(back.Replace("steamcommunity.com%2Fopenid", "evil.com%2Fopenid"))));
    }

    [Fact]
    public async Task Cache_feeds_scanner_artwork_and_owned_uninstalled_games()
    {
        using var steam = new TempDir();
        steam.Write("steamapps/appmanifest_620.acf", "\"AppState\" { \"appid\" \"620\" \"name\" \"Portal 2\" \"StateFlags\" \"4\" \"installdir\" \"Portal 2\" }");
        var cache = new SteamWebCache(System.IO.Path.Combine(steam.Path, "steam-web.json"));
        cache.SetAssets(SteamStoreApi.ParseItems(ItemsJson), ["2420110", "5"], DateTimeOffset.UtcNow);
        cache.SetAccount("76561198000000000", "me");
        cache.SetOwned([new("620", "Portal 2", 1, null), new("2420110", "Phantom Blade Zero", 0, null)], DateTimeOffset.UtcNow);

        var reloaded = new SteamWebCache(System.IO.Path.Combine(steam.Path, "steam-web.json"));
        Assert.True(reloaded.WasChecked("5", DateTimeOffset.UtcNow, TimeSpan.FromDays(1)));
        Assert.Equal(2, reloaded.Owned.Count);

        var scanner = new SteamScanner(EmptyRegistryReader.Instance, steam.Path, allowOnlineArtwork: true, web: reloaded, includeOwned: () => true);
        var games = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(2, games.Count);
        var pbz = games.Single(g => g.ExternalId == "2420110");
        Assert.False(pbz.IsInstalled);
        Assert.Equal("steam://install/2420110", pbz.Launch.Target);
        Assert.Contains("library_capsule_2x.jpg", pbz.Artwork.Cover);
        Assert.Contains("620", scanner.MissingArtwork); // only the unhashed fallback for Portal 2
        Assert.DoesNotContain("2420110", scanner.MissingArtwork);

        var noOwned = new SteamScanner(EmptyRegistryReader.Instance, steam.Path, web: reloaded, includeOwned: () => false);
        Assert.Single(await noOwned.ScanAsync(CancellationToken.None));

        // Changing account forgets the previous owned list.
        reloaded.SetAccount("76561198000000001", "other");
        Assert.Empty(reloaded.Owned);
    }

    [Fact]
    public async Task Library_keeps_owned_games_uninstalled_and_installing_flips_them()
    {
        var repo = new InMemoryGameRepository();
        var owned = TestGames.Make(GameSource.Steam, "2420110", "Phantom Blade Zero") with { IsInstalled = false };
        var service = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [owned])]);
        await service.ScanAsync();
        Assert.False((await repo.GetAllAsync()).Single().IsInstalled);

        var installed = new LibraryService(repo, [new StubScanner(GameSource.Steam, () => [owned with { IsInstalled = true }])]);
        await installed.ScanAsync();
        Assert.True((await repo.GetAllAsync()).Single().IsInstalled);
    }
}
