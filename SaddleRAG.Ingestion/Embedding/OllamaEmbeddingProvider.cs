// OllamaEmbeddingProvider.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.



#region Usings



using SaddleRAG.Core.Interfaces;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using OllamaSharp;

using OllamaSharp.Models;



#endregion



namespace SaddleRAG.Ingestion.Embedding;



/// <summary>

///     Embedding provider using Ollama via OllamaSharp.

///     Supports any Ollama embedding model (nomic-embed-text, mxbai-embed-large, etc.).

/// </summary>

public class OllamaEmbeddingProvider : IEmbeddingProvider

{

    public OllamaEmbeddingProvider(IOptions<OllamaSettings> settings,

                                   ILogger<OllamaEmbeddingProvider> logger)

    {

        mSettings = settings.Value;

        mLogger = logger;

        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));

    }



    private readonly OllamaApiClient mClient;

    private readonly ILogger<OllamaEmbeddingProvider> mLogger;

    private readonly OllamaSettings mSettings;



    /// <inheritdoc />

    public string ProviderId => ProviderIdName;



    /// <inheritdoc />

    public string ModelName => mSettings.EmbeddingModel;



    /// <inheritdoc />

    public int Dimensions => mSettings.EmbeddingDimensions;



    /// <inheritdoc />

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)

    {

        ArgumentNullException.ThrowIfNull(texts);



        float[][] allEmbeddings = Array.Empty<float[]>();



        if (texts.Count > 0)

        {

            allEmbeddings = new float[texts.Count][];



            for(var i = 0; i < texts.Count; i++)

            {

                allEmbeddings[i] = await EmbedSingleWithRetryAsync(texts[i], ct);



                if ((i + 1) % LogProgressInterval == 0)

                    mLogger.LogDebug("Embedded {Count}/{Total} texts", i + 1, texts.Count);

            }



            mLogger.LogInformation("Embedded {Count} texts via Ollama ({Model})",

                                   texts.Count,

                                   mSettings.EmbeddingModel

                                  );

        }



        return allEmbeddings;

    }



    private async Task<float[]> EmbedSingleWithRetryAsync(string text, CancellationToken ct)

    {

        float[] result = [];

        var attempt = 0;

        var succeeded = false;



        while (!succeeded && attempt < MaxRetryAttempts)

        {

            try

            {

                if (attempt > 0)

                {

                    int delayMs = InitialRetryDelayMs * (1 << (attempt - 1));

                    mLogger.LogWarning("Embedding retry attempt {Attempt}/{Max} after {Delay}ms",

                                       attempt + 1,

                                       MaxRetryAttempts,

                                       delayMs

                                      );

                    await Task.Delay(delayMs, ct);

                }



                var response = await mClient.EmbedAsync(new EmbedRequest

                                                            {

                                                                Model = mSettings.EmbeddingModel,

                                                                Input = [text]

                                                            },

                                                        ct

                                                       );



                if (response?.Embeddings == null || response.Embeddings.Count == 0)

                {

                    throw new InvalidOperationException("Ollama returned null or empty embeddings for the input text.");

                }



                result = response.Embeddings[index: 0].Select(d => d).ToArray();

                succeeded = true;

            }

            catch(Exception ex) when(attempt < MaxRetryAttempts - 1)

            {

                mLogger.LogWarning(ex, "Embedding attempt {Attempt} failed", attempt + 1);

            }



            attempt++;

        }



        return result;

    }



    private const string ProviderIdName = "ollama";
    private const int MaxRetryAttempts = 3;

    private const int InitialRetryDelayMs = 1000;

    private const int LogProgressInterval = 50;

}

