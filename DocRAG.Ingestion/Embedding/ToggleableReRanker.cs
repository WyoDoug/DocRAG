// ToggleableReRanker.cs
// Copyright (c) 2012-Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Strategy-aware reranker dispatcher. Holds three concrete
///     rerankers (NoOp, Ollama LLM, CrossEncoder) plus a runtime Enabled
///     bool kill switch. Per call, dispatches to the strategy named in
///     RankingSettings.ReRankerStrategy unless Enabled has been flipped
///     to false (in which case NoOp passes results through unchanged).
///
///     The bool toggle is preserved for backward compatibility with the
///     existing toggle_reranking MCP tool — set Enabled=false to bypass
///     reranking entirely without changing config; set Enabled=true to
///     restore the configured strategy.
/// </summary>
public class ToggleableReRanker : IReRanker
{
    public ToggleableReRanker(IOptions<OllamaSettings> ollamaSettings,
                              IOptions<RankingSettings> rankingSettings,
                              ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(ollamaSettings);
        ArgumentNullException.ThrowIfNull(rankingSettings);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        mRankingSettings = rankingSettings.Value;
        mOllamaReRanker = new OllamaReRanker(ollamaSettings, loggerFactory.CreateLogger<OllamaReRanker>());
        mCrossEncoderReRanker = new CrossEncoderReRanker(ollamaSettings, loggerFactory.CreateLogger<CrossEncoderReRanker>());
        mNoOpReRanker = new NoOpReRanker();
        mEnabled = ollamaSettings.Value.ReRankingEnabled;
        mLogger = loggerFactory.CreateLogger<ToggleableReRanker>();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                         IReadOnlyList<DocChunk> candidates,
                                                         int maxResults,
                                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var active = ResolveActive();
        var result = active.ReRankAsync(query, candidates, maxResults, ct);
        return result;
    }

    /// <summary>
    ///     The strategy that ReRankAsync will dispatch to right now,
    ///     accounting for both the Enabled kill switch and the
    ///     configured ReRankerStrategy. Useful for diagnostic responses
    ///     (toggle_reranking, search_docs Strategy field).
    /// </summary>
    public ReRankerStrategy ActiveStrategy
    {
        get
        {
            var result = !mEnabled ? ReRankerStrategy.Off : mRankingSettings.ReRankerStrategy;
            return result;
        }
    }

    private IReRanker ResolveActive()
    {
        var strategy = ActiveStrategy;
        var result = strategy switch
        {
            ReRankerStrategy.Off => (IReRanker) mNoOpReRanker,
            ReRankerStrategy.Llm => mOllamaReRanker,
            ReRankerStrategy.CrossEncoder => mCrossEncoderReRanker,
            var _ => mNoOpReRanker
        };
        return result;
    }

    #region Enabled property
    private volatile bool mEnabled;
    public bool Enabled
    {
        get => mEnabled;
        set
        {
            mEnabled = value;
            mLogger.LogInformation("Re-ranking {State} (strategy={Strategy})",
                                   value ? EnabledLabel : DisabledLabel,
                                   ActiveStrategy
                                  );
        }
    }
    #endregion

    private readonly OllamaReRanker mOllamaReRanker;
    private readonly CrossEncoderReRanker mCrossEncoderReRanker;
    private readonly NoOpReRanker mNoOpReRanker;
    private readonly RankingSettings mRankingSettings;
    private readonly ILogger<ToggleableReRanker> mLogger;

    private const string EnabledLabel = "enabled";
    private const string DisabledLabel = "disabled";
}
