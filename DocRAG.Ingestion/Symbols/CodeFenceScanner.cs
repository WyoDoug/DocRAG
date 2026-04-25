// // CodeFenceScanner.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Scans chunk content for fenced code blocks and extracts identifier-
///     shaped tokens from inside them. Builds the per-library
///     CodeFenceSymbols set used by the SymbolExtractor's "appears in
///     code fence" keep rule. Recognizes both Markdown triple-backtick
///     fences and HTML &lt;pre&gt;&lt;code&gt; blocks.
/// </summary>
public static class CodeFenceScanner
{
    /// <summary>
    ///     Extract the set of distinct identifier-shaped tokens that appear
    ///     inside code fences anywhere across the supplied chunk contents.
    ///     Returned set is suitable for CorpusContext.CodeFenceSymbols.
    /// </summary>
    public static IReadOnlySet<string> ScanContents(IEnumerable<string> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach(var content in contents.Where(c => !string.IsNullOrEmpty(c)))
            CollectFromContent(content, symbols);
        return symbols;
    }

    private static void CollectFromContent(string content, HashSet<string> output)
    {
        foreach(Match fence in smTripleBacktickRegex.Matches(content))
            CollectIdentifiersFromBlock(fence.Groups[FenceGroupIndex].Value, output);

        foreach(Match fence in smPreCodeRegex.Matches(content))
            CollectIdentifiersFromBlock(fence.Groups[FenceGroupIndex].Value, output);
    }

    private static void CollectIdentifiersFromBlock(string blockContent, HashSet<string> output)
    {
        foreach(Match identifier in smIdentifierRegex.Matches(blockContent).Where(m => m.Value.Length >= MinIdentifierLength))
            AddIdentifierAndLeaves(identifier.Value, output);
    }

    /// <summary>
    ///     Adds the full identifier plus each of its dotted/::-joined
    ///     segments. So "controller.GetAxisStatus" contributes both
    ///     "controller.GetAxisStatus" and "GetAxisStatus" to the set.
    ///     Lets the SymbolExtractor's keep rule fire either by full path
    ///     or by leaf name.
    /// </summary>
    private static void AddIdentifierAndLeaves(string identifier, HashSet<string> output)
    {
        output.Add(identifier);

        var segments = identifier.Split(smSegmentSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach(var segment in segments.Where(s => s.Length >= MinIdentifierLength))
            output.Add(segment);
    }

    // Captures the body of a triple-backtick fence (handles optional language tag).
    private static readonly Regex smTripleBacktickRegex = new(
        @"```[^\r\n]*\r?\n(.*?)```",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    // Captures the body of an HTML <pre><code>...</code></pre> block.
    private static readonly Regex smPreCodeRegex = new(
        @"<pre[^>]*>\s*<code[^>]*>(.*?)</code>\s*</pre>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
    );

    // Matches identifier-shaped tokens (PascalCase, camelCase, snake_case,
    // dotted, ::-joined, ->-joined). Mirrors IdentifierTokenizer's regex
    // but is rooted in code-block content so it does not need callable-
    // shape lookahead.
    private static readonly Regex smIdentifierRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*(?:(?:\.|::|->)[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled
    );

    private static readonly string[] smSegmentSeparators = { ".", "::", "->" };

    private const int FenceGroupIndex = 1;
    private const int MinIdentifierLength = 2;
}
