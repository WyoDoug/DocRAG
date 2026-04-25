// // ReRankerStrategy.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Selects which reranker strategy SearchTools.search_docs uses after
///     hybrid scoring (vector ∥ BM25). Default is Off — the reviewer's
///     bench showed that the legacy LLM categorical reranker hurts
///     identifier queries more often than it helps; flip to a non-Off
///     strategy only after the bench harness confirms a net-positive
///     nDCG@5 for your corpus.
/// </summary>
public enum ReRankerStrategy
{
    /// <summary>
    ///     No reranking. Hybrid score (vector ∥ BM25) is the final score.
    ///     Recommended default — fastest, no plateau artifacts, no
    ///     identifier-query regression.
    /// </summary>
    Off,

    /// <summary>
    ///     Legacy Ollama qwen3:1.7b LLM categorical reranker. Kept for
    ///     backward compatibility. Score blending (final = α·rerank +
    ///     β·hybrid) prevents the reranker's mistakes from being
    ///     unrecoverable, but the reranker's plateau scores (1.0/0.8/
    ///     0.5/0.2/0.0) and 2–7s latency make it a bad default for
    ///     identifier-heavy workloads.
    /// </summary>
    Llm,

    /// <summary>
    ///     Cross-encoder-style reranker hosting Mixedbread mxbai-rerank-
    ///     large-v2 on Ollama. Each (query, document) pair is scored
    ///     independently with a "respond with only the number" prompt,
    ///     yielding continuous floats in [0, 1]. Latency ~50-200ms per
    ///     pair (so ~0.5-2s for a 10-candidate batch). Recommended
    ///     non-Off strategy for production once the bench harness
    ///     confirms it net-helps for your corpus.
    /// </summary>
    CrossEncoder
}
