// // IdentifierTokenizer.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Tokenizes chunk text into candidate identifiers. Recognizes
///     PascalCase, camelCase, snake_case, SCREAMING_SNAKE, kebab-case,
///     dotted paths (Foo.Bar.Baz), ::-joined paths (std::vector::push),
///     -&gt;-joined paths (obj-&gt;member), generic shapes (Foo&lt;T&gt;),
///     and callable shapes (Foo()). Strips trailing/leading punctuation
///     before classification so that prose mentions like "AxisFault." do
///     not survive as tokens with the period attached.
///
///     Kept deliberately library-agnostic: shape-only recognition. The
///     SymbolExtractor consumes the candidates and decides keep/reject
///     using LibraryProfile + corpus context.
/// </summary>
public static class IdentifierTokenizer
{
    /// <summary>
    ///     Produce candidate tokens from chunk content.
    /// </summary>
    public static IReadOnlyList<TokenCandidate> Tokenize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var candidates = new List<TokenCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var matches = smTokenRegex.Matches(content);
        foreach(var token in matches.Select(m => BuildCandidate(m, content)).Where(t => t != null).Cast<TokenCandidate>())
        {
            var dedupKey = MakeDedupKey(token);
            if (!seen.Contains(dedupKey))
            {
                seen.Add(dedupKey);
                candidates.Add(token);
            }
        }

        return candidates;
    }

    private static TokenCandidate? BuildCandidate(Match match, string content)
    {
        TokenCandidate? result = null;

        var raw = match.Value;
        var trimmed = TrimEdgePunctuation(raw);

        if (!string.IsNullOrEmpty(trimmed) && IsIdentifierStartChar(trimmed[0]))
        {
            var (container, leaf) = SplitContainer(trimmed);

            var startIndex = match.Index;
            var endIndex = match.Index + match.Length;

            var hasCallable = HasFollowingChar(content, endIndex, OpenParen);
            var hasGeneric = HasFollowingChar(content, endIndex, OpenAngle);
            var declaredKeyword = ReadPrecedingDeclaredKeyword(content, startIndex);

            result = new TokenCandidate
                         {
                             Name = trimmed,
                             LeafName = leaf,
                             Container = container,
                             HasCallableShape = hasCallable,
                             HasGenericShape = hasGeneric,
                             IsDeclared = declaredKeyword != null,
                             DeclaredFormKeyword = declaredKeyword
                         };
        }

        return result;
    }

    private static string TrimEdgePunctuation(string raw)
    {
        var trimmed = raw.TrimEnd(smTrailingPunctuation).TrimStart(smTrailingPunctuation);
        return trimmed;
    }

    private static (string? Container, string LeafName) SplitContainer(string name)
    {
        (string? Container, string LeafName) result = (null, name);

        // Try separators in order of length (longest first) so "::" is preferred over ":".
        foreach(var separator in smSeparators)
        {
            var idx = name.LastIndexOf(separator, StringComparison.Ordinal);
            if (idx > 0 && idx + separator.Length < name.Length && result.Container == null)
                result = (name[..idx], name[(idx + separator.Length)..]);
        }

        return result;
    }

    private static bool HasFollowingChar(string content, int endIndex, char target)
    {
        bool result = false;
        if (endIndex >= 0 && endIndex < content.Length)
            result = content[endIndex] == target;
        return result;
    }

    private static string? ReadPrecedingDeclaredKeyword(string content, int startIndex)
    {
        // Scan backward, skipping whitespace, then read the preceding word.
        int pos = startIndex - 1;
        while (pos >= 0 && char.IsWhiteSpace(content[pos]))
            pos--;

        var wordEnd = pos + 1;
        while (pos >= 0 && (char.IsLetter(content[pos]) || content[pos] == Underscore))
            pos--;

        var wordStart = pos + 1;
        string? result = null;
        if (wordStart < wordEnd)
        {
            var word = content.Substring(wordStart, wordEnd - wordStart).ToLowerInvariant();
            if (smDeclaredKeywords.Contains(word))
                result = word;
        }

        return result;
    }

    private static bool IsIdentifierStartChar(char c) =>
        char.IsLetter(c) || c == Underscore;

    private static string MakeDedupKey(TokenCandidate candidate) =>
        $"{candidate.Name}|{(candidate.IsDeclared ? '1' : '0')}|{(candidate.HasCallableShape ? '1' : '0')}|{(candidate.HasGenericShape ? '1' : '0')}";

    // Token regex:
    //   - Starts with letter or underscore
    //   - Followed by word chars (letters/digits/underscore) and optional dash segments (kebab-case)
    //   - Optional dotted/::-joined/->joined extensions, each segment again identifier-shaped
    //   - Anchors at word boundary; does NOT include trailing dots/colons (the bug we're fixing)
    private static readonly Regex smTokenRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z][A-Za-z0-9_]*)*(?:(?:\.|::|->)[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled
    );

    private static readonly char[] smTrailingPunctuation =
    {
        '.', ',', ';', ':', '!', '?', ')', '(', ']', '[', '>', '<', '"', '\'', '`'
    };

    private static readonly string[] smSeparators = { SeparatorDoubleColon, SeparatorArrow, SeparatorDot, SeparatorColon };

    private static readonly HashSet<string> smDeclaredKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "interface", "struct", "enum", "record", "type", "def", "function"
    };

    private const char OpenParen = '(';
    private const char OpenAngle = '<';
    private const char Underscore = '_';
    private const string SeparatorDot = ".";
    private const string SeparatorColon = ":";
    private const string SeparatorDoubleColon = "::";
    private const string SeparatorArrow = "->";
}
