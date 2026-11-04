using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Core.Updates;

/// <summary>Semantic version (MAJOR.MINOR.PATCH[-prerelease][+build]) with SemVer 2.0 precedence.</summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static SemVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        string? pre = null;
        var dash = t.IndexOf('-');
        if (dash >= 0)
        {
            pre = t[(dash + 1)..];
            t = t[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        }
        var parts = t.Split('.');
        if (parts.Length is < 2 or > 4) return null; // also accepts assembly-style 0.3.0.0
        var nums = new int[3];
        for (var i = 0; i < Math.Min(3, parts.Length); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        if (parts.Length == 4 && !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        return new SemVersion(nums[0], nums[1], nums[2], pre);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Prerelease is null ? "" : "-" + Prerelease)}";
}

public enum UpdateChannel { Stable, Beta }

public sealed record ReleaseAsset(string Name, long Size, string Url);

public sealed record GitHubRelease(string Tag, bool Prerelease, bool Draft, DateTimeOffset? PublishedAt, string Notes, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public SemVersion? Version => SemVersion.Parse(Tag);
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>The signed update description published with every release (lumen-update.json + .sig).</summary>
public sealed record UpdateManifest
{
    [JsonPropertyName("product")] public string Product { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("channel")] public string Channel { get; init; } = "";
    [JsonPropertyName("installer")] public string Installer { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("commit")] public string? Commit { get; init; }
}

/// <summary>Release discovery, trust and validation rules (pure, tested). See docs/UPDATES.md.</summary>
public static class UpdateRules
{
    public const string Owner = "weirdorox";
    public const string Repo = "projectonex";
    public const string ManifestAsset = "lumen-update.json";
    public const string SignatureAsset = "lumen-update.json.sig";
    public static readonly string ReleasesApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";
    public static readonly string DownloadPrefix = $"https://github.com/{Owner}/{Repo}/releases/download/";

    /// <summary>
    /// Public half of the release signing key. The private half exists only as the GitHub Actions
    /// secret LUMEN_UPDATE_KEY; a release (or a modified asset) without a valid signature from it is
    /// rejected, even though it is downloaded from GitHub.
    /// </summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEbmzbYYzGW05H1hrxM6QhkQkEmQs+
        nJYDif8GJPaJoiQ+GR/Gz3+Y7/oyTsXhAITKUYGSh8qssyN9OBEJ7u2Mwg==
        -----END PUBLIC KEY-----
        """;

    public static string InstallerName(SemVersion v) => $"Lumen-Setup-{v}-x64.exe";

    public static IReadOnlyList<GitHubRelease> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var list = new List<GitHubRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray())
                    assets.Add(new ReleaseAsset(Str(x, "name") ?? "", x.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0, Str(x, "browser_download_url") ?? ""));
            list.Add(new GitHubRelease(
                Str(r, "tag_name") ?? "",
                r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                Str(r, "published_at") is { } p && DateTimeOffset.TryParse(p, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                Str(r, "body") ?? "",
                Str(r, "html_url") ?? "",
                assets));
        }
        return list;
    }

    /// <summary>The newest published release newer than <paramref name="current"/> on the channel, with update metadata.</summary>
    public static GitHubRelease? Newest(IEnumerable<GitHubRelease> releases, SemVersion current, UpdateChannel channel) =>
        releases
            .Where(r => !r.Draft && r.Version is not null)
            .Where(r => channel == UpdateChannel.Beta || (!r.Prerelease && !r.Version!.IsPrerelease))
            .Where(r => r.Asset(ManifestAsset) is not null && r.Asset(SignatureAsset) is not null)
            .Where(r => r.Version! > current)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    /// <summary>A specific release (for rollback: the currently installed version).</summary>
    public static GitHubRelease? ForVersion(IEnumerable<GitHubRelease> releases, SemVersion version) =>
        releases.FirstOrDefault(r => !r.Draft && r.Version is { } v && v.CompareTo(version) == 0 && r.Asset(ManifestAsset) is not null);

    /// <summary>ECDSA P-256 / SHA-256 signature (DER, as written by openssl) over the exact manifest bytes.</summary>
    public static bool VerifySignature(byte[] manifest, byte[] signature, string publicKeyPem = PublicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static UpdateManifest? ParseManifest(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<UpdateManifest>(bytes); }
        catch (JsonException) { return null; }
    }

    /// <summary>Everything that must hold before a single byte of the installer is trusted. Null = OK.</summary>
    public static string? Validate(UpdateManifest m, GitHubRelease release, SemVersion current, bool allowDowngrade = false)
    {
        if (m.Product != "Lumen") return "The update is not for Lumen.";
        var v = SemVersion.Parse(m.Version);
        if (v is null || release.Version is null || v.CompareTo(release.Version) != 0) return "The update's version does not match the release.";
        if (!allowDowngrade && !(v > current)) return $"Refusing to install {v}: it is not newer than {current}.";
        if (m.Installer != InstallerName(v)) return "Unexpected installer file name.";
        if (m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit)) return "The update has no valid checksum.";
        var asset = release.Asset(m.Installer);
        if (asset is null) return "The installer is missing from the release.";
        if (asset.Size != m.Size || m.Size <= 0) return "The installer size does not match the signed metadata.";
        if (!IsTrustedUrl(asset.Url)) return "The installer is not hosted in Lumen's GitHub releases.";
        return null;
    }

    public static bool IsTrustedUrl(string url) =>
        url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public static bool ChecksumMatches(Stream file, string expectedHex)
    {
        var hash = SHA256.HashData(file);
        return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public enum UpdatePhase { None, Installing, Installed, Failed, RolledBack }

/// <summary>
/// Survives the restart: what was being installed, from which version, and where the previous
/// version's verified installer is (for rollback).
/// </summary>
public sealed class UpdateJournal
{
    public UpdatePhase Phase { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string? InstallerPath { get; set; }
    public string? PreviousInstallerPath { get; set; }
    /// <summary>SHA-256 of the previous installer when it was verified - checked again before a rollback.</summary>
    public string? PreviousInstallerSha256 { get; set; }
    public string? LogPath { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int StartsSinceInstall { get; set; }
    public string? Error { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static UpdateJournal Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public static string JournalPath(string dataRoot) => Path.Combine(dataRoot, "updates", "journal.json");

    /// <summary>The previous version's installer, if it still matches the hash recorded when it was verified.</summary>
    public string? VerifiedPreviousInstaller()
    {
        if (PreviousInstallerPath is not { } p || !File.Exists(p) || PreviousInstallerSha256 is not { } sha) return null;
        using var stream = File.OpenRead(p);
        return UpdateRules.ChecksumMatches(stream, sha) ? p : null;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Called on each start. Returns what happened to the last update: installed (running the new
    /// version), failed (still the old version), or unchanged.
    /// </summary>
    public UpdatePhase Reconcile(SemVersion running)
    {
        if (Phase != UpdatePhase.Installing || SemVersion.Parse(ToVersion) is not { } to) return Phase;
        StartsSinceInstall++;
        if (running.CompareTo(to) == 0)
        {
            Phase = UpdatePhase.Installed;
        }
        else if (StartsSinceInstall >= 1)
        {
            Phase = SemVersion.Parse(FromVersion) is { } from && running.CompareTo(from) == 0 ? UpdatePhase.Failed : Phase;
            Error ??= "The installer did not finish. Lumen is still on the previous version.";
        }
        return Phase;
    }
}
