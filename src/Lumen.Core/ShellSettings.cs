using System.Text.Json;
using Lumen.Core.Library;

namespace Lumen.Core;

/// <summary>Typed view over the key/value settings store.</summary>
public sealed class ShellSettings
{
    private readonly ISettingsStore _store;

    public ShellSettings(ISettingsStore store) => _store = store;

    public IReadOnlyList<string> LibraryFolders
    {
        get
        {
            var raw = _store.Get("library.folders");
            if (string.IsNullOrWhiteSpace(raw)) return [];
            try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; }
            catch (JsonException) { return []; }
        }
        set => _store.Set("library.folders", JsonSerializer.Serialize(value.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public bool AllowOnlineArtwork
    {
        get => GetBool("library.onlineArtwork", true);
        set => SetBool("library.onlineArtwork", value);
    }

    public bool PreferGogGalaxy
    {
        get => GetBool("library.gogGalaxy", false);
        set => SetBool("library.gogGalaxy", value);
    }

    /// <summary>Start fullscreen (console mode). Off = windowed.</summary>
    public bool Fullscreen
    {
        get => GetBool("shell.fullscreen", true);
        set => SetBool("shell.fullscreen", value);
    }

    /// <summary>Minimize Lumen while a game launched from it is running.</summary>
    public bool MinimizeOnLaunch
    {
        get => GetBool("shell.minimizeOnLaunch", false);
        set => SetBool("shell.minimizeOnLaunch", value);
    }

    /// <summary>
    /// JSON of the settings captured before a game profile was applied. Present only while a profile
    /// session is active, so a crash or power loss mid-game can be repaired on the next start.
    /// </summary>
    public string? PendingProfileBaseline
    {
        get => _store.Get("profiles.pendingBaseline");
        set => _store.Set("profiles.pendingBaseline", value);
    }

    /// <summary>Smart Power slider position (0-100) used on battery / on AC.</summary>
    public int SmartPowerBattery
    {
        get => GetInt("smartpower.battery", 40);
        set => _store.Set("smartpower.battery", Math.Clamp(value, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public int SmartPowerAc
    {
        get => GetInt("smartpower.ac", 70);
        set => _store.Set("smartpower.ac", Math.Clamp(value, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Re-apply the matching Smart Power position when switching between battery and AC.</summary>
    public bool SmartPowerAuto
    {
        get => GetBool("smartpower.auto", false);
        set => SetBool("smartpower.auto", value);
    }

    /// <summary>Last main tab (Home/Library) - restored after restart, crash or reboot.</summary>
    public int LastTab
    {
        get => GetInt("shell.lastTab", 0);
        set => _store.Set("shell.lastTab", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Last focused game - restored after restart, crash or reboot.</summary>
    public string? LastGameId
    {
        get => _store.Get("shell.lastGame");
        set => _store.Set("shell.lastGame", value);
    }

    /// <summary>When an external display appears (dock/TV), move fullscreen Lumen onto it; back on undock.</summary>
    public bool DockFollowExternal
    {
        get => GetBool("display.dockFollow", true);
        set => SetBool("display.dockFollow", value);
    }

    /// <summary>Performance overlay mode (Off/Minimal/Basic/Detailed/Advanced).</summary>
    public Lumen.Core.Performance.OverlayMode OverlayMode
    {
        get => (Lumen.Core.Performance.OverlayMode)Math.Clamp(GetInt("overlay.mode", 0), 0, 4);
        set => _store.Set("overlay.mode", ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Overlay ignores touches/clicks (layered window). Off = compatibility mode if the overlay is invisible.</summary>
    public bool OverlayClickThrough
    {
        get => GetBool("overlay.clickThrough", true);
        set => SetBool("overlay.clickThrough", value);
    }

    // ---- personalization ----

    public string AccentId
    {
        get => _store.Get("theme.accent") ?? "ember";
        set => _store.Set("theme.accent", value);
    }

    /// <summary>True black surfaces (saves power on OLED panels).</summary>
    public bool OledTheme
    {
        get => GetBool("theme.oled", false);
        set => SetBool("theme.oled", value);
    }

    public bool UiSounds
    {
        get => GetBool("ui.sounds", false);
        set => SetBool("ui.sounds", value);
    }

    public bool HomeShowRecent
    {
        get => GetBool("home.recent", true);
        set => SetBool("home.recent", value);
    }

    public bool HomeShowFavorites
    {
        get => GetBool("home.favorites", true);
        set => SetBool("home.favorites", value);
    }

    public bool HomeShowNew
    {
        get => GetBool("home.new", true);
        set => SetBool("home.new", value);
    }

    /// <summary>A collection shown as its own row on Home (null = none).</summary>
    public string? HomePinnedCollection
    {
        get => _store.Get("home.pinnedCollection") is { Length: > 0 } v ? v : null;
        set => _store.Set("home.pinnedCollection", value ?? "");
    }

    // ---- Steam account ----

    /// <summary>Steam Web API key, encrypted for this Windows user (DPAPI), base64.</summary>
    public string? SteamApiKeyProtected
    {
        get => _store.Get("steam.apiKey") is { Length: > 0 } v ? v : null;
        set => _store.Set("steam.apiKey", value ?? "");
    }

    /// <summary>Show owned Steam games that are not installed (with an Install action).</summary>
    public bool SteamShowOwned
    {
        get => GetBool("steam.showOwned", true);
        set => SetBool("steam.showOwned", value);
    }

    // ---- Quick settings button ----

    /// <summary>The detected OEM/hardware button (serialised <see cref="Lumen.Core.Input.ButtonBinding"/>), or null.</summary>
    public string? QuickSettingsButton
    {
        get => _store.Get("qs.button") is { Length: > 0 } v ? v : null;
        set => _store.Set("qs.button", value ?? "");
    }

    /// <summary>Keyboard fallback shortcut (default Ctrl+Alt+Q).</summary>
    public string QuickSettingsShortcut
    {
        get => _store.Get("qs.shortcut") is { Length: > 0 } v ? v : Lumen.Core.Input.ButtonBinding.DefaultKeyboard.Serialize();
        set => _store.Set("qs.shortcut", value);
    }

    /// <summary>Compatibility: let Quick settings take focus from the game (off = the game keeps focus).</summary>
    public bool QuickSettingsTakesFocus
    {
        get => GetBool("qs.takeFocus", false);
        set => SetBool("qs.takeFocus", value);
    }

    // ---- updates ----

    public Lumen.Core.Updates.UpdateChannel UpdateChannel
    {
        get => _store.Get("update.channel") == "beta" ? Lumen.Core.Updates.UpdateChannel.Beta : Lumen.Core.Updates.UpdateChannel.Stable;
        set => _store.Set("update.channel", value == Lumen.Core.Updates.UpdateChannel.Beta ? "beta" : "stable");
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => DateTimeOffset.TryParse(_store.Get("update.lastCheck"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var v) ? v : null;
        set => _store.Set("update.lastCheck", value?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    public bool FirstRunCompleted
    {
        get => GetBool("shell.firstRunCompleted", false);
        set => SetBool("shell.firstRunCompleted", value);
    }

    private int GetInt(string key, int fallback) =>
        int.TryParse(_store.Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(_store.Get(key), out var v) ? v : fallback;

    private void SetBool(string key, bool value) => _store.Set(key, value ? "true" : "false");
}

/// <summary>In-memory settings (tests, safe mode fallback when the database is unusable).</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string? value) => _values[key] = value;
}
