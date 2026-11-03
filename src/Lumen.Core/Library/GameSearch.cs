using System.Globalization;
using System.Text;
using Lumen.Core.Models;

namespace Lumen.Core.Library;

/// <summary>
/// Instant, typo-tolerant-ish search over the library (no network). Ranking: exact title, prefix,
/// word prefix ("wit" → "The Witcher 3"), acronym ("rdr" → "Red Dead Redemption"), substring, then
/// ordered-subsequence match. Case and accents are ignored. Hidden games only when requested.
/// </summary>
public static class GameSearch
{
    public static IReadOnlyList<GameEntry> Search(IEnumerable<GameEntry> games, string? query, bool includeHidden = false, int max = 200)
    {
        var q = Normalize(query ?? "");
        var pool = games.Where(g => g.IsInstalled && (includeHidden || !g.IsHidden));
        if (q.Length == 0) return pool.OrderBy(g => g.SortTitle, StringComparer.Ordinal).Take(max).ToList();

        return pool
            .Select(g => (Game: g, Score: Score(Normalize(g.Title), q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Game.SortTitle, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Game)
            .ToList();
    }

    internal static int Score(string title, string q)
    {
        if (title == q) return 1000;
        if (title.StartsWith(q, StringComparison.Ordinal)) return 900 - Math.Min(title.Length, 100);
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 800 - Math.Min(title.Length, 100);
        var acronym = new string(words.Select(w => w[0]).ToArray());
        if (q.Length >= 2 && acronym.StartsWith(q, StringComparison.Ordinal)) return 700;
        if (title.Contains(q, StringComparison.Ordinal)) return 600 - title.IndexOf(q, StringComparison.Ordinal);
        if (q.Length >= 3 && IsSubsequence(title.Replace(" ", ""), q.Replace(" ", ""))) return 100;
        return 0;
    }

    private static bool IsSubsequence(string text, string q)
    {
        var i = 0;
        foreach (var c in text)
        {
            if (i < q.Length && c == q[i]) i++;
        }
        return i == q.Length;
    }

    /// <summary>Lower-case, accents removed, punctuation turned into spaces, whitespace collapsed.</summary>
    public static string Normalize(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space && sb.Length > 0)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }
}
