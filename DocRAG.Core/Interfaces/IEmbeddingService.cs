// IEmbeddingService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Generates embedding vectors from text content.
///     Multiple providers supported: Ollama (local), Azure OpenAI, OpenAI.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    ///     Provider identifier for configuration and logging.
    ///     Example: "ollama"
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    ///     Specific model name. Example: "nomic-embed-text"
    /// </summary>
    string ModelName { get; }

    /// <summary>
    ///     Dimensionality of the embedding vectors produced.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    ///     Generate embeddings for one or more texts.
    ///     Implementations should batch efficiently.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
