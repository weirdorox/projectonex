using Lumen.Core.Diagnostics;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Scans user-configured folders. Every immediate sub-folder is treated as one game, and the
/// most plausible executable inside it (max depth 3) is chosen with <see cref="ExecutableHeuristics"/>.
/// </summary>
public sealed class FolderScanner : IGameScanner
{
    private readonly Func<IReadOnlyList<string>> _folders;

    public FolderScanner(Func<IReadOnlyList<string>> folders) => _folders = folders;

    public GameSource Source => GameSource.Folder;
    public string DisplayName => "Folders";

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GameEntry>>(() =>
        {
            var results = new List<GameEntry>();
            foreach (var root in _folders())
            {
                if (!Directory.Exists(root))
                {
                    Log.Warn($"Folders: '{root}' does not exist, skipping.");
                    continue;
                }

                IEnumerable<string> gameDirs;
                try { gameDirs = Directory.EnumerateDirectories(root).ToList(); }
                catch (Exception ex) { Log.Warn($"Folders: cannot list '{root}'", ex); continue; }

                foreach (var dir in gameDirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var exe = ExecutableHeuristics.PickMainExecutable(dir);
                    if (exe is null) continue;
                    results.Add(Create(dir, exe));
                }
            }
            Log.Info($"Folders: found {results.Count} games.");
            return results;
        }, cancellationToken);

    internal static GameEntry Create(string gameDir, string exe)
    {
        var externalId = Path.GetFullPath(exe).ToLowerInvariant();
        return new GameEntry
        {
            Id = GameEntry.MakeId(GameSource.Folder, externalId),
            Source = GameSource.Folder,
            ExternalId = externalId,
            Title = ExecutableHeuristics.PrettifyTitle(Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
            InstallPath = gameDir,
            Launch = new LaunchSpec(LaunchKind.Executable, exe, null, Path.GetDirectoryName(exe)),
            Artwork = new ArtworkSet(Cover: FindLocalCover(gameDir), Icon: exe),
        };
    }

    /// <summary>A user can drop cover.jpg / cover.png into a game folder to set its art.</summary>
    internal static string? FindLocalCover(string gameDir)
    {
        foreach (var name in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png" })
        {
            var p = Path.Combine(gameDir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

public static class ExecutableHeuristics
{
    private static readonly string[] ExcludedFragments =
    [
        "unins", "uninstall", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup",
        "directx", "dotnet", "crash", "report", "launcherhelper", "prereq", "ue4prereq", "easyanticheat",
        "battleye", "cleanup", "update", "patch", "config", "settings", "benchmark", "touchup",
        "unitycrashhandler", "notification_helper", "helper", "server",
    ];

    public static bool IsExcluded(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return ExcludedFragments.Any(f => name.Contains(f, StringComparison.Ordinal));
    }

    /// <summary>
    /// Picks the most likely game executable: exact folder-name match first, then shallowest,
    /// then largest file. Returns null when nothing plausible exists.
    /// </summary>
    public static string? PickMainExecutable(string gameDir, int maxDepth = 3)
    {
        var candidates = new List<(string Path, int Depth, long Size)>();
        Collect(gameDir, 0, maxDepth, candidates);
        if (candidates.Count == 0) return null;

        var folderKey = Normalize(Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        return candidates
            .OrderByDescending(c => Normalize(Path.GetFileNameWithoutExtension(c.Path)) == folderKey)
            .ThenByDescending(c => folderKey.Length > 0 && Normalize(Path.GetFileNameWithoutExtension(c.Path)).Contains(folderKey))
            .ThenBy(c => c.Depth)
            .ThenByDescending(c => c.Size)
            .Select(c => c.Path)
            .First();
    }

    private static void Collect(string dir, int depth, int maxDepth, List<(string, int, long)> into)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
            {
                if (IsExcluded(file)) continue;
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { /* ignore */ }
                into.Add((file, depth, size));
            }
            if (depth >= maxDepth) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var n = Path.GetFileName(sub).ToLowerInvariant();
                if (n is "_commonredist" or "redist" or "redistributables" or "directx" or "support" or "__installer") continue;
                Collect(sub, depth + 1, maxDepth, into);
            }
        }
        catch
        {
            // access denied etc. - skip
        }
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>"my_cool-game" -> "My Cool Game"</summary>
    public static string PrettifyTitle(string folderName)
    {
        var s = folderName.Replace('_', ' ').Replace('.', ' ').Trim();
        if (s.Length == 0) return folderName;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 && char.IsLower(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w);
        return string.Join(' ', words);
    }
}
