// CancellationToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion;
using SaddleRAG.Mcp.Tools;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace SaddleRAG.Tests.Mcp;

public sealed class CancellationToolsTests
{
    [Fact]
    public async Task CancelScrapeSignalledReturnsSignalledOutcome()
    {
        var runner = MakeRunnerSubstitute();
        runner.CancelAsync("abc", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.Signalled);

        var json = await CancellationTools.CancelScrape(runner, jobId: "abc",
                                                        ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"Signalled\"", json);
    }

    [Fact]
    public async Task CancelScrapeNotFoundReturnsNotFoundOutcome()
    {
        var runner = MakeRunnerSubstitute();
        runner.CancelAsync("missing", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.NotFound);

        var json = await CancellationTools.CancelScrape(runner, jobId: "missing",
                                                        ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"NotFound\"", json);
    }

    [Fact]
    public async Task CancelScrapeOrphanCleanedUpReturnsOrphanOutcome()
    {
        var runner = MakeRunnerSubstitute();
        runner.CancelAsync("orphan", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.OrphanCleanedUp);

        var json = await CancellationTools.CancelScrape(runner, jobId: "orphan",
                                                        ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"OrphanCleanedUp\"", json);
    }

    [Fact]
    public async Task CancelScrapeAlreadyTerminalReturnsTerminalOutcome()
    {
        var runner = MakeRunnerSubstitute();
        runner.CancelAsync("done", Arg.Any<CancellationToken>()).Returns(CancelScrapeOutcome.AlreadyTerminal);

        var json = await CancellationTools.CancelScrape(runner, jobId: "done",
                                                        ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Outcome\": \"AlreadyTerminal\"", json);
    }

    private static ScrapeJobRunner MakeRunnerSubstitute()
    {
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
        return runner;
    }
}
