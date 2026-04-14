// // ToggleableReRanker.cs
// // Copyright (c) 2012-Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Wraps the real Ollama re-ranker and a no-op fallback with a runtime toggle.
///     The LLM can enable or disable re-ranking without restarting the service.
/// </summary>
public class ToggleableReRanker : IReRanker
{
    public ToggleableReRanker(IOptions<OllamaSettings> settings, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        mOllamaReRanker = new OllamaReRanker(settings, loggerFactory.CreateLogger<OllamaReRanker>());
        mNoOpReRanker = new NoOpReRanker();
        mEnabled = settings.Value.ReRankingEnabled;
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

        IReRanker active = mEnabled ? mOllamaReRanker : mNoOpReRanker;
        var result = active.ReRankAsync(query, candidates, maxResults, ct);
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
            mLogger.LogInformation("Re-ranking {State}", value ? EnabledLabel : DisabledLabel);
        }
    }
    #endregion

    private readonly OllamaReRanker mOllamaReRanker;
    private readonly NoOpReRanker mNoOpReRanker;
    private readonly ILogger<ToggleableReRanker> mLogger;

    private const string EnabledLabel = "enabled";
    private const string DisabledLabel = "disabled";
}
