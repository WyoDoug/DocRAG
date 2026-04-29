// PageCrawler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Crawls documentation sites using Playwright (headless Chromium).
///     Discovers pages via breadth-first link traversal within allowed URL patterns.
///     Applies a same-scope-unlimited / out-of-scope-depth-limited heuristic
///     so that crawls don't recurse forever into linked GitHub or external sites.
/// </summary>
public class PageCrawler
{
    private record CrawlEntry(string Url,
                              int InScopeDepth,
                              int SameHostDepth,
                              int OffSiteDepth,
                              int RetryAttemptIndex = 0);

    private record RootScope(string Host, string PathPrefix);

    private record FrameStats(int TextLength, int LinkTextLength);

    /// <summary>
    ///     Mutable counters for the dry-run BFS loop.
    ///     Used instead of ref parameters since the helper methods are async.
    /// </summary>
    private class DryRunStats
    {
        public int TotalPages { get; set; }
        public int InScopePages { get; set; }
        public int OutOfScopePages { get; set; }
        public int DepthLimitedSkips { get; set; }
        public int FilteredSkips { get; set; }
        public int FetchErrors { get; set; }
        public bool HitMaxLimit { get; set; }
    }

    public PageCrawler(IPageRepository pageRepository,
                       GitHubRepoScraper gitHubScraper,
                       ILogger<PageCrawler> logger)
    {
        mPageRepository = pageRepository;
        mGitHubScraper = gitHubScraper;
        mLogger = logger;
    }

    private readonly GitHubRepoScraper mGitHubScraper;
    private readonly ILogger<PageCrawler> mLogger;

    private readonly IPageRepository mPageRepository;

    /// <summary>
    ///     Dry-run a crawl: actually fetch every page with Playwright,
    ///     resolve all links, but DO NOT store anything to MongoDB or
    ///     clone any GitHub repos. Returns a detailed report so you can
    ///     decide whether the real crawl will produce reasonable results.
    /// </summary>
    public async Task<DryRunReport> DryRunAsync(ScrapeJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var startTime = DateTime.UtcNow;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                                                                            {
                                                                                Headless = true,
                                                                                Args =
                                                                                    [
                                                                                        $"--user-agent={BrowserUserAgent}"
                                                                                    ]
                                                                            }
                                                                       );

        var rootUri = new Uri(job.RootUrl);
        var rootScope = ComputeRootScope(rootUri);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<CrawlEntry>();
        string normalizedRoot = NormalizeUrl(job.RootUrl) ?? job.RootUrl;
        queue.Enqueue(new CrawlEntry(normalizedRoot, InScopeDepth: 0, SameHostDepth: 0, OffSiteDepth: 0));

        var stats = new DryRunStats();
        var depthDist = new Dictionary<int, int>();
        var pagesByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var githubRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var samplePages = new List<DryRunPageEntry>();
        var errors = new List<DryRunFetchError>();

        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            if (job.MaxPages > 0 && stats.TotalPages >= job.MaxPages)
            {
                stats.HitMaxLimit = true;
                break;
            }

            var entry = queue.Dequeue();
            string url = entry.Url;

            if (visited.Add(url))
            {
                await ProcessDryRunEntryAsync(url,
                                              entry,
                                              job,
                                              rootScope,
                                              browser,
                                              visited,
                                              queue,
                                              githubRepos,
                                              samplePages,
                                              errors,
                                              depthDist,
                                              pagesByHost,
                                              stats,
                                              ct
                                             );
            }
        }

        var report = new DryRunReport
                         {
                             TotalPages = stats.TotalPages,
                             InScopePages = stats.InScopePages,
                             OutOfScopePages = stats.OutOfScopePages,
                             DepthLimitedSkips = stats.DepthLimitedSkips,
                             FilteredSkips = stats.FilteredSkips,
                             FetchErrors = stats.FetchErrors,
                             DepthDistribution = depthDist,
                             PagesByHost = pagesByHost,
                             GitHubReposToClone = githubRepos.OrderBy(r => r).ToList(),
                             SamplePages = samplePages,
                             Errors = errors,
                             ElapsedTime = DateTime.UtcNow - startTime,
                             HitMaxPagesLimit = stats.HitMaxLimit,
                             PagesRemainingInQueue = queue.Count,
                             SamplePendingUrls = queue.Take(SamplePendingUrlCount).Select(e => e.Url).ToList()
                         };

        return report;
    }

    private async Task ProcessDryRunEntryAsync(string url,
                                               CrawlEntry entry,
                                               ScrapeJob job,
                                               RootScope rootScope,
                                               IBrowser browser,
                                               HashSet<string> visited,
                                               Queue<CrawlEntry> queue,
                                               HashSet<string> githubRepos,
                                               List<DryRunPageEntry> samplePages,
                                               List<DryRunFetchError> errors,
                                               Dictionary<int, int> depthDist,
                                               Dictionary<string, int> pagesByHost,
                                               DryRunStats stats,
                                               CancellationToken ct)
    {
        switch(true)
        {
            case true when !IsAllowed(url, job):
                stats.FilteredSkips++;
                break;
            case true when GitHubRepoScraper.TryParseGitHubUrl(url, out string owner, out string repo):
                githubRepos.Add($"{owner}/{repo}");
                break;
            default:
                await ProcessInScopeEntryAsync(url,
                                               entry,
                                               job,
                                               rootScope,
                                               browser,
                                               visited,
                                               queue,
                                               samplePages,
                                               errors,
                                               depthDist,
                                               pagesByHost,
                                               stats,
                                               ct
                                              );
                break;
        }
    }

    private async Task ProcessInScopeEntryAsync(string url,
                                                CrawlEntry entry,
                                                ScrapeJob job,
                                                RootScope rootScope,
                                                IBrowser browser,
                                                HashSet<string> visited,
                                                Queue<CrawlEntry> queue,
                                                List<DryRunPageEntry> samplePages,
                                                List<DryRunFetchError> errors,
                                                Dictionary<int, int> depthDist,
                                                Dictionary<string, int> pagesByHost,
                                                DryRunStats stats,
                                                CancellationToken ct)
    {
        bool inScope = IsInRootScope(url, rootScope);
        bool sameHost = !inScope && IsSameHost(url, rootScope);
        bool depthExceeded = inScope  ? job.InScopeDepth > 0 && entry.InScopeDepth >= job.InScopeDepth :
                             sameHost ? entry.SameHostDepth >= job.SameHostDepth :
                                        entry.OffSiteDepth >= job.OffSiteDepth;

        if (depthExceeded)
            stats.DepthLimitedSkips++;
        else
        {
            await FetchDryRunPageAsync(url,
                                       entry,
                                       job,
                                       rootScope,
                                       browser,
                                       visited,
                                       queue,
                                       samplePages,
                                       errors,
                                       depthDist,
                                       pagesByHost,
                                       inScope,
                                       stats,
                                       ct
                                      );
        }
    }

    private async Task FetchDryRunPageAsync(string url,
                                            CrawlEntry entry,
                                            ScrapeJob job,
                                            RootScope rootScope,
                                            IBrowser browser,
                                            HashSet<string> visited,
                                            Queue<CrawlEntry> queue,
                                            List<DryRunPageEntry> samplePages,
                                            List<DryRunFetchError> errors,
                                            Dictionary<int, int> depthDist,
                                            Dictionary<string, int> pagesByHost,
                                            bool inScope,
                                            DryRunStats stats,
                                            CancellationToken ct)
    {
        mLogger.LogDebug("[dry-run] Fetching {Url}", url);

        var page = await browser.NewPageAsync();
        try
        {
            var response = await NavigateAndPreparePageAsync(page, url, ct);

            switch(response)
            {
                case null:
                    stats.FetchErrors++;
                    errors.Add(new DryRunFetchError
                                   {
                                       Url = url,
                                       HttpStatus = 0,
                                       ErrorKind = "NoResponse",
                                       Message = "Playwright returned null response"
                                   }
                              );
                    break;
                case { Ok: false }:
                    stats.FetchErrors++;
                    errors.Add(new DryRunFetchError
                                   {
                                       Url = url,
                                       HttpStatus = response.Status,
                                       ErrorKind = $"Http{response.Status}",
                                       Message = response.StatusText
                                   }
                              );
                    break;
                default:
                    await ProcessSuccessfulDryRunResponseAsync(page,
                                                               url,
                                                               entry,
                                                               job,
                                                               rootScope,
                                                               inScope,
                                                               visited,
                                                               queue,
                                                               samplePages,
                                                               depthDist,
                                                               pagesByHost,
                                                               stats,
                                                               ct
                                                              );
                    break;
            }
        }
        catch(Exception ex)
        {
            stats.FetchErrors++;
            errors.Add(new DryRunFetchError
                           {
                               Url = url,
                               HttpStatus = 0,
                               ErrorKind = ex.GetType().Name,
                               Message = ex.Message.Length > ErrorMessageMaxLength
                                             ? ex.Message[..ErrorMessageMaxLength]
                                             : ex.Message
                           }
                      );
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task ProcessSuccessfulDryRunResponseAsync(IPage page,
                                                            string url,
                                                            CrawlEntry entry,
                                                            ScrapeJob job,
                                                            RootScope rootScope,
                                                            bool inScope,
                                                            HashSet<string> visited,
                                                            Queue<CrawlEntry> queue,
                                                            List<DryRunPageEntry> samplePages,
                                                            Dictionary<int, int> depthDist,
                                                            Dictionary<string, int> pagesByHost,
                                                            DryRunStats stats,
                                                            CancellationToken ct)
    {
        await ExpandCollapsibleNavigationAsync(page);
        string content = await ExtractMainContentAsync(page);
        var links = await ExtractLinksAsync(page);

        stats.TotalPages++;
        if (inScope)
            stats.InScopePages++;
        else
            stats.OutOfScopePages++;

        bool urlSameHost = !inScope && IsSameHost(url, rootScope);
        int effectiveDepth = inScope     ? entry.InScopeDepth :
                             urlSameHost ? entry.SameHostDepth : entry.OffSiteDepth;

        depthDist.TryGetValue(effectiveDepth, out int existing);
        depthDist[effectiveDepth] = existing + 1;

        string host = new Uri(url).Host;
        pagesByHost.TryGetValue(host, out int hostCount);
        pagesByHost[host] = hostCount + 1;

        if (samplePages.Count < SamplePageLimit)
        {
            samplePages.Add(new DryRunPageEntry
                                {
                                    Url = url,
                                    OutOfScopeDepth = effectiveDepth,
                                    InScope = inScope,
                                    ContentBytes = content.Length,
                                    LinksFound = links.Count
                                }
                           );
        }

        EnqueueDiscoveredLinks(links,
                               visited.Contains,
                               job,
                               rootScope,
                               entry,
                               (child, _) => queue.Enqueue(child)
                              );

        if (job.FetchDelayMs > 0)
            await Task.Delay(job.FetchDelayMs, ct);
    }

    /// <summary>
    ///     Per-crawl mutable state shared by parallel worker tasks.
    ///     One instance per <see cref="CrawlAsync"/> call.
    ///     Two channels back the priority queue: <see cref="InScopeEntries"/>
    ///     for URLs that match the root scope's path prefix, and
    ///     <see cref="OffPathEntries"/> for everything else. Workers drain
    ///     in-scope first so the source path makes progress even when the
    ///     off-path queue explodes from marketing/locale links.
    /// </summary>
    private sealed class CrawlContext
    {
        public required ScrapeJob Job { get; init; }
        public required RootScope RootScope { get; init; }
        public required ChannelWriter<PageRecord> PageOutput { get; init; }
        public required Channel<CrawlEntry> InScopeEntries { get; init; }
        public required Channel<CrawlEntry> OffPathEntries { get; init; }
        public required Channel<CrawlEntry> RetryEntries { get; init; }
        public required ConcurrentDictionary<string, byte> Visited { get; init; }
        public required ConcurrentDictionary<string, byte> ClonedRepos { get; init; }
        public required CrawlBudget Budget { get; init; }
        public required Action<int>? OnPageFetched { get; init; }
        public required Action<int>? OnQueued { get; init; }
        public required Action? OnFetchError { get; init; }
        public required CancellationToken Token { get; init; }

        /// <summary>
        ///     URLs that 403'd in-scope on every retry attempt and were
        ///     ultimately dropped. Surfaced for diagnostics so the caller
        ///     can log or report which pages slipped through.
        /// </summary>
        public ConcurrentBag<string> DroppedInScopeUrls { get; } = new();

        public string? SiteExtension { get; set; }

        private int mInFlight;
        private int mPageCount;

        public int PageCount => Volatile.Read(ref mPageCount);
        public int InFlightCount => Volatile.Read(ref mInFlight);

        public int IncrementPageCount() => Interlocked.Increment(ref mPageCount);
        public void IncrementInFlight() => Interlocked.Increment(ref mInFlight);
        public int DecrementInFlight() => Interlocked.Decrement(ref mInFlight);

        public void EnqueueChild(CrawlEntry entry, bool inScope)
        {
            ArgumentNullException.ThrowIfNull(entry);

            IncrementInFlight();
            var writer = inScope ? InScopeEntries.Writer : OffPathEntries.Writer;
            if (!writer.TryWrite(entry))
                DecrementInFlight();
        }

        /// <summary>
        ///     Schedule a retry of <paramref name="entry"/> after the policy
        ///     delay. In-flight is incremented immediately so the channels
        ///     don't complete during the wait, then the entry is written to
        ///     <see cref="RetryEntries"/> for the dedicated retry worker.
        /// </summary>
        public void ScheduleRetry(CrawlEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var retryEntry = entry with { RetryAttemptIndex = entry.RetryAttemptIndex + 1 };
            var delay = RetryPolicy.ComputeRetryDelay(entry.RetryAttemptIndex);

            IncrementInFlight();

            _ = Task.Run(async () =>
                             {
                                 try
                                 {
                                     await Task.Delay(delay, Token);
                                     if (!RetryEntries.Writer.TryWrite(retryEntry))
                                         DecrementInFlight();
                                 }
                                 catch(OperationCanceledException)
                                 {
                                     DecrementInFlight();
                                 }
                             }
                       );
        }

        public void CompleteAllEntries()
        {
            InScopeEntries.Writer.TryComplete();
            OffPathEntries.Writer.TryComplete();
            RetryEntries.Writer.TryComplete();
        }

        public bool IsVisited(string url)
        {
            ArgumentException.ThrowIfNullOrEmpty(url);

            bool result = Visited.ContainsKey(url);
            return result;
        }

        /// <summary>
        ///     Returns true when <paramref name="url"/> is out-of-scope and
        ///     its host's <see cref="HostScopeFilter"/> has gated the URL's
        ///     path prefix from a prior 403. In-scope URLs are never gated.
        /// </summary>
        public bool IsGated(string url)
        {
            ArgumentException.ThrowIfNullOrEmpty(url);

            bool result = false;
            if (!IsInRootScope(url, RootScope))
            {
                var uri = new Uri(url);
                var filter = Budget.GetScopeFilter(uri.Host);
                result = filter.IsGated(uri);
            }

            return result;
        }
    }

    /// <summary>
    ///     Fetch a single URL into a <see cref="PageRecord"/> without
    ///     starting a BFS — used by the <c>add_page</c> top-up path. Goes
    ///     through the same Playwright + 403 retry loop a regular crawl
    ///     would use, but skips link extraction so we don't drag the rest
    ///     of the site in. Persists the page record on success and
    ///     returns it; returns null when retries are exhausted.
    /// </summary>
    public async Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                                        string version,
                                                        string url,
                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(url);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                                                                            {
                                                                                Headless = true,
                                                                                Args =
                                                                                    [
                                                                                        $"--user-agent={BrowserUserAgent}"
                                                                                    ]
                                                                            }
                                                                       );

        PageRecord? result = null;
        int attempt = 0;
        int maxAttempts = RetryPolicy.MaxRetryAttempts + 1;

        while (result == null && attempt < maxAttempts)
        {
            if (attempt > 0)
            {
                var delay = RetryPolicy.ComputeRetryDelay(attempt - 1);
                mLogger.LogInformation("Retrying single-page fetch of {Url} (attempt {Attempt}/{Max}) after {Delay}",
                                       url,
                                       attempt + 1,
                                       maxAttempts,
                                       delay
                                      );
                await Task.Delay(delay, ct);
            }

            result = await TryFetchSingleOnceAsync(browser, libraryId, version, url, ct);
            attempt++;
        }

        if (result == null)
            mLogger.LogWarning("Single-page fetch failed after {Attempts} attempts: {Url}", maxAttempts, url);

        return result;
    }

    private async Task<PageRecord?> TryFetchSingleOnceAsync(IBrowser browser,
                                                             string libraryId,
                                                             string version,
                                                             string url,
                                                             CancellationToken ct)
    {
        PageRecord? result = null;
        var page = await browser.NewPageAsync();
        try
        {
            var response = await NavigateAndPreparePageAsync(page, url, ct);
            if (response != null && response.Ok)
                result = await BuildAndPersistPageRecordAsync(page, libraryId, version, url, ct);
            else
            {
                int status = response?.Status ?? 0;
                mLogger.LogWarning("Single-page fetch got status {Status} for {Url}", status, url);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogError(ex, "Single-page fetch error for {Url}", url);
        }
        finally
        {
            await page.CloseAsync();
        }

        return result;
    }

    private async Task<PageRecord> BuildAndPersistPageRecordAsync(IPage page,
                                                                    string libraryId,
                                                                    string version,
                                                                    string url,
                                                                    CancellationToken ct)
    {
        string title = await page.TitleAsync();
        await ExpandCollapsibleNavigationAsync(page);
        string content = await ExtractMainContentAsync(page);

        string contentHash = ComputeHash(content);
        string urlHash = ComputeHash(url);

        var record = new PageRecord
                         {
                             Id = $"{libraryId}/{version}/{urlHash[..12]}",
                             LibraryId = libraryId,
                             Version = version,
                             Url = url,
                             Title = title,
                             Category = DocCategory.Unclassified,
                             RawContent = content,
                             FetchedAt = DateTime.UtcNow,
                             ContentHash = contentHash
                         };

        await mPageRepository.UpsertPageAsync(record, ct);
        return record;
    }

    /// <summary>
    ///     Crawl a documentation library starting from the root URL.
    ///     Spawns up to <see cref="MaxParallelWorkers"/> concurrent workers
    ///     that pull entries off a shared channel; each fetch is gated by a
    ///     <see cref="HostRateLimiter"/> keyed on the URL's host.
    /// </summary>
    public async Task CrawlAsync(ScrapeJob job,
                                 ChannelWriter<PageRecord> output,
                                 IReadOnlySet<string>? resumeUrls = null,
                                 Action<int>? onPageFetched = null,
                                 Action<int>? onQueued = null,
                                 Action? onFetchError = null,
                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                                                                            {
                                                                                Headless = true,
                                                                                Args =
                                                                                    [
                                                                                        $"--user-agent={BrowserUserAgent}"
                                                                                    ]
                                                                            }
                                                                       );

        var rootUri = new Uri(job.RootUrl);
        var rootScope = ComputeRootScope(rootUri);

        var ctx = new CrawlContext
                      {
                          Job = job,
                          RootScope = rootScope,
                          PageOutput = output,
                          InScopeEntries = Channel.CreateUnbounded<CrawlEntry>(),
                          OffPathEntries = Channel.CreateUnbounded<CrawlEntry>(),
                          RetryEntries = Channel.CreateUnbounded<CrawlEntry>(),
                          Visited = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase),
                          ClonedRepos = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase),
                          Budget = new CrawlBudget(),
                          OnPageFetched = onPageFetched,
                          OnQueued = onQueued,
                          OnFetchError = onFetchError,
                          Token = ct
                      };

        if (resumeUrls != null)
        {
            foreach(string resumeUrl in resumeUrls)
                ctx.Visited.TryAdd(resumeUrl, value: 0);
            mLogger.LogInformation("Resume: seeded visited set with {Count} existing URLs", resumeUrls.Count);
        }

        string normalizedRoot = NormalizeUrl(job.RootUrl) ?? job.RootUrl;
        var rootEntry = new CrawlEntry(normalizedRoot, InScopeDepth: 0, SameHostDepth: 0, OffSiteDepth: 0);
        ctx.IncrementInFlight();
        ctx.InScopeEntries.Writer.TryWrite(rootEntry);

        int workerCount = Math.Max(1, MaxParallelWorkers);
        var workerTasks = new Task[workerCount + 1];
        for(var i = 0; i < workerCount; i++)
            workerTasks[i] = Task.Run(() => RunCrawlWorkerAsync(ctx, browser), ct);
        workerTasks[workerCount] = Task.Run(() => RunRetryWorkerAsync(ctx, browser), ct);

        try
        {
            await Task.WhenAll(workerTasks);
        }
        finally
        {
            ctx.CompleteAllEntries();
        }

        if (ctx.DroppedInScopeUrls.Count > 0)
        {
            mLogger.LogWarning("Dropped {Count} in-scope URLs after exhausting retries: {Urls}",
                               ctx.DroppedInScopeUrls.Count,
                               string.Join(DroppedUrlSeparator, ctx.DroppedInScopeUrls)
                              );
        }

        mLogger.LogInformation("Crawl complete for {LibraryId} v{Version}: {Count} pages, {Hosts} hosts, {Dropped} dropped",
                               job.LibraryId,
                               job.Version,
                               ctx.PageCount,
                               ctx.Budget.HostCount,
                               ctx.DroppedInScopeUrls.Count
                              );
        output.Complete();
    }

    private async Task RunCrawlWorkerAsync(CrawlContext ctx, IBrowser browser)
    {
        bool keepGoing = true;
        while (keepGoing)
            keepGoing = await TryProcessNextAsync(ctx, browser);
    }

    /// <summary>
    ///     Attempt to dequeue and process one entry, preferring the in-scope
    ///     channel. Returns false only when both channels are completed and
    ///     drained, signalling the worker to exit.
    /// </summary>
    private async Task<bool> TryProcessNextAsync(CrawlContext ctx, IBrowser browser)
    {
        bool keepGoing;

        if (ctx.InScopeEntries.Reader.TryRead(out var entry) ||
            ctx.OffPathEntries.Reader.TryRead(out entry))
        {
            await HandleCrawlEntryAsync(entry, ctx, browser);
            keepGoing = true;
        }
        else
            keepGoing = await WaitForAvailabilityAsync(ctx);

        return keepGoing;
    }

    /// <summary>
    ///     Block until either channel signals readability, both channels
    ///     complete, or cancellation fires. Returns true if more work may
    ///     be available, false if both channels are drained.
    /// </summary>
    private static async Task<bool> WaitForAvailabilityAsync(CrawlContext ctx)
    {
        var inScopeWait = WaitOrFalseAsync(ctx.InScopeEntries.Reader, ctx.Token);
        var offPathWait = WaitOrFalseAsync(ctx.OffPathEntries.Reader, ctx.Token);

        var first = await Task.WhenAny(inScopeWait, offPathWait);
        bool firstAvailable = await first;

        bool result;
        if (firstAvailable)
            result = true;
        else
        {
            var other = first == inScopeWait ? offPathWait : inScopeWait;
            result = await other;
        }

        return result;
    }

    private static async Task<bool> WaitOrFalseAsync(ChannelReader<CrawlEntry> reader, CancellationToken ct)
    {
        bool result;
        try
        {
            result = await reader.WaitToReadAsync(ct);
        }
        catch(OperationCanceledException)
        {
            result = false;
        }

        return result;
    }

    /// <summary>
    ///     Dedicated worker that drains <see cref="CrawlContext.RetryEntries"/>
    ///     sequentially, sleeping <see cref="RetryPolicy.MinDelayBetweenRetriesMs"/>
    ///     between attempts so the WAF sees a slow trickle rather than a burst.
    ///     Runs alongside the main worker pool — main work isn't blocked on
    ///     retries, but retries also don't compete with main work for slots.
    /// </summary>
    private async Task RunRetryWorkerAsync(CrawlContext ctx, IBrowser browser)
    {
        bool keepReading = true;
        while (keepReading)
        {
            try
            {
                keepReading = await ctx.RetryEntries.Reader.WaitToReadAsync(ctx.Token);
            }
            catch(OperationCanceledException)
            {
                keepReading = false;
            }

            if (keepReading && ctx.RetryEntries.Reader.TryRead(out var entry))
            {
                await HandleRetryEntryAsync(entry, ctx, browser);
                keepReading = await DelayBetweenRetriesAsync(ctx.Token);
            }
        }
    }

    private static async Task<bool> DelayBetweenRetriesAsync(CancellationToken ct)
    {
        bool result = true;
        try
        {
            await Task.Delay(RetryPolicy.MinDelayBetweenRetriesMs, ct);
        }
        catch(OperationCanceledException)
        {
            result = false;
        }

        return result;
    }

    /// <summary>
    ///     Process a retry attempt without re-checking <c>Visited</c> or the
    ///     gated-path filter — both were satisfied on the original attempt.
    /// </summary>
    private async Task HandleRetryEntryAsync(CrawlEntry entry, CrawlContext ctx, IBrowser browser)
    {
        try
        {
            await ProcessCrawlEntryAsync(entry, ctx, browser);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogError(ex,
                             "Retry worker error processing {Url} (retry {Attempt})",
                             entry.Url,
                             entry.RetryAttemptIndex
                            );
        }
        finally
        {
            int remaining = ctx.DecrementInFlight();
            if (remaining == 0)
                ctx.CompleteAllEntries();

            ctx.OnQueued?.Invoke(ctx.InFlightCount);
        }
    }

    private async Task HandleCrawlEntryAsync(CrawlEntry entry, CrawlContext ctx, IBrowser browser)
    {
        try
        {
            bool overLimit = ctx.Job.MaxPages > 0 && ctx.PageCount >= ctx.Job.MaxPages;
            bool gated = !overLimit && ctx.IsGated(entry.Url);
            bool firstVisit = !overLimit && !gated && ctx.Visited.TryAdd(entry.Url, value: 0);
            if (firstVisit)
                await ProcessCrawlEntryAsync(entry, ctx, browser);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogError(ex, "Worker error processing {Url}", entry.Url);
        }
        finally
        {
            int remaining = ctx.DecrementInFlight();
            if (remaining == 0)
                ctx.CompleteAllEntries();

            ctx.OnQueued?.Invoke(ctx.InFlightCount);
        }
    }

    private async Task ProcessCrawlEntryAsync(CrawlEntry entry, CrawlContext ctx, IBrowser browser)
    {
        string url = entry.Url;

        switch(true)
        {
            case true when !IsAllowed(url, ctx.Job):
                break;
            case true when GitHubRepoScraper.TryParseGitHubUrl(url, out string owner, out string repo):
                string repoKey = $"{owner}/{repo}";
                if (ctx.ClonedRepos.TryAdd(repoKey, value: 0))
                {
                    mLogger.LogInformation("Delegating to GitHub scraper for {Repo}", repoKey);
                    await mGitHubScraper.ScrapeRepositoryAsync(owner, repo, ctx.Job, ctx.PageOutput, ctx.Token);
                }

                break;
            default:
                await ProcessCrawlScopeAsync(entry, ctx, browser);
                break;
        }
    }

    private async Task ProcessCrawlScopeAsync(CrawlEntry entry, CrawlContext ctx, IBrowser browser)
    {
        string url = entry.Url;
        bool inScope = IsInRootScope(url, ctx.RootScope);
        bool sameHost = !inScope && IsSameHost(url, ctx.RootScope);
        bool depthExceeded = inScope  ? ctx.Job.InScopeDepth > 0 && entry.InScopeDepth >= ctx.Job.InScopeDepth :
                             sameHost ? entry.SameHostDepth >= ctx.Job.SameHostDepth :
                                        entry.OffSiteDepth >= ctx.Job.OffSiteDepth;

        if (depthExceeded)
        {
            int displayDepth = inScope  ? entry.InScopeDepth :
                               sameHost ? entry.SameHostDepth : entry.OffSiteDepth;
            mLogger.LogDebug("Skipping {Url} - depth {Depth} exceeded", url, displayDepth);
        }
        else
            await FetchCrawlPageAsync(entry, inScope, ctx, browser);
    }

    private async Task FetchCrawlPageAsync(CrawlEntry entry, bool inScope, CrawlContext ctx, IBrowser browser)
    {
        string url = entry.Url;
        bool fetchSameHost = !inScope && IsSameHost(url, ctx.RootScope);
        int fetchDepth = fetchSameHost ? entry.SameHostDepth : entry.OffSiteDepth;
        mLogger.LogInformation("Fetching ({Count}) [{Scope} d={Depth}]: {Url}",
                               ctx.PageCount + 1,
                               inScope ? InScopeLabel : OutOfScopeLabel,
                               fetchDepth,
                               url
                              );

        var hostUri = new Uri(url);
        var limiter = ctx.Budget.GetLimiter(hostUri.Host);

        using var slot = await limiter.AcquireAsync(ctx.Token);

        var page = await browser.NewPageAsync();
        try
        {
            string fetchUrl = url;
            var response = await NavigateAndPreparePageAsync(page, fetchUrl, ctx.Token);

            if (response != null && response.Status == HttpNotFound && ctx.SiteExtension == null)
                (page, response, fetchUrl) = await RetryWithExtensionsAsync(url, page, browser, ctx, ctx.Token);

            await DispatchFetchOutcomeAsync(response, page, fetchUrl, entry, ctx, limiter, url);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            limiter.ReportTransientError();
            ctx.OnFetchError?.Invoke();
            mLogger.LogError(ex, "Error fetching {Url}", url);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task DispatchFetchOutcomeAsync(IResponse? response,
                                                  IPage page,
                                                  string fetchUrl,
                                                  CrawlEntry entry,
                                                  CrawlContext ctx,
                                                  HostRateLimiter limiter,
                                                  string originalUrl)
    {
        if (response == null)
        {
            limiter.ReportTransientError();
            ctx.OnFetchError?.Invoke();
            mLogger.LogWarning("No response from {Url}", originalUrl);
        }
        else
            await DispatchKnownResponseAsync(response, page, fetchUrl, entry, ctx, limiter, originalUrl);
    }

    private async Task DispatchKnownResponseAsync(IResponse response,
                                                   IPage page,
                                                   string fetchUrl,
                                                   CrawlEntry entry,
                                                   CrawlContext ctx,
                                                   HostRateLimiter limiter,
                                                   string originalUrl)
    {
        bool inScope = IsInRootScope(originalUrl, ctx.RootScope);

        switch(true)
        {
            case true when response.Ok:
                limiter.ReportSuccess();
                await CompleteSuccessfulFetchAsync(page, fetchUrl, entry, ctx);
                break;
            case true when HostRateLimiter.IsRateLimitStatus(response.Status):
                await HandleRateLimitedAsync(response, limiter, ctx, originalUrl);
                break;
            case true when HostRateLimiter.IsForbiddenStatus(response.Status) && inScope:
                await HandleInScopeForbiddenAsync(response, entry, ctx, limiter, originalUrl);
                break;
            case true when HostRateLimiter.IsForbiddenStatus(response.Status):
                HandleGatedPath(ctx, originalUrl);
                break;
            default:
                limiter.ReportTransientError();
                ctx.OnFetchError?.Invoke();
                mLogger.LogWarning("Failed to fetch {Url}: {Status}", originalUrl, response.Status);
                break;
        }
    }

    /// <summary>
    ///     Handle a 403 on an in-scope URL. The fetcher still slows the
    ///     limiter (the 403 might be rate-driven), but the page itself
    ///     gets queued for sequential retry on the dedicated retry worker
    ///     instead of being dropped immediately. After
    ///     <see cref="RetryPolicy.MaxRetryAttempts"/> failed retries the
    ///     URL is added to <see cref="CrawlContext.DroppedInScopeUrls"/>
    ///     and the error count ticks once.
    /// </summary>
    private async Task HandleInScopeForbiddenAsync(IResponse response,
                                                   CrawlEntry entry,
                                                   CrawlContext ctx,
                                                   HostRateLimiter limiter,
                                                   string originalUrl)
    {
        var retryAfter = await TryReadRetryAfterAsync(response);
        limiter.ReportRateLimited(retryAfter);

        if (entry.RetryAttemptIndex < RetryPolicy.MaxRetryAttempts)
        {
            ctx.ScheduleRetry(entry);
            mLogger.LogWarning("In-scope 403 on {Url}; scheduling retry {Next} of {Max} (retry-after {RetryAfter})",
                               originalUrl,
                               entry.RetryAttemptIndex + 1,
                               RetryPolicy.MaxRetryAttempts,
                               retryAfter
                              );
        }
        else
        {
            ctx.DroppedInScopeUrls.Add(originalUrl);
            ctx.OnFetchError?.Invoke();
            await LogForbiddenDiagnosticsAsync(response, originalUrl, entry.RetryAttemptIndex + 1);
        }
    }

    /// <summary>
    ///     On a final-drop in-scope 403, capture WAF identifying headers and
    ///     a body snippet so we can see what's blocking us. Cloudflare,
    ///     Akamai, AWS WAF, and CloudFront each leave fingerprints
    ///     (server header, cf-ray, via, x-amzn-RequestId) that point at
    ///     which layer rejected the request.
    /// </summary>
    private async Task LogForbiddenDiagnosticsAsync(IResponse response, string url, int totalAttempts)
    {
        string serverHeader = string.Empty;
        string cfRay = string.Empty;
        string via = string.Empty;
        string xAmznRequestId = string.Empty;
        string xAmzCfId = string.Empty;
        string bodySnippet = string.Empty;

        try
        {
            var headers = await response.AllHeadersAsync();
            serverHeader = HeaderOrEmpty(headers, ServerHeader);
            cfRay = HeaderOrEmpty(headers, CfRayHeader);
            via = HeaderOrEmpty(headers, ViaHeader);
            xAmznRequestId = HeaderOrEmpty(headers, XAmznRequestIdHeader);
            xAmzCfId = HeaderOrEmpty(headers, XAmzCfIdHeader);
        }
        catch(PlaywrightException)
        {
        }

        try
        {
            string body = await response.TextAsync();
            int take = Math.Min(body.Length, ForbiddenBodySnippetMaxChars);
            bodySnippet = body[..take].Replace('\n', ' ').Replace('\r', ' ');
        }
        catch(PlaywrightException)
        {
        }

        mLogger.LogWarning("Dropping in-scope {Url} after {Attempts} attempts (still 403). " +
                           "Server={Server} CF-RAY={CfRay} Via={Via} X-Amzn-RequestId={AmznId} " +
                           "X-Amz-Cf-Id={AmzCfId} Body[0..{Take}]={Body}",
                           url,
                           totalAttempts,
                           serverHeader,
                           cfRay,
                           via,
                           xAmznRequestId,
                           xAmzCfId,
                           bodySnippet.Length,
                           bodySnippet
                          );
    }

    private static string HeaderOrEmpty(IDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out string? value) ? value : string.Empty;

    private async Task HandleRateLimitedAsync(IResponse response,
                                              HostRateLimiter limiter,
                                              CrawlContext ctx,
                                              string originalUrl)
    {
        var retryAfter = await TryReadRetryAfterAsync(response);
        limiter.ReportRateLimited(retryAfter);
        ctx.OnFetchError?.Invoke();
        mLogger.LogWarning("Backoff fetching {Url}: status {Status}, retry-after {RetryAfter}",
                           originalUrl,
                           response.Status,
                           retryAfter
                          );
    }

    /// <summary>
    ///     Handle a 403 on an out-of-scope URL. The response means "this path
    ///     is gated", not "you're hitting me too hard" — gate the URL's first
    ///     path segment for the host and let the limiter keep its concurrency
    ///     so unrelated working paths on the same host stay fast.
    /// </summary>
    private void HandleGatedPath(CrawlContext ctx, string originalUrl)
    {
        var uri = new Uri(originalUrl);
        var filter = ctx.Budget.GetScopeFilter(uri.Host);
        filter.GatePrefixOf(uri);
        ctx.OnFetchError?.Invoke();
        mLogger.LogWarning("Gating path {Prefix} on {Host} (403 on {Url})",
                           HostScopeFilter.ExtractFirstSegment(uri),
                           uri.Host,
                           originalUrl
                          );
    }

    private async Task CompleteSuccessfulFetchAsync(IPage page,
                                                    string fetchUrl,
                                                    CrawlEntry entry,
                                                    CrawlContext ctx)
    {
        string title = await page.TitleAsync();
        await ExpandCollapsibleNavigationAsync(page);
        string content = await ExtractMainContentAsync(page);
        var links = await ExtractLinksAsync(page);

        string contentHash = ComputeHash(content);
        string urlHash = ComputeHash(fetchUrl);

        var pageRecord = new PageRecord
                             {
                                 Id = $"{ctx.Job.LibraryId}/{ctx.Job.Version}/{urlHash[..12]}",
                                 LibraryId = ctx.Job.LibraryId,
                                 Version = ctx.Job.Version,
                                 Url = fetchUrl,
                                 Title = title,
                                 Category = DocCategory.Unclassified,
                                 RawContent = content,
                                 FetchedAt = DateTime.UtcNow,
                                 ContentHash = contentHash
                             };

        await mPageRepository.UpsertPageAsync(pageRecord, ctx.Token);
        int newCount = ctx.IncrementPageCount();
        await ctx.PageOutput.WriteAsync(pageRecord, ctx.Token);

        EnqueueDiscoveredLinks(links,
                               ctx.IsVisited,
                               ctx.Job,
                               ctx.RootScope,
                               entry,
                               ctx.EnqueueChild,
                               ctx.SiteExtension != null
                              );

        ctx.OnPageFetched?.Invoke(newCount);

        if (ctx.Job.FetchDelayMs > 0)
            await Task.Delay(ctx.Job.FetchDelayMs, ctx.Token);
    }

    private static async Task<TimeSpan?> TryReadRetryAfterAsync(IResponse response)
    {
        TimeSpan? result = null;
        try
        {
            var headers = await response.AllHeadersAsync();
            if (headers.TryGetValue(RetryAfterHeader, out string? value))
                result = CrawlBudget.ParseRetryAfter(value);
        }
        catch(PlaywrightException)
        {
        }

        return result;
    }

    private async Task<IResponse?> NavigateAndPreparePageAsync(IPage page,
                                                               string url,
                                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(url);

        ct.ThrowIfCancellationRequested();

        var result = await page.GotoAsync(url,
                                          new PageGotoOptions
                                              {
                                                  WaitUntil = WaitUntilState.DOMContentLoaded,
                                                  Timeout = PageTimeoutMs
                                              }
                                         );

        if (result != null && result.Ok)
            await WaitForPageAndFramesAsync(page, url, ct);

        return result;
    }

    private async Task WaitForPageAndFramesAsync(IPage page,
                                                 string url,
                                                 CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(url);

        await WaitForLoadStateWithinTimeoutAsync(() => page.WaitForLoadStateAsync(LoadState.Load),
                                                 MainPageLoadLabel,
                                                 url,
                                                 ct
                                                );

        bool hasFrameElements = await HasFrameElementsAsync(page);
        if (hasFrameElements)
        {
            await WaitForFrameCollectionToStabilizeAsync(page, ct);

            var childFrames = page.Frames.Where(f => f != page.MainFrame).ToList();
            mLogger.LogDebug("Waiting for {Count} child frames on {Url}", childFrames.Count, url);

            foreach(var frame in childFrames)
            {
                string frameDescription = DescribeFrame(frame);
                await WaitForLoadStateWithinTimeoutAsync(() => frame.WaitForLoadStateAsync(LoadState.DOMContentLoaded),
                                                         $"frame {frameDescription}",
                                                         url,
                                                         ct
                                                        );
            }
        }
    }

    private async Task WaitForLoadStateWithinTimeoutAsync(Func<Task> waitOperation,
                                                          string targetDescription,
                                                          string url,
                                                          CancellationToken ct)
    {
        try
        {
            var waitTask = waitOperation();
            var timeoutTask = Task.Delay(LoadStateTimeoutMs, ct);
            var completedTask = await Task.WhenAny(waitTask, timeoutTask);

            if (completedTask == waitTask)
                await waitTask;
            else
            {
                ct.ThrowIfCancellationRequested();
                mLogger.LogDebug("Timed out waiting for {Target} on {Url} after {Timeout}ms",
                                 targetDescription,
                                 url,
                                 LoadStateTimeoutMs
                                );
            }
        }
        catch(PlaywrightException ex)
        {
            mLogger.LogDebug(ex, "Failed waiting for {Target} on {Url}", targetDescription, url);
        }
    }

    private static async Task<bool> HasFrameElementsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        bool result = page.Frames.Count > 1;

        if (!result)
        {
            try
            {
                var frameElementCount =
                    await page.EvaluateAsync<int>("() => document.querySelectorAll('frame,iframe').length");
                result = frameElementCount > 0;
            }
            catch(PlaywrightException)
            {
                result = page.Frames.Count > 1;
            }
        }

        return result;
    }

    private static async Task WaitForFrameCollectionToStabilizeAsync(IPage page,
                                                                     CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var stopwatch = Stopwatch.StartNew();
        int lastFrameCount = page.Frames.Count;
        var stablePollCount = 0;

        while (stopwatch.ElapsedMilliseconds < FrameDiscoveryTimeoutMs && stablePollCount < StableFramePollCount)
        {
            await Task.Delay(FrameDiscoveryPollDelayMs, ct);

            int currentFrameCount = page.Frames.Count;
            if (currentFrameCount == lastFrameCount)
                stablePollCount++;
            else
            {
                stablePollCount = 0;
                lastFrameCount = currentFrameCount;
            }
        }
    }

    private static string DescribeFrame(IFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        string result = frame.Name;

        if (string.IsNullOrWhiteSpace(result))
            result = frame.Url;

        if (string.IsNullOrWhiteSpace(result))
            result = "(unnamed frame)";

        return result;
    }

    /// <summary>
    ///     Normalize and enqueue discovered links from a fetched page.
    ///     Applies 3-tier depth assignment:
    ///     in root scope - both depths reset to 0;
    ///     same host, different path - increment SameHostDepth;
    ///     different host - increment OffSiteDepth.
    ///     The enqueue callback receives the in-scope flag so the caller
    ///     can route entries to a priority channel.
    /// </summary>
    private static void EnqueueDiscoveredLinks(IReadOnlyList<string> links,
                                               Func<string, bool> isVisited,
                                               ScrapeJob job,
                                               RootScope rootScope,
                                               CrawlEntry parentEntry,
                                               Action<CrawlEntry, bool> enqueue,
                                               bool keepExtension = false)
    {
        foreach(string normalized in links
                                     .Select(u => NormalizeUrl(u, keepExtension))
                                     .OfType<string>()
                                     .Where(n => !isVisited(n) && IsAllowed(n, job)))
        {
            bool linkInScope = IsInRootScope(normalized, rootScope);
            bool linkSameHost = !linkInScope && IsSameHost(normalized, rootScope);

            var child = linkInScope switch
                {
                    true => new CrawlEntry(normalized, parentEntry.InScopeDepth + 1, SameHostDepth: 0, OffSiteDepth: 0),
                    false => linkSameHost
                                 ? new CrawlEntry(normalized,
                                                  parentEntry.InScopeDepth,
                                                  parentEntry.SameHostDepth + 1,
                                                  parentEntry.OffSiteDepth
                                                 )
                                 : new CrawlEntry(normalized,
                                                  parentEntry.InScopeDepth,
                                                  parentEntry.SameHostDepth,
                                                  parentEntry.OffSiteDepth + 1
                                                 )
                };

            enqueue(child, linkInScope);
        }
    }

    /// <summary>
    ///     Compute the "scope" of the root URL â€” same domain plus path-prefix.
    ///     A page is considered in-scope if its host matches AND its path
    ///     starts with the root's parent path.
    ///     Example: root https://docs.foo.com/v2/intro/quick-start.html
    ///     scope (foo.com, /v2/intro/)
    ///     in scope: /v2/intro/getting-started.html, /v2/intro/sub/page.html
    ///     out of scope: /v2/api/X.html, /blog/y.html
    /// </summary>
    private static RootScope ComputeRootScope(Uri rootUri)
    {
        string path = rootUri.AbsolutePath;
        int lastSlash = path.LastIndexOf(value: '/');
        string scopePath = lastSlash >= 0 ? path[..(lastSlash + 1)] : "/";

        var result = new RootScope(rootUri.Host, scopePath);
        return result;
    }

    private static bool IsInRootScope(string url, RootScope scope)
    {
        var result = false;
        try
        {
            var uri = new Uri(url);
            result = string.Equals(uri.Host, scope.Host, StringComparison.OrdinalIgnoreCase) &&
                     uri.AbsolutePath.StartsWith(scope.PathPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed URL â€” treat as out of scope
        }

        return result;
    }

    /// <summary>
    ///     Returns true when the URL is on the same host as the root scope
    ///     but does NOT fall within the root path prefix.
    /// </summary>
    private static bool IsSameHost(string url, RootScope scope)
    {
        var result = false;
        try
        {
            var uri = new Uri(url);
            result = string.Equals(uri.Host, scope.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed URL â€” treat as off-site
        }

        return result;
    }

    private static async Task<string> ExtractMainContentAsync(IPage page)
    {
        // First try the main page with CSS selectors
        string result = await ExtractContentFromFrameAsync(page);

        // If the main page has little content, check iframes for a content frame
        if (result.Length < ContentFrameMinChars && page.Frames.Count > 1)
        {
            var bestFrame = await FindContentFrameAsync(page);
            if (bestFrame != null)
            {
                string frameContent = await ExtractTextFromFrameAsync(bestFrame);
                if (frameContent.Length > result.Length)
                    result = frameContent;
            }
        }

        return result;
    }

    private static async Task<string> ExtractContentFromFrameAsync(IPage page)
    {
        var contentSelectors = new[]
                                   {
                                       "main", "article", "[role='main']",
                                       ".content", ".doc-content", ".documentation",
                                       "#content", "#main-content"
                                   };

        var result = string.Empty;

        foreach(string selector in contentSelectors.Where(_ => result == string.Empty))
        {
            var element = await page.QuerySelectorAsync(selector);
            if (element != null)
            {
                string text = await element.InnerTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                    result = text.Trim();
            }
        }

        if (result == string.Empty)
        {
            var body = await page.QuerySelectorAsync(BodySelector);
            string bodyText = body != null ? await body.InnerTextAsync() : string.Empty;
            result = bodyText.Trim();
        }

        return result;
    }

    /// <summary>
    ///     Identify the content frame in a frameset page.
    ///     Scores each child frame by text-to-link ratio and content length.
    ///     Navigation frames are mostly links; content frames are mostly text.
    /// </summary>
    private static async Task<IFrame?> FindContentFrameAsync(IPage page)
    {
        IFrame? bestFrame = null;
        var bestScore = 0f;

        // Skip frame[0] â€” it's the main page itself
        foreach(var frame in page.Frames.Where(f => f != page.MainFrame))
        {
            float score = await ScoreFrameAsync(frame);
            if (score > bestScore)
            {
                bestScore = score;
                bestFrame = frame;
            }
        }

        return bestFrame;
    }

    /// <summary>
    ///     Score a frame as a content frame candidate.
    ///     Higher = more likely to be the content frame.
    ///     Uses text length, text-to-link ratio, and name hints.
    /// </summary>
    private static async Task<float> ScoreFrameAsync(IFrame frame)
    {
        var score = 0f;
        try
        {
            var stats = await frame.EvaluateAsync<FrameStats?>(expression: """
                                                                           (() => {
                                                                               const body = document.body;
                                                                               if (!body) return null;
                                                                               const text = body.innerText || '';
                                                                               const links = document.querySelectorAll('a[href]');
                                                                               const linkText = Array.from(links).map(a => a.innerText || '').join('');
                                                                               return {
                                                                                   textLength: text.length,
                                                                                   linkTextLength: linkText.length
                                                                               };
                                                                           })()
                                                                           """
                                                              );

            if (stats != null && stats.TextLength > 0)
            {
                // Text length score (more text = more likely content)
                float textScore = Math.Min(stats.TextLength / 1000f, ContentFrameMaxTextScore);

                // Text-to-link ratio (high ratio = content, low ratio = nav)
                float nonLinkTextLength = stats.TextLength - stats.LinkTextLength;
                float ratioScore = stats.TextLength > 0
                                       ? (nonLinkTextLength / stats.TextLength) * ContentFrameMaxRatioScore
                                       : 0f;

                // Name hints bonus
                var nameBonus = 0f;
                string frameName = frame.Name.ToLowerInvariant();
                string frameUrl = frame.Url.ToLowerInvariant();
                if (smContentFrameHints.Any(h => frameName.Contains(h) || frameUrl.Contains(h)))
                    nameBonus = ContentFrameNameBonus;

                score = textScore + ratioScore + nameBonus;
            }
        }
        catch
        {
            // Frame may be cross-origin or unavailable
        }

        return score;
    }

    private static async Task<string> ExtractTextFromFrameAsync(IFrame frame)
    {
        var result = string.Empty;
        try
        {
            var text = await frame.EvaluateAsync<string>(expression: """
                                                                     (() => {
                                                                         const selectors = ['main', 'article', '[role="main"]',
                                                                             '.content', '.doc-content', '.documentation',
                                                                             '#content', '#main-content'];
                                                                         for (const sel of selectors) {
                                                                             const el = document.querySelector(sel);
                                                                             if (el && el.innerText && el.innerText.trim().length > 100)
                                                                                 return el.innerText.trim();
                                                                         }
                                                                         return document.body ? document.body.innerText.trim() : '';
                                                                     })()
                                                                     """
                                                        );
            result = text;
        }
        catch
        {
            // Frame may be cross-origin
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> ExtractLinksAsync(IPage page)
    {
        var allLinks = new List<string>();

        // Collect links from the main page
        var mainLinks = await ExtractLinksFromFrameAsync(page.MainFrame);
        allLinks.AddRange(mainLinks);

        // Collect links from all child frames (nav frames have the TOC links)
        foreach(var frame in page.Frames.Where(f => f != page.MainFrame))
        {
            var frameLinks = await ExtractLinksFromFrameAsync(frame);
            allLinks.AddRange(frameLinks);
        }

        var result = allLinks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    private static async Task<IReadOnlyList<string>> ExtractLinksFromFrameAsync(IFrame frame)
    {
        IReadOnlyList<string> result = [];
        try
        {
            string[] links = await frame.EvaluateAsync<string[]>(expression: """
                                                                             Array.from(document.querySelectorAll('a[href]'))
                                                                                 .map(a => a.href)
                                                                                 .filter(h => h.startsWith('http'))
                                                                             """
                                                                );
            result = links;
        }
        catch
        {
            // Frame may be cross-origin or detached
        }

        return result;
    }

    /// <summary>
    ///     Expand every collapsible TOC/sidebar node in the page so that
    ///     all nested links become discoverable. Runs multiple iterations
    ///     because expanding a node may reveal more collapsed nodes.
    ///     Handles common patterns: ARIA (aria-expanded="false"), generic
    ///     .collapsed class, and Infragistics ig-tree (ui-igtree-parentnode
    ///     without ui-igtree-expanded).
    /// </summary>
    private static async Task ExpandCollapsibleNavigationAsync(IPage page)
    {
        const int MaxIterations = 10;

        try
        {
            for(var iteration = 0; iteration < MaxIterations; iteration++)
            {
                var expandedCount = await page.EvaluateAsync<int>(expression: """
                                                                              (() => {
                                                                                  const selectors = [
                                                                                      'li.ui-igtree-parentnode:not(.ui-igtree-expanded) > .ui-igtree-expander',
                                                                                      '[aria-expanded="false"]',
                                                                                      '.toc-item.collapsed > .toggle, .tree-node.collapsed > .toggle'
                                                                                  ];
                                                                                  let clicked = 0;
                                                                                  for (const sel of selectors) {
                                                                                      const els = document.querySelectorAll(sel);
                                                                                      els.forEach(el => {
                                                                                          try {
                                                                                              el.click();
                                                                                              clicked++;
                                                                                          } catch (e) {}
                                                                                      });
                                                                                  }
                                                                                  return clicked;
                                                                              })()
                                                                              """
                                                                 );

                if (expandedCount == 0)
                    break;

                await Task.Delay(CollapsibleExpansionDelayMs);
            }
        }
        catch
        {
            // Best effort â€” if expansion fails, the crawl still proceeds with
            // whatever links are currently visible.
        }
    }

    private static bool IsAllowed(string url, ScrapeJob job)
    {
        var regexTimeout = TimeSpan.FromMilliseconds(RegexTimeoutMs);

        bool isBinary = smBinaryExtensionPatterns.Any(pattern =>
                                                          SafeRegexIsMatch(url, pattern, regexTimeout)
                                                     );

        bool result = !isBinary;

        if (result)
        {
            bool allowed = job.AllowedUrlPatterns.Any(pattern =>
                                                          SafeRegexIsMatch(url, pattern, regexTimeout)
                                                     );
            result = allowed;
        }

        if (result)
        {
            bool excluded = job.ExcludedUrlPatterns.Any(pattern =>
                                                            SafeRegexIsMatch(url, pattern, regexTimeout)
                                                       );
            result = !excluded;
        }

        return result;
    }

    /// <summary>
    ///     Regex.IsMatch with a timeout, treating RegexMatchTimeoutException as non-match.
    /// </summary>
    private static bool SafeRegexIsMatch(string input, string pattern, TimeSpan timeout)
    {
        bool result;
        try
        {
            result = Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, timeout);
        }
        catch(RegexMatchTimeoutException)
        {
            result = false;
        }

        return result;
    }

    /// <summary>
    ///     Try each known extension (.html, .htm, .aspx) on a 404 URL.
    ///     Returns updated page, response, and fetch URL.
    /// </summary>
    private async Task<(IPage Page, IResponse? Response, string FetchUrl)>
        RetryWithExtensionsAsync(string url, IPage page, IBrowser browser, CrawlContext ctx, CancellationToken ct)
    {
        string fetchUrl = url;
        IResponse? response = null;

        foreach (string ext in smExtensionsToStrip)
        {
            string retryUrl = url + ext;
            mLogger.LogDebug("Got 404 for {Url}, retrying with {Ext}: {RetryUrl}", url, ext, retryUrl);
            await page.CloseAsync();
            page = await browser.NewPageAsync();
            response = await NavigateAndPreparePageAsync(page, retryUrl, ct);

            if (response != null && response.Ok)
            {
                ctx.SiteExtension = ext;
                fetchUrl = retryUrl;
                mLogger.LogInformation("Site requires {Ext} extensions, switching to extension-preserving mode", ext);
                break;
            }
        }

        return (page, response, fetchUrl);
    }

    private static string? NormalizeUrl(string url, bool keepExtension = false)
    {
        string? result;
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath.TrimEnd(trimChar: '/');

            if (!keepExtension)
            {
                string? strippedExtension = smExtensionsToStrip
                    .FirstOrDefault(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

                if (strippedExtension != null)
                    path = path[..^strippedExtension.Length];
            }

            var normalized = $"{uri.Scheme}://{uri.Host}{path}";
            if (!string.IsNullOrEmpty(uri.Query))
                normalized += uri.Query;
            result = normalized;
        }
        catch(UriFormatException)
        {
            result = null;
        }

        return result;
    }

    private static string ComputeHash(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        string result = Convert.ToHexStringLower(bytes);
        return result;
    }

    private const int PageTimeoutMs = 30000;
    private const int LoadStateTimeoutMs = 5000;
    private const int FrameDiscoveryTimeoutMs = 2000;
    private const int FrameDiscoveryPollDelayMs = 100;
    private const int StableFramePollCount = 2;
    private const int CollapsibleExpansionDelayMs = 200;
    private const int SamplePendingUrlCount = 30;
    private const int SamplePageLimit = 50;
    private const int RegexTimeoutMs = 100;
    private const int ErrorMessageMaxLength = 200;
    private const int HttpNotFound = 404;
    private const string RetryAfterHeader = "retry-after";
    private const string DroppedUrlSeparator = ", ";
    private const int ForbiddenBodySnippetMaxChars = 400;

    private const string ServerHeader = "server";
    private const string CfRayHeader = "cf-ray";
    private const string ViaHeader = "via";
    private const string XAmznRequestIdHeader = "x-amzn-requestid";
    private const string XAmzCfIdHeader = "x-amz-cf-id";
    private const int MaxParallelWorkers = 8;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private const int ContentFrameMinChars = 200;
    private const float ContentFrameMaxTextScore = 5f;
    private const float ContentFrameMaxRatioScore = 5f;
    private const float ContentFrameNameBonus = 3f;

    private static readonly string[] smContentFrameHints =
            [FrameHintContent, FrameHintMain, FrameHintTopic, FrameHintBody, FrameHintDetail, FrameHintArticle];

    /// <summary>
    ///     File extensions that aren't web pages. Playwright can't render them
    ///     and the content extractor would get nothing useful anyway.
    ///     Applied automatically in addition to ScrapeJob.ExcludedUrlPatterns.
    /// </summary>
    private static readonly string[] smBinaryExtensionPatterns =
        [
            @"\.pdf(\?|$)", @"\.zip(\?|$)", @"\.tar(\?|$)", @"\.gz(\?|$)", @"\.7z(\?|$)",
            @"\.exe(\?|$)", @"\.dmg(\?|$)", @"\.msi(\?|$)",
            @"\.png(\?|$)", @"\.jpg(\?|$)", @"\.jpeg(\?|$)", @"\.gif(\?|$)",
            @"\.svg(\?|$)", @"\.webp(\?|$)", @"\.ico(\?|$)", @"\.bmp(\?|$)",
            @"\.mp3(\?|$)", @"\.mp4(\?|$)", @"\.webm(\?|$)", @"\.mov(\?|$)",
            @"\.woff(\?|$)", @"\.woff2(\?|$)", @"\.ttf(\?|$)", @"\.eot(\?|$)",
            @"\.css(\?|$)", @"\.js(\?|$)", @"\.map(\?|$)"
        ];

    private static readonly string[] smExtensionsToStrip = [ExtensionHtml, ExtensionHtm, ExtensionAspx];
    private const string InScopeLabel = "scope";
    private const string OutOfScopeLabel = "out";
    private const string MainPageLoadLabel = "main page load";
    private const string BodySelector = "body";

    private const string FrameHintContent = "content";
    private const string FrameHintMain = "main";
    private const string FrameHintTopic = "topic";
    private const string FrameHintBody = "body";
    private const string FrameHintDetail = "detail";
    private const string FrameHintArticle = "article";

    private const string ExtensionHtml = ".html";
    private const string ExtensionHtm = ".htm";
    private const string ExtensionAspx = ".aspx";

}
