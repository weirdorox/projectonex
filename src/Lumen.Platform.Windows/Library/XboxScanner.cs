using System.Xml.Linq;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Lumen.Platform.Windows.Library;

/// <summary>
/// Discovers PC Game Pass / Microsoft Store games installed for the current user.
///
/// Uses the documented PackageManager.FindPackagesForUser API and keeps only packages that contain
/// a MicrosoftGame.config (the GDK game manifest). Older UWP-only games without that file are not
/// detected automatically - add them manually. Launching uses the package's AppUserModelID.
/// </summary>
public sealed class XboxScanner : IGameScanner
{
    public GameSource Source => GameSource.Xbox;
    public string DisplayName => "Xbox / Microsoft Store";

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var results = new List<GameEntry>();
            IEnumerable<Package> packages;
            try
            {
                packages = new PackageManager().FindPackagesForUser(string.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn("Xbox: package enumeration is not accessible.", ex);
                return results;
            }

            foreach (var pkg in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var game = TryRead(pkg);
                    if (game is not null) results.Add(game);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Xbox: skipped a package: {ex.Message}");
                }
            }

            Log.Info($"Xbox: found {results.Count} installed games.");
            return results;
        }, cancellationToken);

    private static GameEntry? TryRead(Package pkg)
    {
        if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) return null;
        if (pkg.SignatureKind == PackageSignatureKind.System) return null;

        string location;
        try { location = pkg.InstalledPath; }
        catch { return null; }
        if (string.IsNullOrEmpty(location)) return null;

        var config = Path.Combine(location, "MicrosoftGame.config");
        if (!File.Exists(config)) return null;

        var entries = pkg.GetAppListEntries();
        if (entries.Count == 0) return null;
        var aumid = entries[0].AppUserModelId;
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        var (configTitle, cover, icon) = ReadGameConfig(config, location);
        var title = !string.IsNullOrWhiteSpace(pkg.DisplayName) ? pkg.DisplayName : configTitle ?? pkg.Id.Name;

        string? logo = null;
        try { logo = pkg.Logo?.LocalPath; } catch { /* ignore */ }

        var familyName = pkg.Id.FamilyName;
        return new GameEntry
        {
            Id = GameEntry.MakeId(GameSource.Xbox, familyName),
            Source = GameSource.Xbox,
            ExternalId = familyName,
            Title = title.Trim(),
            InstallPath = location,
            Launch = new LaunchSpec(LaunchKind.PackagedApp, aumid),
            Artwork = new ArtworkSet(Cover: cover, Icon: icon ?? logo, Logo: logo),
        };
    }

    /// <summary>Reads display name and the largest square logo from MicrosoftGame.config.</summary>
    internal static (string? Title, string? Cover, string? Icon) ReadGameConfig(string configPath, string installDir)
    {
        try
        {
            var doc = XDocument.Load(configPath);
            var visuals = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
            string? Attr(string name)
            {
                var v = visuals?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
                if (string.IsNullOrWhiteSpace(v)) return null;
                var full = Path.Combine(installDir, v);
                return File.Exists(full) ? full : null;
            }

            var title = visuals?.Attributes().FirstOrDefault(a => a.Name.LocalName == "DefaultDisplayName")?.Value;
            var big = Attr("Square480x480Logo");
            var small = Attr("Square150x150Logo") ?? Attr("Square44x44Logo");
            return (title, big, small ?? big);
        }
        catch (Exception ex)
        {
            Log.Debug($"Xbox: cannot read {configPath}: {ex.Message}");
            return (null, null, null);
        }
    }
}
