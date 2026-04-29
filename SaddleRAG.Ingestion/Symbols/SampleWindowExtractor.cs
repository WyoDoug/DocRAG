// SampleWindowExtractor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     Pulls a short corpus snippet around the first occurrence of a
///     token in chunk content. Used by the rejection accumulator to
///     attach 2-3 sample sentences to each ExcludedSymbol record so the
///     calling LLM can decide whether the rejection was correct.
///
///     The output is whitespace-normalized (newlines/tabs become single
///     spaces, multiple spaces collapse) and capped at WindowMaxChars
///     total characters. Returns null when the token is not present in
///     the content (defensive — should not happen if the caller's chunk
///     index is consistent, but we don't crash a rescrub over it).
/// </summary>
public static class SampleWindowExtractor
{
    /// <summary>
    ///     Extract a window around the first occurrence of <paramref name="token"/>
    ///     in <paramref name="content"/>. Token comparison is Ordinal
    ///     (case-sensitive) and uses the exact token text.
    /// </summary>
    public static string? Extract(string content, string token)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var index = content.IndexOf(token, StringComparison.Ordinal);
        string? result = null;
        if (index >= 0)
            result = BuildWindow(content, token, index);
        return result;
    }

    private static string BuildWindow(string content, string token, int index)
    {
        var leftStart = ExpandToWordBoundary(content, Math.Max(0, index - WindowSideChars), expandLeft: true);
        var rightEnd = ExpandToWordBoundary(content, Math.Min(content.Length, index + token.Length + WindowSideChars), expandLeft: false);

        var raw = content.Substring(leftStart, rightEnd - leftStart);
        var collapsed = smWhitespaceRegex.Replace(raw, " ").Trim();

        var capped = collapsed.Length <= WindowMaxChars
                         ? collapsed
                         : TruncateAroundToken(collapsed, token);
        return capped;
    }

    /// <summary>
    ///     Walk left or right to the nearest whitespace so the window edge
    ///     sits on a word boundary instead of mid-word. Idempotent at the
    ///     content edges.
    /// </summary>
    private static int ExpandToWordBoundary(string content, int position, bool expandLeft)
    {
        var result = position;
        if (expandLeft)
        {
            while (result > 0 && !char.IsWhiteSpace(content[result - 1]))
                result--;
        }
        else
        {
            while (result < content.Length && !char.IsWhiteSpace(content[result]))
                result++;
        }
        return result;
    }

    /// <summary>
    ///     If the trimmed window still exceeds the cap, truncate from the
    ///     longer side while keeping the token visible.
    /// </summary>
    private static string TruncateAroundToken(string text, string token)
    {
        var tokenIndex = text.IndexOf(token, StringComparison.Ordinal);
        var beforeLen = tokenIndex;
        var afterLen = text.Length - tokenIndex - token.Length;
        var available = WindowMaxChars - token.Length;
        var halfAvailable = available / 2;

        var keepBefore = Math.Min(beforeLen, halfAvailable);
        var keepAfter = Math.Min(afterLen, available - keepBefore);
        keepBefore = Math.Min(beforeLen, available - keepAfter);

        var startIdx = tokenIndex - keepBefore;
        var endIdx = tokenIndex + token.Length + keepAfter;
        var result = text.Substring(startIdx, endIdx - startIdx).Trim();
        return result;
    }

    private static readonly Regex smWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private const int WindowSideChars = 100;
    private const int WindowMaxChars = 200;
}
