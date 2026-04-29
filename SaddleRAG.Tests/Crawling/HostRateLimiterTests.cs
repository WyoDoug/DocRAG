// HostRateLimiterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class HostRateLimiterTests
{
    [Fact]
    public async Task AcquireBlocksOnceConcurrencyExhausted()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 2, minConcurrency: 1, maxConcurrency: 4);

        var slot1 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var slot2 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);

        var third = limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var raced = await Task.WhenAny(third, Task.Delay(50, TestContext.Current.CancellationToken));

        Assert.NotEqual(third, raced);

        slot1.Dispose();
        var slot3 = await third;
        slot2.Dispose();
        slot3.Dispose();
    }

    [Fact]
    public async Task SuccessGrowsConcurrencyAfterThreshold()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 2,
                                          minConcurrency: 1,
                                          maxConcurrency: 4,
                                          growthThreshold: 3
                                         );

        Assert.Equal(2, limiter.CurrentConcurrency);

        for(var i = 0; i < 3; i++)
            limiter.ReportSuccess();

        Assert.Equal(3, limiter.CurrentConcurrency);

        var slots = new List<HostSlot>();
        for(var i = 0; i < 3; i++)
            slots.Add(await limiter.AcquireAsync(TestContext.Current.CancellationToken));

        foreach(var slot in slots)
            slot.Dispose();
    }

    [Fact]
    public void GrowthCappedAtMaxConcurrency()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 4,
                                          minConcurrency: 1,
                                          maxConcurrency: 4,
                                          growthThreshold: 1
                                         );

        for(var i = 0; i < 100; i++)
            limiter.ReportSuccess();

        Assert.Equal(4, limiter.CurrentConcurrency);
    }

    [Fact]
    public void RateLimitedHalvesConcurrency()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 8, minConcurrency: 1, maxConcurrency: 8);

        limiter.ReportRateLimited(retryAfter: TimeSpan.Zero);

        Assert.Equal(4, limiter.CurrentConcurrency);

        limiter.ReportRateLimited(retryAfter: TimeSpan.Zero);

        Assert.Equal(2, limiter.CurrentConcurrency);
    }

    [Fact]
    public void RateLimitedFlooredAtMinConcurrency()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 4, minConcurrency: 2, maxConcurrency: 4);

        for(var i = 0; i < 10; i++)
            limiter.ReportRateLimited(retryAfter: TimeSpan.Zero);

        Assert.Equal(2, limiter.CurrentConcurrency);
    }

    [Fact]
    public void RateLimitedResetsConsecutiveSuccessCounter()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 2,
                                          minConcurrency: 1,
                                          maxConcurrency: 4,
                                          growthThreshold: 3
                                         );

        limiter.ReportSuccess();
        limiter.ReportSuccess();
        limiter.ReportRateLimited(retryAfter: TimeSpan.Zero);

        Assert.Equal(1, limiter.CurrentConcurrency);

        limiter.ReportSuccess();
        limiter.ReportSuccess();

        Assert.Equal(1, limiter.CurrentConcurrency);
    }

    [Fact]
    public async Task RateLimitedArmsPenaltyPause()
    {
        var penaltyDuration = TimeSpan.FromMilliseconds(150);
        var limiter = new HostRateLimiter(initialConcurrency: 2, minConcurrency: 1, maxConcurrency: 4);

        var slot1 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        slot1.Dispose();

        limiter.ReportRateLimited(penaltyDuration);

        var start = DateTime.UtcNow;
        var slot2 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - start;
        slot2.Dispose();

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100),
                    $"Expected penalty pause >=100ms, observed {elapsed.TotalMilliseconds:F0}ms"
                   );
    }

    [Fact]
    public async Task DrainShrinksAvailablePermitsAfterCallersRelease()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 4, minConcurrency: 1, maxConcurrency: 4);

        var s1 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var s2 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var s3 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var s4 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);

        limiter.ReportRateLimited(retryAfter: TimeSpan.Zero);
        Assert.Equal(2, limiter.CurrentConcurrency);

        s1.Dispose();
        s2.Dispose();
        s3.Dispose();
        s4.Dispose();

        await Task.Delay(50, TestContext.Current.CancellationToken);

        var afterRelease1 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var afterRelease2 = await limiter.AcquireAsync(TestContext.Current.CancellationToken);

        var third = limiter.AcquireAsync(TestContext.Current.CancellationToken);
        var raced = await Task.WhenAny(third, Task.Delay(50, TestContext.Current.CancellationToken));
        Assert.NotEqual(third, raced);

        afterRelease1.Dispose();
        var slotThird = await third;
        afterRelease2.Dispose();
        slotThird.Dispose();
    }

    [Fact]
    public void ConstructorRejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostRateLimiter(2, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostRateLimiter(2, 4, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostRateLimiter(0, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostRateLimiter(5, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostRateLimiter(2, 1, 4, growthThreshold: 0));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public void IsRateLimitStatusTrueForRateLimitCodes(int httpStatus)
    {
        Assert.True(HostRateLimiter.IsRateLimitStatus(httpStatus));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(301)]
    [InlineData(304)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public void IsRateLimitStatusFalseForOtherStatuses(int httpStatus)
    {
        Assert.False(HostRateLimiter.IsRateLimitStatus(httpStatus));
    }

    [Fact]
    public void IsForbiddenStatusOnlyTrueFor403()
    {
        Assert.True(HostRateLimiter.IsForbiddenStatus(403));
        Assert.False(HostRateLimiter.IsForbiddenStatus(401));
        Assert.False(HostRateLimiter.IsForbiddenStatus(429));
        Assert.False(HostRateLimiter.IsForbiddenStatus(503));
        Assert.False(HostRateLimiter.IsForbiddenStatus(200));
    }

    [Fact]
    public void TransientErrorResetsSuccessCounterButDoesNotShrink()
    {
        var limiter = new HostRateLimiter(initialConcurrency: 2,
                                          minConcurrency: 1,
                                          maxConcurrency: 4,
                                          growthThreshold: 3
                                         );

        limiter.ReportSuccess();
        limiter.ReportSuccess();
        limiter.ReportTransientError();

        Assert.Equal(2, limiter.CurrentConcurrency);

        limiter.ReportSuccess();
        limiter.ReportSuccess();

        Assert.Equal(2, limiter.CurrentConcurrency);

        limiter.ReportSuccess();

        Assert.Equal(3, limiter.CurrentConcurrency);
    }
}
