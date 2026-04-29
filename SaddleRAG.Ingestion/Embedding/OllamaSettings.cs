// OllamaSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Configuration settings for the Ollama integration.
/// </summary>
public class OllamaSettings
{
    /// <summary>
    ///     Ollama API endpoint.
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    ///     Model name for embeddings.
    /// </summary>
    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;

    /// <summary>
    ///     Output dimensionality of the embedding model.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = DefaultEmbeddingDimensions;

    /// <summary>
    ///     Model name for classification/chat tasks.
    /// </summary>
    public string ClassificationModel { get; set; } = DefaultClassificationModel;

    /// <summary>
    ///     Model name for the legacy LLM categorical reranker
    ///     (ReRankerStrategy = Llm). Smaller instruction-following models
    ///     work for this prompt-based categorical scoring approach.
    /// </summary>
    public string ReRankingModel { get; set; } = DefaultReRankingModel;

    /// <summary>
    ///     Model name for the cross-encoder reranker
    ///     (ReRankerStrategy = CrossEncoder). Defaults to the Mixedbread
    ///     mxbai-rerank-large-v2 community port. Mixedbread AI is registered
    ///     in Berlin; non-Chinese supply chain. The model is a true
    ///     cross-encoder by training but is hosted on Ollama as a generate
    ///     model with a "respond with only the score" prompt — produces
    ///     continuous floats instead of qwen3:1.7b's 5-bucket plateau.
    /// </summary>
    public string CrossEncoderModel { get; set; } = DefaultCrossEncoderModel;

    /// <summary>
    ///     Model name used by the CLI's recon fallback when no calling LLM is
    ///     available. A larger model than the classification/reranking ones is
    ///     preferred because recon does broader reasoning ("what language is
    ///     this", "what's the casing convention"). The CLI refuses to silently
    ///     fall back to a smaller model when this one is not pulled.
    /// </summary>
    public string ReconModel { get; set; } = DefaultReconModel;

    /// <summary>
    ///     Minimum self-reported confidence required before a recon-produced
    ///     profile is persisted to MongoDB. Below this threshold, the CLI
    ///     refuses to write the profile unless the user explicitly accepts
    ///     a low-confidence result. Protects CI environments (which typically
    ///     lack the VRAM for the recon model) from caching bad profiles that
    ///     then drive every subsequent extraction.
    /// </summary>
    public float ReconMinConfidence { get; set; } = DefaultReconMinConfidence;

    /// <summary>
    ///     Timeout in seconds for pulling a model.
    /// </summary>
    public int ModelPullTimeoutSeconds { get; set; } = DefaultModelPullTimeoutSeconds;

    /// <summary>
    ///     Whether to use Ollama-based re-ranking for search results.
    ///     When false, NoOpReRanker passes results through unchanged.
    /// </summary>
    public bool ReRankingEnabled { get; init; } = true;

    /// <summary>
    ///     Configuration section name in appsettings.
    /// </summary>
    public const string SectionName = "Ollama";

    public const string DefaultEndpoint = "http://localhost:11434";
    public const string DefaultEmbeddingModel = "nomic-embed-text";
    public const string DefaultClassificationModel = "qwen3:1.7b";
    public const string DefaultReRankingModel = "qwen3:1.7b";
    public const string DefaultCrossEncoderModel = "rjmalagon/mxbai-rerank-large-v2:1.5b-fp16";
    public const string DefaultReconModel = "qwen3:14b";
    public const int DefaultEmbeddingDimensions = 768;
    public const int DefaultModelPullTimeoutSeconds = 600;
    public const float DefaultReconMinConfidence = 0.6f;
}
