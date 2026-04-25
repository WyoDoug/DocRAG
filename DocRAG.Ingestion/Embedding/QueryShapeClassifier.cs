// // QueryShapeClassifier.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Cheap classifier deciding whether a query is identifier-shaped
///     (PascalCase, dotted, ::-joined, callable, snake_case) or prose-
///     shaped. SearchTools uses the result to gate the LLM reranker —
///     identifier queries skip rerank entirely (the hybrid score already
///     wins on them), prose queries can use rerank when configured.
/// </summary>
public static class QueryShapeClassifier
{
    /// <summary>
    ///     Returns true when the query carries any identifier signal
    ///     (CamelCase, dotted path, callable shape, snake_case
    ///     identifier, or ::-joined name). Returns false for prose-only
    ///     queries like "how do I configure homing".
    /// </summary>
    public static bool IsIdentifierShaped(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var hasDotted = smDottedPathRegex.IsMatch(query);
        var hasDoubleColon = smDoubleColonRegex.IsMatch(query);
        var hasCallable = smCallableRegex.IsMatch(query);
        var hasCamel = smCamelCaseRegex.IsMatch(query);
        var hasSnake = smSnakeCaseRegex.IsMatch(query);

        var result = hasDotted || hasDoubleColon || hasCallable || hasCamel || hasSnake;
        return result;
    }

    private static readonly Regex smDottedPathRegex = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]+\b",
        RegexOptions.Compiled
    );

    private static readonly Regex smDoubleColonRegex = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*::[A-Za-z_][A-Za-z0-9_]+\b",
        RegexOptions.Compiled
    );

    // Callable shape: an identifier immediately followed by '('.
    private static readonly Regex smCallableRegex = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*\(",
        RegexOptions.Compiled
    );

    // CamelCase / PascalCase: at least one mid-word capital, no separators.
    private static readonly Regex smCamelCaseRegex = new(
        @"\b[A-Za-z]+[a-z]+[A-Z][A-Za-z0-9]*\b",
        RegexOptions.Compiled
    );

    // snake_case: contains underscore between identifier chars.
    private static readonly Regex smSnakeCaseRegex = new(
        @"\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+\b",
        RegexOptions.Compiled
    );
}
