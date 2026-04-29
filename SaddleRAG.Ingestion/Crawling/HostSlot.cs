// HostSlot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     RAII slot returned by <see cref="HostRateLimiter.AcquireAsync"/>.
///     Disposing returns the permit to the pool.
/// </summary>
public readonly struct HostSlot : IDisposable
{
    public HostSlot(HostRateLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        mLimiter = limiter;
    }

    private readonly HostRateLimiter mLimiter;

    public void Dispose()
    {
        mLimiter.Release();
    }
}
