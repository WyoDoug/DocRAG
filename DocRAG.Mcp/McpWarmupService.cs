// McpWarmupService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Diagnostics;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Embedding;
using Microsoft.Extensions.Options;

#endregion

namespace DocRAG.Mcp;

public sealed class McpWarmupService : BackgroundService
{
    public McpWarmupService(IServiceProvider serviceProvider,
                            McpWarmupState warmupState,
                            ILogger<McpWarmupService> logger)
    {
        mServiceProvider = serviceProvider;
        mWarmupState = warmupState;
        mLogger = logger;
    }

    private readonly ILogger<McpWarmupService> mLogger;
    private readonly IServiceProvider mServiceProvider;
    private readonly McpWarmupState mWarmupState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupSw = Stopwatch.StartNew();
        var stepSw = Stopwatch.StartNew();

        mWarmupState.MarkStarted(PhaseStarting);
        mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Starting", startupSw.Elapsed.TotalSeconds);

        try
        {
            using var scope = mServiceProvider.CreateScope();

            var contextFactory = scope.ServiceProvider.GetRequiredService<DocRagDbContextFactory>();
            var dbSettings = scope.ServiceProvider.GetRequiredService<IOptions<DocRagDbSettings>>().Value;
            var repositoryFactory = scope.ServiceProvider.GetRequiredService<RepositoryFactory>();
            var bootstrapper = scope.ServiceProvider.GetRequiredService<OllamaBootstrapper>();
            var vectorSearch = scope.ServiceProvider.GetRequiredService<IVectorSearchProvider>();
            var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
            var reRanker = scope.ServiceProvider.GetRequiredService<IReRanker>();

            var profileNames = GetProfilesToBootstrap(contextFactory, dbSettings);

            stepSw.Restart();
            var requiredModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var profile in profileNames)
                await DiscoverModelsForProfileAsync(profile,
                                                    contextFactory,
                                                    repositoryFactory,
                                                    requiredModels,
                                                    stoppingToken
                                                   );

            mWarmupState.MarkPhase(PhaseMongoDbProfilesDiscovered);
            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - MongoDB profiles discovered",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );

            stepSw.Restart();
            await bootstrapper.BootstrapAsync(requiredModels.ToList(), stoppingToken);
            mWarmupState.MarkPhase(PhaseOllamaBootstrapFinished);
            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Ollama bootstrap finished",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );

            stepSw.Restart();
            foreach(var profile in profileNames)
                await LoadChunksForProfileAsync(profile, repositoryFactory, vectorSearch, stoppingToken);

            mWarmupState.MarkPhase(PhaseVectorIndicesLoaded);
            mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Vector indices loaded",
                                   startupSw.Elapsed.TotalSeconds,
                                   stepSw.ElapsedMilliseconds
                                  );

            try
            {
                stepSw.Restart();
                await embeddingProvider.EmbedAsync([WarmupProbeText], stoppingToken);
                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - nomic-embed-text warm",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );

                stepSw.Restart();
                await reRanker.ReRankAsync(WarmupProbeText, [], maxResults: 1, stoppingToken);
                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - qwen3:1.7b warm",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );

                stepSw.Restart();
                var warmupEmbedding = (await embeddingProvider.EmbedAsync([WarmupSearchProbeText], stoppingToken))[0];
                var warmupFilter = new VectorSearchFilter { Profile = null };
                await vectorSearch.SearchAsync(warmupEmbedding, warmupFilter, maxResults: 1, stoppingToken);
                mLogger.LogInformation("[Warmup] T+{Sec:F1}s ({Step}ms) - Full pipeline warm",
                                       startupSw.Elapsed.TotalSeconds,
                                       stepSw.ElapsedMilliseconds
                                      );
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            {
                mLogger.LogWarning(ex, "[Warmup] T+{Sec:F1}s - Warmup probe failed", startupSw.Elapsed.TotalSeconds);
            }

            mWarmupState.MarkCompleted(nameof(ScrapeJobStatus.Completed));
            mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Completed", startupSw.Elapsed.TotalSeconds);
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
        {
            mWarmupState.MarkFailed(PhaseCanceled, WarmupCanceledMessage);
            mLogger.LogInformation("[Warmup] T+{Sec:F1}s - Canceled", startupSw.Elapsed.TotalSeconds);
        }
        catch(Exception ex)
        {
            mWarmupState.MarkFailed(nameof(ScrapeJobStatus.Failed), ex.Message);
            mLogger.LogWarning(ex, "[Warmup] T+{Sec:F1}s - Failed", startupSw.Elapsed.TotalSeconds);
        }
    }

    private async Task DiscoverModelsForProfileAsync(string? profile,
                                                     DocRagDbContextFactory contextFactory,
                                                     RepositoryFactory repositoryFactory,
                                                     HashSet<string> requiredModels,
                                                     CancellationToken stoppingToken)
    {
        try
        {
            var ctx = contextFactory.GetForProfile(profile);
            using var indexCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            indexCts.CancelAfter(TimeSpan.FromSeconds(seconds: 10));
            await ctx.EnsureIndexesAsync(indexCts.Token);

            var libRepo = repositoryFactory.GetLibraryRepository(profile);
            var libraries = await libRepo.GetAllLibrariesAsync(stoppingToken);
            foreach(var lib in libraries)
            {
                var version = await libRepo.GetVersionAsync(lib.Id, lib.CurrentVersion, stoppingToken);
                if (version != null && !string.IsNullOrEmpty(version.EmbeddingModelName))
                    requiredModels.Add(version.EmbeddingModelName);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            mLogger.LogWarning(ex, "Failed to inspect profile {Profile}, skipping at startup", profile ?? "(default)");
        }
    }

    private async Task LoadChunksForProfileAsync(string? profile,
                                                 RepositoryFactory repositoryFactory,
                                                 IVectorSearchProvider vectorSearch,
                                                 CancellationToken stoppingToken)
    {
        try
        {
            var libRepo = repositoryFactory.GetLibraryRepository(profile);
            var chunkRepo = repositoryFactory.GetChunkRepository(profile);

            var libraries = await libRepo.GetAllLibrariesAsync(stoppingToken);
            foreach(var lib in libraries)
            {
                var chunks = await chunkRepo.GetChunksAsync(lib.Id, lib.CurrentVersion, stoppingToken);
                var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();
                if (embeddedChunks.Count > 0)
                {
                    await vectorSearch.IndexChunksAsync(profile,
                                                        lib.Id,
                                                        lib.CurrentVersion,
                                                        embeddedChunks,
                                                        stoppingToken
                                                       );
                    mLogger.LogInformation("Loaded {Count} chunks for {Profile}/{Library} v{Version}",
                                           embeddedChunks.Count,
                                           profile ?? "(default)",
                                           lib.Id,
                                           lib.CurrentVersion
                                          );
                }
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            mLogger.LogWarning(ex, "Failed to load chunks for profile {Profile}, skipping", profile ?? "(default)");
        }
    }

    private static IReadOnlyList<string?> GetProfilesToBootstrap(DocRagDbContextFactory contextFactory,
                                                                 DocRagDbSettings dbSettings)
    {
        var result = new List<string?> { null };

        if (dbSettings.BootstrapAllProfilesAtStartup)
            result.AddRange(contextFactory.GetProfileNames().Cast<string?>());
        else
        {
            var defaultProfileName = contextFactory.GetDefaultProfileName();
            if (!string.IsNullOrWhiteSpace(defaultProfileName))
                result.Add(defaultProfileName);
        }

        var distinctProfiles = result
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();

        return distinctProfiles;
    }

    private const string PhaseStarting = "Starting";
    private const string PhaseMongoDbProfilesDiscovered = "MongoDB profiles discovered";
    private const string PhaseOllamaBootstrapFinished = "Ollama bootstrap finished";
    private const string PhaseVectorIndicesLoaded = "Vector indices loaded";
    private const string PhaseCanceled = "Canceled";
    private const string WarmupProbeText = "warmup";
    private const string WarmupSearchProbeText = "warmup search";
    private const string WarmupCanceledMessage = "Warmup canceled during shutdown.";
}
