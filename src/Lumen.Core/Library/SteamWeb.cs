using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lumen.Core.Diagnostics;

namespace Lumen.Core.Library;

/// <summary>Store artwork for one app as published by Steam (hashed CDN paths included).</summary>
public sealed record SteamAssets(string? Capsule, string? Hero, string? Header, string? Logo, string? Icon);

public sealed record SteamOwnedGame(string AppId, string Name, int PlaytimeMinutes, DateTimeOffset? LastPlayed);

/// <summary>
/// Steam's public store service (no key, no login) for artwork, and the documented Web API for the
/// owned-games list (user's own key). Pure URL building and parsing - tested.
/// </summary>
public static class SteamStoreApi
{
    public const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/";

    public static string GetItemsUrl(IEnumerable<string> appIds)
    {
        var ids = string.Join(',', appIds.Where(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(id => $"{{\"appid\":{id}}}"));
        var input = $"{{\"ids\":[{ids}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
        return "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input);
    }

    /// <summary>appid -> assets; apps the store does not know (success != 1) map to null.</summary>
    public static Dictionary<string, SteamAssets?> ParseItems(string json)
    {
        var result = new Dictionary<string, SteamAssets?>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("store_items", out var items) || items.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            var appId = item.TryGetProperty("appid", out var a) && a.TryGetInt64(out var id) ? id.ToString(CultureInfo.InvariantCulture)
                      : item.TryGetProperty("id", out var i) && i.TryGetInt64(out var id2) ? id2.ToString(CultureInfo.InvariantCulture) : null;
            if (appId is null) continue;
            if (!item.TryGetProperty("success", out var s) || s.GetInt32() != 1 || !item.TryGetProperty("assets", out var assets))
            {
                result[appId] = null;
                continue;
            }

            var format = Str(assets, "asset_url_format");
            string? Url(params string[] keys)
            {
                if (format is null || format.Contains("://", StringComparison.Ordinal)) return null;
                foreach (var key in keys)
                {
                    if (Str(assets, key) is not { Length: > 0 } file || file.Contains("..", StringComparison.Ordinal)) continue;
                    return AssetBase + format.TrimStart('/').Replace("${FILENAME}", file, StringComparison.Ordinal);
                }
                return null;
            }

            result[appId] = new SteamAssets(
                Capsule: Url("library_capsule_2x", "library_capsule"),
                Hero: Url("library_hero", "library_hero_2x"),
                Header: Url("header", "main_capsule"),
                Logo: Url("library_logo", "library_logo_2x"),
                Icon: Str(assets, "community_icon") is { Length: > 0 } icon && Regex.IsMatch(icon, "^[0-9a-f]{40}$")
                    ? $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{icon}.jpg"
                    : null);
        }
        return result;
    }

    public static string OwnedGamesUrl(string apiKey, string steamId) =>
        "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?include_appinfo=1&include_played_free_games=1&format=json" +
        $"&key={Uri.EscapeDataString(apiKey)}&steamid={Uri.EscapeDataString(steamId)}";

    /// <summary>Null when the response has no games list (private profile or bad key).</summary>
    public static IReadOnlyList<SteamOwnedGame>? ParseOwnedGames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<SteamOwnedGame>();
        foreach (var g in games.EnumerateArray())
        {
            if (!g.TryGetProperty("appid", out var a) || !a.TryGetInt64(out var appId)) continue;
            var name = Str(g, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var playtime = g.TryGetProperty("playtime_forever", out var p) && p.TryGetInt32(out var pt) ? pt : 0;
            DateTimeOffset? last = g.TryGetProperty("rtime_last_played", out var r) && r.TryGetInt64(out var unix) && unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            list.Add(new SteamOwnedGame(appId.ToString(CultureInfo.InvariantCulture), name.Trim(), playtime, last));
        }
        return list;
    }

    public static bool IsValidApiKey(string? key) => key is not null && Regex.IsMatch(key.Trim(), "^[0-9A-Fa-f]{32}$");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// "Sign in through Steam" (OpenID 2.0 on steamcommunity.com). The password is typed on Steam's own
/// page; Lumen only receives the public SteamID64, and verifies the answer with Steam.
/// </summary>
public static class SteamOpenId
{
    public const string Endpoint = "https://steamcommunity.com/openid/login";
    public const string ReturnTo = "http://localhost/lumen-steam-signin";
    private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";
    private static readonly Regex ClaimedId = new(@"^https://steamcommunity\.com/openid/id/(\d{17})$");

    public static string LoginUrl() =>
        Endpoint + "?" + string.Join('&', new[]
        {
            ("openid.ns", "http://specs.openid.net/auth/2.0"),
            ("openid.mode", "checkid_setup"),
            ("openid.return_to", ReturnTo),
            ("openid.realm", "http://localhost/"),
            ("openid.identity", IdentifierSelect),
            ("openid.claimed_id", IdentifierSelect),
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));

    public static bool IsReturn(string url) => url.StartsWith(ReturnTo, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Query(string url)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = url.IndexOf('?');
        if (i < 0) return q;
        foreach (var part in url[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            q[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return q;
    }

    /// <summary>The SteamID64 from a positive assertion, or null (cancelled / malformed).</summary>
    public static string? SteamId(IReadOnlyDictionary<string, string> query)
    {
        if (!query.TryGetValue("openid.mode", out var mode) || mode != "id_res") return null;
        if (!query.TryGetValue("openid.claimed_id", out var claimed)) return null;
        if (!query.TryGetValue("openid.return_to", out var returnTo) || !IsReturn(returnTo)) return null;
        var m = ClaimedId.Match(claimed);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Body for Steam's check_authentication call (the signature is checked by Steam).</summary>
    public static string VerificationBody(IReadOnlyDictionary<string, string> query) =>
        string.Join('&', query.Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal))
            .Select(kv => (kv.Key, Value: kv.Key == "openid.mode" ? "check_authentication" : kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    public static bool IsVerified(string response) =>
        response.Split('\n').Any(l => l.Trim() == "is_valid:true");
}

/// <summary>
/// On-disk cache of Steam web data (artwork lookups and the owned-games list) so scans stay
/// offline and fast. Written by the app, read by <see cref="SteamScanner"/>.
/// </summary>
public sealed class SteamWebCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _path;
    private readonly object _gate = new();
    private Data _data = new();

    public sealed class Data
    {
        public Dictionary<string, SteamAssets?> Assets { get; set; } = new();
        public Dictionary<string, DateTimeOffset> AssetsCheckedAt { get; set; } = new();
        public List<SteamOwnedGame> Owned { get; set; } = [];
        public string? SteamId { get; set; }
        public string? PersonaName { get; set; }
        public DateTimeOffset? OwnedFetchedAt { get; set; }
    }

    public SteamWebCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn("Steam web cache unreadable - starting fresh.", ex);
        }
    }

    public string? SteamId { get { lock (_gate) return _data.SteamId; } }
    public string? PersonaName { get { lock (_gate) return _data.PersonaName; } }
    public DateTimeOffset? OwnedFetchedAt { get { lock (_gate) return _data.OwnedFetchedAt; } }

    public IReadOnlyList<SteamOwnedGame> Owned { get { lock (_gate) return _data.Owned.ToList(); } }

    public SteamAssets? Assets(string appId)
    {
        lock (_gate) return _data.Assets.TryGetValue(appId, out var a) ? a : null;
    }

    /// <summary>True if looked up recently (found or confirmed unknown) - avoids asking again on every scan.</summary>
    public bool WasChecked(string appId, DateTimeOffset now, TimeSpan maxAge)
    {
        lock (_gate) return _data.AssetsCheckedAt.TryGetValue(appId, out var at) && now - at < maxAge;
    }

    public void SetAssets(IReadOnlyDictionary<string, SteamAssets?> found, IEnumerable<string> requested, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var id in requested)
            {
                _data.AssetsCheckedAt[id] = now;
                if (found.TryGetValue(id, out var a)) _data.Assets[id] = a;
            }
        }
        Save();
    }

    public void SetAccount(string? steamId, string? persona)
    {
        lock (_gate)
        {
            if (_data.SteamId != steamId)
            {
                _data.Owned = [];
                _data.OwnedFetchedAt = null;
            }
            _data.SteamId = steamId;
            _data.PersonaName = persona;
        }
        Save();
    }

    public void SetOwned(IReadOnlyList<SteamOwnedGame> owned, DateTimeOffset now)
    {
        lock (_gate)
        {
            _data.Owned = owned.ToList();
            _data.OwnedFetchedAt = now;
        }
        Save();
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_data, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Saving Steam web cache failed.", ex);
        }
    }
}
