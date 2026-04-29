// ChunkBoundaryAudit.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     Counts chunks where the chunker cut through a dotted identifier.
///     A real cut means chunk N ends with <c>Foo.</c> AND chunk N+1 (in the
///     same page, sequentially) starts with <c>Bar</c>, AND <c>Foo.Bar</c>
///     appears as a dotted identifier somewhere else in the corpus.
///
///     This rules out the dominant false-positive of a naive trailing-period
///     check: most chunks ending with <c>Identifier.</c> are just normal
///     sentence-ends ("...the axis is disabled."), not chunker bugs. By
///     requiring the join to match a known dotted pattern in the corpus,
///     this audit produces an honest signal that responds to chunker fixes.
/// </summary>
public static class ChunkBoundaryAudit
{
    /// <summary>
    ///     Number of chunks in the input that show a corpus-confirmed
    ///     identifier cut. Drops to ~0 when the chunker correctly preserves
    ///     dotted identifiers.
    /// </summary>
    public static int CountIssues(IReadOnlyList<DocChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        return EnumerateIssues(chunks).Count();
    }

    /// <summary>
    ///     Per-issue detail for the same audit CountIssues runs. Yields each
    ///     corpus-confirmed cut as a <see cref="BoundaryIssue"/> with prev/
    ///     next chunk IDs, joined identifier, and surrounding context.
    /// </summary>
    public static IEnumerable<BoundaryIssue> EnumerateIssues(IReadOnlyList<DocChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var dottedIdentifiers = CollectDottedIdentifiers(chunks);

        foreach(var pageGroup in GroupAndOrderByPage(chunks))
        {
            for(var i = 0; i < pageGroup.Count - 1; i++)
            {
                var issue = TryBuildIssue(pageGroup[i], pageGroup[i + 1], dottedIdentifiers);
                if (issue != null)
                    yield return issue;
            }

            // Leading-dot only counts when there's a previous chunk in the
            // same page — without a predecessor, ".NET" / ".gitignore" / etc.
            // at the start of chunk 0 are content, not chunker cuts.
            for(var i = 1; i < pageGroup.Count; i++)
            {
                var leadingDotIssue = TryBuildLeadingDotIssue(pageGroup[i]);
                if (leadingDotIssue != null)
                    yield return leadingDotIssue;
            }
        }
    }

    private static BoundaryIssue? TryBuildIssue(DocChunk prev, DocChunk next, HashSet<string> dottedIdentifiers)
    {
        BoundaryIssue? result = null;
        var trimmedEnd = prev.Content.TrimEnd();
        var endsWithPeriod = trimmedEnd.Length > 0 && trimmedEnd[^1] == Period;
        if (endsWithPeriod)
        {
            var prevLast = LastWordWithoutTrailingPeriod(trimmedEnd);
            var trimmedStart = next.Content.TrimStart();
            var nextFirst = FirstWord(trimmedStart);
            var hasContent = !string.IsNullOrEmpty(prevLast) && !string.IsNullOrEmpty(nextFirst);
            var startsLetterOnBothSides = hasContent
                                       && char.IsLetter(prevLast[0])
                                       && char.IsLetter(nextFirst[0]);
            var joined = startsLetterOnBothSides ? $"{prevLast}.{nextFirst}" : null;
            if (joined != null && dottedIdentifiers.Contains(joined))
                result = new BoundaryIssue
                             {
                                 PrevChunkId = prev.Id,
                                 NextChunkId = next.Id,
                                 PageUrl = prev.PageUrl,
                                 JoinedIdentifier = joined,
                                 PrevTail = TailSnippet(prev.Content, ContextSnippetLength),
                                 NextHead = HeadSnippet(next.Content, ContextSnippetLength)
                             };
        }
        return result;
    }

    private static BoundaryIssue? TryBuildLeadingDotIssue(DocChunk chunk)
    {
        BoundaryIssue? result = null;
        var leading = chunk.Content.TrimStart();
        if (leading.Length > 0 && leading[0] == Period)
            result = new BoundaryIssue
                         {
                             PrevChunkId = LeadingDotPrevPlaceholder,
                             NextChunkId = chunk.Id,
                             PageUrl = chunk.PageUrl,
                             JoinedIdentifier = LeadingDotPlaceholder,
                             PrevTail = string.Empty,
                             NextHead = HeadSnippet(chunk.Content, ContextSnippetLength)
                         };
        return result;
    }

    private static bool IsCorpusConfirmedCut(DocChunk prev, DocChunk next, HashSet<string> dottedIdentifiers) =>
        TryBuildIssue(prev, next, dottedIdentifiers) != null;

    private static string TailSnippet(string text, int max) =>
        text.Length <= max ? text : text[^max..];

    private static string HeadSnippet(string text, int max) =>
        text.Length <= max ? text : text[..max];

    /// <summary>
    ///     Build the set of dotted identifiers known to exist in the corpus.
    ///     Anything matching <c>Foo.Bar</c> or longer dotted paths is added
    ///     as the full path AND each pairwise prefix-leaf (Foo.Bar from
    ///     Foo.Bar.Baz, Bar.Baz from Foo.Bar.Baz) so the boundary-join check
    ///     fires for cuts at any segment in the chain.
    /// </summary>
    private static HashSet<string> CollectDottedIdentifiers(IReadOnlyList<DocChunk> chunks)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach(var chunk in chunks)
        {
            foreach(Match match in smDottedIdentifierRegex.Matches(chunk.Content))
            {
                result.Add(match.Value);
                AddPairwiseSegments(match.Value, result);
            }
        }
        return result;
    }

    private static void AddPairwiseSegments(string dottedPath, HashSet<string> output)
    {
        var segments = dottedPath.Split(Period);
        for(var i = 0; i < segments.Length - 1; i++)
            output.Add($"{segments[i]}.{segments[i + 1]}");
    }

    private static IEnumerable<List<DocChunk>> GroupAndOrderByPage(IReadOnlyList<DocChunk> chunks) =>
        chunks
            .GroupBy(c => c.PageUrl)
            .Select(g => g.OrderBy(c => ParseChunkOrder(c.Id).Main)
                          .ThenBy(c => ParseChunkOrder(c.Id).Sub)
                          .ToList());

    /// <summary>
    ///     Chunk IDs end with <c>{index}</c> or <c>{index}-{subIndex}</c>.
    ///     Parse both for stable sort within a page.
    /// </summary>
    private static (int Main, int Sub) ParseChunkOrder(string chunkId)
    {
        var lastSlash = chunkId.LastIndexOf('/');
        var tail = lastSlash >= 0 ? chunkId[(lastSlash + 1)..] : chunkId;
        var parts = tail.Split('-');
        var main = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        var sub = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
        return (main, sub);
    }

    private static string LastWordWithoutTrailingPeriod(string text)
    {
        var idx = text.Length - 1;
        if (idx >= 0 && text[idx] == Period)
            idx--;
        var end = idx + 1;
        while (idx >= 0 && (char.IsLetterOrDigit(text[idx]) || text[idx] == Underscore))
            idx--;
        var result = text[(idx + 1)..end];
        return result;
    }

    private static string FirstWord(string text)
    {
        var idx = 0;
        while (idx < text.Length && (char.IsLetterOrDigit(text[idx]) || text[idx] == Underscore))
            idx++;
        var result = text[..idx];
        return result;
    }

    // Matches dotted identifier paths: Foo.Bar, Foo.Bar.Baz. Each segment
    // must start with a letter or underscore and use only word characters.
    // Matches WITHIN code-fence blocks and prose alike — we want any
    // appearance of a dotted form to count toward "this is a real path".
    private static readonly Regex smDottedIdentifierRegex = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b",
        RegexOptions.Compiled
    );

    private const char Period = '.';
    private const char Underscore = '_';
    private const int ContextSnippetLength = 80;
    private const string LeadingDotPlaceholder = "(leading-dot)";
    private const string LeadingDotPrevPlaceholder = "(none)";
}
