// CrawlBudgetTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Crawling;

#endregion

namespace DocRAG.Tests.Crawling;

public sealed class CrawlBudgetTests
{
    [Fact]
    public void GetLimiterReturnsSameInstancePerHost()
    {
        var budget = new CrawlBudget();

        var first = budget.GetLimiter("docs.example.com");
        var second = budget.GetLimiter("docs.example.com");
        var other = budget.GetLimiter("api.example.com");

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(2, budget.HostCount);
    }

    [Fact]
    public void HostLookupIsCaseInsensitive()
    {
        var budget = new CrawlBudget();

        var lower = budget.GetLimiter("docs.example.com");
        var upper = budget.GetLimiter("DOCS.EXAMPLE.COM");

        Assert.Same(lower, upper);
        Assert.Equal(1, budget.HostCount);
    }

    [Fact]
    public void GetScopeFilterReturnsSameInstancePerHost()
    {
        var budget = new CrawlBudget();

        var first = budget.GetScopeFilter("docs.example.com");
        var second = budget.GetScopeFilter("docs.example.com");
        var other = budget.GetScopeFilter("api.example.com");

        Assert.Same(first, second);
        Assert.NotSame(first, other);
    }

    [Fact]
    public void GetScopeFilterIsCaseInsensitive()
    {
        var budget = new CrawlBudget();

        var lower = budget.GetScopeFilter("docs.example.com");
        var upper = budget.GetScopeFilter("DOCS.EXAMPLE.COM");

        Assert.Same(lower, upper);
    }

    [Fact]
    public void GetSnapshotReturnsCurrentConcurrencyPerHost()
    {
        var budget = new CrawlBudget(initialConcurrency: 4, minConcurrency: 1, maxConcurrency: 8);

        var docsLimiter = budget.GetLimiter("docs.example.com");
        budget.GetLimiter("api.example.com");

        docsLimiter.ReportRateLimited(retryAfter: TimeSpan.Zero);

        var snapshot = budget.GetSnapshot();

        Assert.Equal(2, snapshot["docs.example.com"]);
        Assert.Equal(4, snapshot["api.example.com"]);
    }

    [Fact]
    public void ParseRetryAfterAcceptsDeltaSeconds()
    {
        var result = CrawlBudget.ParseRetryAfter("30");

        Assert.Equal(TimeSpan.FromSeconds(30), result);
    }

    [Fact]
    public void ParseRetryAfterAcceptsHttpDate()
    {
        var future = DateTime.UtcNow.AddMinutes(2);
        string headerValue = future.ToString("R");

        var result = CrawlBudget.ParseRetryAfter(headerValue);

        Assert.NotNull(result);
        Assert.True(result.Value > TimeSpan.FromSeconds(60));
        Assert.True(result.Value < TimeSpan.FromMinutes(3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-30")]
    public void ParseRetryAfterReturnsNullForInvalidInput(string? input)
    {
        var result = CrawlBudget.ParseRetryAfter(input);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRetryAfterReturnsNullForPastDate()
    {
        var past = DateTime.UtcNow.AddMinutes(-5).ToString("R");

        var result = CrawlBudget.ParseRetryAfter(past);

        Assert.Null(result);
    }
}
