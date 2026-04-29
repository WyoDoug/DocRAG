// HostScopeFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Collections.Concurrent;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Per-host blacklist of gated path prefixes.
///     When an out-of-scope URL returns 403, the URL's first path segment
///     is added to this filter; subsequent fetches whose first segment
///     matches an entry are dropped before they hit the network.
///     Edge WAFs frequently gate marketing or product paths
///     (e.g. <c>/products/</c>, <c>/it-it/</c>) while leaving documentation
///     paths open — slowing the whole host on a single gated 403 punishes
///     the working path. This filter prunes by prefix instead.
/// </summary>
/// <remarks>
///     One instance per host. <see cref="CrawlBudget"/> holds the dictionary
///     keyed by host and routes lookups through the matching filter.
/// </remarks>
public sealed class HostScopeFilter
{
    public HostScopeFilter()
    {
        mGatedPrefixes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<string, byte> mGatedPrefixes;

    /// <summary>
    ///     Snapshot of currently gated path prefixes for this host.
    ///     Useful for diagnostics and tests.
    /// </summary>
    public IReadOnlyCollection<string> GatedPrefixes => mGatedPrefixes.Keys.ToArray();

    /// <summary>
    ///     Mark <paramref name="uri"/>'s first path segment as gated for this host.
    ///     Subsequent calls to <see cref="IsGated"/> for any URL sharing that
    ///     first segment return true.
    /// </summary>
    public void GatePrefixOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string prefix = ExtractFirstSegment(uri);
        mGatedPrefixes.TryAdd(prefix, value: 0);
    }

    /// <summary>
    ///     Returns true if <paramref name="uri"/>'s first path segment has
    ///     been gated by a prior 403 on this host.
    /// </summary>
    public bool IsGated(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string prefix = ExtractFirstSegment(uri);
        bool result = mGatedPrefixes.ContainsKey(prefix);
        return result;
    }

    /// <summary>
    ///     Compute the path-prefix key used for gating decisions.
    ///     For <c>/products/platform/atlas-data-federation</c> returns <c>/products/</c>;
    ///     for <c>/try</c> returns <c>/try/</c>;
    ///     for <c>/</c> returns <c>/</c>.
    /// </summary>
    public static string ExtractFirstSegment(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string path = uri.AbsolutePath;
        string result = "/";

        if (path.Length > 1)
        {
            int secondSlash = path.IndexOf(value: '/', startIndex: 1);
            result = secondSlash > 0
                         ? path[..(secondSlash + 1)]
                         : path + "/";
        }

        return result;
    }
}
