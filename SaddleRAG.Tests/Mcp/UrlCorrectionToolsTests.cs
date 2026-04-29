// UrlCorrectionToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Tools;
using Microsoft.Extensions.Hosting;
using NSubstitute;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class UrlCorrectionToolsTests
{
    [Fact]
    public async Task SubmitUrlCorrectionDryRunReportsCascadeWithoutWriting()
    {
        var harness = MakeFactory();
        harness.ChunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50);
        harness.PageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(20);

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner,
                                                                library: "foo",
                                                                version: "1.0",
                                                                newUrl: "https://docs.foo.com",
                                                                dryRun: true,
                                                                profile: null,
                                                                ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Chunks\": 50", json);
        Assert.Contains("\"Pages\": 20", json);
        await harness.Runner.DidNotReceive()
                            .QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await harness.Runner.DidNotReceive()
                            .CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionDryRunListsActiveJobsAsWouldCancel()
    {
        var harness = MakeFactory();
        var orphan = MakeJobRecord("orphan-1", "foo", "1.0", ScrapeJobStatus.Running);
        harness.ScrapeJobRepo.ListActiveJobsAsync("foo", "1.0", Arg.Any<CancellationToken>())
                             .Returns(new[] { orphan });

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner,
                                                                library: "foo",
                                                                version: "1.0",
                                                                newUrl: "https://docs.foo.com",
                                                                dryRun: true,
                                                                profile: null,
                                                                ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"WouldCancel\":", json);
        Assert.Contains("\"orphan-1\"", json);
        await harness.Runner.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionApplyDropsAndRequeues()
    {
        var harness = MakeFactory();
        harness.ChunkRepo.DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50L);
        harness.PageRepo.DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(20L);
        harness.Runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                      .Returns("new-job-id");

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner,
                                                                library: "foo",
                                                                version: "1.0",
                                                                newUrl: "https://docs.foo.com",
                                                                dryRun: false,
                                                                profile: null,
                                                                ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\": \"new-job-id\"", json);
        await harness.ChunkRepo.Received(1).DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.PageRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.ProfileRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.IndexRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.Bm25Repo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.LibraryRepo.Received(1).ClearSuspectAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await harness.Runner.Received(1).QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionApplyCancelsParallelRunningJobsBeforeRequeue()
    {
        var harness = MakeFactory();
        var first = MakeJobRecord("running-1", "foo", "1.0", ScrapeJobStatus.Running);
        var second = MakeJobRecord("running-2", "foo", "1.0", ScrapeJobStatus.Running);
        harness.ScrapeJobRepo.ListActiveJobsAsync("foo", "1.0", Arg.Any<CancellationToken>())
                             .Returns(new[] { first, second });
        harness.Runner.CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns(CancelScrapeOutcome.Signalled);
        harness.Runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                      .Returns("new-job-id");

        var json = await UrlCorrectionTools.SubmitUrlCorrection(harness.Factory, harness.Runner,
                                                                library: "foo",
                                                                version: "1.0",
                                                                newUrl: "https://docs.foo.com",
                                                                dryRun: false,
                                                                profile: null,
                                                                ct: TestContext.Current.CancellationToken);

        await harness.Runner.Received(1).CancelAsync("running-1", Arg.Any<CancellationToken>());
        await harness.Runner.Received(1).CancelAsync("running-2", Arg.Any<CancellationToken>());
        Assert.Contains("\"running-1\"", json);
        Assert.Contains("\"running-2\"", json);
        Assert.Contains("\"CancelledJobs\":", json);
    }

    private static ScrapeJobRecord MakeJobRecord(string id, string library, string version, ScrapeJobStatus status) =>
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
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(10)
            };

    private sealed record TestHarness(RepositoryFactory Factory,
                                       ILibraryRepository LibraryRepo,
                                       ScrapeJobRunner Runner,
                                       IChunkRepository ChunkRepo,
                                       IPageRepository PageRepo,
                                       ILibraryProfileRepository ProfileRepo,
                                       ILibraryIndexRepository IndexRepo,
                                       IBm25ShardRepository Bm25Repo,
                                       IScrapeJobRepository ScrapeJobRepo);

    private static TestHarness MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var scrapeJobRepo = Substitute.For<IScrapeJobRepository>();
        scrapeJobRepo.ListActiveJobsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Array.Empty<ScrapeJobRecord>());
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeJobRepo);

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var runner = Substitute.ForPartsOf<ScrapeJobRunner>(
            new object?[]
            {
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                lifetime
            });
        runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(string.Empty);
        return new TestHarness(factory, libraryRepo, runner, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo, scrapeJobRepo);
    }
}
