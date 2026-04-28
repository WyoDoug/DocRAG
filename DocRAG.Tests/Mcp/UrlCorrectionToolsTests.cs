// UrlCorrectionToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion;
using DocRAG.Mcp.Tools;
using Microsoft.Extensions.Hosting;
using NSubstitute;

#endregion

namespace DocRAG.Tests.Mcp;

public sealed class UrlCorrectionToolsTests
{
    [Fact]
    public async Task SubmitUrlCorrectionDryRunReportsCascadeWithoutWriting()
    {
        var (factory, libraryRepo, runner, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo) = MakeFactory();
        chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50);
        pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(20);

        var json = await UrlCorrectionTools.SubmitUrlCorrection(factory, runner,
                                                                library: "foo",
                                                                version: "1.0",
                                                                newUrl: "https://docs.foo.com",
                                                                dryRun: true,
                                                                profile: null,
                                                                ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Chunks\": 50", json);
        Assert.Contains("\"Pages\": 20", json);
        await runner.DidNotReceive().QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitUrlCorrectionApplyDropsAndRequeues()
    {
        var (factory, libraryRepo, runner, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo) = MakeFactory();
        chunkRepo.DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50L);
        pageRepo.DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(20L);
        runner.QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("new-job-id");

        var json = await UrlCorrectionTools.SubmitUrlCorrection(factory, runner,
                                                                library: "foo",
                                                                version: "1.0",
                                                                newUrl: "https://docs.foo.com",
                                                                dryRun: false,
                                                                profile: null,
                                                                ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\": \"new-job-id\"", json);
        await chunkRepo.Received(1).DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await pageRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await profileRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await indexRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await bm25Repo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await libraryRepo.Received(1).ClearSuspectAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await runner.Received(1).QueueAsync(Arg.Any<ScrapeJob>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private static (RepositoryFactory factory,
                    ILibraryRepository libraryRepo,
                    ScrapeJobRunner runner,
                    IChunkRepository chunkRepo,
                    IPageRepository pageRepo,
                    ILibraryProfileRepository profileRepo,
                    ILibraryIndexRepository indexRepo,
                    IBm25ShardRepository bm25Repo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);

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
        return (factory, libraryRepo, runner, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo);
    }
}
