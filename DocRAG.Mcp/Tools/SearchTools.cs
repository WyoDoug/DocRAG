// // SearchTools.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Embedding;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for searching documentation content. Uses hybrid scoring
///     (vector cosine ∥ BM25 keyword overlap) with query-shape gating to
///     skip the LLM reranker on identifier queries (where it consistently
///     hurts quality and costs 2–7s of latency for nothing). When a
///     reranker IS used, its score is blended with hybrid (not replaced),
///     so the reranker's mistakes stay recoverable.
/// </summary>
[McpServerToolType]
public static class SearchTools
{
    [McpServerTool(Name = "search_docs")]
    [Description("Search documentation using natural language. Works across all ingested libraries " +
                 "or filtered to a specific one. Filter by category to narrow results: " +
                 "Overview (concepts, architecture, getting started), " +
                 "HowTo (tutorials, guides, walkthroughs), " +
                 "Sample (code examples, demos), " +
                 "ApiReference (class/method/property docs), " +
                 "ChangeLog (release notes, migration guides). " +
                 "Omit category to search everything."
                )]
    public static async Task<string> SearchDocs(IVectorSearchProvider vectorSearch,
                                                IEmbeddingProvider embeddingProvider,
                                                IReRanker reRanker,
                                                RepositoryFactory repositoryFactory,
                                                IOptions<RankingSettings> rankingOptions,
                                                [Description("Natural language search query")]
                                                string query,
                                                [Description("Library identifier — omit to search all libraries")]
                                                string? library = null,
                                                [Description("Filter to category: Overview, HowTo, Sample, ApiReference, ChangeLog"
                                                            )]
                                                string? category = null,
                                                [Description("Specific version — defaults to current")]
                                                string? version = null,
                                                [Description("Maximum results (default 5)")]
                                                int maxResults = 5,
                                                [Description("Optional database profile name")]
                                                string? profile = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(reRanker);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(rankingOptions);
        ArgumentException.ThrowIfNullOrEmpty(query);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var resolvedVersion = await ResolveIfNeeded(libraryRepository, library, version, ct);
        string json;

        if (library != null && resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            json = await ExecuteSearchAsync(vectorSearch,
                                            embeddingProvider,
                                            reRanker,
                                            repositoryFactory,
                                            rankingOptions.Value,
                                            query,
                                            library,
                                            resolvedVersion,
                                            category,
                                            maxResults,
                                            profile,
                                            ct
                                           );
        }

        return json;
    }

    [McpServerTool(Name = "get_class_reference")]
    [Description("Look up API reference for a specific class or type. " +
                 "If library is omitted, searches across ALL libraries. " +
                 "Tries exact match first, then fuzzy match."
                )]
    public static async Task<string> GetClassReference(RepositoryFactory repositoryFactory,
                                                       [Description("Class name (partial or full)")]
                                                       string className,
                                                       [Description("Library identifier — omit to search all libraries"
                                                                   )]
                                                       string? library = null,
                                                       [Description("Specific version — defaults to current")]
                                                       string? version = null,
                                                       [Description("Optional database profile name")]
                                                       string? profile = null,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(className);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepository = repositoryFactory.GetChunkRepository(profile);
        var resolvedVersion = await ResolveIfNeeded(libraryRepository, library, version, ct);
        string json;

        if (library != null && resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
        {
            var results = await FetchClassReferenceAsync(libraryRepository, chunkRepository, library, resolvedVersion, className, ct);
            var response = results.Select(c => new
                                                   {
                                                       c.LibraryId,
                                                       c.QualifiedName,
                                                       c.PageTitle,
                                                       c.SectionPath,
                                                       c.PageUrl,
                                                       c.Content
                                                   }
                                         );
            json = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return json;
    }

    [McpServerTool(Name = "get_library_overview")]
    [Description("Get an overview of what a library is and how to get started. " +
                 "Returns Overview-category documentation chunks. " +
                 "If no Overview content exists, returns the most relevant chunks of any category."
                )]
    public static async Task<string> GetLibraryOverview(IVectorSearchProvider vectorSearch,
                                                        IEmbeddingProvider embeddingProvider,
                                                        RepositoryFactory repositoryFactory,
                                                        [Description("Library identifier")] string library,
                                                        [Description("Specific version — defaults to current")]
                                                        string? version = null,
                                                        [Description("Optional database profile name")]
                                                        string? profile = null,
                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var resolvedVersion = await LibraryTools.ResolveVersionAsync(libraryRepository, library, version, ct);
        string json;

        if (resolvedVersion == null)
            json = LibraryNotFoundJson(library);
        else
            json = await BuildLibraryOverviewAsync(vectorSearch, embeddingProvider, library, resolvedVersion, profile, ct);

        return json;
    }

    private static async Task<string> BuildLibraryOverviewAsync(IVectorSearchProvider vectorSearch,
                                                                IEmbeddingProvider embeddingProvider,
                                                                string library,
                                                                string resolvedVersion,
                                                                string? profile,
                                                                CancellationToken ct)
    {
        var query = $"{library} overview getting started introduction";
        var embeddings = await embeddingProvider.EmbedAsync([query], ct);

        var overviewFilter = new VectorSearchFilter
                                 {
                                     Profile = profile,
                                     LibraryId = library,
                                     Version = resolvedVersion,
                                     Category = DocCategory.Overview
                                 };

        var results = await vectorSearch.SearchAsync(embeddings[0], overviewFilter, MaxOverviewResults, ct);

        if (results.Count == 0)
        {
            var fallbackFilter = new VectorSearchFilter
                                     {
                                         Profile = profile,
                                         LibraryId = library,
                                         Version = resolvedVersion
                                     };
            results = await vectorSearch.SearchAsync(embeddings[0], fallbackFilter, MaxOverviewResults, ct);
        }

        var response = results.Select(r => new
                                               {
                                                   r.Chunk.LibraryId,
                                                   r.Chunk.Category,
                                                   r.Chunk.PageTitle,
                                                   r.Chunk.SectionPath,
                                                   r.Chunk.PageUrl,
                                                   r.Chunk.Content,
                                                   r.Score
                                               }
                                     );

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static async Task<IReadOnlyList<DocChunk>> FetchClassReferenceAsync(ILibraryRepository libraryRepository,
                                                                                IChunkRepository chunkRepository,
                                                                                string? library,
                                                                                string? resolvedVersion,
                                                                                string className,
                                                                                CancellationToken ct)
    {
        IReadOnlyList<DocChunk> results;

        if (library != null)
            results = await chunkRepository.FindByQualifiedNameAsync(library,
                                                                     resolvedVersion ?? string.Empty,
                                                                     className,
                                                                     ct
                                                                    );
        else
        {
            var libraries = await libraryRepository.GetAllLibrariesAsync(ct);
            var allResults = new List<DocChunk>();
            foreach(var lib in libraries)
            {
                var chunks = await chunkRepository.FindByQualifiedNameAsync(lib.Id, lib.CurrentVersion, className, ct);
                allResults.AddRange(chunks);
            }
            results = allResults;
        }

        return results;
    }

    private static async Task<string?> ResolveIfNeeded(ILibraryRepository libraryRepository,
                                                       string? library,
                                                       string? version,
                                                       CancellationToken ct)
    {
        string? result = null;
        if (library != null)
            result = await LibraryTools.ResolveVersionAsync(libraryRepository, library, version, ct);
        return result;
    }

    private static async Task<string> ExecuteSearchAsync(IVectorSearchProvider vectorSearch,
                                                         IEmbeddingProvider embeddingProvider,
                                                         IReRanker reRanker,
                                                         RepositoryFactory repositoryFactory,
                                                         RankingSettings rankingSettings,
                                                         string query,
                                                         string? library,
                                                         string? resolvedVersion,
                                                         string? category,
                                                         int maxResults,
                                                         string? profile,
                                                         CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        DocCategory? categoryFilter = null;
        if (!string.IsNullOrEmpty(category) && Enum.TryParse<DocCategory>(category, ignoreCase: true, out var parsed))
            categoryFilter = parsed;

        var embedSw = Stopwatch.StartNew();
        var embeddings = await embeddingProvider.EmbedAsync([query], ct);
        embedSw.Stop();

        var filter = new VectorSearchFilter
                         {
                             Profile = profile,
                             LibraryId = library,
                             Version = resolvedVersion,
                             Category = categoryFilter
                         };

        var vectorSw = Stopwatch.StartNew();
        var candidateCount = maxResults * CandidateMultiplier;
        var searchResults = await vectorSearch.SearchAsync(embeddings[0], filter, candidateCount, ct);
        vectorSw.Stop();

        var bm25Sw = Stopwatch.StartNew();
        var bm25Scores = await GetBm25ScoresAsync(repositoryFactory, library, resolvedVersion, profile, query, ct);
        bm25Sw.Stop();

        var hybrid = BlendVectorAndBm25(searchResults, bm25Scores, rankingSettings.Bm25Weight);

        var queryIsIdentifierShape = QueryShapeClassifier.IsIdentifierShaped(query);
        var rerankActive = ShouldRerank(rankingSettings.ReRankerStrategy, queryIsIdentifierShape, hybrid.Count);

        var rerankSw = Stopwatch.StartNew();
        var ranked = await ApplyRerankerOrPassThroughAsync(reRanker,
                                                           query,
                                                           hybrid,
                                                           maxResults,
                                                           rerankActive,
                                                           rankingSettings.ReRankBlendWeight,
                                                           ct
                                                          );
        rerankSw.Stop();
        totalSw.Stop();

        var json = SerializeSearchResponse(ranked,
                                            embedSw,
                                            vectorSw,
                                            bm25Sw,
                                            rerankSw,
                                            totalSw,
                                            hybrid.Count,
                                            rerankActive,
                                            queryIsIdentifierShape,
                                            rankingSettings
                                           );
        return json;
    }

    private static async Task<IReadOnlyDictionary<string, double>> GetBm25ScoresAsync(RepositoryFactory repositoryFactory,
                                                                                      string? library,
                                                                                      string? resolvedVersion,
                                                                                      string? profile,
                                                                                      string query,
                                                                                      CancellationToken ct)
    {
        IReadOnlyDictionary<string, double> result = smEmptyBm25Scores;

        if (library != null && resolvedVersion != null)
        {
            var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
            var bm25ShardRepo = repositoryFactory.GetBm25ShardRepository(profile);
            var index = await indexRepo.GetAsync(library, resolvedVersion, ct);
            if (index != null && index.Bm25.DocumentCount > 0)
            {
                var lookup = new ShardedBm25TermLookup(bm25ShardRepo, library, resolvedVersion, index.Bm25.ShardCount);
                result = await Bm25Scorer.ScoreAsync(lookup, index.Bm25, query, ct);
            }
        }

        return result;
    }

    private static IReadOnlyList<HybridCandidate> BlendVectorAndBm25(IReadOnlyList<VectorSearchResult> vectorResults,
                                                                     IReadOnlyDictionary<string, double> bm25Scores,
                                                                     float bm25Weight)
    {
        var maxBm25 = bm25Scores.Count > 0 ? bm25Scores.Values.Max() : 0.0;
        var vectorWeight = 1.0f - bm25Weight;

        var blended = vectorResults
                      .Select(vr => BuildHybridCandidate(vr, bm25Scores, maxBm25, bm25Weight, vectorWeight))
                      .OrderByDescending(c => c.HybridScore)
                      .ToList();
        return blended;
    }

    private static HybridCandidate BuildHybridCandidate(VectorSearchResult vr,
                                                        IReadOnlyDictionary<string, double> bm25Scores,
                                                        double maxBm25,
                                                        float bm25Weight,
                                                        float vectorWeight)
    {
        var bm25 = bm25Scores.TryGetValue(vr.Chunk.Id, out var s) ? s : 0.0;
        var bm25Norm = maxBm25 > 0 ? bm25 / maxBm25 : 0.0;
        var hybrid = (vectorWeight * vr.Score) + (bm25Weight * bm25Norm);
        var result = new HybridCandidate
                         {
                             Chunk = vr.Chunk,
                             VectorScore = vr.Score,
                             Bm25Score = bm25Norm,
                             HybridScore = hybrid
                         };
        return result;
    }

    private static bool ShouldRerank(ReRankerStrategy strategy, bool queryIsIdentifierShape, int candidateCount)
    {
        var fingerprint = (strategy, queryIsIdentifierShape, EnoughCandidates: candidateCount >= ReRankMinCandidates);

        var result = fingerprint switch
        {
            (ReRankerStrategy.Off, _, _) => false,
            (ReRankerStrategy.Llm, true, _) => false,
            (ReRankerStrategy.Llm, false, false) => false,
            (ReRankerStrategy.Llm, false, true) => true,
            var _ => false
        };
        return result;
    }

    private static async Task<IReadOnlyList<RankedResult>> ApplyRerankerOrPassThroughAsync(IReRanker reRanker,
                                                                                            string query,
                                                                                            IReadOnlyList<HybridCandidate> hybrid,
                                                                                            int maxResults,
                                                                                            bool rerankActive,
                                                                                            float blendWeight,
                                                                                            CancellationToken ct)
    {
        IReadOnlyList<RankedResult> result;

        if (!rerankActive)
            result = hybrid.Take(maxResults)
                           .Select(c => new RankedResult
                                            {
                                                Chunk = c.Chunk,
                                                FinalScore = (float) c.HybridScore,
                                                VectorScore = c.VectorScore,
                                                Bm25Score = (float) c.Bm25Score,
                                                RerankScore = null
                                            }
                                  )
                           .ToList();
        else
            result = await BlendWithRerankerAsync(reRanker, query, hybrid, maxResults, blendWeight, ct);

        return result;
    }

    private static async Task<IReadOnlyList<RankedResult>> BlendWithRerankerAsync(IReRanker reRanker,
                                                                                  string query,
                                                                                  IReadOnlyList<HybridCandidate> hybrid,
                                                                                  int maxResults,
                                                                                  float blendWeight,
                                                                                  CancellationToken ct)
    {
        var hybridByChunkId = hybrid.ToDictionary(c => c.Chunk.Id, c => c, StringComparer.Ordinal);
        var candidateChunks = hybrid.Select(c => c.Chunk).ToList();
        var rerankResults = await reRanker.ReRankAsync(query, candidateChunks, hybrid.Count, ct);

        var hybridWeight = 1.0f - blendWeight;
        var blended = rerankResults
                      .Select(rr => BlendRankedResult(rr, hybridByChunkId, blendWeight, hybridWeight))
                      .OrderByDescending(r => r.FinalScore)
                      .Take(maxResults)
                      .ToList();
        return blended;
    }

    private static RankedResult BlendRankedResult(ReRankResult rr,
                                                  IReadOnlyDictionary<string, HybridCandidate> hybridByChunkId,
                                                  float blendWeight,
                                                  float hybridWeight)
    {
        var hybridCandidate = hybridByChunkId.TryGetValue(rr.Chunk.Id, out var hc) ? hc : null;
        var hybridScore = hybridCandidate?.HybridScore ?? 0.0;
        var vectorScore = hybridCandidate?.VectorScore ?? 0f;
        var bm25Score = hybridCandidate != null ? (float) hybridCandidate.Bm25Score : 0f;
        var final = (blendWeight * rr.RelevanceScore) + (hybridWeight * (float) hybridScore);

        var result = new RankedResult
                         {
                             Chunk = rr.Chunk,
                             FinalScore = final,
                             VectorScore = vectorScore,
                             Bm25Score = bm25Score,
                             RerankScore = rr.RelevanceScore
                         };
        return result;
    }

    private static string SerializeSearchResponse(IReadOnlyList<RankedResult> ranked,
                                                  Stopwatch embedSw,
                                                  Stopwatch vectorSw,
                                                  Stopwatch bm25Sw,
                                                  Stopwatch rerankSw,
                                                  Stopwatch totalSw,
                                                  int candidateCount,
                                                  bool rerankActive,
                                                  bool queryIsIdentifierShape,
                                                  RankingSettings rankingSettings)
    {
        var results = ranked.Select(r => new
                                             {
                                                 r.Chunk.LibraryId,
                                                 r.Chunk.Category,
                                                 r.Chunk.PageTitle,
                                                 r.Chunk.SectionPath,
                                                 r.Chunk.PageUrl,
                                                 r.Chunk.Content,
                                                 r.Chunk.QualifiedName,
                                                 r.Chunk.CodeLanguage,
                                                 RelevanceScore = r.FinalScore,
                                                 r.VectorScore,
                                                 r.Bm25Score,
                                                 r.RerankScore
                                             }
                                   );

        var response = new
                           {
                               Results = results,
                               Timing = new
                                            {
                                                EmbedMs = embedSw.ElapsedMilliseconds,
                                                VectorSearchMs = vectorSw.ElapsedMilliseconds,
                                                Bm25Ms = bm25Sw.ElapsedMilliseconds,
                                                ReRankMs = rerankSw.ElapsedMilliseconds,
                                                TotalMs = totalSw.ElapsedMilliseconds,
                                                CandidateCount = candidateCount
                                            },
                               Strategy = new
                                              {
                                                  ReRankerStrategy = rankingSettings.ReRankerStrategy.ToString(),
                                                  RerankActive = rerankActive,
                                                  QueryIsIdentifierShape = queryIsIdentifierShape,
                                                  rankingSettings.Bm25Weight,
                                                  rankingSettings.ReRankBlendWeight
                                              }
                           };

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static string LibraryNotFoundJson(string library)
    {
        var error = new
                        {
                            Error = $"Library '{library}' not found. Use list_libraries to see available libraries, " +
                                    IndexNewLibrariesHint
                        };
        var result = JsonSerializer.Serialize(error, smJsonOptions);
        return result;
    }

    private record HybridCandidate
    {
        public required DocChunk Chunk { get; init; }
        public required float VectorScore { get; init; }
        public required double Bm25Score { get; init; }
        public required double HybridScore { get; init; }
    }

    private record RankedResult
    {
        public required DocChunk Chunk { get; init; }
        public required float FinalScore { get; init; }
        public required float VectorScore { get; init; }
        public required float Bm25Score { get; init; }
        public float? RerankScore { get; init; }
    }

    private const string IndexNewLibrariesHint = "or scrape_docs/index_project_dependencies to index new ones.";
    private const int CandidateMultiplier = 2;
    private const int ReRankMinCandidates = 6;
    private const int MaxOverviewResults = 5;

    private static readonly IReadOnlyDictionary<string, double> smEmptyBm25Scores =
        new Dictionary<string, double>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
