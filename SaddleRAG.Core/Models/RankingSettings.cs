// RankingSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Configuration knobs for hybrid retrieval and reranking. Bound from
///     the "Ranking" section of appsettings.json.
/// </summary>
public class RankingSettings
{
    /// <summary>
    ///     Weight applied to the BM25 score when blending with vector
    ///     similarity. Hybrid score is (1 - Bm25Weight) * vector +
    ///     Bm25Weight * bm25, both normalized to [0,1].
    /// </summary>
    public float Bm25Weight { get; set; } = DefaultBm25Weight;

    /// <summary>
    ///     Weight applied to the reranker score when blending with the
    ///     hybrid score. Final score is ReRankBlendWeight * rerank +
    ///     (1 - ReRankBlendWeight) * hybrid. Pure replacement (1.0) is
    ///     what makes the legacy LLM reranker's mistakes unrecoverable;
    ///     0.6 is the sweet spot per the bench harness.
    /// </summary>
    public float ReRankBlendWeight { get; set; } = DefaultReRankBlendWeight;

    /// <summary>
    ///     Threshold for the SymbolExtractor's prose-mention backstop.
    ///     A capitalized identifier appearing this many times in prose
    ///     (outside code fences) survives extraction even when no other
    ///     keep rule fires. Lower → more recall, more noise.
    /// </summary>
    public int ProseMentionThreshold { get; set; } = DefaultProseMentionThreshold;

    /// <summary>
    ///     Default reranker strategy used when toggle_reranking activates
    ///     reranking. Off is the recommended starting point until a bench
    ///     run confirms a non-Off strategy net-helps.
    /// </summary>
    public ReRankerStrategy ReRankerStrategy { get; set; } = ReRankerStrategy.Off;

    /// <summary>
    ///     Configuration section name in appsettings.
    /// </summary>
    public const string SectionName = "Ranking";

    public const float DefaultBm25Weight = 0.4f;
    public const float DefaultReRankBlendWeight = 0.6f;
    public const int DefaultProseMentionThreshold = 3;
}
