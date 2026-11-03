using System.Net;
using System.Text;
using System.Text.Json;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace Lumen.App.Services;

/// <summary>
/// Steam online features: artwork lookups (public store service, no account) and the signed-in
/// account's owned games (Steam Web API with the user's own key). The key is stored encrypted for
/// this Windows user; the Steam password never passes through Lumen.
/// </summary>
public sealed class SteamOnlineService
{
    private static readonly TimeSpan ArtworkRecheck = TimeSpan.FromDays(7);
    private static readonly TimeSpan OwnedMaxAge = TimeSpan.FromHours(6);
    private static readonly HttpClient Http = CreateClient();
    private readonly AppHost _host;

    public SteamOnlineService(AppHost host) => _host = host;

    private SteamWebCache Cache => _host.SteamWeb;

    public bool IsSignedIn => Cache.SteamId is not null;
    public string? SteamId => Cache.SteamId;
    public string? PersonaName => Cache.PersonaName;
    public bool HasApiKey => _host.Settings.SteamApiKeyProtected is not null;
    public DateTimeOffset? OwnedFetchedAt => Cache.OwnedFetchedAt;
    public int OwnedCount => Cache.Owned.Count;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen/0.3 (handheld game launcher)");
        return c;
    }

    // ---------------- artwork ----------------

    /// <summary>Looks up store artwork for apps without local covers. True if anything new was found.</summary>
    public async Task<bool> ResolveArtworkAsync(IEnumerable<string> appIds, CancellationToken ct = default)
    {
        if (!_host.Settings.AllowOnlineArtwork) return false;
        var now = DateTimeOffset.UtcNow;
        var todo = appIds.Distinct().Where(id => !Cache.WasChecked(id, now, ArtworkRecheck)).ToList();
        if (todo.Count == 0) return false;

        var found = false;
        foreach (var batch in todo.Chunk(40))
        {
            try
            {
                var json = await Http.GetStringAsync(SteamStoreApi.GetItemsUrl(batch), ct).ConfigureAwait(false);
                var items = SteamStoreApi.ParseItems(json);
                Cache.SetAssets(items, batch, now);
                found |= items.Values.Any(a => a?.Capsule is not null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Log.Warn($"Steam artwork lookup failed ({ex.Message}).");
                break; // offline - try again next scan
            }
        }
        Log.Info($"Steam artwork: looked up {todo.Count} apps, new art: {found}.");
        return found;
    }

    // ---------------- account ----------------

    /// <summary>Called by the sign-in window with Steam's OpenID answer. Verifies it with Steam.</summary>
    public async Task<string?> CompleteSignInAsync(string returnUrl)
    {
        var query = SteamOpenId.Query(returnUrl);
        var steamId = SteamOpenId.SteamId(query);
        if (steamId is null) return "Steam sign-in was cancelled.";
        try
        {
            using var content = new StringContent(SteamOpenId.VerificationBody(query), Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await Http.PostAsync(SteamOpenId.Endpoint, content);
            var text = await response.Content.ReadAsStringAsync();
            if (!SteamOpenId.IsVerified(text))
            {
                Log.Warn("Steam OpenID answer could not be verified.");
                return "Steam could not confirm the sign-in. Please try again.";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Verifying Steam sign-in failed.", ex);
            return "Could not reach Steam to confirm the sign-in.";
        }
        Cache.SetAccount(steamId, null);
        Log.Info("Signed in to Steam (SteamID received).");
        if (HasApiKey) await RefreshAsync(force: true);
        return null;
    }

    public async Task<string?> SetApiKeyAsync(string key)
    {
        key = key.Trim();
        if (!SteamStoreApi.IsValidApiKey(key)) return "A Steam Web API key is 32 characters (0-9, A-F).";
        _host.Settings.SteamApiKeyProtected = await ProtectAsync(key);
        Log.Info("Steam Web API key saved (encrypted).");
        return IsSignedIn ? await RefreshAsync(force: true) : null;
    }

    public void SignOut()
    {
        Cache.SetAccount(null, null);
        _host.Settings.SteamApiKeyProtected = null;
        Log.Info("Signed out of Steam in Lumen (key removed).");
    }

    /// <summary>Refreshes the owned-games list (and persona name). Returns an error text or null.</summary>
    public async Task<string?> RefreshAsync(bool force = false)
    {
        if (Cache.SteamId is not { } steamId) return "Not signed in to Steam.";
        if (!force && Cache.OwnedFetchedAt is { } at && DateTimeOffset.UtcNow - at < OwnedMaxAge) return null;
        var key = await UnprotectAsync(_host.Settings.SteamApiKeyProtected);
        if (key is null) return "Add your Steam Web API key to load your library.";
        try
        {
            using var response = await Http.GetAsync(SteamStoreApi.OwnedGamesUrl(key, steamId));
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return "Steam rejected the API key. Create a new one and try again.";
            var owned = SteamStoreApi.ParseOwnedGames(await response.Content.ReadAsStringAsync());
            if (owned is null)
                return "Steam returned no games. In Steam › Profile › Edit Profile › Privacy, set \"Game details\" to Public.";
            Cache.SetOwned(owned, DateTimeOffset.UtcNow);
            Log.Info($"Steam: {owned.Count} owned games loaded.");

            try
            {
                var summary = await Http.GetStringAsync(
                    $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(key)}&steamids={steamId}");
                using var doc = JsonDocument.Parse(summary);
                var persona = doc.RootElement.GetProperty("response").GetProperty("players")[0].GetProperty("personaname").GetString();
                Cache.SetAccount(steamId, persona);
            }
            catch (Exception ex)
            {
                Log.Debug($"Steam persona lookup failed: {ex.Message}");
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Loading Steam library failed.", ex);
            return "Could not reach Steam.";
        }
    }

    private static async Task<string> ProtectAsync(string value)
    {
        var provider = new DataProtectionProvider("LOCAL=user");
        var buffer = CryptographicBuffer.ConvertStringToBinary(value, BinaryStringEncoding.Utf8);
        var protectedBuffer = await provider.ProtectAsync(buffer);
        CryptographicBuffer.CopyToByteArray(protectedBuffer, out var bytes);
        return Convert.ToBase64String(bytes);
    }

    private static async Task<string?> UnprotectAsync(string? stored)
    {
        if (stored is null) return null;
        try
        {
            var provider = new DataProtectionProvider();
            var plain = await provider.UnprotectAsync(CryptographicBuffer.CreateFromByteArray(Convert.FromBase64String(stored)));
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain);
        }
        catch (Exception ex)
        {
            Log.Warn("Stored Steam key could not be decrypted.", ex);
            return null;
        }
    }
}
