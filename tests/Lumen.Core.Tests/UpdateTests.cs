using System.Security.Cryptography;
using System.Text;
using Lumen.Core.Updates;

namespace Lumen.Core.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("1.0.0", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta", "1.0.0-alpha.9")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta")]
    [InlineData("0.10.0", "0.9.0")]
    public void Precedence(string higher, string lower)
    {
        Assert.True(SemVersion.Parse(higher)! > SemVersion.Parse(lower)!);
        Assert.True(SemVersion.Parse(lower)! < SemVersion.Parse(higher)!);
    }

    [Fact]
    public void Parsing()
    {
        Assert.Equal("0.3.0", SemVersion.Parse("v0.3.0")!.ToString());
        Assert.Equal("0.3.0", SemVersion.Parse("0.3.0.0")!.ToString());
        Assert.Equal("0.3.0", SemVersion.Parse("0.3.0+abc123")!.ToString());
        Assert.Equal("0.4.0-beta.1", SemVersion.Parse("v0.4.0-beta.1")!.ToString());
        foreach (var bad in new[] { null, "", "v", "1", "1.x.0", "1.0.0-", "1.0.0-be ta" })
            Assert.Null(SemVersion.Parse(bad));
    }
}

public class UpdateRulesTests
{
    private const string ReleasesJson = """
        [
          {"tag_name":"v0.5.0","prerelease":false,"draft":true,"assets":[{"name":"lumen-update.json"},{"name":"lumen-update.json.sig"}]},
          {"tag_name":"v0.5.0-beta.1","prerelease":true,"draft":false,"published_at":"2026-11-02T10:00:00Z","body":"beta notes",
           "assets":[{"name":"lumen-update.json","size":10,"browser_download_url":"https://github.com/weirdorox/projectonex/releases/download/v0.5.0-beta.1/lumen-update.json"},
                     {"name":"lumen-update.json.sig","size":72,"browser_download_url":"https://github.com/weirdorox/projectonex/releases/download/v0.5.0-beta.1/lumen-update.json.sig"}]},
          {"tag_name":"v0.4.0","prerelease":false,"draft":false,"published_at":"2026-11-01T10:00:00Z","body":"stable notes","html_url":"https://github.com/weirdorox/projectonex/releases/tag/v0.4.0",
           "assets":[{"name":"lumen-update.json","size":10,"browser_download_url":"https://github.com/weirdorox/projectonex/releases/download/v0.4.0/lumen-update.json"},
                     {"name":"lumen-update.json.sig","size":72,"browser_download_url":"https://github.com/weirdorox/projectonex/releases/download/v0.4.0/lumen-update.json.sig"},
                     {"name":"Lumen-Setup-0.4.0-x64.exe","size":123456,"browser_download_url":"https://github.com/weirdorox/projectonex/releases/download/v0.4.0/Lumen-Setup-0.4.0-x64.exe"}]},
          {"tag_name":"v0.3.9","prerelease":false,"draft":false,"assets":[]}
        ]
        """;

    private static readonly SemVersion Current = SemVersion.Parse("0.3.0")!;

    [Fact]
    public void Channels_drafts_and_metadata_requirements()
    {
        var releases = UpdateRules.ParseReleases(ReleasesJson);
        Assert.Equal(4, releases.Count);
        Assert.Equal("v0.4.0", UpdateRules.Newest(releases, Current, UpdateChannel.Stable)!.Tag);   // draft 0.5.0 ignored, 0.3.9 has no metadata
        Assert.Equal("v0.5.0-beta.1", UpdateRules.Newest(releases, Current, UpdateChannel.Beta)!.Tag);
        Assert.Null(UpdateRules.Newest(releases, SemVersion.Parse("0.4.0")!, UpdateChannel.Stable)); // up to date
        Assert.Null(UpdateRules.Newest(releases, SemVersion.Parse("0.6.0")!, UpdateChannel.Beta));   // never downgrade
        Assert.Equal("stable notes", UpdateRules.ForVersion(releases, SemVersion.Parse("0.4.0")!)!.Notes);
    }

    private static UpdateManifest Manifest(string version = "0.4.0", long size = 123456, string? installer = null) => new()
    {
        Product = "Lumen", Version = version, Channel = "stable", Installer = installer ?? $"Lumen-Setup-{version}-x64.exe",
        Size = size, Sha256 = new string('a', 64),
    };

    [Fact]
    public void Validation_rejects_mismatches_downgrades_and_foreign_urls()
    {
        var release = UpdateRules.ParseReleases(ReleasesJson).Single(r => r.Tag == "v0.4.0");
        Assert.Null(UpdateRules.Validate(Manifest(), release, Current));
        Assert.NotNull(UpdateRules.Validate(Manifest(version: "0.4.1"), release, Current));       // version != tag
        Assert.NotNull(UpdateRules.Validate(Manifest(size: 5), release, Current));                // size mismatch
        Assert.NotNull(UpdateRules.Validate(Manifest(installer: "evil.exe"), release, Current));
        Assert.NotNull(UpdateRules.Validate(Manifest() with { Sha256 = "xyz" }, release, Current));
        Assert.NotNull(UpdateRules.Validate(Manifest() with { Product = "Other" }, release, Current));
        Assert.NotNull(UpdateRules.Validate(Manifest(), release, SemVersion.Parse("0.4.0")!));    // not newer
        Assert.Null(UpdateRules.Validate(Manifest(), release, SemVersion.Parse("0.5.0")!, allowDowngrade: true)); // rollback

        var foreign = release with { Assets = [.. release.Assets.Select(a => a.Name.EndsWith(".exe") ? a with { Url = "https://evil.example/Lumen-Setup-0.4.0-x64.exe" } : a)] };
        Assert.NotNull(UpdateRules.Validate(Manifest(), foreign, Current));
        Assert.False(UpdateRules.IsTrustedUrl("http://github.com/weirdorox/projectonex/releases/download/x"));
    }

    [Fact]
    public void Signature_and_checksum()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = key.ExportSubjectPublicKeyInfoPem();
        var manifest = Encoding.UTF8.GetBytes("{\"product\":\"Lumen\"}");
        var sig = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.True(UpdateRules.VerifySignature(manifest, sig, pub));
        var tampered = (byte[])manifest.Clone();
        tampered[3] ^= 1;
        Assert.False(UpdateRules.VerifySignature(tampered, sig, pub));
        Assert.False(UpdateRules.VerifySignature(manifest, sig)); // signed by another key than Lumen's
        Assert.False(UpdateRules.VerifySignature(manifest, [1, 2, 3], pub));

        var data = Encoding.UTF8.GetBytes("installer");
        var hex = Convert.ToHexString(SHA256.HashData(data));
        Assert.True(UpdateRules.ChecksumMatches(new MemoryStream(data), hex.ToLowerInvariant()));
        Assert.False(UpdateRules.ChecksumMatches(new MemoryStream([.. data, 0]), hex));
    }

    [Fact]
    public void Journal_reconciles_after_restart()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "journal.json");
        new UpdateJournal { Phase = UpdatePhase.Installing, FromVersion = "0.3.0", ToVersion = "0.4.0" }.Save(path);

        var ok = UpdateJournal.Load(path);
        Assert.Equal(UpdatePhase.Installed, ok.Reconcile(SemVersion.Parse("0.4.0")!));

        var failed = UpdateJournal.Load(path);
        Assert.Equal(UpdatePhase.Failed, failed.Reconcile(SemVersion.Parse("0.3.0")!));
        Assert.NotNull(failed.Error);

        Assert.Equal(UpdatePhase.None, new UpdateJournal().Reconcile(Current));
        Assert.Equal(UpdatePhase.None, UpdateJournal.Load(Path.Combine(dir.Path, "missing.json")).Phase);
    }
}
