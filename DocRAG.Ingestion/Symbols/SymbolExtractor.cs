// SymbolExtractor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Decides which tokenized identifier candidates survive as symbols
///     and classifies each into a SymbolKind. Driven by the LibraryProfile
///     produced by reconnaissance plus, when available, a CorpusContext
///     carrying cross-chunk facts (code-fence symbols, prose mention
///     counts).
///
///     Keep rules (any one is sufficient unless rejected by the stoplist):
///       1. Appears in profile.LikelySymbols (recon's boost set).
///       2. Appears in corpus.CodeFenceSymbols (somewhere in the corpus,
///          inside a fenced code block).
///       3. Has a Container — appeared in X.Member or X::Member shape.
///       4. Was preceded by a declared-form keyword (class, interface,
///          struct, enum, record, type, def, function).
///       5. Has internal structure prose words don't have: "_", ".",
///          "::", "->", mid-word capital, callable shape "(", or generic
///          shape "&lt;".
///       6. Appears at or above ProseMentionThreshold capitalized in
///          prose corpus-wide AND is not in the stoplist.
///
///     Reject overrides: stoplist match drops a candidate regardless of
///     other signals.
/// </summary>
public class SymbolExtractor
{
    public SymbolExtractor(int proseMentionThreshold = DefaultProseMentionThreshold)
    {
        if (proseMentionThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(proseMentionThreshold),
                                                  proseMentionThreshold,
                                                  "ProseMentionThreshold must be >= 1");
        mProseMentionThreshold = proseMentionThreshold;
    }

    private readonly int mProseMentionThreshold;

    /// <summary>
    ///     Extract symbols from a single chunk's content.
    /// </summary>
    public ExtractedSymbols Extract(string content,
                                    LibraryProfile profile,
                                    CorpusContext? corpusContext = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(profile);

        var corpus = corpusContext ?? CorpusContext.Empty;
        var tokens = IdentifierTokenizer.Tokenize(content);
        var likelySet = BuildLikelySet(profile);

        var kept = new List<Symbol>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach(var token in tokens.Where(t => IsAdmissible(t, likelySet, corpus)))
        {
            var symbol = Classify(token, likelySet, profile);
            if (!seenNames.Contains(symbol.Name))
            {
                seenNames.Add(symbol.Name);
                kept.Add(symbol);
            }
        }

        var primary = PickPrimaryName(kept);
        var result = new ExtractedSymbols
                         {
                             Symbols = kept,
                             PrimaryQualifiedName = primary
                         };
        return result;
    }

    private bool IsAdmissible(TokenCandidate token, HashSet<string> likelySet, CorpusContext corpus)
    {
        var rejected = Stoplist.Contains(token.LeafName)
                    || Stoplist.Contains(token.Name)
                    || UnitsLookup.IsUnit(token.LeafName)
                    || UnitsLookup.IsUnit(token.Name)
                    || token.Name.Length < MinIdentifierLength;
        var result = !rejected && ShouldKeep(token, likelySet, corpus);
        return result;
    }

    private bool ShouldKeep(TokenCandidate token,
                            HashSet<string> likelySet,
                            CorpusContext corpus)
    {
        var name = token.Name;
        var leaf = token.LeafName;

        bool inLikely = likelySet.Contains(name) || likelySet.Contains(leaf);
        bool inCodeFence = corpus.CodeFenceSymbols.Contains(name) || corpus.CodeFenceSymbols.Contains(leaf);
        bool hasContainer = !string.IsNullOrEmpty(token.Container);
        bool hasInternalStructure = HasInternalStructure(name);
        bool proseFrequent = IsProseFrequent(name, corpus) || IsProseFrequent(leaf, corpus);

        var result = token.IsDeclared
                  || inLikely
                  || inCodeFence
                  || hasContainer
                  || hasInternalStructure
                  || token.HasCallableShape
                  || token.HasGenericShape
                  || proseFrequent;
        return result;
    }

    private bool IsProseFrequent(string identifier, CorpusContext corpus)
    {
        bool result = false;
        if (!IsLikelyAbbreviation(identifier)
            && corpus.ProseMentionCounts.TryGetValue(identifier, out var count))
            result = count >= mProseMentionThreshold;
        return result;
    }

    /// <summary>
    ///     All-uppercase tokens of length &lt;= ShortAbbreviationMaxLength are
    ///     more likely to be acronyms or abbreviations than symbols (RAM, BD,
    ///     PC, NET, TCP, UTF). They are NOT admissible via the prose-frequent
    ///     rule alone — they need a stronger signal (likely-symbols list,
    ///     code-fence appearance, declared form, callable shape).
    /// </summary>
    private static bool IsLikelyAbbreviation(string identifier)
    {
        var hasContent = !string.IsNullOrEmpty(identifier);
        var allUpperOrDigit = hasContent && identifier.All(c => char.IsUpper(c) || char.IsDigit(c));
        var result = allUpperOrDigit && identifier.Length <= ShortAbbreviationMaxLength;
        return result;
    }

    private static bool HasInternalStructure(string name)
    {
        bool result = name.Contains(Underscore, StringComparison.Ordinal)
                   || name.Contains(Dot, StringComparison.Ordinal)
                   || name.Contains(DoubleColon, StringComparison.Ordinal)
                   || name.Contains(Arrow, StringComparison.Ordinal)
                   || HasMidWordCapital(name);
        return result;
    }

    /// <summary>
    ///     True when name contains a real camelCase boundary: either a
    ///     lowercase-then-uppercase transition (the "x|Y" boundary in
    ///     MoveLinear), or an uppercase-cluster followed by a lowercase
    ///     cluster (the "XX|Yzz" boundary in PIDController, XMLParser,
    ///     IOError). All-uppercase tokens (IMPORTANT, RAM, BD, CPU) and
    ///     unit-style suffixes (GHz, MHz, kHz) do NOT have a camelCase
    ///     boundary and return false.
    /// </summary>
    private static bool HasMidWordCapital(string name)
    {
        bool found = false;
        for (int i = 1; i < name.Length && !found; i++)
        {
            var prev = name[i - 1];
            var curr = name[i];
            var lowerToUpper = char.IsLower(prev) && char.IsUpper(curr);
            var acronymThenLowerCluster = i >= 2
                                       && char.IsUpper(name[i - 2])
                                       && char.IsUpper(prev)
                                       && char.IsLower(curr)
                                       && i + 1 < name.Length
                                       && char.IsLower(name[i + 1]);
            found = lowerToUpper || acronymThenLowerCluster;
        }
        return found;
    }

    private static Symbol Classify(TokenCandidate token, HashSet<string> likelySet, LibraryProfile profile)
    {
        var kind = ResolveKind(token, likelySet, profile);
        var result = new Symbol
                         {
                             Name = token.Name,
                             Kind = kind,
                             Container = token.Container
                         };
        return result;
    }

    private static SymbolKind ResolveKind(TokenCandidate token, HashSet<string> likelySet, LibraryProfile profile)
    {
        var declared = (token.IsDeclared, token.DeclaredFormKeyword?.ToLowerInvariant());

        var kind = declared switch
        {
            (true, KeywordClass) => SymbolKind.Type,
            (true, KeywordInterface) => SymbolKind.Type,
            (true, KeywordStruct) => SymbolKind.Type,
            (true, KeywordRecord) => SymbolKind.Type,
            (true, KeywordType) => SymbolKind.Type,
            (true, KeywordEnum) => SymbolKind.Enum,
            (true, KeywordDef) => SymbolKind.Function,
            (true, KeywordFunction) => SymbolKind.Function,
            var _ => ResolveKindFromShape(token, likelySet, profile)
        };
        return kind;
    }

    private static SymbolKind ResolveKindFromShape(TokenCandidate token, HashSet<string> likelySet, LibraryProfile profile)
    {
        var shape = (token.HasCallableShape,
                     token.HasGenericShape,
                     HasContainer: !string.IsNullOrEmpty(token.Container),
                     InLikely: likelySet.Contains(token.Name) || likelySet.Contains(token.LeafName));

        var kind = shape switch
        {
            (true, _, _, _) => SymbolKind.Function,
            (false, true, _, _) => SymbolKind.Type,
            (false, false, true, _) => SymbolKind.Property,
            (false, false, false, true) => SymbolKind.Type,
            var _ => InferKindFromCasing(token, profile)
        };
        return kind;
    }

    /// <summary>
    ///     Last-resort kind guess based on the profile's casing conventions.
    ///     A PascalCase leaf with mid-word capital and no other signal is
    ///     classified as Type when the profile says types are PascalCase.
    ///     A snake_case leaf is classified as Function when the profile says
    ///     methods/functions are snake_case (Python-style). Anything else
    ///     stays Unknown.
    /// </summary>
    private static SymbolKind InferKindFromCasing(TokenCandidate token, LibraryProfile profile)
    {
        var leaf = token.LeafName;

        var isPascal = MatchesPascalCase(leaf);
        var isSnake = MatchesSnakeCase(leaf);

        var typesArePascal = string.Equals(profile.Casing.Types, CasingPascal, StringComparison.OrdinalIgnoreCase);
        var methodsAreSnake = string.Equals(profile.Casing.Methods, CasingSnake, StringComparison.OrdinalIgnoreCase);

        var fingerprint = (isPascal, isSnake, typesArePascal, methodsAreSnake);

        var kind = fingerprint switch
        {
            (true, _, true, _) => SymbolKind.Type,
            (_, true, _, true) => SymbolKind.Function,
            var _ => SymbolKind.Unknown
        };
        return kind;
    }

    private static bool MatchesPascalCase(string leaf)
    {
        var hasContent = !string.IsNullOrEmpty(leaf);
        var startsUpper = hasContent && char.IsUpper(leaf[0]);
        var hasMidCapital = hasContent && HasMidWordCapital(leaf);
        var hasNoUnderscore = hasContent && !leaf.Contains(Underscore, StringComparison.Ordinal);
        var result = startsUpper && hasMidCapital && hasNoUnderscore;
        return result;
    }

    private static bool MatchesSnakeCase(string leaf)
    {
        var hasContent = !string.IsNullOrEmpty(leaf);
        var hasUnderscore = hasContent && leaf.Contains(Underscore, StringComparison.Ordinal);
        var allLowerOrDigitOrUnderscore = hasContent && leaf.All(c => char.IsLower(c) || char.IsDigit(c) || c == Underscore);
        var result = hasUnderscore && allLowerOrDigitOrUnderscore;
        return result;
    }

    private static HashSet<string> BuildLikelySet(LibraryProfile profile)
    {
        var result = new HashSet<string>(profile.LikelySymbols, StringComparer.Ordinal);
        return result;
    }

    private static string? PickPrimaryName(IReadOnlyList<Symbol> symbols)
    {
        string? result = null;
        if (symbols.Count > 0)
        {
            var ordered = symbols
                          .Select((s, i) => (Symbol: s, Index: i, Rank: RankKind(s.Kind)))
                          .OrderBy(t => t.Rank)
                          .ThenBy(t => t.Index)
                          .First();
            result = ordered.Symbol.Name;
        }

        return result;
    }

    private static int RankKind(SymbolKind kind) => kind switch
    {
        SymbolKind.Type => 0,
        SymbolKind.Enum => 1,
        SymbolKind.Function => 2,
        SymbolKind.Property => 3,
        SymbolKind.Parameter => 4,
        SymbolKind.Namespace => 5,
        var _ => 6
    };

    private const int DefaultProseMentionThreshold = 3;
    private const int MinIdentifierLength = 2;
    private const int ShortAbbreviationMaxLength = 4;

    private const char Underscore = '_';
    private const char Dot = '.';
    private const string DoubleColon = "::";
    private const string Arrow = "->";

    private const string CasingPascal = "PascalCase";
    private const string CasingSnake = "snake_case";

    private const string KeywordClass = "class";
    private const string KeywordInterface = "interface";
    private const string KeywordStruct = "struct";
    private const string KeywordRecord = "record";
    private const string KeywordType = "type";
    private const string KeywordEnum = "enum";
    private const string KeywordDef = "def";
    private const string KeywordFunction = "function";
}
