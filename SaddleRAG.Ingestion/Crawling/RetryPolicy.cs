// RetryPolicy.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Retry policy for in-scope URLs that returned an HTTP 403 on first
///     fetch. WAFs occasionally classify a fresh fetcher as a bot and 403
///     a single page even when the rest of the docs path is open; a short
///     pause and a re-fetch usually succeeds.
///     The crawl handles these as "problem children": each attempt waits
///     <see cref="ComputeRetryDelay"/> before re-issuing, attempts run on
///     a single dedicated retry worker (so they're serialized), and the
///     worker waits <see cref="MinDelayBetweenRetriesMs"/> between every
///     retry it processes — even successful ones — so we don't burst.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    ///     Maximum number of retry attempts after the initial fetch failure.
    ///     With <c>MaxRetryAttempts = 2</c> a page is tried at most three
    ///     times overall: initial + retry 1 + retry 2.
    /// </summary>
    public const int MaxRetryAttempts = 2;

    /// <summary>
    ///     Initial backoff before the first retry (attempt index 0).
    ///     Doubles per attempt index up to <see cref="MaxRetryDelayMs"/>.
    /// </summary>
    public const int InitialRetryDelayMs = 1000;

    /// <summary>
    ///     Cap on the per-attempt backoff. The doubling schedule levels off
    ///     here so an unlucky page never waits longer than this between tries.
    /// </summary>
    public const int MaxRetryDelayMs = 5000;

    /// <summary>
    ///     Minimum gap between two retries the dedicated worker processes,
    ///     applied even after a successful retry. Keeps the WAF-friendly
    ///     trickle pace regardless of how many retries are queued.
    /// </summary>
    public const int MinDelayBetweenRetriesMs = 1000;

    /// <summary>
    ///     Compute the wait before retry <paramref name="attemptIndex"/>.
    ///     <paramref name="attemptIndex"/> is 0 for the first retry, 1 for
    ///     the second, and so on. Formula:
    ///     <c>min(InitialRetryDelayMs * 2^attemptIndex, MaxRetryDelayMs)</c>.
    /// </summary>
    public static TimeSpan ComputeRetryDelay(int attemptIndex)
    {
        if (attemptIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(attemptIndex), attemptIndex, "attemptIndex must be >= 0");

        long ms = (long) InitialRetryDelayMs << attemptIndex;
        long capped = Math.Min(ms, MaxRetryDelayMs);
        var result = TimeSpan.FromMilliseconds(capped);
        return result;
    }
}
