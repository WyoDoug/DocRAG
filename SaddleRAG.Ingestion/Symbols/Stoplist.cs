// Stoplist.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     Backup filter for the symbol extractor. Words matching the stoplist
///     can never be classified as symbols, even if they happen to look
///     identifier-shaped. This catches the residue of cases the
///     context-driven keep rules miss — single letters, common English
///     stopwords capitalized at the start of a sentence, and programming-
///     prose words that the docs site uses as noun-phrase nouns
///     ("Returns", "Values", "When", "Each").
///
///     The list deliberately stays modest in size. Most filtering happens
///     via the keep rules; this is a final safety net, not the primary
///     mechanism.
/// </summary>
public static class Stoplist
{
    /// <summary>
    ///     Returns true if the candidate (lowercased) is in the stoplist
    ///     and should never be classified as a symbol.
    /// </summary>
    public static bool Contains(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var key = candidate.ToLowerInvariant();
        var result = smStopwords.Contains(key);
        return result;
    }

    /// <summary>
    ///     Profile-aware stoplist check. Returns Global if the candidate is
    ///     in the universal stoplist, Library if it's in the profile's
    ///     stoplist (case-insensitive), else None. Global wins when both
    ///     match — surfaces the more specific diagnostic.
    /// </summary>
    public static StoplistMatch Match(string candidate, LibraryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);

        var inGlobal = Contains(candidate);
        var inLibrary = profile.Stoplist.Contains(candidate, StringComparer.OrdinalIgnoreCase);

        var result = (inGlobal, inLibrary) switch
        {
            (true, _) => StoplistMatch.Global,
            (false, true) => StoplistMatch.Library,
            _ => StoplistMatch.None
        };
        return result;
    }

    private static readonly HashSet<string> smStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Single letters that occasionally survive tokenization
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",

        // English top-100 stopwords plus common sentence-leading words
        "the", "and", "or", "but", "if", "then", "else", "when", "where", "while",
        "for", "with", "without", "from", "to", "into", "onto", "upon", "off",
        "of", "in", "on", "at", "by", "as", "is", "are", "was", "were", "be",
        "been", "being", "have", "has", "had", "do", "does", "did", "will",
        "would", "should", "could", "may", "might", "must", "shall", "can",
        "this", "that", "these", "those", "there", "here", "what", "which",
        "who", "whom", "whose", "why", "how", "all", "any", "some", "not",
        "only", "own", "same", "so", "than", "too", "very", "just", "now", "also",
        "before", "after", "above", "below", "during", "each", "every", "both",
        "between", "among", "such", "more", "most", "less", "many", "much", "few",
        "another", "other", "others", "an", "you", "your", "yours", "we", "us",
        "our", "ours", "they", "them", "their", "theirs", "it", "its", "he", "him",
        "his", "she", "her", "hers",

        // Programming-prose words that look identifier-shaped at sentence start
        // and that documentation writers heavily capitalize
        "represents", "returns", "values", "value", "use", "uses", "used", "using",
        "see", "note", "notes", "example", "examples", "default", "defaults",
        "type", "types", "name", "names", "list", "lists", "method", "methods",
        "function", "functions", "class", "classes", "interface", "interfaces",
        "object", "objects", "member", "members", "field", "fields", "property",
        "properties", "parameter", "parameters", "argument", "arguments", "result",
        "results", "output", "outputs", "input", "inputs", "true", "false", "null",
        "none", "undefined", "yes", "no", "warning", "warnings", "error", "errors",
        "exception", "exceptions", "available", "supports", "supported", "requires",
        "required", "optional", "deprecated", "obsolete", "internal", "private",
        "public", "protected", "static", "abstract", "virtual", "override", "sealed",
        "readonly", "lets", "let", "make", "makes", "set", "sets", "get", "gets",

        // Documentation callout / admonition words. These appear capitalized
        // (often UPPERCASE) at the head of inline notes — IMPORTANT, CAUTION,
        // TIP — and tokenize as identifier-shaped. They are never symbols.
        // (note/warning already covered above.)
        "important", "caution", "tip", "tips", "info", "danger", "notice",
        "remark", "remarks",

        // Common UI button / label text that surfaces in scraped help content
        // (BACK, NEXT, OK, CANCEL etc. as UPPERCASE button labels)
        "back", "next", "previous", "ok", "cancel", "close", "done", "submit",
        "menu", "search", "home", "exit", "edit", "view", "save", "open"
    };
}
