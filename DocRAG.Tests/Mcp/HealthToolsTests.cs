// HealthToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Mcp.Tools;
using NSubstitute;

namespace DocRAG.Tests.Mcp;

public sealed class HealthToolsTests
{
    [Fact]
    public async Task GetLibraryHealthReturnsExpectedShape()
    {
        var (factory, libraryRepo, chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        libraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "foo/1.0",
                                    LibraryId = "foo",
                                    Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 50,
                                    ChunkCount = 250,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768,
                                    BoundaryIssuePct = 7.0
                                });
        chunkRepo.GetLanguageMixAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, double> { ["csharp"] = 0.8, ["unfenced"] = 0.2 });
        chunkRepo.GetHostnameDistributionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, int> { ["docs.foo.com"] = 50 });

        var json = await HealthTools.GetLibraryHealth(factory, library: "foo", version: null, profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"chunkCount\": 250", json);
        Assert.Contains("\"boundaryIssuePct\": 7", json);
        Assert.Contains("\"languageMix\":", json);
        // BoundaryIssuePct=7 falls in the 5 <= pct < 10 range
        Assert.Contains("rechunk_library may help", json);
    }

    [Fact]
    public async Task GetLibraryHealthNotFoundReturnsErrorJson()
    {
        var (factory, libraryRepo, _) = MakeFactory();
        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>()).Returns((LibraryRecord?) null);

        var json = await HealthTools.GetLibraryHealth(factory, library: "missing", version: null, profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLibraryHealthHighBoundaryPctRecommendsRechunk()
    {
        var (factory, libraryRepo, chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo", Name = "f", Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        libraryRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "foo/1.0", LibraryId = "foo", Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 50, ChunkCount = 250,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768,
                                    BoundaryIssuePct = 12.0
                                });
        chunkRepo.GetLanguageMixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, double>());
        chunkRepo.GetHostnameDistributionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<string, int>());

        var json = await HealthTools.GetLibraryHealth(factory, library: "foo", version: null, profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        // BoundaryIssuePct=12 is >= 10% threshold
        Assert.Contains("rechunk_library recommended", json);
    }

    [Fact]
    public async Task GetDashboardIndexEmptyDbRecommendsIngestion()
    {
        var (factory, libraryRepo, _) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryRecord>());

        var jobRepo = Substitute.For<IScrapeJobRepository>();
        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ScrapeJobRecord>());

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"libraryCount\": 0", json);
        Assert.Contains("Database is empty", json);
    }

    [Fact]
    public async Task GetDashboardIndexPopulatedDbAggregatesAcrossLibraries()
    {
        var (factory, libraryRepo, _) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                   .Returns(new[]
                                {
                                    new LibraryRecord { Id = "a", Name = "a", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } },
                                    new LibraryRecord { Id = "b", Name = "b", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } }
                                });
        libraryRepo.GetVersionAsync("a", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "a/1.0", LibraryId = "a", Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 100, ChunkCount = 500,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768,
                                    Suspect = true,
                                    SuspectReasons = new[] { "LanguageMismatch" }
                                });
        libraryRepo.GetVersionAsync("b", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "b/1.0", LibraryId = "b", Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 100, ChunkCount = 500,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768,
                                    Suspect = false,
                                    SuspectReasons = Array.Empty<string>()
                                });

        var jobRepo = Substitute.For<IScrapeJobRepository>();
        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ScrapeJobRecord>());

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"libraryCount\": 2", json);
        Assert.Contains("\"suspectCount\": 1", json);
        Assert.Contains("\"a\"", json);
    }

    private static (RepositoryFactory factory, ILibraryRepository libraryRepo, IChunkRepository chunkRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        return (factory, libraryRepo, chunkRepo);
    }
}
