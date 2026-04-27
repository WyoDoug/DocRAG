// RescrubServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Recon;
using DocRAG.Ingestion.Symbols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

#endregion

namespace DocRAG.Tests.Recon;

public sealed class RescrubServiceTests
{
    [Fact]
    public async Task ReturnsReconNeededWhenProfileMissing()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryProfile?) null);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                TestContext.Current.CancellationToken
                                               );

        Assert.True(result.ReconNeeded);
        await chunkRepo.DidNotReceive().GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRunDoesNotWriteChunks()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile());

        var legacyChunk = MakeLegacyChunk("class Controller { }");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new[] { legacyChunk });

        var options = new RescrubOptions { DryRun = true };
        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                "lib",
                                                "1.0",
                                                options,
                                                TestContext.Current.CancellationToken
                                               );

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Processed);
        Assert.True(result.Changed > 0);
        await chunkRepo.DidNotReceive().UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
        await indexRepo.DidNotReceive().UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BumpsParserVersionAndPersistsChunks()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile());

        var legacyChunk = MakeLegacyChunk("class Controller { void MoveLinear() { } }");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new[] { legacyChunk });

        var options = new RescrubOptions();
        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                "lib",
                                                "1.0",
                                                options,
                                                TestContext.Current.CancellationToken
                                               );

        Assert.False(result.DryRun);
        Assert.Equal(1, result.Processed);
        Assert.True(result.Changed > 0);
        Assert.True(result.IndexesBuilt);

        await chunkRepo.Received(1).UpsertChunksAsync(Arg.Is<IReadOnlyList<DocChunk>>(list => list.Count == 1
                                                                                             && list[0].ParserVersion == ParserVersionInfo.Current
                                                                                             && list[0].Symbols.Count > 0
                                                                                            ),
                                                      Arg.Any<CancellationToken>()
                                                     );

        await indexRepo.Received(1).UpsertAsync(Arg.Is<LibraryIndex>(idx => idx.Manifest.LastParserVersion == ParserVersionInfo.Current),
                                                Arg.Any<CancellationToken>()
                                               );
    }

    [Fact]
    public async Task IsIdempotentWhenChunksAreAlreadyCurrent()
    {
        var classifier = MakeClassifier();
        var service = new RescrubService(new SymbolExtractor(), classifier, NullLogger<RescrubService>.Instance);
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();

        var profile = MakeProfile();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(profile);

        var alreadyCurrent = MakeCurrentChunkFromContent("class Controller { void MoveLinear() { } }", profile);

        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new[] { alreadyCurrent });

        // Existing index whose manifest matches the current parser/profile/classifier exactly,
        // so auto-detect skips reclassification and the rescrub finds no changes.
        indexRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new LibraryIndex
                              {
                                  Id = "lib/1.0",
                                  LibraryId = "lib",
                                  Version = "1.0",
                                  Manifest = new LibraryManifest
                                                 {
                                                     LastParserVersion = ParserVersionInfo.Current,
                                                     LastProfileHash = LibraryProfileService.ComputeHash(profile),
                                                     LastClassifierVersion = classifier.GetCurrentVersion()
                                                 }
                              }
                         );

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                TestContext.Current.CancellationToken
                                               );

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Changed);
        Assert.False(result.DidReclassify);
        await chunkRepo.DidNotReceive().UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
    }

    private static RescrubService MakeService()
    {
        var classifier = MakeClassifier();
        var extractor = new SymbolExtractor();
        var service = new RescrubService(extractor, classifier, NullLogger<RescrubService>.Instance);
        return service;
    }

    private static DocRAG.Ingestion.Classification.LlmClassifier MakeClassifier()
    {
        var settings = Options.Create(new DocRAG.Ingestion.Embedding.OllamaSettings());
        var result = new DocRAG.Ingestion.Classification.LlmClassifier(settings,
                                                                        NullLogger<DocRAG.Ingestion.Classification.LlmClassifier>.Instance);
        return result;
    }

    private static LibraryProfile MakeProfile()
    {
        var result = new LibraryProfile
                         {
                             Id = "lib/1.0",
                             LibraryId = "lib",
                             Version = "1.0",
                             Source = "test",
                             Languages = ["C#"],
                             Casing = new CasingConventions { Types = "PascalCase" }
                         };
        return result;
    }

    private static DocChunk MakeLegacyChunk(string content) =>
        new()
            {
                Id = "lib/1.0/abc/0",
                LibraryId = "lib",
                Version = "1.0",
                PageUrl = "https://example.com/page",
                PageTitle = "Page",
                Category = DocCategory.ApiReference,
                Content = content,
                ParserVersion = 1
            };

    private static DocChunk MakeCurrentChunkFromContent(string content, LibraryProfile profile)
    {
        var extractor = new SymbolExtractor();
        var extracted = extractor.Extract(content, profile);
        var result = new DocChunk
                         {
                             Id = "lib/1.0/abc/0",
                             LibraryId = "lib",
                             Version = "1.0",
                             PageUrl = "https://example.com/page",
                             PageTitle = "Page",
                             Category = DocCategory.ApiReference,
                             Content = content,
                             Symbols = extracted.Symbols,
                             QualifiedName = extracted.PrimaryQualifiedName,
                             ParserVersion = ParserVersionInfo.Current
                         };
        return result;
    }
}
