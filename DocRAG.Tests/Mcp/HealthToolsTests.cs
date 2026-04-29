// HealthToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

using DocRAG.Core.Enums;
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
        var (factory, libraryRepo, chunkRepo, _) = MakeFactory();
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
        Assert.Contains("rechunk_library may help", json);
    }

    [Fact]
    public async Task GetLibraryHealthNotFoundReturnsErrorJson()
    {
        var (factory, libraryRepo, _, _) = MakeFactory();
        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>()).Returns((LibraryRecord?) null);

        var json = await HealthTools.GetLibraryHealth(factory, library: "missing", version: null, profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLibraryHealthHighBoundaryPctRecommendsRechunk()
    {
        var (factory, libraryRepo, chunkRepo, _) = MakeFactory();
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

        Assert.Contains("rechunk_library recommended", json);
    }

    [Fact]
    public async Task GetDashboardIndexEmptyDbRecommendsIngestion()
    {
        var (factory, libraryRepo, _, _) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryRecord>());

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"libraryCount\": 0", json);
        Assert.Contains("Database is empty", json);
    }

    [Fact]
    public async Task GetDashboardIndexPopulatedDbAggregatesAcrossLibraries()
    {
        var (factory, libraryRepo, _, _) = MakeFactory();
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

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"libraryCount\": 2", json);
        Assert.Contains("\"suspectCount\": 1", json);
        Assert.Contains("\"a\"", json);
    }

    [Fact]
    public async Task GetDashboardIndexIncludesOrphanRunningJobsOutsideRecentWindow()
    {
        var (factory, libraryRepo, _, jobRepo) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryRecord>());

        var orphan = MakeJobRecord(id: "orphan-1",
                                    library: "mongodb.driver",
                                    version: "3.4.0",
                                    status: ScrapeJobStatus.Running,
                                    createdAt: DateTime.UtcNow - TimeSpan.FromDays(14),
                                    lastProgressAt: DateTime.UtcNow - TimeSpan.FromDays(14));
        var fresh = MakeJobRecord(id: "fresh-1",
                                   library: "foo",
                                   version: "1.0",
                                   status: ScrapeJobStatus.Completed,
                                   createdAt: DateTime.UtcNow - TimeSpan.FromMinutes(5),
                                   lastProgressAt: null);

        jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(new[] { fresh });
        jobRepo.ListRunningJobsAsync(Arg.Any<CancellationToken>())
               .Returns(new[] { orphan });

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"orphan-1\"", json);
        Assert.Contains("\"fresh-1\"", json);
    }

    [Fact]
    public async Task GetDashboardIndexProjectionUsesPascalCaseStaleAndPipelineState()
    {
        var (factory, libraryRepo, _, jobRepo) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                   .Returns(new[]
                                {
                                    new LibraryRecord { Id = "a", Name = "a", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } }
                                });
        libraryRepo.GetVersionAsync("a", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "a/1.0", LibraryId = "a", Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 1, ChunkCount = 1,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768
                                });

        var orphan = MakeJobRecord(id: "stale-running",
                                    library: "a",
                                    version: "1.0",
                                    status: ScrapeJobStatus.Running,
                                    createdAt: DateTime.UtcNow - TimeSpan.FromDays(2),
                                    lastProgressAt: DateTime.UtcNow - TimeSpan.FromDays(2));
        jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(new[] { orphan });
        jobRepo.ListRunningJobsAsync(Arg.Any<CancellationToken>())
               .Returns(new[] { orphan });

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Stale\": true", json);
        Assert.DoesNotContain("\"stale\":", json);
        Assert.Contains("\"PipelineState\": \"Running\"", json);
    }

    [Fact]
    public async Task GetDashboardIndexSuggestsCancelScrapeWhenStaleOrphanPresent()
    {
        var (factory, libraryRepo, _, jobRepo) = MakeFactory();
        libraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                   .Returns(new[]
                                {
                                    new LibraryRecord { Id = "a", Name = "a", Hint = "h", CurrentVersion = "1.0", AllVersions = new() { "1.0" } }
                                });
        libraryRepo.GetVersionAsync("a", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new LibraryVersionRecord
                                {
                                    Id = "a/1.0", LibraryId = "a", Version = "1.0",
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = 1, ChunkCount = 1,
                                    EmbeddingProviderId = "ollama",
                                    EmbeddingModelName = "nomic-embed-text",
                                    EmbeddingDimensions = 768
                                });

        var orphan = MakeJobRecord(id: "stale",
                                    library: "a",
                                    version: "1.0",
                                    status: ScrapeJobStatus.Running,
                                    createdAt: DateTime.UtcNow - TimeSpan.FromDays(1),
                                    lastProgressAt: DateTime.UtcNow - TimeSpan.FromDays(1));
        jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<ScrapeJobRecord>());
        jobRepo.ListRunningJobsAsync(Arg.Any<CancellationToken>())
               .Returns(new[] { orphan });

        var json = await HealthTools.GetDashboardIndex(factory, profile: null,
                                                       ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"tool\": \"cancel_scrape\"", json);
    }

    private static ScrapeJobRecord MakeJobRecord(string id,
                                                  string library,
                                                  string version,
                                                  ScrapeJobStatus status,
                                                  DateTime createdAt,
                                                  DateTime? lastProgressAt) =>
        new()
            {
                Id = id,
                Job = new ScrapeJob
                          {
                              RootUrl = "https://example.com",
                              LibraryHint = library,
                              LibraryId = library,
                              Version = version,
                              AllowedUrlPatterns = Array.Empty<string>()
                          },
                Status = status,
                CreatedAt = createdAt,
                LastProgressAt = lastProgressAt
            };

    private static (RepositoryFactory factory,
                    ILibraryRepository libraryRepo,
                    IChunkRepository chunkRepo,
                    IScrapeJobRepository jobRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var jobRepo = Substitute.For<IScrapeJobRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        jobRepo.ListRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<ScrapeJobRecord>());
        jobRepo.ListRunningJobsAsync(Arg.Any<CancellationToken>())
               .Returns(Array.Empty<ScrapeJobRecord>());
        return (factory, libraryRepo, chunkRepo, jobRepo);
    }
}
