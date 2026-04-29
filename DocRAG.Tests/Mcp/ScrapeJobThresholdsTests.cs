// ScrapeJobThresholdsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Tests.Mcp;

public sealed class ScrapeJobThresholdsTests
{
    [Fact]
    public void IsStaleRunningTrueWhenLastProgressOlderThanCutoff()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(4);
        var job = MakeJob(ScrapeJobStatus.Running,
                           createdAt: now - TimeSpan.FromDays(1),
                           lastProgressAt: now - TimeSpan.FromDays(1));

        Assert.True(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    [Fact]
    public void IsStaleRunningFalseWhenLastProgressRecentEnough()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(4);
        var job = MakeJob(ScrapeJobStatus.Running,
                           createdAt: now - TimeSpan.FromHours(8),
                           lastProgressAt: now - TimeSpan.FromMinutes(30));

        Assert.False(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    [Fact]
    public void IsStaleRunningFallsBackToCreatedAtWhenLastProgressNull()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(4);
        var job = MakeJob(ScrapeJobStatus.Running,
                           createdAt: now - TimeSpan.FromDays(2),
                           lastProgressAt: null);

        Assert.True(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    [Fact]
    public void IsStaleRunningFalseForNonRunningStatus()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(4);
        var job = MakeJob(ScrapeJobStatus.Cancelled,
                           createdAt: now - TimeSpan.FromDays(7),
                           lastProgressAt: now - TimeSpan.FromDays(7));

        Assert.False(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    private static ScrapeJobRecord MakeJob(ScrapeJobStatus status,
                                            DateTime createdAt,
                                            DateTime? lastProgressAt) =>
        new()
            {
                Id = "job",
                Job = new ScrapeJob
                          {
                              RootUrl = "https://example.com",
                              LibraryHint = "h",
                              LibraryId = "foo",
                              Version = "1.0",
                              AllowedUrlPatterns = Array.Empty<string>()
                          },
                Status = status,
                CreatedAt = createdAt,
                LastProgressAt = lastProgressAt
            };
}
