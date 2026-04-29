// RetryPolicyTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class RetryPolicyTests
{
    [Fact]
    public void FirstRetryWaitsInitialDelay()
    {
        var delay = RetryPolicy.ComputeRetryDelay(attemptIndex: 0);

        Assert.Equal(TimeSpan.FromMilliseconds(RetryPolicy.InitialRetryDelayMs), delay);
    }

    [Fact]
    public void SecondRetryDoublesInitialDelay()
    {
        var delay = RetryPolicy.ComputeRetryDelay(attemptIndex: 1);

        Assert.Equal(TimeSpan.FromMilliseconds(RetryPolicy.InitialRetryDelayMs * 2), delay);
    }

    [Fact]
    public void ThirdRetryQuadruplesInitialDelay()
    {
        var delay = RetryPolicy.ComputeRetryDelay(attemptIndex: 2);

        Assert.Equal(TimeSpan.FromMilliseconds(RetryPolicy.InitialRetryDelayMs * 4), delay);
    }

    [Fact]
    public void RetryDelayIsCappedAtMax()
    {
        var delay = RetryPolicy.ComputeRetryDelay(attemptIndex: 10);

        Assert.Equal(TimeSpan.FromMilliseconds(RetryPolicy.MaxRetryDelayMs), delay);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(20)]
    public void RetryDelayDoesNotExceedCap(int attemptIndex)
    {
        var delay = RetryPolicy.ComputeRetryDelay(attemptIndex);

        Assert.True(delay.TotalMilliseconds <= RetryPolicy.MaxRetryDelayMs);
    }

    [Fact]
    public void NegativeAttemptIndexThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.ComputeRetryDelay(attemptIndex: -1));
    }

    [Fact]
    public void MaxRetryAttemptsAllowsTwoRetries()
    {
        Assert.Equal(2, RetryPolicy.MaxRetryAttempts);
    }
}
