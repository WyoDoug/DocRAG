// RescrubService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Classification;
using DocRAG.Ingestion.Embedding;
using DocRAG.Ingestion.Symbols;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Ingestion.Recon;

/// <summary>
///     Workhorse for rescrub_library. Re-runs the symbol extractor (and
///     optionally the classifier) over chunks already stored in MongoDB,
///     without re-crawling the source pages or re-embedding chunk text.
///     Builds or rebuilds CodeFenceSymbols (the per-library identifier
///     index for the SymbolExtractor's "appears in code fence" keep
///     rule). Updates the LibraryManifest tracking parser/profile/
///     classifier versions so future rescrubs can auto-detect what
///     changed.
/// </summary>
public class RescrubService
{
    public RescrubService(SymbolExtractor extractor,
                          LlmClassifier classifier,
                          ILogger<RescrubService> logger)
    {
        mExtractor = extractor;
        mClassifier = classifier;
        mLogger = logger;
    }

    private readonly SymbolExtractor mExtractor;
    private readonly LlmClassifier mClassifier;
    private readonly ILogger<RescrubService> mLogger;

    /// <summary>
    ///     Run rescrub against the (libraryId, version). Returns a result
    ///     with counts plus a sample of diffs. Idempotent and resumable.
    /// </summary>
    public async Task<RescrubResult> RescrubAsync(IChunkRepository chunkRepo,
                                                  ILibraryProfileRepository profileRepo,
                                                  ILibraryIndexRepository indexRepo,
                                                  IBm25ShardRepository bm25ShardRepo,
                                                  IExcludedSymbolsRepository excludedRepo,
                                                  string libraryId,
                                                  string version,
                                                  RescrubOptions options,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunkRepo);
        ArgumentNullException.ThrowIfNull(profileRepo);
        ArgumentNullException.ThrowIfNull(indexRepo);
        ArgumentNullException.ThrowIfNull(bm25ShardRepo);
        ArgumentNullException.ThrowIfNull(excludedRepo);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(options);

        var profile = await profileRepo.GetAsync(libraryId, version, ct);
        RescrubResult result;

        if (profile == null)
            result = new RescrubResult { LibraryId = libraryId, Version = version, ReconNeeded = true };
        else
            result = await RunRescrubAsync(chunkRepo, indexRepo, bm25ShardRepo, excludedRepo, libraryId, version, profile, options, ct);

        return result;
    }

    private async Task<RescrubResult> RunRescrubAsync(IChunkRepository chunkRepo,
                                                      ILibraryIndexRepository indexRepo,
                                                      IBm25ShardRepository bm25ShardRepo,
                                                      IExcludedSymbolsRepository excludedRepo,
                                                      string libraryId,
                                                      string version,
                                                      LibraryProfile profile,
                                                      RescrubOptions options,
                                                      CancellationToken ct)
    {
        var existingIndex = await indexRepo.GetAsync(libraryId, version, ct);
        var chunks = await chunkRepo.GetChunksAsync(libraryId, version, ct);
        var scoped = options.MaxChunks.HasValue ? chunks.Take(options.MaxChunks.Value).ToList() : chunks.ToList();

        var doReclassify = ResolveReClassify(options.ReClassify, profile, existingIndex);
        var corpus = BuildCorpusContext(scoped);
        var boundaryIssues = options.BoundaryAudit ? ChunkBoundaryAudit.CountIssues(scoped) : 0;

        var accumulator = new RejectionAccumulator(libraryId, version, Math.Max(scoped.Count, 1));
        var keptCount = 0;

        var processedDiffs = await ProcessChunksAsync(chunkRepo,
                                                      scoped,
                                                      profile,
                                                      corpus,
                                                      doReclassify,
                                                      options.DryRun,
                                                      accumulator,
                                                      count => keptCount += count,
                                                      ct
                                                     );

        var excludedEntries = accumulator.Build();
        var excludedCount = excludedEntries.Count;

        var indexesBuilt = false;
        if (options.RebuildIndexes && !options.DryRun)
        {
            await PersistLibraryIndexAsync(indexRepo, bm25ShardRepo, libraryId, version, profile, corpus, scoped, ct);
            indexesBuilt = true;
        }

        if (!options.DryRun)
        {
            await excludedRepo.DeleteAllForLibraryAsync(libraryId, version, ct);
            await excludedRepo.UpsertManyAsync(excludedEntries, ct);
        }

        var sample = options.DryRun
                         ? processedDiffs
                         : processedDiffs.Take(SampleDiffsCount).ToList();
        var hints = BuildHints(libraryId, version, scoped.Count, keptCount, excludedCount);

        var result = new RescrubResult
                         {
                             LibraryId = libraryId,
                             Version = version,
                             Processed = scoped.Count,
                             Changed = processedDiffs.Count,
                             BoundaryIssues = boundaryIssues,
                             DidReclassify = doReclassify,
                             IndexesBuilt = indexesBuilt,
                             DryRun = options.DryRun,
                             SampleDiffs = sample,
                             ExcludedCount = excludedCount,
                             Hints = hints
                         };

        mLogger.LogInformation("Rescrub {Library}/{Version}: processed={Processed}, changed={Changed}, excluded={Excluded}, reclassify={Reclassify}, dryRun={DryRun}",
                               libraryId,
                               version,
                               scoped.Count,
                               processedDiffs.Count,
                               excludedCount,
                               doReclassify,
                               options.DryRun
                              );

        return result;
    }

    private async Task<List<RescrubDiff>> ProcessChunksAsync(IChunkRepository chunkRepo,
                                                             IReadOnlyList<DocChunk> chunks,
                                                             LibraryProfile profile,
                                                             CorpusContext corpus,
                                                             bool doReclassify,
                                                             bool dryRun,
                                                             RejectionAccumulator accumulator,
                                                             Action<int> keptObserved,
                                                             CancellationToken ct)
    {
        var diffs = new List<RescrubDiff>();
        var updated = new List<DocChunk>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var oneResult = await RescrubOneAsync(chunk, profile, corpus, doReclassify, ct);
            keptObserved(oneResult.Extracted.Symbols.Count);
            foreach(var rejected in oneResult.Extracted.Rejected)
                accumulator.Record(rejected, i, chunk.Content);

            if (oneResult.Diff != null)
            {
                diffs.Add(oneResult.Diff);
                if (!dryRun)
                    updated.Add(oneResult.Chunk);
            }
        }

        if (!dryRun && updated.Count > 0)
            await chunkRepo.UpsertChunksAsync(updated, ct);

        return diffs;
    }

    private async Task<RescrubOneResult> RescrubOneAsync(DocChunk chunk,
                                                         LibraryProfile profile,
                                                         CorpusContext corpus,
                                                         bool doReclassify,
                                                         CancellationToken ct)
    {
        var extracted = mExtractor.Extract(chunk.Content, profile, corpus);

        var newCategory = chunk.Category;
        if (doReclassify)
            newCategory = await ReclassifyAsync(chunk, profile, ct);

        var changed = HasChanged(chunk, extracted, newCategory);
        DocChunk newChunk = chunk;
        RescrubDiff? diff = null;

        if (changed)
        {
            newChunk = chunk with
                           {
                               Symbols = extracted.Symbols,
                               QualifiedName = extracted.PrimaryQualifiedName ?? chunk.QualifiedName,
                               ParserVersion = ParserVersionInfo.Current,
                               Category = newCategory
                           };

            diff = new RescrubDiff
                       {
                           ChunkId = chunk.Id,
                           OldQualifiedName = chunk.QualifiedName,
                           NewQualifiedName = newChunk.QualifiedName,
                           OldSymbolCount = chunk.Symbols.Count,
                           NewSymbolCount = extracted.Symbols.Count,
                           OldCategory = chunk.Category == newCategory ? null : chunk.Category,
                           NewCategory = chunk.Category == newCategory ? null : newCategory
                       };
        }

        var result = new RescrubOneResult
                         {
                             Chunk = newChunk,
                             Diff = diff,
                             Extracted = extracted
                         };
        return result;
    }

    private async Task<Core.Enums.DocCategory> ReclassifyAsync(DocChunk chunk, LibraryProfile profile, CancellationToken ct)
    {
        var pageRecord = new PageRecord
                             {
                                 Id = chunk.Id,
                                 LibraryId = chunk.LibraryId,
                                 Version = chunk.Version,
                                 Url = chunk.PageUrl,
                                 Title = chunk.PageTitle,
                                 RawContent = chunk.Content,
                                 Category = chunk.Category,
                                 FetchedAt = DateTime.UtcNow,
                                 ContentHash = string.Empty
                             };
        var hint = profile.Languages.Count > 0
                       ? string.Join(LanguagesSeparator, profile.Languages)
                       : profile.LibraryId;
        var (category, _) = await mClassifier.ClassifyAsync(pageRecord, hint, ct);
        return category;
    }

    private static bool HasChanged(DocChunk chunk, ExtractedSymbols extracted, Core.Enums.DocCategory newCategory)
    {
        var symbolsChanged = !SymbolListsEqual(chunk.Symbols, extracted.Symbols);
        var nameChanged = !string.Equals(chunk.QualifiedName,
                                         extracted.PrimaryQualifiedName ?? chunk.QualifiedName,
                                         StringComparison.Ordinal);
        var versionChanged = chunk.ParserVersion < ParserVersionInfo.Current;
        var categoryChanged = chunk.Category != newCategory;
        var result = symbolsChanged || nameChanged || versionChanged || categoryChanged;
        return result;
    }

    private static bool SymbolListsEqual(IReadOnlyList<Symbol> a, IReadOnlyList<Symbol> b)
    {
        var equal = a.Count == b.Count;
        if (equal)
        {
            for (int i = 0; i < a.Count && equal; i++)
                equal = string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)
                     && a[i].Kind == b[i].Kind
                     && string.Equals(a[i].Container, b[i].Container, StringComparison.Ordinal);
        }
        return equal;
    }

    private bool ResolveReClassify(bool? explicitChoice, LibraryProfile profile, LibraryIndex? existingIndex)
    {
        var result = explicitChoice ?? AutoDetectReClassifyOrFalse(profile, existingIndex);
        return result;
    }

    private bool AutoDetectReClassifyOrFalse(LibraryProfile profile, LibraryIndex? existingIndex)
    {
        var result = existingIndex is { } index && AutoDetectReClassify(index.Manifest, profile);
        return result;
    }

    private bool AutoDetectReClassify(LibraryManifest manifest, LibraryProfile profile)
    {
        var profileChanged = !string.Equals(manifest.LastProfileHash,
                                            LibraryProfileService.ComputeHash(profile),
                                            StringComparison.Ordinal);
        var classifierChanged = !string.Equals(manifest.LastClassifierVersion,
                                               mClassifier.GetCurrentVersion(),
                                               StringComparison.Ordinal);
        var result = profileChanged || classifierChanged;
        return result;
    }

    private static CorpusContext BuildCorpusContext(IReadOnlyList<DocChunk> chunks)
    {
        var contents = chunks.Select(c => c.Content);
        var fenceSymbols = CodeFenceScanner.ScanContents(contents);
        var proseCounts = CountProseMentions(chunks);

        var result = new CorpusContext
                         {
                             CodeFenceSymbols = fenceSymbols,
                             ProseMentionCounts = proseCounts
                         };
        return result;
    }

    private static IReadOnlyDictionary<string, int> CountProseMentions(IReadOnlyList<DocChunk> chunks)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach(var chunk in chunks)
        {
            var stripped = StripCodeFences(chunk.Content);
            var matches = smCapitalizedIdentifierRegex.Matches(stripped);
            foreach(Match m in matches.Where(m => m.Value.Length >= MinIdentifierLength))
                counts[m.Value] = counts.TryGetValue(m.Value, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    private static string StripCodeFences(string content)
    {
        var stripped = smTripleBacktickRegex.Replace(content, string.Empty);
        stripped = smPreCodeRegex.Replace(stripped, string.Empty);
        return stripped;
    }

    private async Task PersistLibraryIndexAsync(ILibraryIndexRepository indexRepo,
                                                IBm25ShardRepository bm25ShardRepo,
                                                string libraryId,
                                                string version,
                                                LibraryProfile profile,
                                                CorpusContext corpus,
                                                IReadOnlyList<DocChunk> chunks,
                                                CancellationToken ct)
    {
        var manifest = new LibraryManifest
                           {
                               LastParserVersion = ParserVersionInfo.Current,
                               LastProfileHash = LibraryProfileService.ComputeHash(profile),
                               LastClassifierVersion = mClassifier.GetCurrentVersion(),
                               LastBuiltUtc = DateTime.UtcNow
                           };

        var bm25Build = Bm25IndexBuilder.Build(libraryId, version, chunks);
        await bm25ShardRepo.ReplaceShardsAsync(libraryId, version, bm25Build.Shards, ct);

        var index = new LibraryIndex
                        {
                            Id = LibraryIndexRepository.MakeId(libraryId, version),
                            LibraryId = libraryId,
                            Version = version,
                            Bm25 = bm25Build.Stats,
                            CodeFenceSymbols = corpus.CodeFenceSymbols.ToList(),
                            Manifest = manifest
                        };

        await indexRepo.UpsertAsync(index, ct);
    }

    private static readonly Regex smCapitalizedIdentifierRegex = new(
        @"\b[A-Z][A-Za-z0-9_]+\b",
        RegexOptions.Compiled
    );

    private static readonly Regex smTripleBacktickRegex = new(
        @"```[^\r\n]*\r?\n.*?```",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    private static readonly Regex smPreCodeRegex = new(
        @"<pre[^>]*>.*?</pre>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase
    );

    private sealed record RescrubOneResult
    {
        public required DocChunk Chunk { get; init; }
        public required RescrubDiff? Diff { get; init; }
        public required ExtractedSymbols Extracted { get; init; }
    }

    private static IReadOnlyList<string> BuildHints(string libraryId,
                                                     string version,
                                                     int processedChunks,
                                                     int keptCount,
                                                     int excludedCount)
    {
        var totalCandidates = keptCount + excludedCount;
        var ratio = totalCandidates > 0 ? (double) excludedCount / totalCandidates : 0;
        var trigger = excludedCount >= HintMinAbsoluteExclusions
                   && ratio >= HintMinExclusionRatio;
        IReadOnlyList<string> result = trigger
                                            ? new[]
                                                  {
                                                      $"Rescrub complete: {processedChunks} chunks, {keptCount} candidate tokens kept, {excludedCount} excluded as likely noise.",
                                                      "If list_classes/list_functions output looks off, refine per-library:",
                                                      $"  list_excluded_symbols(library='{libraryId}', version='{version}') — review rejections with sample sentences",
                                                      "  add_to_likely_symbols(...) — promote tokens that ARE real symbols",
                                                      "  add_to_stoplist(...) — demote tokens that are noise",
                                                      "  Then call rescrub_library again to apply.",
                                                      "These steps are OPTIONAL — only worth running if symbol coverage looks wrong on spot-check."
                                                  }
                                            : Array.Empty<string>();
        return result;
    }

    private const int SampleDiffsCount = 10;
    private const int MinIdentifierLength = 2;
    private const string LanguagesSeparator = ", ";
    private const int HintMinAbsoluteExclusions = 20;
    private const double HintMinExclusionRatio = 0.05;
}
