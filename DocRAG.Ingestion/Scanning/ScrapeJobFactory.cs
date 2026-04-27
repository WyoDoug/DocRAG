// ScrapeJobFactory.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Scanning;

/// <summary>
///     Creates ScrapeJob instances from a URL with sensible auto-derived defaults.
///     Used by scrape_docs and the dependency indexing pipeline.
/// </summary>
public static class ScrapeJobFactory
{
    /// <summary>
    ///     Create a ScrapeJob from a URL with auto-derived scope and defaults.
    /// </summary>
    public static ScrapeJob CreateFromUrl(string url,
                                          string libraryId,
                                          string version,
                                          string? hint = null,
                                          int maxPages = DefaultMaxPages,
                                          int fetchDelayMs = DefaultFetchDelayMs,
                                          bool forceClean = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var uri = new Uri(url);

        var job = new ScrapeJob
                      {
                          RootUrl = url,
                          LibraryId = libraryId,
                          Version = version,
                          LibraryHint = hint ?? libraryId,
                          AllowedUrlPatterns = [uri.Host],
                          ExcludedUrlPatterns = smDefaultExcludedPatterns,
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs,
                          InScopeDepth = DefaultInScopeDepth,
                          SameHostDepth = DefaultSameHostDepth,
                          OffSiteDepth = DefaultOffSiteDepth,
                          ForceClean = forceClean
                      };
        return job;
    }

    private const int DefaultMaxPages = 0;
    private const int DefaultFetchDelayMs = 500;
    private const int DefaultInScopeDepth = 10;
    private const int DefaultSameHostDepth = 5;
    private const int DefaultOffSiteDepth = 1;

    private const string ExcludeBlog = @"/blog/";
    private const string ExcludePricing = @"/pricing/";
    private const string ExcludeLogin = @"/login/";
    private const string ExcludeSearch = @"/search";
    private const string ExcludeAccount = @"/account/";
    private const string ExcludeCart = @"/cart/";
    private const string ExcludeMailto = @"mailto:";

    private static readonly string[] smDefaultExcludedPatterns =
        [
            ExcludeBlog, ExcludePricing, ExcludeLogin, ExcludeSearch,
            ExcludeAccount, ExcludeCart, ExcludeMailto, @"#"
        ];
}
