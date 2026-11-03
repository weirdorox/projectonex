using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Maps a running process to a library entry. Used to recognise the game in the foreground no
/// matter how it was started (Lumen, Steam's own UI, a desktop shortcut...).
///
/// Rules: an exact executable match wins; otherwise the game whose install folder is the longest
/// prefix of the process path (on a folder boundary) wins. Store clients and Lumen itself never match
/// because their executables are not inside a game's install folder.
/// </summary>
public static class GameMatcher
{
    public static GameEntry? Match(string? processPath, IEnumerable<GameEntry> games)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        var path = Normalize(processPath);

        GameEntry? best = null;
        var bestLength = -1;
        foreach (var g in games)
        {
            if (!g.IsInstalled) continue;

            if (g.Launch.Kind == LaunchKind.Executable && Normalize(g.Launch.Target) == path)
                return g;

            if (string.IsNullOrWhiteSpace(g.InstallPath)) continue;
            var dir = Normalize(g.InstallPath).TrimEnd('/') + "/";
            if (dir.Length <= 3) continue; // never match a drive root
            if (path.StartsWith(dir, StringComparison.Ordinal) && dir.Length > bestLength)
            {
                best = g;
                bestLength = dir.Length;
            }
        }
        return best;
    }

    internal static string Normalize(string p) => p.Trim().Replace('\\', '/').ToLowerInvariant();
}
