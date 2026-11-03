using System.Text;

namespace Lumen.Core.Library;

/// <summary>A node of Valve's text KeyValues format (used by libraryfolders.vdf and appmanifest_*.acf).</summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, VdfNode>> _ordered = [];

    public string? Value { get; }

    public VdfNode(string? value = null) => Value = value;

    public bool IsObject => Value is null;

    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _ordered;

    public VdfNode? this[string key] => _children.TryGetValue(key, out var n) ? n : null;

    public string? GetString(string key) => this[key]?.Value;

    internal void Add(string key, VdfNode node)
    {
        // Duplicate keys: first one wins for lookup, all kept for enumeration.
        _children.TryAdd(key, node);
        _ordered.Add(new(key, node));
    }
}

/// <summary>
/// Tolerant parser for Valve text KeyValues. Supports quoted/unquoted tokens, escapes,
/// // comments and conditional tags like [$WIN32] (ignored). Never throws on malformed input;
/// returns whatever was parsed.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var pos = 0;
        ParseObject(text, ref pos, root, isRoot: true);
        return root;
    }

    private static void ParseObject(string s, ref int pos, VdfNode target, bool isRoot)
    {
        while (true)
        {
            var key = NextToken(s, ref pos, out var keyKind);
            if (keyKind == TokenKind.End) return;
            if (keyKind == TokenKind.Conditional) continue;
            if (keyKind == TokenKind.Close)
            {
                if (isRoot) continue; // stray brace at root - ignore
                return;
            }
            if (keyKind == TokenKind.Open) // object without key - parse and discard
            {
                ParseObject(s, ref pos, new VdfNode(), isRoot: false);
                continue;
            }

            var save = pos;
            var value = NextToken(s, ref pos, out var valueKind);
            if (valueKind == TokenKind.Conditional)
            {
                save = pos;
                value = NextToken(s, ref pos, out valueKind);
            }

            switch (valueKind)
            {
                case TokenKind.Open:
                    var child = new VdfNode();
                    ParseObject(s, ref pos, child, isRoot: false);
                    target.Add(key!, child);
                    break;
                case TokenKind.String:
                    target.Add(key!, new VdfNode(value));
                    // Skip optional trailing conditional
                    var peekPos = pos;
                    NextToken(s, ref peekPos, out var peekKind);
                    if (peekKind == TokenKind.Conditional) pos = peekPos;
                    break;
                case TokenKind.Close:
                    target.Add(key!, new VdfNode(string.Empty));
                    if (!isRoot) return;
                    break;
                default: // End
                    target.Add(key!, new VdfNode(string.Empty));
                    pos = save;
                    return;
            }
        }
    }

    private enum TokenKind { String, Open, Close, Conditional, End }

    private static string? NextToken(string s, ref int pos, out TokenKind kind)
    {
        // skip whitespace and comments
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            break;
        }

        if (pos >= s.Length) { kind = TokenKind.End; return null; }

        var ch = s[pos];
        if (ch == '{') { pos++; kind = TokenKind.Open; return null; }
        if (ch == '}') { pos++; kind = TokenKind.Close; return null; }
        if (ch == '[')
        {
            var end = s.IndexOf(']', pos);
            pos = end < 0 ? s.Length : end + 1;
            kind = TokenKind.Conditional;
            return null;
        }

        var sb = new StringBuilder();
        if (ch == '"')
        {
            pos++;
            while (pos < s.Length)
            {
                var c = s[pos++];
                if (c == '"') break;
                if (c == '\\' && pos < s.Length)
                {
                    var e = s[pos++];
                    sb.Append(e switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => e,
                    });
                    continue;
                }
                sb.Append(c);
            }
        }
        else
        {
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"'))
                sb.Append(s[pos++]);
        }

        kind = TokenKind.String;
        return sb.ToString();
    }
}
